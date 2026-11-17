using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 批量填写信息页：左边挑文件，右边填字段。每个字段前有开关，只有勾上的才会写库（未勾 = 不改动）。
/// 入口：素材库选中素材后点「批量填写」；导入完成后在导入页点「填写这批文件的信息」。
/// </summary>
public sealed partial class BatchEditViewModel : ObservableObject
{
#pragma warning disable MVVMTK0045
    public static BatchEditViewModel Shared { get; } = new();

    /// <summary>跨页面传递：导入完成后把新素材 id 带过来。</summary>
    public static List<string> PendingIds { get; } = new();

    /// <summary>跨页面传递：直接带素材对象（例如「批量编辑某个影像合集」），优先于选择集。</summary>
    public static IReadOnlyList<MediaAsset>? PendingAssets { get; set; }

    public ObservableCollection<BatchAssetItem> Items { get; } = new();

    [ObservableProperty] private string _summaryText = "还没有选择素材";
    [ObservableProperty] private string _patchText = "勾选左侧要改的字段";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _allChecked;
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _busyVisibility = Visibility.Collapsed;

    // ---- 字段开关与值 ----
    [ObservableProperty] private bool _useCapturedAt;
    [ObservableProperty] private DateTimeOffset _capturedDate = DateTimeOffset.Now.Date;
    [ObservableProperty] private TimeSpan _capturedTime = new(12, 0, 0);

    [ObservableProperty] private bool _useLocation;
    [ObservableProperty] private string _countryText = "";
    [ObservableProperty] private string _provinceText = "";
    [ObservableProperty] private string _cityText = "";

    [ObservableProperty] private bool _useDevice;
    [ObservableProperty] private string _cameraMakeText = "";
    [ObservableProperty] private string _deviceKeyText = "";

    [ObservableProperty] private bool _useTheme;

    // ---------------- 自定义类别 ----------------
    [ObservableProperty] private bool _useCustomCategory;
    [ObservableProperty] private string _customDimension = "";
    [ObservableProperty] private string _customValueText = "";
    public ObservableCollection<string> CustomDimensions { get; } = new();
    [ObservableProperty] private string _themeText = "";
    [ObservableProperty] private string _toneText = "";

    [ObservableProperty] private bool _usePostProcess;
    [ObservableProperty] private int _postProcessIndex;   // 0 未后期 / 1 已后期 / 2 未知

    [ObservableProperty] private bool _useLog;
    [ObservableProperty] private int _logIndex;            // 0 非 Log / 1 Log
    [ObservableProperty] private string _logProfileText = "";

    [ObservableProperty] private bool _useRating;
    [ObservableProperty] private int _ratingIndex;         // 0-5

    [ObservableProperty] private bool _useColorLabel;
    [ObservableProperty] private int _colorLabelIndex;     // 0 无 / 1 红 / 2 黄 / 3 绿 / 4 蓝 / 5 紫

    [ObservableProperty] private bool _usePick;
    [ObservableProperty] private int _pickIndex;           // 0 选用 / 1 废片

    [ObservableProperty] private bool _useTitle;
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _captionText = "";

    [ObservableProperty] private bool _useKeywords;
    [ObservableProperty] private string _keywordsText = "";

    /// <summary>从图库当前选择载入。</summary>
    public async Task LoadFromLibrarySelectionAsync()
    {
        var assets = LibraryViewModel.Shared.SelectedItems.Select(i => i.Asset).ToList();
        await LoadAsync(assets);
    }

    /// <summary>从导入结果（按路径查 id）载入。</summary>
    public async Task LoadFromPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { await LoadAsync(Array.Empty<MediaAsset>()); return; }
        try
        {
            var ids = await AppServices.Store.GetIdsByPathsAsync(paths);
            var assets = await AppServices.Store.GetByIdsAsync(ids);
            await LoadAsync(assets);
        }
        catch (Exception ex)
        {
            Log.Warn("[batch] 按路径载入失败", ex);
            await LoadAsync(Array.Empty<MediaAsset>());
        }
    }

    private async Task LoadCustomDimensionsAsync()
    {
        try
        {
            var dims = await AppServices.Store.ListCustomDimensionsAsync();
            CustomDimensions.Clear();
            foreach (var d in dims) CustomDimensions.Add(d);
        }
        catch { }
    }

    public async Task LoadAsync(IReadOnlyList<MediaAsset> assets)
    {
        Items.Clear();
        foreach (var asset in assets) Items.Add(new BatchAssetItem(asset));
        AllChecked = Items.Count > 0;
        RefreshSummary();
        await Task.CompletedTask;
        _ = LoadCustomDimensionsAsync();
    }

    partial void OnIsBusyChanged(bool value)
        => BusyVisibility = value ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshSummary()
    {
        var checkedCount = Items.Count(i => i.IsChecked);
        SummaryText = Items.Count == 0
            ? "还没有选择素材：先在图库里框选，或从导入完成页跳过来"
            : $"已选 {checkedCount} / {Items.Count} 条素材";
        PatchText = BuildPatch(checkedOnly: false).Describe();
        EmptyVisibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetAllChecked(bool value)
    {
        foreach (var item in Items) item.IsChecked = value;
        RefreshSummary();
    }

    public void OnItemCheckedChanged() => RefreshSummary();

    private BatchEditPatch BuildPatch(bool checkedOnly = true)
    {
        var patch = new BatchEditPatch();
        if (UseCapturedAt)
        {
            var d = CapturedDate.Date + CapturedTime;
            patch.CapturedAt = new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local));
        }
        if (UseLocation)
        {
            patch.Country = CountryText.Trim();
            patch.Province = ProvinceText.Trim();
            patch.City = CityText.Trim();
        }
        if (UseDevice)
        {
            patch.CameraMake = CameraMakeText.Trim();
            patch.DeviceKey = DeviceKeyText.Trim();
        }
        if (UseTheme)
        {
            patch.Theme = ThemeText.Trim();
            patch.Tone = ToneText.Trim();
        }
        if (UseCustomCategory && !string.IsNullOrWhiteSpace(CustomDimension))
        {
            patch.CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CustomDimension.Trim()] = CustomValueText.Trim(),
            };
        }
        // UI 顺序：0 未后期 / 1 已后期 / 2 未知；枚举顺序：Unknown=0 / Unprocessed=1 / Processed=2
        if (UsePostProcess) patch.PostProcess = PostProcessIndex switch { 0 => 1, 1 => 2, _ => 0 };
        if (UseLog)
        {
            patch.IsLog = LogIndex == 1;
            patch.LogProfile = LogProfileText.Trim();
        }
        if (UseRating) patch.Rating = Math.Clamp(RatingIndex, 0, 5);
        if (UseColorLabel) patch.ColorLabel = ColorLabelValue(ColorLabelIndex);
        if (UsePick) patch.IsPick = PickIndex == 0;
        if (UseTitle)
        {
            patch.Title = TitleText.Trim();
            patch.Caption = CaptionText.Trim();
        }
        if (UseKeywords)
        {
            patch.AddKeywords = KeywordsText
                .Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return patch;
    }

    private static string ColorLabelValue(int index) => index switch
    {
        1 => "red",
        2 => "yellow",
        3 => "green",
        4 => "blue",
        5 => "purple",
        _ => "",
    };

    /// <summary>把勾选的字段写到勾选的素材上。</summary>
    public async Task<int> ApplyAsync()
    {
        var targets = Items.Where(i => i.IsChecked).Select(i => i.Asset).ToList();
        if (targets.Count == 0) return 0;

        var patch = BuildPatch();
        if (!patch.HasChanges)
        {
            AppEvents.SetError("没有勾选任何字段");
            return 0;
        }

        IsBusy = true;
        BusyText = $"写入 {targets.Count} 条素材…";
        try
        {
            var ids = targets.Select(a => a.Id).ToList();
            var affected = await AppServices.Store.ApplyBatchEditAsync(ids, patch);
            foreach (var asset in targets)
            {
                // 让内存对象与库里一致（页面不重新拉数据也能看到"已写入"）
                if (patch.CapturedAt is { } t) asset.CapturedAt = t;
                if (patch.City is not null) asset.City = patch.City.Length == 0 ? null : patch.City;
                if (patch.Country is not null) asset.Country = patch.Country.Length == 0 ? null : patch.Country;
                if (patch.Province is not null) asset.Province = patch.Province.Length == 0 ? null : patch.Province;
                if (patch.CameraMake is not null) asset.CameraMake = patch.CameraMake.Length == 0 ? null : patch.CameraMake;
                if (patch.DeviceKey is not null) asset.DeviceKey = patch.DeviceKey.Length == 0 ? null : patch.DeviceKey;
                if (patch.Theme is not null) asset.Theme = patch.Theme.Length == 0 ? null : patch.Theme;
                if (patch.Tone is not null) asset.Tone = patch.Tone.Length == 0 ? null : patch.Tone;
                if (patch.PostProcess is { } pp) asset.PostProcess = (PostProcessState)pp;
                if (patch.IsLog is { } isLog) asset.IsLog = isLog;
                if (patch.LogProfile is not null) asset.LogProfile = patch.LogProfile.Length == 0 ? null : patch.LogProfile;
                if (patch.Rating is { } r) asset.Rating = r;
                if (patch.ColorLabel is not null) asset.ColorLabel = patch.ColorLabel.Length == 0 ? null : patch.ColorLabel;
                if (patch.IsPick is { } pick) asset.IsPick = pick;
                if (patch.Title is not null) asset.Title = patch.Title.Length == 0 ? null : patch.Title;
                if (patch.Caption is not null) asset.Caption = patch.Caption.Length == 0 ? null : patch.Caption;
                AppServices.Thumbs.Invalidate(asset.Id);
            }

            foreach (var item in Items) item.Refresh();
            LibraryViewModel.Shared.RequestRefresh();
            AppEvents.SetStatus($"已为 {affected} 条素材写入：{patch.Describe()}");
            Log.Info($"[batch] 批量写入 {affected} 条：{patch.Describe()}");
            return affected;
        }
        catch (Exception ex)
        {
            Log.Error("[batch] 批量写入失败", ex);
            AppEvents.SetError($"批量写入失败：{ex.Message}");
            return 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnAllCheckedChanged(bool value)
    {
        if (value == Items.All(i => i.IsChecked) && value) return;
        SetAllChecked(value);
    }
#pragma warning restore MVVMTK0045
}

/// <summary>批量填写页左侧的一行素材。</summary>
#pragma warning disable MVVMTK0045
public sealed partial class BatchAssetItem : ObservableObject
{
    public BatchAssetItem(MediaAsset asset)
    {
        Asset = asset;
        Refresh();
    }

    public MediaAsset Asset { get; }

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private string _fileText = "";
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private string _kindText = "";

    public void Refresh()
    {
        FileText = Asset.FileName;
        KindText = Asset.Kind switch
        {
            MediaKind.Video => "视频",
            MediaKind.Audio => "音频",
            _ => "图片",
        };

        var parts = new List<string>();
        if (Asset.CapturedAt is { } t) parts.Add(t.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (!string.IsNullOrWhiteSpace(Asset.City)) parts.Add(Asset.City!);
        if (!string.IsNullOrWhiteSpace(Asset.DeviceKey)) parts.Add(Asset.DeviceKey!);
        MetaText = parts.Count == 0 ? "（无时间/地点/设备信息）" : string.Join(" · ", parts);
    }
}
#pragma warning restore MVVMTK0045
