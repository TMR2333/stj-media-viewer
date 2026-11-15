using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

#pragma warning disable MVVMTK0045 // 未启用 AOT；字段式 [ObservableProperty] 在 WinUI 3 下等价且更易读

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 画廊视图（Apple Photos / Camera Raw 那种"大画布 + 底部胶片带"）的 ViewModel。
/// - 数据源：当前素材库的**当前筛选条件**（复用 LibraryViewModel.BuildQuery，含搜索/分面/分组），一次取全量；
/// - 画布：一次只解码一张（2400px），切换时取消上一张，前后各预取一张缩略图；
/// - 胶片带：横向缩略图，选中即联动画布；键盘 ←/→ 翻页、0–5 评分、P/X 选用废片、6–9 色标。
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    public static GalleryViewModel Shared { get; } = new();

    public ObservableCollection<GalleryItem> Items { get; } = new();

    // ------------------------------------------------------------------ 当前项

    [ObservableProperty]
    private int _currentIndex = -1;

    [ObservableProperty]
    private ImageSource? _currentImage;

    [ObservableProperty]
    private bool _isLoadingImage;

    [ObservableProperty]
    private string _infoTitle = "";

    [ObservableProperty]
    private string _infoSubtitle = "";

    [ObservableProperty]
    private string _counterText = "";

    [ObservableProperty]
    private string _ratingText = "";

    [ObservableProperty]
    private string _labelText = "";

    [ObservableProperty]
    private string _pickText = "";

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _contentVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _videoVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _isFilmstripVisible = true;

    [ObservableProperty]
    private bool _isInspectorVisible;

    [ObservableProperty]
    private string _zoomText = "适应窗口";

    public Visibility InspectorVisibility => IsInspectorVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilmstripVisibility => IsFilmstripVisible ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsInspectorVisibleChanged(bool value) => OnPropertyChanged(nameof(InspectorVisibility));
    partial void OnIsFilmstripVisibleChanged(bool value) => OnPropertyChanged(nameof(FilmstripVisibility));

    public MediaAsset? CurrentAsset =>
        CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex].Asset : null;

    public int Count => Items.Count;

    private CancellationTokenSource? _imageCts;
    private bool _initialized;

    // ==================================================================================
    //  载入
    // ==================================================================================

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            // 再次进入：只刷新当前张（素材可能在其它页面被改过）
            await ReloadCurrentAsync();
            return;
        }
        _initialized = true;
        await ReloadAsync();
    }

    /// <summary>按当前筛选条件重新加载整个列表。</summary>
    public async Task ReloadAsync(int preserveIndex = -1)
    {
        try
        {
            var query = LibraryViewModel.Shared.BuildQuery(0, 0);   // limit=0 → 全部
            var assets = await AppServices.Store.QueryAsync(query);

            Items.Clear();
            foreach (var asset in assets) Items.Add(new GalleryItem(asset));

            EmptyVisibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContentVisibility = Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            var index = preserveIndex >= 0
                ? Math.Clamp(preserveIndex, 0, Math.Max(0, Items.Count - 1))
                : Math.Min(CurrentIndex < 0 ? 0 : CurrentIndex, Math.Max(0, Items.Count - 1));

            CurrentIndex = -1;
            if (Items.Count > 0) await ShowIndexAsync(index);
        }
        catch (Exception ex)
        {
            Log.Warn("画廊加载失败", ex);
            AppEvents.SetError("画廊加载失败：" + ex.Message);
        }
    }

    // ==================================================================================
    //  切换
    // ==================================================================================

    public async Task ShowIndexAsync(int index)
    {
        if (Items.Count == 0) return;
        index = Math.Clamp(index, 0, Items.Count - 1);

        var old = CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;
        if (old is not null) old.IsCurrent = false;

        CurrentIndex = index;
        var item = Items[index];
        item.IsCurrent = true;

        var asset = item.Asset;
        InfoTitle = asset.FileName ?? Path.GetFileName(asset.FullPath);
        InfoSubtitle = BuildSubtitle(asset);
        CounterText = $"{index + 1} / {Items.Count}";
        RatingText = new string('★', Math.Clamp(asset.Rating, 0, 5)) + new string('☆', 5 - Math.Clamp(asset.Rating, 0, 5));
        LabelText = asset.ColorLabel switch
        {
            "red" => "● 红",
            "yellow" => "● 黄",
            "green" => "● 绿",
            "blue" => "● 蓝",
            "purple" => "● 紫",
            _ => "",
        };
        PickText = asset.IsPick switch
        {
            true => "✓ 选用",
            false => "✕ 废片",
            _ => "",
        };
        VideoVisibility = asset.IsVideo ? Visibility.Visible : Visibility.Collapsed;

        // 检视面板跟着联动（画廊右侧面板）
        try { InspectorViewModel.Shared.Show(asset); }
        catch (Exception ex) { Log.Info("画廊联动检视面板失败：" + ex.Message); }

        await LoadCanvasAsync(asset);
    }

    public Task NextAsync() => CurrentIndex < Items.Count - 1
        ? ShowIndexAsync(CurrentIndex + 1)
        : Task.CompletedTask;

    public Task PrevAsync() => CurrentIndex > 0
        ? ShowIndexAsync(CurrentIndex - 1)
        : Task.CompletedTask;

    public Task FirstAsync() => Items.Count > 0 ? ShowIndexAsync(0) : Task.CompletedTask;

    public Task LastAsync() => Items.Count > 0 ? ShowIndexAsync(Items.Count - 1) : Task.CompletedTask;

    private async Task ReloadCurrentAsync()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Items.Count)
            await ShowIndexAsync(CurrentIndex);
    }

    private async Task LoadCanvasAsync(MediaAsset asset)
    {
        _imageCts?.Cancel();
        _imageCts?.Dispose();
        _imageCts = new CancellationTokenSource();
        var token = _imageCts.Token;

        IsLoadingImage = true;
        CurrentImage = null;
        try
        {
            ImageSource? image;
            if (asset.IsVideo)
            {
                image = await ImageSourceLoader.LoadVideoPosterAsync(asset, 2400, token);
            }
            else
            {
                image = await ImageSourceLoader.LoadAsync(asset, 2400, token);
                // 缩略图服务产出的最大档未必够大，预览优先直读原文件
                image ??= await ImageSourceLoader.LoadFromFileAsync(asset.FullPath, 4096, token);
            }

            if (token.IsCancellationRequested) return;
            CurrentImage = image;
            if (image is null) InfoSubtitle += " · 无法解码";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"画廊载图失败 {asset.FileName}: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoadingImage = false;
        }
    }

    private static string BuildSubtitle(MediaAsset a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.DisplayDevice)) parts.Add(a.DisplayDevice);
        if (a.CapturedAt is { } t) parts.Add(t.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        if (a.Width > 0 && a.Height > 0) parts.Add($"{a.Width} × {a.Height}");
        if (a.SizeBytes > 0) parts.Add(Formatting.FileSize(a.SizeBytes));
        if (a.IsVideo && !string.IsNullOrWhiteSpace(a.VideoCodec))
            parts.Add(StjMediaViewer.Core.Transcode.VideoCodecNames.DisplayLabel(a.VideoCodec));
        if (!string.IsNullOrWhiteSpace(a.City)) parts.Add(a.City!);
        return string.Join(" · ", parts);
    }

    // ==================================================================================
    //  快速编辑（评分 / 色标 / 选用）
    // ==================================================================================

    public async Task SetRatingAsync(int rating)
    {
        var asset = CurrentAsset;
        if (asset is null) return;
        var value = Math.Clamp(rating, 0, 5);
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.Rating = value);
        RatingText = new string('★', value) + new string('☆', 5 - value);
        RefreshItem(asset);
    }

    public async Task SetColorLabelAsync(string? label)
    {
        var asset = CurrentAsset;
        if (asset is null) return;
        var next = string.Equals(asset.ColorLabel, label, StringComparison.OrdinalIgnoreCase) ? null : label;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.ColorLabel = next);
        LabelText = next switch
        {
            "red" => "● 红",
            "yellow" => "● 黄",
            "green" => "● 绿",
            "blue" => "● 蓝",
            "purple" => "● 紫",
            _ => "",
        };
        RefreshItem(asset);
    }

    public async Task SetPickAsync(bool? pick)
    {
        var asset = CurrentAsset;
        if (asset is null) return;
        var next = asset.IsPick == pick ? null : pick;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.IsPick = next);
        PickText = next switch { true => "✓ 选用", false => "✕ 废片", _ => "" };
        RefreshItem(asset);
    }

    private void RefreshItem(MediaAsset asset)
    {
        var item = Items.FirstOrDefault(i => ReferenceEquals(i.Asset, asset));
        item?.Refresh();
    }
}

/// <summary>胶片带里的一项（缩略图直接复用 ThumbnailImage 控件的 Asset 绑定）。</summary>
public sealed partial class GalleryItem : ObservableObject
{
    public GalleryItem(MediaAsset asset) => Asset = asset;

    public MediaAsset Asset { get; }

    [ObservableProperty]
    private bool _isCurrent;

    public Visibility CurrentRingVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(CurrentRingVisibility));

    public string FileName => Asset.FileName ?? Path.GetFileName(Asset.FullPath);
    public string RatingText => Asset.Rating > 0 ? new string('★', Math.Clamp(Asset.Rating, 0, 5)) : "";
    public Visibility RatingVisibility => Asset.Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CodecVisibility => Asset.IsVideo && !string.IsNullOrWhiteSpace(Asset.VideoCodec)
        ? Visibility.Visible : Visibility.Collapsed;
    public string CodecText => Asset.IsVideo
        ? StjMediaViewer.Core.Transcode.VideoCodecNames.ShortBadge(Asset.VideoCodec)
        : "";
    public Visibility RawJpegVisibility => Asset.IsRawJpegPair ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        OnPropertyChanged(nameof(RatingText));
        OnPropertyChanged(nameof(RatingVisibility));
        OnPropertyChanged(nameof(CodecText));
        OnPropertyChanged(nameof(CodecVisibility));
        OnPropertyChanged(nameof(RawJpegVisibility));
    }
}