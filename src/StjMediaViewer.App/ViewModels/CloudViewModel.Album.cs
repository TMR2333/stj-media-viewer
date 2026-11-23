using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 云相册（与云同步同一页面）：浏览云端目录、按需缩略图、导入到素材库、导出本地素材。
/// 浏览只列一层；缩略图优先 Range 读取内嵌预览，失败才整文件下载。
/// </summary>
public sealed partial class CloudViewModel
{
    private CloudAlbumService? _album;

    private CloudAlbumService Album => _album ??= new CloudAlbumService(
        AppServices.Cloud.CreateStorage, AppServices.Thumbnails, AppServices.Settings.AppDataDirectory);

    public ObservableCollection<CloudAlbumRow> AlbumRows { get; } = new();
    private readonly HashSet<string> _albumSelection = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private string _albumPath = "";
    [ObservableProperty] private string _albumBreadcrumb = "/";
    [ObservableProperty] private bool _albumBusy;
    [ObservableProperty] private string _albumStatus = "选一个同步目标，再点「刷新」加载云相册。";

    public Visibility AlbumBusyVisibility => AlbumBusy ? Visibility.Visible : Visibility.Collapsed;

    partial void OnAlbumBusyChanged(bool value) => OnPropertyChanged(nameof(AlbumBusyVisibility));

    public int AlbumSelectedCount => _albumSelection.Count;

    public async Task LoadAlbumAsync(string? path = null)
    {
        var target = SelectedTarget?.Target ?? Targets.FirstOrDefault()?.Target;
        if (target is null)
        {
            AlbumStatus = "先添加 / 选择一个云同步目标";
            return;
        }
        if (AlbumBusy) return;

        AlbumBusy = true;
        AlbumPath = path ?? AlbumPath;
        AlbumBreadcrumb = "/" + AlbumPath;
        AlbumStatus = "正在列出云端目录…";
        try
        {
            var entries = await Album.ListFolderAsync(target, AlbumPath);
            AlbumRows.Clear();
            _albumSelection.Clear();
            OnPropertyChanged(nameof(AlbumSelectedCount));
            foreach (var entry in entries) AlbumRows.Add(new CloudAlbumRow(entry));
            AlbumStatus = entries.Count == 0
                ? "这个目录是空的"
                : $"共 {entries.Count} 项（{entries.Count(e => e.IsDirectory)} 个文件夹 / {entries.Count(e => !e.IsDirectory)} 个文件）";
            _ = LoadAlbumThumbnailsAsync(target, AlbumRows.ToList());
        }
        catch (Exception ex)
        {
            AlbumStatus = "列出云端目录失败：" + ex.Message;
            Log.Warn("[cloud-album] 列目录失败", ex);
        }
        finally { AlbumBusy = false; }
    }

    private async Task LoadAlbumThumbnailsAsync(CloudTarget target, List<CloudAlbumRow> rows)
    {
        var files = rows.Where(r => r.IsImageLike || r.IsVideoLike).ToList();
        using var gate = new SemaphoreSlim(3, 3);
        var tasks = files.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var path = await Album.GetThumbnailPathAsync(target, row.Entry, 320);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    bitmap.UriSource = new Uri(path);
                    row.Thumbnail = bitmap;
                }
            }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    public void SetAlbumSelection(IEnumerable<CloudAlbumRow> rows)
    {
        _albumSelection.Clear();
        foreach (var row in rows) _albumSelection.Add(row.Entry.RelativePath);
        OnPropertyChanged(nameof(AlbumSelectedCount));
    }

    public void SelectAllAlbum()
    {
        _albumSelection.Clear();
        foreach (var row in AlbumRows) _albumSelection.Add(row.Entry.RelativePath);
        OnPropertyChanged(nameof(AlbumSelectedCount));
    }

    public Task AlbumUpAsync()
    {
        var parent = AlbumPath.Contains('/') ? AlbumPath[..AlbumPath.LastIndexOf('/')] : "";
        return LoadAlbumAsync(parent);
    }

    public Task AlbumRefreshAsync() => LoadAlbumAsync(AlbumPath);

    public async Task OpenAlbumRowAsync(CloudAlbumRow row)
    {
        if (row.IsDirectory)
        {
            await LoadAlbumAsync(row.Entry.RelativePath);
            return;
        }
        await DownloadPreviewAsync(row);
    }

    /// <summary>下载整文件到临时目录（预览用），返回临时路径。</summary>
    public async Task<string?> DownloadPreviewAsync(CloudAlbumRow row)
    {
        var target = SelectedTarget?.Target;
        if (target is null) return null;
        try
        {
            AlbumStatus = $"正在下载预览：{row.Name}";
            var path = await Album.DownloadFullAsync(target, row.Entry);
            AlbumStatus = "预览已就绪";
            return path;
        }
        catch (Exception ex)
        {
            AlbumStatus = "预览下载失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>下载选中项到第一个本地素材库，并触发扫描。</summary>
    public async Task ImportAlbumSelectionAsync()
    {
        var target = SelectedTarget?.Target;
        var library = AppServices.Settings.Current.Libraries.FirstOrDefault();
        if (target is null || library is null)
        {
            AlbumStatus = library is null ? "先在设置 → 媒体库添加一个本地素材库" : "先选择云同步目标";
            return;
        }
        var rows = AlbumRows.Where(r => _albumSelection.Contains(r.Entry.RelativePath) && !r.IsDirectory).ToList();
        if (rows.Count == 0)
        {
            AlbumStatus = "先勾选要下载的文件";
            return;
        }

        AlbumBusy = true;
        try
        {
            var progress = new Progress<string>(msg => AlbumStatus = msg);
            var result = await Album.ImportAsync(target, rows.Select(r => r.Entry).ToList(), library, progress);
            AlbumStatus = "已导入素材库：" + result.Describe();
            if (result.Messages.Count > 0) AlbumStatus += " · " + result.Messages[0];
            AppEvents.RaiseLibraryContentChanged();
            await AppServices.Library.ScanLibraryAsync(library, new ScanOptions());
            AppEvents.RaiseLibraryContentChanged();
        }
        catch (Exception ex)
        {
            AlbumStatus = "导入失败：" + ex.Message;
            Log.Warn("[cloud-album] 导入失败", ex);
        }
        finally { AlbumBusy = false; }
    }

    /// <summary>把本地素材库里选中的素材（含 XMP 边车）上传到当前云相册目录。</summary>
    public async Task ExportLibrarySelectionAsync()
    {
        var target = SelectedTarget?.Target;
        if (target is null)
        {
            AlbumStatus = "先选择云同步目标";
            return;
        }
        var assets = await LibraryViewModel.Shared.ResolveOperationTargetsAsync();
        if (assets.Count == 0)
        {
            AlbumStatus = "先在素材库选中要上传的素材";
            return;
        }

        AlbumBusy = true;
        try
        {
            var progress = new Progress<string>(msg => AlbumStatus = msg);
            var result = await Album.ExportAsync(target, assets, AlbumPath, progress);
            AlbumStatus = "已上传到云相册：" + result.Describe();
            if (result.Messages.Count > 0) AlbumStatus += " · " + result.Messages[0];
        }
        catch (Exception ex)
        {
            AlbumStatus = "上传失败：" + ex.Message;
            Log.Warn("[cloud-album] 上传失败", ex);
        }
        finally { AlbumBusy = false; }
    }
}

/// <summary>云相册里的一行（文件夹或文件）。</summary>
public sealed partial class CloudAlbumRow : ObservableObject
{
    public CloudAlbumRow(CloudEntry entry) => Entry = entry;

    public CloudEntry Entry { get; }
    public string Name => Entry.FileName;
    public bool IsDirectory => Entry.IsDirectory;
    public string SizeText => IsDirectory ? "" : FormatSize(Entry.SizeBytes);
    public string ModifiedText => Entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string IconGlyph => IsDirectory ? "\uE8B7" : IsImageLike ? "\uEB9F" : IsVideoLike ? "\uE714" : "\uE8A5";

    public bool IsImageLike => !IsDirectory && (
        Entry.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        IsRawLike);

    public bool IsVideoLike => !IsDirectory && (
        Entry.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
        Entry.FileName.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));

    private bool IsRawLike => new[] { ".arw", ".cr2", ".cr3", ".nef", ".raf", ".orf", ".rw2", ".dng" }
        .Any(ext => Entry.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty] private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.0} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}