using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 素材库页面的状态机：筛选条件 → AssetQuery → 分页增量加载。
/// 所有筛选 / 排序 / 文本搜索都以"取消上一次请求 + 版本号校验"的方式合并，
/// 保证快速切筛选时旧结果不会覆盖新结果。
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int PageSize = 200;
    private const int FilterDebounceMs = 110;

    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;
    private bool _facetsBuilt;
    private bool _suppressRefresh;

    /// <summary>单例：换设计语言会重建页面，筛选条件与已加载的结果必须保留。</summary>
    public static LibraryViewModel Shared { get; } = new();

    private LibraryViewModel()
    {
        KindGroup = new FilterGroupViewModel(FilterGroupKind.Kind, "媒体类型", true);
        FormatGroup = new FilterGroupViewModel(FilterGroupKind.Format, "文件格式", true);
        DeviceGroup = new FilterGroupViewModel(FilterGroupKind.Device, "拍摄设备", true);
        YearGroup = new FilterGroupViewModel(FilterGroupKind.Year, "拍摄年份", false);
        MonthGroup = new FilterGroupViewModel(FilterGroupKind.Month, "拍摄月份", false);
        CityGroup = new FilterGroupViewModel(FilterGroupKind.City, "拍摄地点", true);
        PostProcessGroup = new FilterGroupViewModel(FilterGroupKind.PostProcess, "后期状态", false);
        RawJpgGroup = new FilterGroupViewModel(FilterGroupKind.RawJpg, "JPG / RAW", false);
        LogGroup = new FilterGroupViewModel(FilterGroupKind.Log, "视频 Log", false);
        ThemeGroup = new FilterGroupViewModel(FilterGroupKind.Theme, "拍摄主题", true);
        ToneGroup = new FilterGroupViewModel(FilterGroupKind.Tone, "拍摄影调", true);

        Groups = new ObservableCollection<FilterGroupViewModel>
        {
            KindGroup, FormatGroup, DeviceGroup, YearGroup, MonthGroup, CityGroup,
            PostProcessGroup, RawJpgGroup, LogGroup, ThemeGroup, ToneGroup,
        };
        foreach (var g in Groups) g.SelectionChanged += OnGroupSelectionChanged;

        BuildInitialOptions();
    }

    // ---------------- 数据 ----------------
    public ObservableCollection<AssetCardItem> Items { get; } = new();
    public ObservableCollection<FilterGroupViewModel> Groups { get; }

    public FilterGroupViewModel KindGroup { get; }
    public FilterGroupViewModel FormatGroup { get; }
    public FilterGroupViewModel DeviceGroup { get; }
    public FilterGroupViewModel YearGroup { get; }
    public FilterGroupViewModel MonthGroup { get; }
    public FilterGroupViewModel CityGroup { get; }
    public FilterGroupViewModel PostProcessGroup { get; }
    public FilterGroupViewModel RawJpgGroup { get; }
    public FilterGroupViewModel LogGroup { get; }
    public FilterGroupViewModel ThemeGroup { get; }
    public FilterGroupViewModel ToneGroup { get; }

    // ---------------- 搜索 / 排序 ----------------
    [ObservableProperty]
    private string _searchText = "";

    public string SortKey { get; private set; } = AssetSortKeys.CapturedAt;

    public bool Descending { get; private set; } = true;

    // ---------------- 快捷筛选 ----------------
    [ObservableProperty]
    private bool _onlyUnclassified;

    [ObservableProperty]
    private bool _onlyRaw;

    [ObservableProperty]
    private bool _onlyLog;

    // ---------------- 结果状态 ----------------
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private int _selectionCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isLibraryEmpty;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private Visibility _selectionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _loadingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _loadMoreVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _emptyTitle = "还没有素材";

    [ObservableProperty]
    private string _emptyCaption = "选择一个媒体库文件夹，开始扫描并整理你的素材。";

    /// <summary>空状态里的操作按钮文案：库为空 → 选媒体库；筛没了 → 清筛选。</summary>
    public string EmptyActionText => TotalCount == 0 ? "选择媒体库文件夹" : "清空筛选";

    /// <summary>空状态里的"选择媒体库"按钮：只在库里一条素材都没有时出现。</summary>
    public Visibility EmptyActionVisibility => TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>空状态里的"清空筛选"按钮：筛到 0 条时出现。</summary>
    public Visibility EmptyClearVisibility => TotalCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _activeFilterSummary = "";

    [ObservableProperty]
    private Visibility _activeFilterSummaryVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _isDemoMode = AppServices.IsDemoMode;

    public Visibility DemoVisibility => IsDemoMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前主选中素材（检视面板用）。</summary>
    public MediaAsset? PrimaryAsset { get; private set; }

    /// <summary>当前多选集合（AI 队列"选中项"范围会用到）。</summary>
    public IReadOnlyList<AssetCardItem> SelectedItems { get; private set; } = Array.Empty<AssetCardItem>();

    public event EventHandler<MediaAsset?>? PrimaryAssetChanged;
    public event EventHandler? SelectionChanged;

    public bool IsAnyFilterActive =>
        Groups.Any(g => g.HasSelection) || OnlyUnclassified || OnlyRaw || OnlyLog || !string.IsNullOrWhiteSpace(SearchText);

    // ==================================================================================
    //  初始化 / 刷新
    // ==================================================================================
    public void Initialize() => RequestRefresh(immediate: true);

    public void RequestRefresh(bool immediate = false)
    {
        _refreshVersion++;
        var version = _refreshVersion;

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        _ = RefreshAsync(version, _refreshCts.Token, immediate);
    }

    private async Task RefreshAsync(int version, CancellationToken ct, bool immediate)
    {
        try
        {
            if (!immediate) await Task.Delay(FilterDebounceMs, ct);
            if (ct.IsCancellationRequested || version != _refreshVersion) return;

            IsLoading = true;
            LoadingVisibility = Visibility.Visible;

            var pageQuery = BuildQuery(0, PageSize);
            var facetQuery = BuildFacetQuery();

            var itemsTask = AppServices.Store.QueryAsync(pageQuery, ct);
            var countTask = AppServices.Store.CountAsync(pageQuery, ct);
            var facetsTask = AppServices.Store.GetFacetsAsync(facetQuery, ct);
            await Task.WhenAll(itemsTask, countTask, facetsTask);

            if (ct.IsCancellationRequested || version != _refreshVersion) return;

            var items = await itemsTask;
            var filtered = await countTask;
            var facets = await facetsTask;

            ApplyFacets(facets);
            TotalCount = facets.Total;
            FilteredCount = filtered;
            IsLibraryEmpty = facets.Total == 0;

            Items.Clear();
            foreach (var a in items) Items.Add(new AssetCardItem(a));

            HasMore = items.Count >= PageSize;
            LoadMoreVisibility = HasMore ? Visibility.Visible : Visibility.Collapsed;
            UpdateResultText();
            UpdateEmptyState();
            ClearSelectionInternal();
        }
        catch (OperationCanceledException)
        {
            // 被更新的筛选取消：静默丢弃。
        }
        catch (Exception ex)
        {
            Log.Warn("查询素材失败", ex);
            AppEvents.SetError($"查询素材失败：{ex.Message}");
        }
        finally
        {
            if (version == _refreshVersion)
            {
                IsLoading = false;
                LoadingVisibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>加载下一页（滚动到底 / 点击"加载更多"）。</summary>
    public async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMore) return;
        var version = _refreshVersion;
        try
        {
            IsLoading = true;
            var query = BuildQuery(Items.Count, PageSize);
            var items = await AppServices.Store.QueryAsync(query);
            if (version != _refreshVersion) return;
            foreach (var a in items) Items.Add(new AssetCardItem(a));
            HasMore = items.Count >= PageSize;
            LoadMoreVisibility = HasMore ? Visibility.Visible : Visibility.Collapsed;
            AppEvents.SetStatus($"已加载 {Items.Count} / {FilteredCount} 张素材");
        }
        catch (Exception ex)
        {
            Log.Warn("加载下一页失败", ex);
        }
        finally
        {
            if (version == _refreshVersion) IsLoading = false;
        }
    }

    public void RefreshAfterExternalChange() => RequestRefresh(immediate: true);

    // ==================================================================================
    //  查询编译
    // ==================================================================================
    public AssetQuery BuildQuery(int offset, int limit)
    {
        var q = new AssetQuery
        {
            Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Offset = offset,
            Limit = limit,
            SortBy = SortKey,
            Descending = Descending,
            OnlyUnclassified = OnlyUnclassified,
        };

        foreach (var id in KindGroup.SelectedIds)
            if (Enum.TryParse<MediaKind>(id, true, out var kind)) q.Kinds.Add(kind);

        foreach (var id in FormatGroup.SelectedIds) q.Formats.Add(id);
        foreach (var id in DeviceGroup.SelectedIds) q.Devices.Add(id);
        foreach (var id in CityGroup.SelectedIds) q.Cities.Add(id);
        foreach (var id in ThemeGroup.SelectedIds) q.Themes.Add(id);
        foreach (var id in ToneGroup.SelectedIds) q.Tones.Add(id);

        var yearId = YearGroup.SelectedIds.FirstOrDefault();
        var monthId = MonthGroup.SelectedIds.FirstOrDefault();
        if (int.TryParse(yearId, out var year))
        {
            int month = int.TryParse(monthId, out var m) ? m : 0;
            if (month is >= 1 and <= 12)
            {
                q.CapturedFrom = new DateTimeOffset(year, month, 1, 0, 0, 0, LocalOffset);
                q.CapturedTo = q.CapturedFrom.Value.AddMonths(1).AddTicks(-1);
            }
            else
            {
                q.CapturedFrom = new DateTimeOffset(year, 1, 1, 0, 0, 0, LocalOffset);
                q.CapturedTo = new DateTimeOffset(year, 12, 31, 23, 59, 59, LocalOffset);
            }
        }

        var pp = PostProcessGroup.SelectedIds.FirstOrDefault();
        if (pp is not null) q.PostProcess = Vocabularies.ParsePostProcess(pp);

        var rawJpg = RawJpgGroup.SelectedIds.FirstOrDefault();
        if (rawJpg == "raw") q.IsRaw = true;
        else if (rawJpg == "jpg") q.IsRaw = false;

        var log = LogGroup.SelectedIds.FirstOrDefault();
        if (log == "log") q.IsLog = true;
        else if (log == "nonlog") q.IsLog = false;

        if (OnlyRaw) q.IsRaw = true;
        if (OnlyLog) q.IsLog = true;

        return q;
    }

    private static TimeSpan LocalOffset => TimeZoneInfo.Local.BaseUtcOffset;

    /// <summary>分面计数用：只保留文本，不叠加维度筛选，保证各维度计数稳定可比。</summary>
    private AssetQuery BuildFacetQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
    };

    // ==================================================================================
    //  分面 → 侧栏
    // ==================================================================================
    private void BuildInitialOptions()
    {
        foreach (var g in Groups)
        {
            if (g.Options.Count > 0) continue;
            switch (g.Kind)
            {
                case FilterGroupKind.Kind:
                    g.ResetOptions(new[]
                    {
                        ("Image", "图片", 0), ("Video", "视频", 0), ("Audio", "音频", 0),
                    });
                    break;
                case FilterGroupKind.PostProcess:
                    g.ResetOptions(new[]
                    {
                        ("processed", "已调色", 0), ("unprocessed", "未调色", 0), ("unknown", "未标记", 0),
                    });
                    break;
                case FilterGroupKind.RawJpg:
                    g.ResetOptions(new[] { ("raw", "仅 RAW", 0), ("jpg", "仅 JPG", 0) });
                    break;
                case FilterGroupKind.Log:
                    g.ResetOptions(new[] { ("log", "Log 素材", 0), ("nonlog", "非 Log", 0) });
                    break;
                case FilterGroupKind.Month:
                    g.ResetOptions(Enumerable.Range(1, 12).Select(m => ($"{m}", $"{m} 月", 0)));
                    foreach (var o in g.Options) o.IsEnabled = false;
                    break;
            }
        }
    }

    private void ApplyFacets(FacetCounts facets)
    {
        _suppressRefresh = true;
        try
        {
            KindGroup.UpdateCounts(facets.ByKind);

            if (!_facetsBuilt || FormatGroup.Options.Count == 0)
            {
                FormatGroup.ResetOptions(facets.ByFormat
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key, kv.Value)));
                DeviceGroup.ResetOptions(facets.ByDevice
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key, kv.Value)));
                CityGroup.ResetOptions(facets.ByCity
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Key, kv.Value)));
                YearGroup.ResetOptions(facets.ByYear
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Key, kv.Value)));
                ThemeGroup.ResetOptions(facets.ByTheme
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, Vocabularies.ThemeDisplay(kv.Key, "zh-CN"), kv.Value)));
                ToneGroup.ResetOptions(facets.ByTone
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, Vocabularies.ToneDisplay(kv.Key, "zh-CN"), kv.Value)));
            }
            else
            {
                FormatGroup.UpdateCounts(facets.ByFormat);
                DeviceGroup.UpdateCounts(facets.ByDevice);
                CityGroup.UpdateCounts(facets.ByCity);
                YearGroup.UpdateCounts(facets.ByYear);
                ThemeGroup.UpdateCounts(facets.ByTheme);
                ToneGroup.UpdateCounts(facets.ByTone);
            }
            _facetsBuilt = true;

            PostProcessGroup.UpdateCounts(facets.ByPostProcess);

            int images = facets.ByKind.TryGetValue(nameof(MediaKind.Image), out var im) ? im : 0;
            RawJpgGroup.SetOptionCount("raw", facets.RawCount);
            RawJpgGroup.SetOptionCount("jpg", Math.Max(0, images - facets.RawCount));

            int videos = facets.ByKind.TryGetValue(nameof(MediaKind.Video), out var vd) ? vd : 0;
            LogGroup.SetOptionCount("log", facets.LogCount);
            LogGroup.SetOptionCount("nonlog", Math.Max(0, videos - facets.LogCount));

            // 月份只有在选了年份时才有意义，计数按当前年份现算。
            var yearId = YearGroup.SelectedIds.FirstOrDefault();
            MonthGroup.SetOptionsEnabled(yearId is not null);
            if (yearId is not null) _ = RefreshMonthCountsAsync(yearId);
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private async Task RefreshMonthCountsAsync(string yearId)
    {
        if (!int.TryParse(yearId, out var year)) return;
        try
        {
            for (int m = 1; m <= 12; m++)
            {
                var q = BuildFacetQuery();
                q.CapturedFrom = new DateTimeOffset(year, m, 1, 0, 0, 0, LocalOffset);
                q.CapturedTo = q.CapturedFrom.Value.AddMonths(1).AddTicks(-1);
                var count = await AppServices.Store.CountAsync(q);
                MonthGroup.SetOptionCount($"{m}", count);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"计算月份计数失败：{ex.Message}");
        }
    }

    // ==================================================================================
    //  交互
    // ==================================================================================
    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressRefresh) return;
        OnFilterChanged();
    }

    private void OnFilterChanged()
    {
        // 年份被取消时，月份选择必须一并清掉，否则会带着"某年某月"的幽灵条件查询。
        if (!YearGroup.HasSelection) MonthGroup.ClearSilently();
        MonthGroup.SetOptionsEnabled(YearGroup.HasSelection);

        UpdateActiveFilterSummary();
        RequestRefresh();
    }

    public void ClearAllFilters()
    {
        _suppressRefresh = true;
        foreach (var g in Groups) g.Clear();
        _suppressRefresh = false;
        OnlyUnclassified = false;
        OnlyRaw = false;
        OnlyLog = false;
        SearchText = "";
        UpdateActiveFilterSummary();
        RequestRefresh(immediate: true);
    }

    public void SetSort(string sortKey, bool? descending = null)
    {
        if (SortKey == sortKey && (descending is null || Descending == descending.Value)) return;
        SortKey = sortKey;
        if (descending.HasValue) Descending = descending.Value;
        RequestRefresh(immediate: true);
    }

    public void ToggleSortDirection()
    {
        Descending = !Descending;
        RequestRefresh(immediate: true);
    }

    public void SetSearch(string? text)
    {
        var value = text ?? "";
        if (SearchText == value) return;
        SearchText = value;
        UpdateActiveFilterSummary();
        RequestRefresh();
    }

    partial void OnOnlyUnclassifiedChanged(bool value) => OnQuickFilterChanged();
    partial void OnOnlyRawChanged(bool value) => OnQuickFilterChanged();
    partial void OnOnlyLogChanged(bool value) => OnQuickFilterChanged();

    private void OnQuickFilterChanged()
    {
        if (_suppressRefresh) return;
        UpdateActiveFilterSummary();
        RequestRefresh();
    }

    private void UpdateActiveFilterSummary()
    {
        var parts = new List<string>();
        foreach (var g in Groups)
            if (g.HasSelection) parts.Add($"{g.Title}: {string.Join(" / ", g.Options.Where(o => o.IsSelected).Select(o => o.Display))}");
        if (OnlyUnclassified) parts.Add("只看未分类");
        if (OnlyRaw) parts.Add("只看 RAW");
        if (OnlyLog) parts.Add("只看 Log");
        if (!string.IsNullOrWhiteSpace(SearchText)) parts.Add($"搜索: {SearchText}");

        ActiveFilterSummary = string.Join("   ·   ", parts);
        ActiveFilterSummaryVisibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateResultText()
    {
        ResultText = FilteredCount == TotalCount
            ? $"共 {TotalCount} 张素材"
            : $"筛选出 {FilteredCount} / {TotalCount} 张";
    }

    private void UpdateEmptyState()
    {
        var show = Items.Count == 0 && !IsLoading;
        EmptyVisibility = show ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(EmptyActionVisibility));
        OnPropertyChanged(nameof(EmptyClearVisibility));
        OnPropertyChanged(nameof(EmptyActionText));
        if (!show) return;

        if (TotalCount == 0)
        {
            EmptyTitle = IsDemoMode ? "演示库为空" : "还没有素材";
            EmptyCaption = "选择一个媒体库文件夹，开始扫描并整理你的素材。";
        }
        else
        {
            EmptyTitle = "没有符合条件的素材";
            EmptyCaption = "试着减少筛选条件，或清空筛选看看全部素材。";
        }
    }

    // ==================================================================================
    //  选择
    // ==================================================================================
    public void SetSelection(IReadOnlyList<AssetCardItem> selected)
    {
        SelectedItems = selected;
        foreach (var item in Items)
        {
            var isSelected = selected.Contains(item);
            if (item.IsSelected != isSelected) item.IsSelected = isSelected;
        }

        SelectionCount = selected.Count;
        SelectionText = selected.Count > 0 ? $"已选 {selected.Count} 张" : "";
        SelectionVisibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var primary = selected.Count > 0 ? selected[^1] : null;
        var asset = primary?.Asset;
        if (!ReferenceEquals(asset, PrimaryAsset))
        {
            PrimaryAsset = asset;
            PrimaryAssetChanged?.Invoke(this, asset);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSelectionInternal()
    {
        foreach (var item in Items) item.IsSelected = false;
        SelectionCount = 0;
        SelectionText = "";
        SelectionVisibility = Visibility.Collapsed;
        if (PrimaryAsset is not null)
        {
            PrimaryAsset = null;
            PrimaryAssetChanged?.Invoke(this, null);
        }
    }

    /// <summary>单条素材被就地修改（评分/色标/分类）后刷新卡片。</summary>
    public void UpdateAsset(MediaAsset asset)
    {
        foreach (var item in Items)
        {
            if (item.Id == asset.Id)
            {
                item.UpdateAsset(asset);
                break;
            }
        }
        if (PrimaryAsset?.Id == asset.Id)
        {
            PrimaryAsset = asset;
            PrimaryAssetChanged?.Invoke(this, asset);
        }
    }
}