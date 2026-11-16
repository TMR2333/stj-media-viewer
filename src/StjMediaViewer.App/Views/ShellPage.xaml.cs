using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.System;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 应用外壳。页面本身只做"路由 + 命令转发"，状态都在 <see cref="ShellViewModel.Shared"/>。
/// 设计语言切换时 ui-shell 会重新 Navigate MainWindow.RootFrame，本页会被重建，
/// 因此这里不保存任何必须跨切换存活的状态。
/// </summary>
public sealed partial class ShellPage : Page
{
    private bool _initialized;
    private bool _syncingSearch;

    public ShellPage()
    {
        InitializeComponent();
        Current = this;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 背景光场里有负 margin 的椭圆，必须裁剪到窗口范围，否则会溢出窗口边界
        // （既影响观感，也会让应用内截图导出比窗口更大的画布）。
        RootGrid.SizeChanged += OnRootSizeChanged;
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RootGrid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
    }

    /// <summary>供其它页面调用（打开全屏预览、切换导航）。</summary>
    public static ShellPage? Current { get; private set; }

    public ShellViewModel ViewModel => ShellViewModel.Shared;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        ApplySavedTheme();
        AppServices.Design.Changed += OnDesignChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 页面重建（切换明暗主题）后把搜索词恢复回搜索框。
        if (!string.IsNullOrEmpty(ViewModel.SearchText) && SearchBox.Text != ViewModel.SearchText)
        {
            _syncingSearch = true;
            try { SearchBox.Text = ViewModel.SearchText; }
            finally { _syncingSearch = false; }
        }

        // 恢复上次的导航项（换皮肤重建后不跳回默认页；启动时用设置里持久化的 LastViewId）。
        var initialView = AppServices.Settings.Current.LastViewId;
        if (string.IsNullOrWhiteSpace(initialView)) initialView = ViewModel.SelectedNavId;
        ViewModel.SelectedNavId = initialView!;
        NavigateTo(initialView!);
        SyncNavSelection(initialView!);

        // 首次启动：进新用户引导（可跳过；设置页可重新播放）
        if (!AppServices.Settings.Current.OnboardingCompleted)
            ShowOnboarding();

        // 调试用：STJ_DEMO_SEARCH=<词> 预设搜索词 / STJ_DEMO_SORT=<sortKey> 预设排序（截图验收用）
        var demoSearch = Environment.GetEnvironmentVariable("STJ_DEMO_SEARCH");
        if (!string.IsNullOrEmpty(demoSearch))
        {
            ViewModels.LibraryViewModel.Shared.SearchText = demoSearch;
        }
        var demoSort = Environment.GetEnvironmentVariable("STJ_DEMO_SORT");
        if (!string.IsNullOrEmpty(demoSort))
        {
            // 等 LibraryPage 初始化完再改排序，否则会被 Initialize 覆盖
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1200);
                ViewModels.LibraryViewModel.Shared.SetSort(demoSort, descending: false);
            });
        }

        // 调试用：STJ_DEMO_FILTER=<optionText> 预选一个分面筛选项（截图验收用）
        var demoFilter = Environment.GetEnvironmentVariable("STJ_DEMO_FILTER");
        if (!string.IsNullOrEmpty(demoFilter))
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1500);
                var vm = ViewModels.LibraryViewModel.Shared;
                var option = vm.Groups.SelectMany(g => g.Options)
                    .FirstOrDefault(o => o.Display.Contains(demoFilter, StringComparison.OrdinalIgnoreCase));
                if (option is null) return;
                option.IsSelected = true;
            });
        }

        // 调试用：STJ_DEMO_DIALOG=transcode 时自动对第一条视频打开转码对话框（截图验收）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_DIALOG") == "transcode")
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var videos = await AppServices.Store.QueryAsync(new Core.Models.AssetQuery
                    {
                        Kinds = { Core.Models.MediaKind.Video },
                        Limit = 1,
                    });
                    if (videos.Count == 0) return;
                    var dialog = new TranscodeDialog(videos[0]) { XamlRoot = Content.XamlRoot };
                    await dialog.ShowAsync();
                }
                catch (Exception ex) { Core.Util.Log.Warn("演示转码对话框失败", ex); }
            });
        }

        AppEvents.SetStatus(AppServices.IsDemoMode
            ? "演示模式：正在使用内置样本素材（72 条），可直接体验筛选 / 检视 / 预览"
            : "就绪");
        _ = ViewModel.RefreshCacheSizeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        AppServices.Design.Changed -= OnDesignChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ShellViewModel.SearchText) or null or "")) return;
        if (_syncingSearch) return;
        if (SearchBox.Text == ViewModel.SearchText) return;
        _syncingSearch = true;
        try { SearchBox.Text = ViewModel.SearchText; }
        finally { _syncingSearch = false; }
    }

    // ==================================================================================
    //  主题 / 设计语言
    // ==================================================================================
    private void ApplySavedTheme()
    {
        try
        {
            RootGrid.RequestedTheme = AppServices.Design.ResolveTheme();
        }
        catch (Exception ex)
        {
            Log.Warn("应用主题失败", ex);
        }
        var isDark = RootGrid.RequestedTheme == ElementTheme.Dark ||
                     (RootGrid.RequestedTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        ViewModel.ToggleThemeVisual(isDark);
    }

    private async void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var goDark = !ViewModel.IsDarkMode;
        RootGrid.RequestedTheme = goDark ? ElementTheme.Dark : ElementTheme.Light;
        ViewModel.ToggleThemeVisual(goDark);
        try
        {
            await AppServices.Design.SetThemeModeAsync(goDark ? "dark" : "light");
            AppEvents.SetStatus(goDark ? "已切换到深色主题" : "已切换到明亮主题");
        }
        catch (Exception ex)
        {
            Log.Warn("保存主题设置失败", ex);
        }
    }

    private void OnDesignChanged(object? sender, EventArgs e)
    {
        // 设计语言已收敛为单一 Fluent；这里只需要同步明暗模式。
        ApplyThemeFromSettings();
    }

    /// <summary>设置页改完明暗模式后调用，让 Shell 根元素立即按新主题重解析 token。</summary>
    public void ApplyThemeFromSettings() => ApplySavedTheme();

    // ==================================================================================
    //  搜索
    // ==================================================================================
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSearch) return;
        // ViewModel 内部做 250ms 防抖，然后通过 AppEvents.SearchChanged 通知素材库页面。
        ViewModel.SearchText = SearchBox.Text;
        SearchRequested?.Invoke(this, SearchBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ViewModel.CommitSearch();
        e.Handled = true;
    }

    public static event EventHandler<string>? SearchRequested;

    // ==================================================================================
    //  导航
    // ==================================================================================
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string viewId }) return;
        ViewModel.SelectedNavId = viewId;
        PersistNavId(viewId);
        NavigateTo(viewId);
    }

    /// <summary>把当前视图 id 落到设置里：进程重启 / 换皮肤重建后都能回到同一页。</summary>
    private static void PersistNavId(string viewId)
    {
        try
        {
            if (AppServices.Settings.Current.LastViewId == viewId) return;
            AppServices.Settings.Current.LastViewId = viewId;
            _ = AppServices.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("保存最后视图失败", ex);
        }
    }

    private void NavigateTo(string viewId)
    {
        var pageType = ResolvePageType(viewId);
        if (pageType is not null)
        {
            PlaceholderHost.Visibility = Visibility.Collapsed;
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, viewId,
                    new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            return;
        }

        // 还没接入的视图（例如地图，由另一位负责）：显示占位而不是抛异常。
        if (ViewFactories.TryGetValue(viewId, out var factory))
        {
            PlaceholderHost.Visibility = Visibility.Collapsed;
            ContentFrame.Content = factory();
            return;
        }

        ContentFrame.Content = null;
        PlaceholderHost.Visibility = Visibility.Visible;
        Placeholder.Text = $"「{ViewText(viewId)}」视图尚未接入";
    }

    /// <summary>视图 id → Page 类型。名字必须与任务约定一致（TimelinePage / DuplicatesPage / AiQueuePage / SettingsPage）。</summary>
    private static Type? ResolvePageType(string viewId) => viewId switch
    {
        "library" => typeof(LibraryPage),
        "gallery" => typeof(GalleryPage),
        "import" => typeof(ImportPage),
        "timeline" => typeof(TimelinePage),
        "lut" => typeof(LutPage),
        "batch" => typeof(BatchEditPage),
        "duplicates" => typeof(DuplicatesPage),
        "cloud" => typeof(CloudPage),
        "aiqueue" => typeof(AiQueuePage),
        "organize" => typeof(OrganizePage),
        "settings" => typeof(SettingsPage),
        "about" => typeof(AboutPage),
        _ => null,
    };

    private void SyncNavSelection(string viewId)
    {
        foreach (var menuItem in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (menuItem is NavigationViewItem { Tag: string tag } item && tag == viewId)
            {
                if (!ReferenceEquals(NavView.SelectedItem, item)) NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void BackToLibrary_Click(object sender, RoutedEventArgs e) => SyncNavSelection("library");

    /// <summary>
    /// 引导期间关掉 Mica：Mica 跟随系统深色，导航栏与状态栏透过深色背板发黑，
    /// 和浅色引导页割裂。切成纯色背板（用 RootGrid 的浅色主题底），结束后再恢复。
    /// </summary>
    private static void UseSolidBackdropForOnboarding(bool solid)
    {
        try
        {
            if (App.MainWindow is null) return;
            if (solid) App.MainWindow.SystemBackdrop = null;
            else _ = AppServices.WindowChrome.RestoreBackdropAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("切换引导页背板失败", ex);
        }
    }

    /// <summary>供子页面跳转（例如 AI 队列 → 设置）。</summary>
    public void NavigateToView(string viewId) => SyncNavSelection(viewId);

    /// <summary>引导结束：回到素材库（选中项没变也要强制导航一次）。</summary>
    public void LeaveOnboarding()
    {
        try
        {
            UseSolidBackdropForOnboarding(false);
            PersistNavId("library");
            SyncNavSelection("library");
            NavigateTo("library");
        }
        catch (Exception ex)
        {
            Log.Warn("引导结束后返回素材库失败", ex);
        }
    }

    /// <summary>打开新用户引导（首次启动 / 设置里重新播放）。</summary>
    /// <param name="replay">true = 用户主动重播（结束后恢复原主题）；false = 首次引导（结束切深色）。</param>
    public void ShowOnboarding(bool replay = false)
    {
        try
        {
            OnboardingPage.EnterReplay = replay;

            // 引导期间固定浅色：演示动画是浅色扁平风，右侧面板也跟着浅色才不割裂。
            // persist:false —— 不覆盖用户已保存的主题，结束后再决定。
            _ = AppServices.Design.SetThemeModeAsync("light", persist: false);

            UseSolidBackdropForOnboarding(true);
            Log.Info($"[onboarding] 打开引导页（{(replay ? "重播" : "首次启动")}）");
            if (ContentFrame.CurrentSourcePageType != typeof(OnboardingPage))
                ContentFrame.Navigate(typeof(OnboardingPage), null,
                    new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }
        catch (Exception ex)
        {
            Log.Warn("打开新用户引导失败", ex);
        }
    }

    private static readonly Dictionary<string, Func<UIElement>> ViewFactories = new();

    /// <summary>视图注册：还没做成 Page 的视图可以挂到这里。</summary>
    public static void RegisterView(string viewId, Func<UIElement> factory) => ViewFactories[viewId] = factory;

    private static string ViewText(string viewId) => viewId switch
    {
        "timeline" => "时间线",
        "map" => "地图",
        "duplicates" => "重复项",
        "aiqueue" => "AI 队列",
        "settings" => "设置",
        _ => viewId,
    };

    // ==================================================================================
    //  扫描 / AI 队列
    // ==================================================================================
    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await PickAndScanLibraryAsync();

    private async Task PickAndScanLibraryAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            AppEvents.ReportScan(new ScanStatus
            {
                IsActive = true,
                IsIndeterminate = true,
                Text = $"正在扫描 {folder.Path} …",
            });

            var root = await AppServices.Library.AddLibraryAsync(folder.Path);
            var progress = new Progress<ScanProgress>(p => AppEvents.ReportScan(new ScanStatus
            {
                IsActive = true,
                IsIndeterminate = p.Total <= 0,
                Percent = p.Percent,
                Text = $"{p.Phase} · {p.Processed}/{p.Total} · {Path.GetFileName(p.CurrentPath ?? "")}",
            }));

            var result = await AppServices.Library.ScanLibraryAsync(root, new ScanOptions(), progress);
            AppEvents.ReportScan(new ScanStatus
            {
                IsActive = false,
                Text = $"扫描完成：发现 {result.MediaFound} 个媒体文件（新增 {result.Added}）",
            });
            AppEvents.RaiseLibraryContentChanged();
            _ = ViewModel.RefreshCacheSizeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("扫描媒体库失败", ex);
            AppEvents.ReportScan(new ScanStatus { IsActive = false });
            AppEvents.SetError($"扫描失败：{ex.Message}");
        }
    }

    private void AiButton_Click(object sender, RoutedEventArgs e) => SyncNavSelection("aiqueue");

    // ==================================================================================
    //  全屏预览
    // ==================================================================================
    /// <summary>由素材库 / 检视面板调用：用当前列表打开全屏预览。</summary>
    public void ShowPreview(IReadOnlyList<MediaAsset> assets, int index)
    {
        if (assets.Count == 0) return;
        Preview.Show(assets, index);
    }

    public void ClosePreview() => Preview.Close();

    /// <summary>
    /// 预览层键盘兜底：正常情况下按键在 PreviewOverlay 内部处理（会标记 Handled）；
    /// 一旦焦点跑到 ScrollViewer 之类的元素上丢了上下文，这里补一次 Esc / ← / →。
    /// </summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!Preview.ViewModel.IsOpen) return;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Preview.Close();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                Preview.ViewModel.Previous();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                Preview.ViewModel.Next();
                e.Handled = true;
                break;
        }
    }
}