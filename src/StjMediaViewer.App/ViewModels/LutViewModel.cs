using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.ViewModels;

/// <summary>LUT 管理页：内置 LUT 自动生成、用户 LUT 导入、按素材预览与分配。</summary>
public sealed partial class LutViewModel : ObservableObject
{
#pragma warning disable MVVMTK0045
    public static LutViewModel Shared { get; } = new();

    private bool _initialized;

    public ObservableCollection<LutCardItem> Items { get; } = new();

    [ObservableProperty] private LutCardItem? _selected;
    [ObservableProperty] private ImageSource? _previewSource;
    [ObservableProperty] private string _previewTitle = "选择一条 LUT 预览";
    [ObservableProperty] private string _previewSubtitle = "";
    [ObservableProperty] private string _summaryText = "正在扫描 LUT…";
    [ObservableProperty] private string _detectText = "";
    [ObservableProperty] private Visibility _detectVisibility = Visibility.Collapsed;
    [ObservableProperty] private bool _autoAssignForSony = true;
    [ObservableProperty] private string _userFolderText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _contentVisibility = Visibility.Visible;
    [ObservableProperty] private string _sampleText = "";
    [ObservableProperty] private Visibility _applyVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _deleteVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _hintVisibility = Visibility.Visible;
    [ObservableProperty] private Visibility _busyVisibility = Visibility.Collapsed;

    private MediaAsset? _sample;
    private CancellationTokenSource? _previewCts;

    /// <summary>页面首次进入或需要重扫时调用。</summary>
    public async Task InitializeAsync(bool forceRefresh = false)
    {
        AutoAssignForSony = AppServices.Settings.Current.AutoAssignLutForSony;
        UserFolderText = AppServices.Luts.UserFolder;

        if (_initialized && !forceRefresh)
        {
            await SelectSampleAsync();
            return;
        }

        _initialized = true;
        await RefreshAsync(forceRefresh);
    }

    public async Task RefreshAsync(bool forceRefresh = false)
    {
        IsBusy = true;
        BusyText = "扫描 LUT 目录…";
        try
        {
            var list = await AppServices.Luts.ListAsync(forceRefresh);
            Items.Clear();
            foreach (var entry in list)
                Items.Add(new LutCardItem(entry, AppServices.Luts));

            var builtIn = list.Count(e => e.Category == LutCategory.BuiltIn);
            var official = list.Count(e => e.Category == LutCategory.Official);
            var creative = list.Count(e => e.Category == LutCategory.Creative);
            SummaryText = $"共 {list.Count} 条 LUT · 内置 {builtIn} · 官方风格 {official} · 创意 {creative}";
            EmptyVisibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContentVisibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            await SelectSampleAsync();

            // 默认选中：优先素材当前自动匹配的 LUT，否则第一条内置
            var preferred = _sample is null ? null : AppServices.Luts.ResolveForAsset(_sample);
            Selected = Items.FirstOrDefault(i => i.Entry.Id == preferred?.Id)
                       ?? Items.FirstOrDefault(i => i.Entry.Category == LutCategory.BuiltIn)
                       ?? Items.FirstOrDefault();
            if (Selected is null) return;

            // 统计自动套用情况
            var sonyLog = await CountSonyLogOnlineAsync();
            if (sonyLog > 0 && AutoAssignForSony)
            {
                DetectText = $"检测到 {sonyLog} 条素材命中「设备 ↔ LUT 绑定」：已自动套用各自绑定的监视 LUT。" +
                             "可在「设置 → 设备 ↔ LUT 绑定」里调整；没绑定的设备不会自动套。";
                DetectVisibility = Visibility.Visible;
            }
            else if (sonyLog > 0)
            {
                DetectText = $"检测到 {sonyLog} 条素材命中设备绑定，但自动套用总开关已关闭。";
                DetectVisibility = Visibility.Visible;
            }
            else
            {
                DetectVisibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[lut] 刷新失败", ex);
            SummaryText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<int> CountSonyLogOnlineAsync()
    {
        try
        {
            var assets = await AppServices.Store.QueryAsync(new AssetQuery
            {
                Limit = 0,
                Kinds = { MediaKind.Video, MediaKind.Image },
            });
            return assets.Count(a => Core.Color.DeviceLutRules.ResolveLutId(a, AppServices.Settings.Current.DeviceLutBindings) is not null);
        }
        catch
        {
            return 0;
        }
    }

    private async Task SelectSampleAsync()
    {
        try
        {
            var selected = LibraryViewModel.Shared.SelectedItems.FirstOrDefault()?.Asset;
            if (selected is not null)
            {
                _sample = selected;
                SampleText = $"预览素材：{selected.FileName}（图库当前选中）";
                ApplyVisibility = Visibility.Visible;
                return;
            }

            var candidates = await AppServices.Store.QueryAsync(new AssetQuery { Limit = 0, Kinds = { MediaKind.Video } });
            var sony = candidates.FirstOrDefault(a =>
                Core.Color.DeviceLutRules.ResolveLutId(a, AppServices.Settings.Current.DeviceLutBindings) is not null);
            _sample = sony ?? candidates.FirstOrDefault() ?? (await AppServices.Store.QueryAsync(new AssetQuery { Limit = 1 })).FirstOrDefault();
            SampleText = _sample is null
                ? "图库里还没有素材可做预览"
                : $"预览素材：{_sample.FileName}（{(sony is not null ? "Sony Log 样片" : "素材库第一条视频")}）";
            ApplyVisibility = _sample is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            _sample = null;
            SampleText = "预览素材不可用";
        }
    }

    partial void OnPreviewSourceChanged(ImageSource? value)
        => HintVisibility = value is null ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsBusyChanged(bool value)
        => BusyVisibility = value ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedChanged(LutCardItem? value)
    {
        DeleteVisibility = value is { Entry.Category: not LutCategory.BuiltIn } ? Visibility.Visible : Visibility.Collapsed;
        _ = UpdatePreviewAsync(value);
    }

    private async Task UpdatePreviewAsync(LutCardItem? item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (item is null) { PreviewSource = null; return; }
        PreviewTitle = item.Entry.Name;
        PreviewSubtitle = item.Entry.Subtitle;

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        if (_sample is null) { PreviewSource = null; return; }

        IsBusy = true;
        BusyText = "生成预览…";
        try
        {
            var path = await AppServices.Luts.RenderPreviewAsync(_sample, item.Entry, 900, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_previewCts, cts)) return;
            PreviewSource = path is null
                ? null
                : new BitmapImage { DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("[lut] 预览生成失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts)) { IsBusy = false; }
            cts.Dispose();
        }
    }

    /// <summary>把选中的 LUT 应用到图库选中的素材（没有选中就应用到样例素材）。</summary>
    public async Task<int> ApplyToSelectionAsync()
    {
        if (Selected is null) return 0;
        var targets = LibraryViewModel.Shared.SelectedItems.Select(i => i.Asset).ToList();
        if (targets.Count == 0 && _sample is not null) targets.Add(_sample);
        if (targets.Count == 0) return 0;

        int n = 0;
        foreach (var asset in targets)
        {
            try
            {
                await AppServices.Store.SetAssetLutAsync(asset.Id, Selected.Entry.Id);
                asset.LutId = Selected.Entry.Id;
                AppServices.Thumbs.Invalidate(asset.Id);
                n++;
            }
            catch (Exception ex)
            {
                Log.Warn($"[lut] 应用失败 {asset.FileName}", ex);
            }
        }

        AppEvents.SetStatus(n > 0 ? $"已为 {n} 条素材套用 LUT：{Selected.Entry.Name}" : "没有可套用的素材");
        return n;
    }

    /// <summary>恢复自动匹配（清空素材上的显式 LUT）。</summary>
    public async Task<int> ClearSelectionAsync()
    {
        var targets = LibraryViewModel.Shared.SelectedItems.Select(i => i.Asset).ToList();
        if (targets.Count == 0 && _sample is not null) targets.Add(_sample);

        int n = 0;
        foreach (var asset in targets)
        {
            try
            {
                await AppServices.Store.SetAssetLutAsync(asset.Id, null);
                asset.LutId = null;
                AppServices.Thumbs.Invalidate(asset.Id);
                n++;
            }
            catch { /* 忽略单条失败 */ }
        }
        AppEvents.SetStatus(n > 0 ? $"已恢复自动匹配（{n} 条素材）" : "没有可恢复的素材");
        return n;
    }

    public async Task<bool> DeleteSelectedAsync()
    {
        if (Selected is null || Selected.Entry.Category == LutCategory.BuiltIn) return false;
        var ok = await AppServices.Luts.DeleteAsync(Selected.Entry);
        if (ok)
        {
            AppServices.Thumbs.Clear();
            await RefreshAsync(forceRefresh: true);
        }
        return ok;
    }

    public async Task SetAutoAssignAsync(bool value)
    {
        AutoAssignForSony = value;
        AppServices.Settings.Current.AutoAssignLutForSony = value;
        await AppServices.Settings.SaveAsync();
        AppServices.Thumbs.Clear();
        await RefreshAsync();
    }

    /// <summary>导入 .cube 文件到用户目录。</summary>
    public async Task<int> ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;
        IsBusy = true;
        BusyText = "导入 LUT…";
        try
        {
            var entries = await AppServices.Luts.ImportAsync(paths);
            AppServices.Thumbs.Clear();
            await RefreshAsync(forceRefresh: true);
            if (entries.Count > 0)
                Selected = Items.FirstOrDefault(i => i.Entry.Id == entries[0].Id) ?? Selected;
            AppEvents.SetStatus(entries.Count > 0 ? $"已导入 {entries.Count} 条 LUT" : "没有导入任何 LUT（只支持 .cube）");
            return entries.Count;
        }
        catch (Exception ex)
        {
            Log.Warn("[lut] 导入失败", ex);
            AppEvents.SetError($"导入 LUT 失败：{ex.Message}");
            return 0;
        }
        finally { IsBusy = false; }
    }
#pragma warning restore MVVMTK0045
}

/// <summary>LUT 列表项。</summary>
public sealed class LutCardItem
{
    public LutCardItem(LutEntry entry, ILutService luts)
    {
        Entry = entry;
        CategoryText = entry.CategoryText;
        SizeText = entry.SizeText;
        DetailText = entry.Subtitle;
        DescriptionText = entry.Description ?? "";
        CategoryVisibility = entry.Category == LutCategory.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        BrandVisibility = entry.Brand == "Sony" ? Visibility.Visible : Visibility.Collapsed;
        AutoBadgeVisibility = entry.Category == LutCategory.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
    }

    public LutEntry Entry { get; }
    public string Name => Entry.Name;
    public string CategoryText { get; }
    public string SizeText { get; }
    public string DetailText { get; }
    public string DescriptionText { get; }
    public Visibility CategoryVisibility { get; }
    public Visibility BrandVisibility { get; }
    public Visibility AutoBadgeVisibility { get; }
}