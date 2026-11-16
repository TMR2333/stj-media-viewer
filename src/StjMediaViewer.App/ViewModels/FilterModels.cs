using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>筛选维度。与 AssetQuery 的字段一一对应，用来把选择状态编译成查询。</summary>
public enum FilterGroupKind
{
    Kind,
    Format,
    Device,
    Year,
    Month,
    City,
    PostProcess,
    RawJpg,
    Log,
    Theme,
    Tone,
    /// <summary>用户自建维度（维度名即分组标题，值是自由文本）。</summary>
    Custom,
}

/// <summary>一个可勾选的筛选项（带计数）。</summary>
public sealed partial class FilterOptionViewModel : ObservableObject
{
    private readonly FilterGroupViewModel _owner;
    private bool _silent;

    public FilterOptionViewModel(FilterGroupViewModel owner, string id, string display, int count)
    {
        _owner = owner;
        Id = id;
        Display = display;
        Count = count;
    }

    public string Id { get; }
    public string Display { get; set; }
    public string Hint { get; set; } = "";

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isSelected;

    public string CountText => Count > 0 ? Count.ToString() : "";
    public Visibility CountVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public double RowOpacity => IsEnabled ? 1.0 : 0.45;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionVisibility));
        if (!_silent) _owner.NotifyOptionChanged(this);
    }
    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CountVisibility));
    }

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(RowOpacity));

    /// <summary>由分组在"单选互斥"时静默清除，不触发刷新（避免递归刷新）。</summary>
    internal void SetSelectedSilently(bool value)
    {
        _silent = true;
        try { IsSelected = value; }
        finally { _silent = false; }
    }
}

/// <summary>筛选侧栏里的一个分组（折叠器）。</summary>
public sealed partial class FilterGroupViewModel : ObservableObject
{
    private bool _suppress;

    public FilterGroupViewModel(FilterGroupKind kind, string title, bool isMultiSelect, IEnumerable<FilterOptionViewModel>? options = null)
    {
        Kind = kind;
        Title = title;
        IsMultiSelect = isMultiSelect;
        Options = new ObservableCollection<FilterOptionViewModel>(options ?? Array.Empty<FilterOptionViewModel>());
        _isExpanded = kind is FilterGroupKind.Kind or FilterGroupKind.RawJpg or FilterGroupKind.Log;
    }

    public FilterGroupKind Kind { get; }
    public string Title { get; }
    public bool IsMultiSelect { get; }
    public ObservableCollection<FilterOptionViewModel> Options { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private Visibility _summaryVisibility = Visibility.Collapsed;

    /// <summary>选择变化（谁变了都算）；LibraryViewModel 订阅后统一防抖刷新。</summary>
    public event EventHandler? SelectionChanged;

    public IEnumerable<string> SelectedIds => Options.Where(o => o.IsSelected).Select(o => o.Id);

    public bool HasSelection => Options.Any(o => o.IsSelected);

    public void Clear()
    {
        _suppress = true;
        foreach (var o in Options) o.SetSelectedSilently(false);
        _suppress = false;
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>没有可选值时整组隐藏（避免出现空荡荡的"拍摄主题"）。</summary>
    public Visibility GroupVisibility => Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal void NotifyOptionChanged(FilterOptionViewModel changed)
    {
        if (_suppress) return;
        if (!IsMultiSelect && changed.IsSelected)
        {
            _suppress = true;
            foreach (var o in Options)
                if (!ReferenceEquals(o, changed) && o.IsSelected) o.SetSelectedSilently(false);
            _suppress = false;
        }
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummary()
    {
        int n = Options.Count(o => o.IsSelected);
        Summary = n > 0 ? $"已选 {n}" : "";
        SummaryVisibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>用分面计数重建选项，保持已选状态（重新打开媒体库 / 扫描完成后用）。</summary>
    public void ResetOptions(IEnumerable<(string Id, string Display, int Count)> items)
    {
        var selected = new HashSet<string>(SelectedIds, StringComparer.OrdinalIgnoreCase);
        Options.Clear();
        foreach (var (id, display, count) in items)
        {
            var opt = new FilterOptionViewModel(this, id, display, count);
            if (selected.Contains(id)) opt.SetSelectedSilently(true);
            Options.Add(opt);
        }
        UpdateSummary();
        OnPropertyChanged(nameof(GroupVisibility));
    }

    /// <summary>整组启用/禁用（例如月份依赖年份）。</summary>
    public void SetOptionsEnabled(bool enabled)
    {
        foreach (var o in Options) o.IsEnabled = enabled;
    }

    /// <summary>静默清空选择（不触发刷新），用于父维度被取消的场景。</summary>
    public void ClearSilently()
    {
        _suppress = true;
        foreach (var o in Options) o.SetSelectedSilently(false);
        _suppress = false;
        UpdateSummary();
    }

    /// <summary>只刷新计数，不动选项结构（保持滚动位置与选中状态）。</summary>
    public void UpdateCounts(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var o in Options)
            o.Count = counts.TryGetValue(o.Id, out var c) ? c : 0;
    }

    public void SetOptionCount(string id, int count)
    {
        var o = Options.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (o is not null) o.Count = count;
    }
}