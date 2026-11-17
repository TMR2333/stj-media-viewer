using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 素材库主页面：左侧筛选栏 + 中间虚拟化网格 + 右侧检视面板。
/// 状态全部放在 <see cref="LibraryViewModel.Shared"/> 里（换皮肤会重建页面），
/// 页面只负责把 UI 事件翻译成 ViewModel 调用。
/// </summary>
public sealed partial class LibraryPage : Page
{
    private static string _density = "medium";
    private static bool _inspectorVisible = true;

    private bool _eventsAttached;
    private bool _initializedOnce;
    private ScrollViewer? _listScrollViewer;
    private readonly List<ScrollViewer> _trackedScrollViewers = new();

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyDemoFilterFocus();
        // 首屏加载完成后再预取：此时 Items 才是完整的一页
    }

    public LibraryViewModel ViewModel => LibraryViewModel.Shared;

    // ==================================================================================
    //  生命周期
    // ==================================================================================
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        ApplyDensity(_density);
        // 首次布局后按当前窗口尺寸决定三栏开合（之前用固定 true，会在窄窗口把检视面板顶出来）
        ApplyResponsiveLayout(WindowWidth);

        if (!_initializedOnce)
        {
            _initializedOnce = true;
            SortBox.SelectedIndex = 0;
            SortOrderText.Text = ViewModel.Descending ? "降序" : "升序";
            if (ViewModel.Items.Count == 0) ViewModel.Initialize();
        }

        InspectorViewModel.Shared.Show(ViewModel.PrimaryAsset);
        UpdateShellCounts();
        _ = CheckLibraryAvailabilityAsync();
        if (Environment.GetEnvironmentVariable("STJ_DEMO_ONLY_RAW") == "1")
            ViewModel.OnlyRaw = true;

        // 截图用：等首屏加载完再打开检视面板，并优先选中一张多主题素材
        if (Environment.GetEnvironmentVariable("STJ_DEMO_INSPECTOR") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                DispatcherQueue.TryEnqueue(() =>
                {
                    InspectorToggle.IsChecked = true;
                    ApplyInspectorVisibility(true);
                    var pick = ViewModel.Items.FirstOrDefault(i => i.Asset.Themes.Count > 1) ?? ViewModel.Items.FirstOrDefault();
                    if (pick is not null) ViewModel.SetSelection(new[] { pick });

                    // 滚到「手动分类」区域，让多主题 chips 进画面
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await Task.Delay(900);
                        if (FindDescendant<ScrollViewer>(Inspector) is { } scroller)
                            scroller.ChangeView(null, 1500, null, true);
                    });
                });
            });
        }
        _ = ShellViewModel.Shared.RefreshCacheSizeAsync();
        TryHookScrollViewer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachEvents();

    private void AttachEvents()
    {
        if (_eventsAttached) return;
        _eventsAttached = true;

        ViewModel.PrimaryAssetChanged += OnPrimaryAssetChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        AppEvents.SearchChanged += OnSearchChanged;
        AppEvents.LibraryContentChanged += OnLibraryContentChanged;
        AppEvents.AssetUpdated += OnAssetUpdated;
        Inspector.PreviewRequested += OnInspectorPreviewRequested;
    }

    private void DetachEvents()
    {
        if (!_eventsAttached) return;
        _eventsAttached = false;

        ViewModel.PrimaryAssetChanged -= OnPrimaryAssetChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        AppEvents.SearchChanged -= OnSearchChanged;
        AppEvents.LibraryContentChanged -= OnLibraryContentChanged;
        AppEvents.AssetUpdated -= OnAssetUpdated;
        Inspector.PreviewRequested -= OnInspectorPreviewRequested;

        foreach (var sv in _trackedScrollViewers) sv.ViewChanged -= OnListScrollViewChanged;
        _trackedScrollViewers.Clear();
        _listScrollViewer = null;
    }

    private void OnPrimaryAssetChanged(object? sender, MediaAsset? asset)
    {
        InspectorViewModel.Shared.Show(asset);
        UpdateShellCounts();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.TotalCount):
            case nameof(LibraryViewModel.FilteredCount):
            case nameof(LibraryViewModel.SelectionCount):
            case null:
            case "":
                UpdateShellCounts();
                break;
        }
    }

    /// <summary>
    /// 媒体库目录被删除 / 移动后（例如删掉旧版软件文件夹再装新版），
    /// 网格里会只剩空白占位：这里明确提示，并给一个直达修复入口。
    /// </summary>
    private async Task CheckLibraryAvailabilityAsync()
    {
        try
        {
            var libraries = await AppServices.Library.GetLibrariesAsync();
            var missing = libraries.Where(l => !string.IsNullOrWhiteSpace(l.Path) && !Directory.Exists(l.Path)).ToList();
            if (missing.Count == 0)
            {
                LibraryIssueBar.IsOpen = false;
                return;
            }

            LibraryIssueBar.Message = missing.Count == 1
                ? $"找不到「{missing[0].Name}」的目录：{missing[0].Path}。缩略图不会生成；如果文件夹被移动/改名，用「重新定位」修复。"
                : $"有 {missing.Count} 个媒体库目录不存在，缩略图不会生成：{string.Join("；", missing.Select(l => l.Name))}。";
            LibraryIssueBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warn("检查媒体库目录可用性失败", ex);
        }
    }

    private void FixMissingLibrary_Click(object sender, RoutedEventArgs e)
        => ShellPage.Current?.NavigateToView("settings");

    private void UpdateShellCounts()
    {
        var shell = ShellViewModel.Shared;
        shell.UpdateLibrarySummary(ViewModel.TotalCount, ViewModel.FilteredCount);
        shell.UpdateSelectionSummary(ViewModel.SelectionCount);
    }

    private void OnSearchChanged(object? sender, string text) => ViewModel.SetSearch(text);

    private void OnLibraryContentChanged(object? sender, EventArgs e) => ViewModel.RefreshAfterExternalChange();

    private void OnAssetUpdated(object? sender, MediaAsset asset) => ViewModel.UpdateAsset(asset);

    private void OnInspectorPreviewRequested(object? sender, MediaAsset asset)
    {
        var list = ViewModel.Items.Select(i => i.Asset).ToList();
        var index = list.FindIndex(a => a.Id == asset.Id);
        if (index < 0)
        {
            // 检视面板里的素材可能已被筛掉：直接以单张列表打开，避免预览出"别人"。
            ShellPage.Current?.ShowPreview(new[] { asset }, 0);
            return;
        }
        ShellPage.Current?.ShowPreview(list, index);
    }

    // ==================================================================================
    //  工具条
    // ==================================================================================
    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        // 清空筛选同时清掉顶栏搜索框（两处状态必须同步，否则用户会看到"残留搜索词"）。
        ShellViewModel.Shared.SearchText = "";
        ViewModel.ClearAllFilters();
    }

    private void OnlyRaw_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OnlyRaw = (sender as ToggleButton)?.IsChecked == true;

    private void OnlyLog_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OnlyLog = (sender as ToggleButton)?.IsChecked == true;

    private void OnlyUncolored_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OnlyUncolored = (sender as ToggleButton)?.IsChecked == true;

    private void OnlyColored_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OnlyColored = (sender as ToggleButton)?.IsChecked == true;

    /// <summary>卡片进入视野时预热 StorageFile，拖出时不至于卡住。</summary>
    private void AssetList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not AssetCardItem card) return;
        try
        {
            DragFileCache.Preload(new[] { card.Asset.FullPath }.Concat(card.Asset.CompanionPaths));
        }
        catch { /* 预热失败不影响展示 */ }
    }

    /// <summary>
    /// 把素材拖到 Photoshop / Lightroom 等外部软件：DataPackage 里放真实磁盘文件
    /// （JPG/RAW 配对的两个文件一起给），目标程序直接按路径打开。
    /// </summary>
    private void AssetList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        try
        {
            var assets = e.Items.OfType<AssetCardItem>().Select(i => i.Asset).ToList();
            if (assets.Count == 0) assets = ViewModel.SelectedItems.Select(i => i.Asset).ToList();
            if (assets.Count == 0) { e.Cancel = true; return; }

            var files = new List<Windows.Storage.StorageFile>();
            foreach (var asset in assets)
            {
                foreach (var path in new[] { asset.FullPath }.Concat(asset.CompanionPaths))
                {
                    var file = DragFileCache.Take(path);
                    if (file is not null) files.Add(file);
                }
            }

            if (files.Count == 0) { e.Cancel = true; return; }

            e.Data.SetStorageItems(files);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.Data.Properties.Description = files.Count == 1
                ? Path.GetFileName(files[0].Path)
                : $"{files.Count} 个文件";

            Log.Info("[drag] 拖出 " + files.Count + " 个文件：" +
                     string.Join(", ", files.Take(4).Select(f => Path.GetFileName(f.Path))));
            AppEvents.SetStatus(files.Count == 1
                ? $"拖出 1 个文件：{Path.GetFileName(files[0].Path)}"
                : $"拖出 {files.Count} 个文件");
        }
        catch (Exception ex)
        {
            Log.Warn("拖拽素材失败", ex);
            e.Cancel = true;
        }
    }

    private void EmptyState_ActionInvoked(object? sender, EventArgs e)
    {
        if (ViewModel.TotalCount == 0)
        {
            _ = PickAndScanLibraryAsync();
        }
        else
        {
            ShellViewModel.Shared.SearchText = "";
            ViewModel.ClearAllFilters();
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag }) ViewModel.SetSort(tag);
    }

    private void SortOrder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSortDirection();
        SortOrderText.Text = ViewModel.Descending ? "降序" : "升序";
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) ApplyDensity(tag);
    }

    /// <summary>RAW 副本开关：关 = 按 JPG+RAW 分组折叠（默认），开 = 每个文件各成一项。</summary>
    private void GroupCopiesToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowGroupCopies = GroupCopiesToggle.IsChecked == true;
        ViewModel.RequestRefresh(immediate: true);
    }

    private void ApplyDensity(string tag)
    {
        _density = tag;
        DensitySmall.IsChecked = tag == "small";
        DensityMedium.IsChecked = tag == "medium";
        DensityLarge.IsChecked = tag == "large";
        DensityList.IsChecked = tag == "list";

        var isList = tag == "list";
        AssetList.ItemsPanel = (ItemsPanelTemplate)Resources[tag switch
        {
            "small" => "PanelSmall",
            "large" => "PanelLarge",
            "list" => "PanelList",
            _ => "PanelMedium",
        }];
        AssetList.ItemTemplate = (DataTemplate)Resources[isList ? "RowTemplate" : "CardTemplate"];
        TryHookScrollViewer();
    }

    /// <summary>截图/验收用：STJ_DEMO_FOCUS_FILTERS=theme 时只展开主题/影调两组。</summary>
    private void ApplyDemoFilterFocus()
    {
        var focus = Environment.GetEnvironmentVariable("STJ_DEMO_FOCUS_FILTERS");
        if (string.IsNullOrEmpty(focus)) return;

        // 等分面数据把分组建好（自定义类别是动态生成的），再统一展开 + 滚到底
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1800);

            foreach (var group in ViewModel.Groups)
            {
                bool want = focus.Contains("custom", StringComparison.OrdinalIgnoreCase)
                    ? group.Kind == FilterGroupKind.Custom
                    : focus.Contains("theme", StringComparison.OrdinalIgnoreCase) &&
                      group.Kind is FilterGroupKind.Theme or FilterGroupKind.Tone;
                group.IsExpanded = want;
            }

            await Task.Delay(300);
            try { FilterScroll.ChangeView(null, FilterScroll.ScrollableHeight, null); } catch { }
        });
    }

    private void Gallery_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(GalleryPage));

    private void Duplicates_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DuplicatesPage));

    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItems.Count == 0)
        {
            AppEvents.SetError("先在图库里选中素材，再批量填写信息");
            return;
        }
        Frame.Navigate(typeof(BatchEditPage));
    }

    // ==================================================================================
    //  自适应布局：窗口尺寸变化时实时重排（不依赖页面切换/重启）
    // ==================================================================================
    private bool _filterVisible = true;
    private bool _filterAutoHidden;
    private bool _inspectorAutoHidden;

    /// <summary>宽度断点：< 1000 收起筛选栏，< 1280 收起检视面板；变宽后恢复用户自己的选择。</summary>
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(WindowWidth);

    /// <summary>窗口内容宽度（DIP）。页面被三栏分割后 ActualWidth 会骗人，所以用 XamlRoot。</summary>
    private double WindowWidth => XamlRoot?.Size.Width ?? ActualWidth;

    private void ApplyResponsiveLayout(double width)
    {
        // 只改"当前渲染状态"，不动用户偏好（否则自动收起会把偏好也改掉，变宽后回不来）。
        bool wantFilterHidden = width < 1000;
        if (wantFilterHidden != _filterAutoHidden)
        {
            _filterAutoHidden = wantFilterHidden;
            RenderFilterVisibility(wantFilterHidden ? false : _filterVisible);
        }

        bool wantInspectorHidden = width < 1280;
        if (wantInspectorHidden != _inspectorAutoHidden)
        {
            _inspectorAutoHidden = wantInspectorHidden;
            RenderInspectorVisibility(wantInspectorHidden ? false : _inspectorVisible);
        }
    }

    private void FilterToggle_Click(object sender, RoutedEventArgs e)
    {
        _filterVisible = FilterToggle.IsChecked == true;
        ApplyFilterVisibility(_filterVisible);
    }

    private void ApplyFilterVisibility(bool visible)
    {
        _filterVisible = visible;
        RenderFilterVisibility(visible);
    }

    private void RenderFilterVisibility(bool visible)
    {
        FilterToggle.IsChecked = visible;
        FilterHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FilterColumn.Width = visible ? new GridLength(248) : new GridLength(0);
    }

    private void InspectorToggle_Click(object sender, RoutedEventArgs e) =>
        ApplyInspectorVisibility(InspectorToggle.IsChecked == true);

    private void ApplyInspectorVisibility(bool visible)
    {
        _inspectorVisible = visible;
        RenderInspectorVisibility(visible);
    }

    private void RenderInspectorVisibility(bool visible)
    {
        InspectorToggle.IsChecked = visible;
        InspectorHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = visible ? new GridLength(348) : new GridLength(0);
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadMoreAsync();

    // ==================================================================================
    //  筛选栏
    // ==================================================================================
    private void FilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FilterOptionViewModel option }) return;
        if (!option.IsEnabled) return;
        option.IsSelected = !option.IsSelected;
    }

    // ==================================================================================
    //  网格交互
    // ==================================================================================
    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = AssetList.SelectedItems.OfType<AssetCardItem>().ToList();
        ViewModel.SetSelection(selected);
    }

    private void AssetList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = FindItemAt(e.OriginalSource);
        if (item is not null) OpenPreview(item);
    }

    private void AssetList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
            case VirtualKey.Enter:
                var item = AssetList.SelectedItem as AssetCardItem
                           ?? AssetList.SelectedItems.OfType<AssetCardItem>().LastOrDefault();
                if (item is not null)
                {
                    OpenPreview(item);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Escape:
                AssetList.SelectedItems.Clear();
                e.Handled = true;
                break;
        }
    }

    private void OpenPreview(AssetCardItem item)
    {
        var list = ViewModel.Items.Select(i => i.Asset).ToList();
        var index = ViewModel.Items.IndexOf(item);
        ShellPage.Current?.ShowPreview(list, Math.Max(0, index));
    }

    private void AssetList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindItemAt(e.OriginalSource);
        if (item is null) return;
        if (!AssetList.SelectedItems.Contains(item)) AssetList.SelectedItem = item;
        ShowContextMenu(item, e.GetPosition(AssetList));
    }

    private static AssetCardItem? FindItemAt(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is ListViewItem { Content: AssetCardItem card }) return card;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // ==================================================================================
    //  悬停快速操作（卡片内）
    // ==================================================================================
    private void CardHover_Entered(object sender, PointerRoutedEventArgs e) => SetHoverOpacity(sender, 1.0);

    private void CardHover_Moved(object sender, PointerRoutedEventArgs e) => SetHoverOpacity(sender, 1.0);

    private void CardHover_Exited(object sender, PointerRoutedEventArgs e) => SetHoverOpacity(sender, 0.0);

    private static void SetHoverOpacity(object sender, double opacity)
    {
        if (sender is UIElement element) element.Opacity = opacity;
    }

    private void CardPreview_Click(object sender, RoutedEventArgs e)
    {
        if (FindItemFromSender(sender) is { } item) OpenPreview(item);
    }

    private async void CardAi_Click(object sender, RoutedEventArgs e)
    {
        if (FindItemFromSender(sender) is { } item) await AnalyzeAssetsAsync(new[] { item.Asset });
    }

    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        if (FindItemFromSender(sender) is not { } item) return;
        if (!AssetList.SelectedItems.Contains(item)) AssetList.SelectedItem = item;
        ShowContextMenu(item, new Windows.Foundation.Point(0, 0));
    }

    private static AssetCardItem? FindItemFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as AssetCardItem;

    // ==================================================================================
    //  右键菜单
    // ==================================================================================
    private void ShowContextMenu(AssetCardItem item, Windows.Foundation.Point position)
    {
        var flyout = new MenuFlyout();

        var ratingMenu = new MenuFlyoutSubItem { Text = "评分" };
        for (int r = 0; r <= 5; r++)
        {
            var value = r;
            var mi = new MenuFlyoutItem { Text = r == 0 ? "无评分" : new string('\u2605', r) };
            mi.Click += async (_, _) => await ApplyToTargetsAsync(item,
                a => { a.Rating = value; a.Provenance[ClassDimension.Rating] = FieldProvenance.Of(ClassSource.Manual); },
                $"已评分 {value} 星");
            ratingMenu.Items.Add(mi);
        }
        flyout.Items.Add(ratingMenu);

        var labelMenu = new MenuFlyoutSubItem { Text = "颜色标签" };
        foreach (var label in Vocabularies.ColorLabels)
        {
            var id = label.Id.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : label.Id;
            var mi = new MenuFlyoutItem { Text = label.Zh };
            mi.Click += async (_, _) => await ApplyToTargetsAsync(item,
                a => a.ColorLabel = id, $"已标记为{label.Zh}");
            labelMenu.Items.Add(mi);
        }
        flyout.Items.Add(labelMenu);

        var pickMenu = new MenuFlyoutSubItem { Text = "选用 / 废片" };
        var pickItem = new MenuFlyoutItem { Text = "选用" };
        pickItem.Click += async (_, _) => await ApplyToTargetsAsync(item, a => a.IsPick = true, "已标记为选用");
        var rejectItem = new MenuFlyoutItem { Text = "废片" };
        rejectItem.Click += async (_, _) => await ApplyToTargetsAsync(item, a => a.IsPick = false, "已标记为废片");
        var clearPickItem = new MenuFlyoutItem { Text = "清除标记" };
        clearPickItem.Click += async (_, _) => await ApplyToTargetsAsync(item, a => a.IsPick = null, "已清除选用标记");
        pickMenu.Items.Add(pickItem);
        pickMenu.Items.Add(rejectItem);
        pickMenu.Items.Add(clearPickItem);
        flyout.Items.Add(pickMenu);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var aiItem = new MenuFlyoutItem { Text = "AI 分类此条" };
        aiItem.Click += async (_, _) => await AnalyzeAssetsAsync(TargetAssets(item));
        flyout.Items.Add(aiItem);

        var explorerItem = new MenuFlyoutItem { Text = "在资源管理器中打开" };
        explorerItem.Click += (_, _) => OpenInExplorer(item.Asset);
        flyout.Items.Add(explorerItem);

        var copyItem = new MenuFlyoutItem { Text = "复制路径" };
        copyItem.Click += (_, _) => CopyPaths(TargetAssets(item));
        flyout.Items.Add(copyItem);

        flyout.ShowAt(AssetList, position);
    }

    private IReadOnlyList<MediaAsset> TargetAssets(AssetCardItem fallback)
    {
        var selected = AssetList.SelectedItems.OfType<AssetCardItem>().Select(i => i.Asset).ToList();
        if (selected.Count == 0) selected.Add(fallback.Asset);
        return selected;
    }

    private async Task ApplyToTargetsAsync(AssetCardItem fallback, Action<MediaAsset> mutate, string status)
    {
        var targets = TargetAssets(fallback);
        try
        {
            foreach (var asset in targets)
            {
                mutate(asset);
                await AppServices.Store.UpsertAsync(asset);
                AppEvents.RaiseAssetUpdated(asset);
            }
            if (ViewModel.PrimaryAsset is { } primary) InspectorViewModel.Shared.Show(primary);
            AppEvents.SetStatus($"{status}（{targets.Count} 张）");
        }
        catch (Exception ex)
        {
            Log.Warn("批量修改失败", ex);
            AppEvents.SetError($"修改失败：{ex.Message}");
        }
    }

    private async Task AnalyzeAssetsAsync(IReadOnlyList<MediaAsset> targets)
    {
        if (targets.Count == 0) return;
        AppEvents.SetStatus($"AI 正在分析 {targets.Count} 张素材…");
        var done = 0;
        foreach (var asset in targets)
        {
            try
            {
                var suggestion = await AppServices.Ai.AnalyzeAssetAsync(asset);
                if (!suggestion.Success)
                {
                    AppEvents.SetError($"AI 分类不可用：{suggestion.Error}");
                    return;
                }
                InspectorViewModel.ApplySuggestion(asset, suggestion);
                await AppServices.Store.UpsertAsync(asset);
                AppEvents.RaiseAssetUpdated(asset);
                done++;
            }
            catch (Exception ex)
            {
                Log.Warn($"AI 分类失败 {asset.FileName}", ex);
                AppEvents.SetError($"AI 分类失败：{ex.Message}");
                return;
            }
        }
        if (ViewModel.PrimaryAsset is { } primary) InspectorViewModel.Shared.Show(primary);
        AppEvents.SetStatus($"AI 分类完成：{done} 张素材已更新");
    }

    private static void OpenInExplorer(MediaAsset asset)
    {
        try
        {
            var path = asset.FullPath;
            var args = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path) ?? path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开资源管理器失败", ex);
            AppEvents.SetError("无法打开资源管理器");
        }
    }

    private static void CopyPaths(IReadOnlyList<MediaAsset> assets)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(string.Join(Environment.NewLine, assets.Select(a => a.FullPath)));
            Clipboard.SetContent(package);
            AppEvents.SetStatus($"已复制 {assets.Count} 条路径");
        }
        catch (Exception ex)
        {
            Log.Warn("复制路径失败", ex);
        }
    }

    // ==================================================================================
    //  空状态：选择媒体库 + 扫描
    // ==================================================================================
    private async void PickLibrary_Click(object sender, RoutedEventArgs e) => await PickAndScanLibraryAsync();

    private async Task PickAndScanLibraryAsync()
    {
        try
        {
            var picker = new FolderPicker();
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
        }
        catch (Exception ex)
        {
            Log.Warn("扫描媒体库失败", ex);
            AppEvents.ReportScan(new ScanStatus { IsActive = false });
            AppEvents.SetError($"扫描失败：{ex.Message}");
        }
    }

    // ==================================================================================
    //  滚动到底自动加载
    // ==================================================================================
    private void TryHookScrollViewer()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var viewer = FindDescendant<ScrollViewer>(AssetList);
            if (viewer is null || ReferenceEquals(viewer, _listScrollViewer)) return;
            if (_listScrollViewer is not null) _listScrollViewer.ViewChanged -= OnListScrollViewChanged;
            _listScrollViewer = viewer;
            viewer.ViewChanged += OnListScrollViewChanged;
            _trackedScrollViewers.Clear();
            _trackedScrollViewers.Add(viewer);
        });
    }

    private void OnListScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var viewer = sender as ScrollViewer;
        if (viewer is null || ViewModel.IsLoading || !ViewModel.HasMore) return;
        var remaining = viewer.ScrollableHeight - viewer.VerticalOffset;
        if (remaining <= 480) _ = ViewModel.LoadMoreAsync();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}