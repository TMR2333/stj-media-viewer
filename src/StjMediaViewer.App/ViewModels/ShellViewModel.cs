using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// Shell 的状态：搜索防抖、扫描进度、状态栏计数、皮肤名、主题明暗。
/// 单例：切换设计语言时 ShellPage 会被 MainWindow 重建，顶栏状态不丢失。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const int SearchDebounceMs = 250;

    private DispatcherQueueTimer? _searchTimer;
    private DispatcherQueueTimer? _statusClearTimer;
    private bool _statusIsTransient;

    public static ShellViewModel Shared { get; } = new();

    private ShellViewModel()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is not null)
        {
            _searchTimer = queue.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(SearchDebounceMs);
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) => AppEvents.RaiseSearchChanged(SearchText);

            _statusClearTimer = queue.CreateTimer();
            _statusClearTimer.Interval = TimeSpan.FromSeconds(9);
            _statusClearTimer.IsRepeating = false;
            _statusClearTimer.Tick += (_, _) =>
            {
                if (_statusIsTransient) SetStatus("就绪", false, transient: false);
            };
        }

        AppEvents.StatusChanged += OnStatusChanged;
        AppEvents.ScanProgressChanged += OnScanProgress;
        if (AppServices.Design is not null)
        {
            AppServices.Design.Changed += (_, _) => UpdateSkinName();
            UpdateSkinName();
        }
        IsDarkMode = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        ThemeGlyph = IsDarkMode ? "\uE706" : "\uE708";
        _ = RefreshCacheSizeAsync();
    }

    // ==================================================================================
    //  搜索
    // ==================================================================================
    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value)
    {
        if (_searchTimer is null) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>回车：跳过防抖立即查询。</summary>
    public void CommitSearch()
    {
        _searchTimer?.Stop();
        AppEvents.RaiseSearchChanged(SearchText);
    }

    public void SetSearchTextWithoutDispatch(string text) => SearchText = text;

    // ==================================================================================
    //  导航
    // ==================================================================================
    [ObservableProperty]
    private string _selectedNavId = "library";

    // ==================================================================================
    //  状态栏
    // ==================================================================================
    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private Visibility _statusErrorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _totalText = "";

    [ObservableProperty]
    private string _filteredText = "";

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private string _cacheText = "";

    [ObservableProperty]
    private string _skinText = "Fluent";

    [ObservableProperty]
    private string _aiQueueText = "AI 队列";

    [ObservableProperty]
    private Visibility _selectionVisibility = Visibility.Collapsed;

    public Visibility DemoVisibility => AppServices.IsDemoMode ? Visibility.Visible : Visibility.Collapsed;

    // ==================================================================================
    //  扫描进度
    // ==================================================================================
    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanPercent;

    [ObservableProperty]
    private bool _isScanIndeterminate = true;

    [ObservableProperty]
    private Visibility _scanBarVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _scanRingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _scanText = "";

    // ==================================================================================
    //  主题 / 皮肤
    // ==================================================================================
    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private string _themeGlyph = "\uE706";

    [ObservableProperty]
    private string _themeTooltip = "切换到明亮主题";

    public void ToggleThemeVisual(bool dark)
    {
        IsDarkMode = dark;
        ThemeGlyph = dark ? "\uE706" : "\uE708";
        ThemeTooltip = dark ? "切换到明亮主题" : "切换到深色主题";
    }

    private void UpdateSkinName()
    {
        // 单一设计语言：Fluent
        SkinText = "Fluent";
    }

    /// <summary>扫描/写入完成后刷新缓存体积（异步，不阻塞 UI）。</summary>
    public async Task RefreshCacheSizeAsync()
    {
        try
        {
            var bytes = await AppServices.Thumbnails.GetCacheSizeBytesAsync();
            CacheText = $"缓存 {Formatting.FileSize(bytes)}";
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn($"读取缩略图缓存体积失败：{ex.Message}");
            CacheText = "缓存 —";
        }
    }

    // ==================================================================================
    //  外部事件
    // ==================================================================================
    private void OnStatusChanged(object? sender, ScanStatus e)
    {
        SetStatus(e.Text, e.IsError, transient: !e.IsError);
    }

    private void SetStatus(string text, bool isError, bool transient)
    {
        StatusText = string.IsNullOrWhiteSpace(text) ? "就绪" : text;
        StatusErrorVisibility = isError ? Visibility.Visible : Visibility.Collapsed;
        _statusIsTransient = transient && !isError;
        _statusClearTimer?.Stop();
        if (_statusIsTransient) _statusClearTimer?.Start();
    }

    private void OnScanProgress(object? sender, ScanStatus e)
    {
        IsScanning = e.IsActive;
        ScanBarVisibility = e.IsActive && !e.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        ScanRingVisibility = e.IsActive ? Visibility.Visible : Visibility.Collapsed;
        IsScanIndeterminate = e.IsIndeterminate;
        ScanPercent = Math.Clamp(e.Percent, 0, 100);
        ScanText = e.Text;
        if (!string.IsNullOrEmpty(e.Text)) SetStatus(e.Text, e.IsError, transient: !e.IsActive && !e.IsError);
    }

    public void UpdateLibrarySummary(int total, int filtered)
    {
        TotalText = $"素材 {total}";
        FilteredText = filtered == total ? "" : $"筛选 {filtered}";
    }

    public void UpdateSelectionSummary(int count)
    {
        SelectionText = count > 0 ? $"已选 {count}" : "";
        SelectionVisibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}