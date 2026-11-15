using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>时间线里的"一天"。</summary>
public sealed partial class TimelineDayViewModel : ObservableObject
{
    public TimelineDayViewModel(DateOnly day, int count, int imageCount, int videoCount, int audioCount, double barWidth)
    {
        Day = day;
        Count = count;
        BarWidth = barWidth;
        DayText = day.ToString("MM-dd");
        WeekdayText = day.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日",
        };
        var parts = new List<string>();
        if (imageCount > 0) parts.Add($"图 {imageCount}");
        if (videoCount > 0) parts.Add($"视频 {videoCount}");
        if (audioCount > 0) parts.Add($"音频 {audioCount}");
        CountDetail = parts.Count > 0 ? string.Join(" · ", parts) : "无素材";
    }

    public DateOnly Day { get; }
    public int Count { get; }
    public double BarWidth { get; }
    public string DayText { get; }
    public string WeekdayText { get; }
    public string CountDetail { get; }
    public string CountText => Count.ToString();

    [ObservableProperty]
    private bool _isSelected;

    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(SelectionVisibility));
}

/// <summary>时间线里的一个月（可折叠）。</summary>
public sealed partial class TimelineMonthViewModel : ObservableObject
{
    public TimelineMonthViewModel(int month, int count, ObservableCollection<TimelineDayViewModel> days)
    {
        Month = month;
        Days = days;
        Title = $"{month} 月";
        CountText = $"{count} 张";
        _isExpanded = count > 0;
    }

    public int Month { get; }
    public string Title { get; }
    public string CountText { get; }
    public ObservableCollection<TimelineDayViewModel> Days { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>时间线里的一年。</summary>
public sealed partial class TimelineYearViewModel : ObservableObject
{
    public TimelineYearViewModel(int year, int count, ObservableCollection<TimelineMonthViewModel> months)
    {
        Year = year;
        Months = months;
        Title = year.ToString();
        CountText = $"{count} 张";
        _isExpanded = true;
    }

    public int Year { get; }
    public string Title { get; }
    public string CountText { get; }
    public ObservableCollection<TimelineMonthViewModel> Months { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>年份快速跳转 chip。</summary>
public sealed partial class YearChipViewModel : ObservableObject
{
    public YearChipViewModel(int year, int count) { Year = year; Title = year.ToString(); CountText = count.ToString(); }

    public int Year { get; }
    public string Title { get; }
    public string CountText { get; }

    [ObservableProperty]
    private bool _isSelected;

    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(SelectionVisibility));
}

/// <summary>
/// 时间线：按 年 → 月 → 日 三级分组（IAssetStore.GetTimelineAsync），
/// 选中某一天后用时间区间查询把当天的缩略图墙拉出来。
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    public static TimelineViewModel Shared { get; } = new();

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _wallCts;
    private bool _built;

    private TimelineViewModel()
    {
        Years = new ObservableCollection<TimelineYearViewModel>();
        YearChips = new ObservableCollection<YearChipViewModel>();
        Items = new ObservableCollection<AssetCardItem>();
    }

    // ---------------- 树 ----------------
    public ObservableCollection<TimelineYearViewModel> Years { get; }
    public ObservableCollection<YearChipViewModel> YearChips { get; }

    // ---------------- 缩略图墙 ----------------
    public ObservableCollection<AssetCardItem> Items { get; }

    [ObservableProperty]
    private string _selectedDayTitle = "选择一个日期";

    [ObservableProperty]
    private string _selectedDaySummary = "左侧点选某一天，右侧会显示当天的素材。";

    [ObservableProperty]
    private string _wallSummary = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private Visibility _loadingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _emptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _wallEmptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _totalSummary = "";

    [ObservableProperty]
    private int _selectedYear;

    [ObservableProperty]
    private string _density = "medium";

    public DateOnly? SelectedDay { get; private set; }

    // ==================================================================================
    //  加载
    // ==================================================================================
    public async Task LoadAsync(bool force = false)
    {
        if (_built && !force) return;
        _built = true;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        LoadingVisibility = Visibility.Visible;
        try
        {
            var buckets = await AppServices.Store.GetTimelineAsync(new AssetQuery { Limit = int.MaxValue }, ct);
            if (ct.IsCancellationRequested) return;
            BuildTree(buckets);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("加载时间线失败", ex);
            AppEvents.SetError($"加载时间线失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
            LoadingVisibility = Visibility.Collapsed;
        }
    }

    private void BuildTree(IReadOnlyList<TimelineBucket> buckets)
    {
        Years.Clear();
        YearChips.Clear();

        var total = buckets.Sum(b => b.Count);
        TotalSummary = buckets.Count == 0
            ? "还没有可用的拍摄时间数据"
            : $"{buckets.Count} 天 · {total} 张素材";
        EmptyVisibility = buckets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var byYear = buckets
            .GroupBy(b => b.Day.Year)
            .OrderByDescending(g => g.Key);

        // 每天的柱宽按全局最大计数归一化（最多 64px，最少 4px）
        var maxCount = buckets.Count == 0 ? 1 : buckets.Max(b => b.Count);

        foreach (var yearGroup in byYear)
        {
            var months = new ObservableCollection<TimelineMonthViewModel>();
            foreach (var monthGroup in yearGroup.GroupBy(b => b.Day.Month).OrderByDescending(g => g.Key))
            {
                var days = new ObservableCollection<TimelineDayViewModel>();
                foreach (var bucket in monthGroup.OrderBy(b => b.Day))
                {
                    var width = 4 + 60.0 * bucket.Count / maxCount;
                    days.Add(new TimelineDayViewModel(bucket.Day, bucket.Count, bucket.ImageCount, bucket.VideoCount, bucket.AudioCount, width));
                }
                months.Add(new TimelineMonthViewModel(monthGroup.Key, monthGroup.Sum(b => b.Count), days));
            }

            var yearModel = new TimelineYearViewModel(yearGroup.Key, yearGroup.Sum(b => b.Count), months);
            Years.Add(yearModel);
            YearChips.Add(new YearChipViewModel(yearGroup.Key, yearGroup.Sum(b => b.Count)));
        }

        // 默认选中最新的一天
        var firstDay = Years.SelectMany(y => y.Months).SelectMany(m => m.Days).FirstOrDefault();
        if (firstDay is not null)
        {
            SelectedYear = firstDay.Day.Year;
            UpdateYearChips();
            _ = SelectDayAsync(firstDay);
        }
        else
        {
            Items.Clear();
            WallEmptyVisibility = Visibility.Visible;
        }
    }

    private void UpdateYearChips()
    {
        foreach (var chip in YearChips) chip.IsSelected = chip.Year == SelectedYear;
        foreach (var year in Years) year.IsExpanded = year.Year == SelectedYear;
    }

    // ==================================================================================
    //  交互
    // ==================================================================================
    public Task SelectDayAsync(TimelineDayViewModel day)
    {
        foreach (var d in Years.SelectMany(y => y.Months).SelectMany(m => m.Days))
            d.IsSelected = ReferenceEquals(d, day);

        SelectedDay = day.Day;
        SelectedDayTitle = day.Day.ToString("yyyy 年 M 月 d 日") + " · " + day.WeekdayText;
        SelectedDaySummary = day.CountDetail;
        AppEvents.SetStatus($"时间线：{SelectedDayTitle}（{day.Count} 张）");
        return LoadWallAsync(day.Day);
    }

    public Task SelectYearAsync(int year)
    {
        SelectedYear = year;
        UpdateYearChips();
        var firstDay = Years.FirstOrDefault(y => y.Year == year)?
            .Months.SelectMany(m => m.Days).FirstOrDefault();
        return firstDay is null ? Task.CompletedTask : SelectDayAsync(firstDay);
    }

    private async Task LoadWallAsync(DateOnly day)
    {
        _wallCts?.Cancel();
        _wallCts?.Dispose();
        _wallCts = new CancellationTokenSource();
        var ct = _wallCts.Token;

        IsLoading = true;
        LoadingVisibility = Visibility.Visible;
        try
        {
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.BaseUtcOffset);
            var to = from.AddDays(1).AddTicks(-1);
            var query = new AssetQuery
            {
                CapturedFrom = from,
                CapturedTo = to,
                Limit = 1000,
                SortBy = AssetSortKeys.CapturedAt,
                Descending = false,
            };

            var assets = await AppServices.Store.QueryAsync(query, ct);
            if (ct.IsCancellationRequested) return;

            Items.Clear();
            foreach (var asset in assets) Items.Add(new AssetCardItem(asset));
            WallSummary = assets.Count == 0 ? "这一天没有素材" : $"当天 {assets.Count} 张";
            WallEmptyVisibility = assets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("加载时间线缩略图墙失败", ex);
            AppEvents.SetError($"加载当天素材失败：{ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                LoadingVisibility = Visibility.Collapsed;
            }
        }
    }

    public void SetDensity(string density)
    {
        Density = density;
    }
}