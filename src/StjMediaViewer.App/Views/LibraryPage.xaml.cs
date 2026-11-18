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
    /// <summary>侧边栏切到照片库 / 视频库时调用：只改范围，不重建整页。</summary>
    public void ApplyNavScope(string viewId)
    {
        var scope = LibraryScopeRules.FromViewId(viewId);
        if (ViewModel.Scope == scope) return;
        ViewModel.SetScope(scope);
        // 换库时网格轻微淡入，避免列表"啪"地换成另一批素材
        PageLayout.FadeIn(AssetList, 180);
    }

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
            // 按侧边栏进入的是照片库还是视频库，先锁定媒体类型范围
            ViewModel.SetScope(LibraryScopeRules.FromViewId(ShellViewModel.Shared.SelectedNavId));
            if (ViewModel.Items.Count == 0) ViewModel.Initialize();
        }

        InspectorViewModel.Shared.Show(ViewModel.PrimaryAsset);
        UpdateShellCounts();
        _ = CheckLibraryAvailabilityAsync();
        if (Environment.GetEnvironmentVariable("STJ_DEMO_ONLY_RAW") == "1")
            ViewModel.OnlyRaw = true;

        // 截图用：STJ_DEMO_MERGE_FILTER=1 展开「合并组」分组并滚到它
        if (Environment.GetEnvironmentVariable("STJ_DEMO_MERGE_FILTER") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.FrequentDeviceGroup.IsExpanded = false;
                    ViewModel.DeviceGroup.IsExpanded = false;
                    ViewModel.CollectionGroup.IsExpanded = false;
                    ViewModel.MergeGroupGroup.IsExpanded = true;
                    FilterScroll.ChangeView(null, 300, null, true);
                });
            });
        }

        // 截图用：STJ_DEMO_DELETE_REJECTS=1 点开「删除废片」确认框（不真的执行）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_DELETE_REJECTS") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                DispatcherQueue.TryEnqueue(() => DeleteRejects_Click(this, new RoutedEventArgs()));
            });
        }

        // 截图用：STJ_DEMO_COLLECTION_OPEN=1 打开第一个影像合集（看合集横幅）
        //          =meta 打开后顺便看元数据；=batch 打开后进批量编辑
        var collectionDemo = Environment.GetEnvironmentVariable("STJ_DEMO_COLLECTION_OPEN");
        if (!string.IsNullOrEmpty(collectionDemo))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await ViewModel.RefreshCollectionsAsync();
                    var first = ViewModel.Collections.FirstOrDefault();
                    if (first is null) return;
                    ViewModel.OpenCollection(first.Id);
                    await Task.Delay(1500);
                    if (collectionDemo == "meta") CollectionMetadata_Click(this, new RoutedEventArgs());
                    else if (collectionDemo == "batch") CollectionBatchEdit_Click(this, new RoutedEventArgs());
                });
            });
        }

        // 截图用：STJ_DEMO_COLLECTION_FILTER=1 展开「影像合集」分组并滚到它
        if (Environment.GetEnvironmentVariable("STJ_DEMO_COLLECTION_FILTER") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.FrequentDeviceGroup.IsExpanded = false;
                    ViewModel.DeviceGroup.IsExpanded = false;
                    ViewModel.CollectionGroup.IsExpanded = true;
                    FilterScroll.ChangeView(null, 300, null, true);
                });
            });
        }

        // 截图用：STJ_DEMO_MERGE_DIALOG=1 打开"合并为大文件夹"对话框（带 4 张选中素材）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_MERGE_DIALOG") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2800);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var pick = ViewModel.Items.Take(4).ToList();
                    if (pick.Count > 0) ViewModel.SetSelection(pick);
                    await ShowMergeGroupsDialogAsync(pick.Select(p => p.Asset).ToList());
                });
            });
        }

        // 截图用：STJ_DEMO_COLLECTIONS=1 打开影像合集对话框（带 3 张选中素材）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_COLLECTIONS") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2800);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var pick = ViewModel.Items.Take(3).ToList();
                    if (pick.Count > 0) ViewModel.SetSelection(pick);
                    await ShowCollectionsDialogAsync(pick.Select(p => p.Asset).ToList());
                });
            });
        }

        // 验收用：STJ_DEMO_MERGE_LOADMORE=1 合并组跨页时"加载更多"是否仍然只保留封面
        if (Environment.GetEnvironmentVariable("STJ_DEMO_MERGE_LOADMORE") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3200);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await ViewModel.RefreshMergeGroupsAsync();
                    var group = ViewModel.MergeGroups.OrderByDescending(g => g.AssetCount).FirstOrDefault();
                    if (group is null) return;
                    var memberIds = (await AppServices.Store.GetMergeGroupAssetIdsAsync(group.Id)).ToHashSet(StringComparer.Ordinal);

                    void LogState(string label)
                    {
                        var leaked = ViewModel.Items.Count(i => i.MergeGroup is null && memberIds.Contains(i.Id));
                        var groupCards = ViewModel.Items.Count(i => i.MergeGroup?.Id == group.Id);
                        Core.Util.Log.Info($"[merge-loadmore] {label} items={ViewModel.Items.Count} groupCards={groupCards} leakedMembers={leaked} " +
                                           $"hasMore={ViewModel.HasMore} loading={ViewModel.IsLoading} filtered={ViewModel.FilteredCount}");
                    }

                    // 等首屏刷新收尾（缩略图预热时 IsLoading 仍为 true，LoadMore 会被跳过）
                    for (int i = 0; i < 40 && ViewModel.IsLoading; i++) await Task.Delay(250);
                    LogState("page1");
                    await ViewModel.LoadMoreAsync();
                    await Task.Delay(900);
                    LogState("afterLoadMore");
                });
            });
        }

        // 验收用：STJ_DEMO_MERGE_OPEN=1 打开第一个合并组；=2 打开后立刻测「返回」
        var mergeDemo = Environment.GetEnvironmentVariable("STJ_DEMO_MERGE_OPEN");
        if (!string.IsNullOrEmpty(mergeDemo))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2800);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await ViewModel.RefreshMergeGroupsAsync();
                    var group = ViewModel.MergeGroups.FirstOrDefault();
                    if (group is null) return;
                    var before = ViewModel.Items.Count;
                    OpenMergeGroupCard(group);

                    if (mergeDemo != "2") return;
                    await Task.Delay(1800);
                    var opened = ViewModel.Items.Count;
                    var banner = ViewModel.MergeGroupBannerVisibility;
                    MergeGroupBack_Click(this, new RoutedEventArgs());
                    await Task.Delay(1800);
                    Core.Util.Log.Info($"[merge-open] before={before} opened={opened} banner={banner} " +
                                       $"back={ViewModel.Items.Count} canGoBack={ViewModel.CanGoBack}");
                });
            });
        }

        // 验收用：STJ_DEMO_MARQUEE_SCROLL=1 框选 → 自动滚动 → 继续框选，检查上面的选中是否被清掉
        if (Environment.GetEnvironmentVariable("STJ_DEMO_MARQUEE_SCROLL") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 模拟一次"从空白处按下开始拖"
                    _marqueeActive = true;
                    _marqueeAdditive = false;
                    _marqueeBase.Clear();
                    _marqueeHits.Clear();
                    ApplyMarqueeSelection(new Windows.Foundation.Rect(380, 190, 700, 200));
                    Core.Util.Log.Info($"[marquee-scroll] top: vm={ViewModel.SelectedItems.Count}");
                });

                await Task.Delay(700);
                DispatcherQueue.TryEnqueue(() =>
                {
                    FindDescendant<ScrollViewer>(AssetList)?.ChangeView(null, 900, null, true);
                });

                await Task.Delay(900);
                DispatcherQueue.TryEnqueue(() =>
                {
                    ApplyMarqueeSelection(new Windows.Foundation.Rect(380, 400, 700, 200));
                    EndMarquee();
                    _marqueeActive = false;
                    Core.Util.Log.Info($"[marquee-scroll] after scroll+bottom: vm={ViewModel.SelectedItems.Count} " +
                                       $"list={AssetList.SelectedItems.Count} firstStill={ViewModel.Items.FirstOrDefault()?.IsSelected}");
                });
            });
        }

        // 验收用：STJ_DEMO_SELECT_SCROLL=1 选中第 1 张 → 滚到下面 → 再选中第 N 张，检查第 1 张是否还在
        if (Environment.GetEnvironmentVariable("STJ_DEMO_SELECT_SCROLL") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var first = ViewModel.Items.FirstOrDefault();
                    if (first is null) return;
                    first.IsSelected = true;
                    AssetList.SelectedItems.Add(first);
                    Core.Util.Log.Info($"[select-scroll] after first: list={AssetList.SelectedItems.Count} vm={ViewModel.SelectedItems.Count}");

                    var scroller = FindDescendant<ScrollViewer>(AssetList);
                    scroller?.ChangeView(null, 1200, null, true);
                });

                await Task.Delay(1200);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var visible = ViewModel.Items.FirstOrDefault(i => AssetList.ContainerFromItem(i) is not null && !i.IsSelected);
                    if (visible is null) return;
                    visible.IsSelected = true;
                    AssetList.SelectedItems.Add(visible);
                    Core.Util.Log.Info($"[select-scroll] after scroll+second: list={AssetList.SelectedItems.Count} vm={ViewModel.SelectedItems.Count} " +
                                       $"firstStillSelected={ViewModel.Items.FirstOrDefault()?.IsSelected}");
                });
            });
        }

        // 验收用：STJ_DEMO_SELECT_TEST=1 框选 2 张后再"Ctrl 单击"追加 1 张，检查旧选中是否保留
        if (Environment.GetEnvironmentVariable("STJ_DEMO_SELECT_TEST") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var rect = new Windows.Foundation.Rect(456, 250, 760, 470);
                    ShowMarquee(rect);
                    ApplyMarqueeSelection(rect);
                });

                await Task.Delay(1200);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var afterMarquee = ViewModel.SelectedItems.Count;
                    var extra = ViewModel.Items.FirstOrDefault(i => !i.IsSelected);
                    if (extra is null) return;
                    extra.IsSelected = true;                 // 模拟 Ctrl+单击追加
                    AssetList.SelectedItems.Add(extra);
                    var afterCtrl = ViewModel.SelectedItems.Count;
                    var listCount = AssetList.SelectedItems.Count;
                    Core.Util.Log.Info($"[select-test] marquee={afterMarquee} ctrl={afterCtrl} list={listCount} " +
                                       $"keep={afterCtrl > afterMarquee} sync={(listCount == afterCtrl)}");
                });
            });
        }

        // 截图用：STJ_DEMO_MARQUEE=1 模拟一次框选（画框 + 选中框内卡片）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_MARQUEE") == "1")
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var rect = new Windows.Foundation.Rect(456, 250, 760, 470);
                    ShowMarquee(rect);
                    ApplyMarqueeSelection(rect);
                });
            });
        }

        // 截图用：STJ_DEMO_PREVIEW=<素材绝对路径> 直接打开全屏预览（验证视频代理等）
        if (Environment.GetEnvironmentVariable("STJ_DEMO_PREVIEW") is { Length: > 0 } demoPreviewPath)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                try
                {
                    var asset = await AppServices.Store.GetByPathAsync(demoPreviewPath);
                    if (asset is not null)
                    {
                        DispatcherQueue.TryEnqueue(() => ShellPage.Current?.ShowPreview(new[] { asset }, 0));

                        // 截图用：STJ_DEMO_PREVIEW_ROTATE=90 时打开预览后自动旋转，验证工具条和变换
                        if (int.TryParse(Environment.GetEnvironmentVariable("STJ_DEMO_PREVIEW_ROTATE"), out var rotate) && rotate != 0)
                        {
                            await Task.Delay(1500);
                            DispatcherQueue.TryEnqueue(() => _ = PreviewViewModel.Shared.RotateAsync(rotate));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("demo preview failed", ex);
                }
            });
        }

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

    // ==================================================================================
    //  影像合集：合集级编辑入口（批量编辑 / 查看元数据 / 重命名 / 删除 / 全选）
    // ==================================================================================
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.SelectAllLoaded();
        AppEvents.SetStatus(count > 0 ? $"已全选 {count} 张" : "当前清单为空");
    }

    private async void CollectionSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveCollectionId is not { } id) return;
        var assets = await ViewModel.LoadCollectionAssetsAsync(id);
        if (assets.Count == 0) { AppEvents.SetStatus("这个合集里还没有素材"); return; }

        // 只选中当前网格里已经加载到的那些（不在页内的用临时卡片补齐，供批量填写使用）
        var ids = assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var cards = ViewModel.Items.Where(i => ids.Contains(i.Id)).ToList();
        ViewModel.SetSelection(cards);
        AppEvents.SetStatus($"已选中合集内 {cards.Count} / {assets.Count} 张");
    }

    private async void CollectionBatchEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveCollectionId is not { } id) return;
        var assets = await ViewModel.LoadCollectionAssetsAsync(id);
        if (assets.Count == 0) { AppEvents.SetStatus("这个合集里还没有素材"); return; }
        BatchEditViewModel.PendingAssets = assets;
        Frame.Navigate(typeof(BatchEditPage));
    }

    private async void CollectionMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveCollectionId is not { } id) return;
        var assets = await ViewModel.LoadCollectionAssetsAsync(id);
        if (assets.Count == 0) { AppEvents.SetStatus("这个合集里还没有素材"); return; }

        var ids = assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var cards = ViewModel.Items.Where(i => ids.Contains(i.Id)).ToList();
        if (cards.Count > 0)
        {
            ViewModel.SetSelection(cards);
            InspectorToggle.IsChecked = true;
            InspectorViewModel.Shared.Show(cards[0].Asset);
            ApplyInspectorVisibility(true);
            AppEvents.SetStatus($"检视面板：{cards[0].Asset.FileName}（合集共 {assets.Count} 张）");
        }
        else
        {
            AppEvents.SetStatus("合集素材不在当前清单里：先清空筛选或切到对应素材库");
        }
    }

    private async void CollectionRename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveCollectionId is not { } id) return;
        var current = ViewModel.Collections.FirstOrDefault(c => c.Id == id);
        var box = new TextBox { Text = current?.DisplayName ?? "", PlaceholderText = "合集名称", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = "重命名影像合集",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await AppServices.Store.RenameCollectionAsync(id, box.Text);
        AppEvents.SetStatus($"合集已重命名为「{box.Text}」");
        await ViewModel.RefreshCollectionsAsync();
    }

    private async void CollectionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveCollectionId is not { } id) return;
        var current = ViewModel.Collections.FirstOrDefault(c => c.Id == id);
        var confirm = new ContentDialog
        {
            Title = "删除影像合集",
            Content = $"确定删除「{current?.DisplayName ?? "这个合集"}」吗？\n只删合集归类，不会删除素材文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await AppServices.Store.DeleteCollectionAsync(id);
        ViewModel.CollectionGroup.Clear();
        AppEvents.SetStatus("合集已删除（素材都还在）");
        await ViewModel.RefreshCollectionsAsync();
    }

    // ==================================================================================
    //  删除废片：先移入系统回收站，再从索引库移除
    // ==================================================================================
    private async void DeleteRejects_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rejects = await ViewModel.LoadRejectedAsync();
            if (rejects.Count == 0)
            {
                AppEvents.SetStatus("当前没有标记为「废片」的素材");
                return;
            }

            var bytes = rejects.Sum(a => a.SizeBytes);
            var ok = await AppServices.Dialogs.ConfirmAsync(
                "删除废片",
                $"把 {rejects.Count} 个标记为「废片」的素材移入系统回收站（约 {Formatting.FileSize(bytes)}），并从索引库移除。\n\n" +
                "回收站里的文件可以还原；索引记录删除后需要重新扫描才能恢复。",
                "移入回收站", "取消");
            if (!ok) return;

            await DeleteAssetsToRecycleBinAsync(rejects, "废片");
        }
        catch (Exception ex)
        {
            Log.Warn("删除废片失败", ex);
            AppEvents.SetError($"删除废片失败：{ex.Message}");
        }
    }

    /// <summary>把一批素材移到回收站，然后清理索引并刷新清单。</summary>
    private async Task DeleteAssetsToRecycleBinAsync(IReadOnlyList<MediaAsset> assets, string label)
    {
        if (assets.Count == 0) return;

        var deleted = await AppServices.Operations.DeleteToRecycleBinAsync(assets);
        // 不管文件是否全部成功，索引里已经不在磁盘上的记录都要清掉，避免幽灵条目
        try
        {
            var gone = assets.Where(a => !File.Exists(a.FullPath)).Select(a => a.Id).ToList();
            if (gone.Count > 0) await AppServices.Store.DeleteAsync(gone);
        }
        catch (Exception ex)
        {
            Log.Warn("清理已删除素材的索引失败", ex);
        }

        AppEvents.RaiseLibraryContentChanged();
        ViewModel.RequestRefresh(immediate: true);
        await ViewModel.RefreshCollectionsAsync();
        await ViewModel.RefreshMergeGroupsAsync();

        AppEvents.SetStatus(deleted >= assets.Count
            ? $"已把 {deleted} 个{label}移入回收站"
            : $"已移入回收站 {deleted} / {assets.Count} 个{label}（其余可能已被占用或已删除）");
    }

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
    // ==================================================================================
    //  框选（像资源管理器那样：在空白处按下 → 拖出选择框 → 框到的卡片全选中）
    // ==================================================================================

    private bool _marqueeActive;
    private bool _marqueeAdditive;
    private Windows.Foundation.Point _marqueeStart;
    private readonly List<AssetCardItem> _marqueeBase = new();
    /// <summary>本次框选累计命中的卡片：列表往下滚动时，上面的容器会被回收，不能只按当前视口算。</summary>
    private readonly HashSet<AssetCardItem> _marqueeHits = new();
    private ScrollViewer? _assetListScroller;

    private void AssetList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(AssetList);
            if (!point.Properties.IsLeftButtonPressed) return;
            if (IsOnScrollBar(e.OriginalSource as DependencyObject)) return;
            var pointInCanvas = e.GetCurrentPoint(MarqueeCanvas).Position;

            // 卡片之间的空隙（container 的外边距）也算"空白处"，从那里按下就拖框；
            // 卡面本身仍是普通点选。注意不要用 Alt：Alt+拖动是 Windows 的"移动窗口"手势。
            if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } pressedItem)
            {
                const double cardMargin = 6;   // CardTemplate 里卡片的 Margin
                var bounds = pressedItem.TransformToVisual(MarqueeCanvas)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, pressedItem.ActualWidth, pressedItem.ActualHeight));
                var inCard = pointInCanvas.X >= bounds.X + cardMargin &&
                             pointInCanvas.X <= bounds.X + bounds.Width - cardMargin &&
                             pointInCanvas.Y >= bounds.Y + cardMargin &&
                             pointInCanvas.Y <= bounds.Y + bounds.Height - cardMargin;
                if (inCard) return;
            }

            _marqueeActive = true;
            _marqueeAdditive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
            _marqueeStart = e.GetCurrentPoint(MarqueeCanvas).Position;

            _marqueeBase.Clear();
            _marqueeHits.Clear();
            if (_marqueeAdditive) _marqueeBase.AddRange(ViewModel.SelectedItems);

            ShowMarquee(new Windows.Foundation.Rect(_marqueeStart, _marqueeStart));
            AssetList.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("框选按下失败", ex);
        }
    }

    private void AssetList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        try
        {
            var pos = e.GetCurrentPoint(MarqueeCanvas).Position;
            var rect = NormalizeRect(_marqueeStart, pos);
            MarqueeRect.Width = rect.Width;
            MarqueeRect.Height = rect.Height;
            Canvas.SetLeft(MarqueeRect, rect.X);
            Canvas.SetTop(MarqueeRect, rect.Y);

            ApplyMarqueeSelection(rect);
            AutoScrollOnEdge(e.GetCurrentPoint(AssetList).Position);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("框选移动失败", ex);
        }
    }

    private void AssetList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        try
        {
            EndMarquee();
            AssetList.ReleasePointerCapture(e.Pointer);
        }
        catch { /* 指针已释放 */ }
        e.Handled = true;
    }

    private void AssetList_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        EndMarquee();
    }

    private void EndMarquee()
    {
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
    }

    private void ShowMarquee(Windows.Foundation.Rect rect)
    {
        MarqueeRect.Visibility = Visibility.Visible;
        MarqueeRect.Width = Math.Max(1, rect.Width);
        MarqueeRect.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(MarqueeRect, rect.X);
        Canvas.SetTop(MarqueeRect, rect.Y);
    }

    private static Windows.Foundation.Rect NormalizeRect(Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Windows.Foundation.Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>把与框选矩形相交的（已实现出来的）卡片选上。</summary>
    private void ApplyMarqueeSelection(Windows.Foundation.Rect rect)
    {
        // 先并入本次拖动已经命中过的（可能已滚出视口、容器被回收）
        var selected = new List<AssetCardItem>(_marqueeBase);
        foreach (var item in _marqueeHits)
            if (!selected.Contains(item)) selected.Add(item);
        for (int i = 0; i < ViewModel.Items.Count; i++)
        {
            if (AssetList.ContainerFromIndex(i) is not ListViewItem container) continue;
            if (container.ActualWidth <= 0 || container.ActualHeight <= 0) continue;

            Windows.Foundation.Rect bounds;
            try
            {
                bounds = container.TransformToVisual(MarqueeCanvas)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch
            {
                continue;
            }

            var intersects = rect.X < bounds.X + bounds.Width && rect.X + rect.Width > bounds.X &&
                             rect.Y < bounds.Y + bounds.Height && rect.Y + rect.Height > bounds.Y;
            if (!intersects) continue;
            var item = ViewModel.Items[i];
            _marqueeHits.Add(item);
            if (!selected.Contains(item)) selected.Add(item);
        }

        if (selected.Count == ViewModel.SelectedItems.Count &&
            selected.SequenceEqual(ViewModel.SelectedItems))
            return;

        ViewModel.SetSelection(selected);
        SyncListViewSelection(selected);
    }

    /// <summary>
    /// 把选中集同步进 ListView。
    /// 关键：框选只写了 ViewModel 的选中集，ListView 自己还是空的；
    /// 之后按 Ctrl 单击时 ListView 会用"自己的"选中集去追加，于是把框选结果全丢掉。
    /// </summary>
    private void SyncListViewSelection(IReadOnlyList<AssetCardItem> selected)
    {
        if (_syncingListSelection) return;
        _syncingListSelection = true;
        try
        {
            var wanted = selected.ToHashSet();
            foreach (var item in AssetList.SelectedItems.OfType<AssetCardItem>().ToList())
                if (!wanted.Contains(item)) AssetList.SelectedItems.Remove(item);

            var present = AssetList.SelectedItems.OfType<AssetCardItem>().ToHashSet();
            foreach (var item in selected)
                if (!present.Contains(item)) AssetList.SelectedItems.Add(item);
        }
        catch (Exception ex)
        {
            Log.Warn("同步列表选中失败", ex);
        }
        finally
        {
            _syncingListSelection = false;
        }
    }

    private bool _syncingListSelection;

    /// <summary>拖到列表上下边缘时自动滚动（资源管理器的手感）。</summary>
    private void AutoScrollOnEdge(Windows.Foundation.Point positionInList)
    {
        _assetListScroller ??= FindDescendant<ScrollViewer>(AssetList);
        if (_assetListScroller is null) return;

        const double edge = 28;
        const double step = 24;
        if (positionInList.Y < edge)
            _assetListScroller.ChangeView(null, Math.Max(0, _assetListScroller.VerticalOffset - step), null, true);
        else if (positionInList.Y > AssetList.ActualHeight - edge)
            _assetListScroller.ChangeView(null, _assetListScroller.VerticalOffset + step, null, true);
    }

    private static bool IsOnScrollBar(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar) return true;
            if (node is ListView) return false;
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingListSelection) return;
        var selected = AssetList.SelectedItems.OfType<AssetCardItem>().ToList();
        ViewModel.SetSelection(selected);
    }

    private void AssetList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = FindItemAt(e.OriginalSource);
        if (item is null) return;
        if (item.IsMergeGroupCard && item.MergeGroup is { } group)
        {
            OpenMergeGroupCard(group);
            return;
        }
        OpenPreview(item);
    }

    /// <summary>打开合并组：切到"合并组"筛选，只显示组内成员（此时不折叠，可逐张操作）。</summary>
    private double _mergeGroupBackScroll;

    private void OpenMergeGroupCard(MergeGroup group)
    {
        _mergeGroupBackScroll = FindDescendant<ScrollViewer>(AssetList)?.VerticalOffset ?? 0;
        ViewModel.OpenMergeGroup(group.Id);
        AppEvents.SetStatus($"已打开合并组「{group.DisplayName}」（{group.AssetCount} 张）");
    }

    /// <summary>「返回」：回到打开合并组之前的视图（筛选 + 滚动位置）。</summary>
    private void MergeGroupBack_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PopViewState();
        AppEvents.SetStatus("已返回上一步视图");
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(400);   // 等清单按恢复后的筛选刷新完
            try
            {
                FindDescendant<ScrollViewer>(AssetList)?.ChangeView(null, _mergeGroupBackScroll, null, true);
            }
            catch { }
        });
    }

    private void AssetList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.A when IsCtrlDown():
                SelectAll_Click(sender, e);
                e.Handled = true;
                break;
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

    private static bool IsCtrlDown()
    {
        try
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch { return false; }
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
    // ==================================================================================
    //  合并组：把多重曝光 / 星轨 / 演示视频等合并成一个大文件夹（封面可自定义）
    // ==================================================================================
    private async void MergeGroups_Click(object sender, RoutedEventArgs e)
        => await ShowMergeGroupsDialogAsync(ViewModel.SelectedItems.Select(i => i.Asset).ToList());

    private async Task DissolveMergeGroupAsync(MergeGroup group)
    {
        await AppServices.Store.DeleteMergeGroupAsync(group.Id);
        AppEvents.SetStatus($"已解散合并组「{group.DisplayName}」（素材都还在）");
        await ViewModel.RefreshMergeGroupsAsync();
    }

    /// <summary>把当前素材所在合并组的封面设为它。</summary>
    private async Task SetMergeCoverAsync(MediaAsset asset)
    {
        var membership = await AppServices.Store.GetMergeMembershipAsync(new[] { asset.Id });
        if (!membership.TryGetValue(asset.Id, out var groupId))
        {
            AppEvents.SetStatus("这张素材不在任何合并组里");
            return;
        }
        await AppServices.Store.SetMergeGroupCoverAsync(groupId, asset.Id);
        var group = (await AppServices.Store.GetMergeGroupsAsync()).FirstOrDefault(g => g.Id == groupId);
        AppEvents.SetStatus($"已把「{asset.FileName}」设为「{group?.DisplayName ?? "合并组"}」的封面");
        await ViewModel.RefreshMergeGroupsAsync();
    }

    private async Task LeaveMergeGroupAsync(MediaAsset asset)
    {
        var membership = await AppServices.Store.GetMergeMembershipAsync(new[] { asset.Id });
        if (!membership.TryGetValue(asset.Id, out var groupId))
        {
            AppEvents.SetStatus("这张素材不在任何合并组里");
            return;
        }
        await AppServices.Store.RemoveFromMergeGroupAsync(groupId, new[] { asset.Id });
        AppEvents.SetStatus($"已把「{asset.FileName}」移出合并组");
        await ViewModel.RefreshMergeGroupsAsync();
    }

    private async Task ShowMergeGroupsDialogAsync(IReadOnlyList<MediaAsset> targets)
    {
        try
        {
            var groups = (await AppServices.Store.GetMergeGroupsAsync()).ToList();
            var panel = new StackPanel { Spacing = 10, MinWidth = 470 };

            panel.Children.Add(new TextBlock
            {
                Text = targets.Count >= 2
                    ? $"把选中的 {targets.Count} 张合并成一个大文件夹；封面默认第一张，之后可右键任意成员「设为合并组封面」。"
                    : "选中 2 张以上素材才能合并；封面默认第一张，之后可右键任意成员「设为合并组封面」。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["AppCaptionTextStyle"],
            });

            // —— 新建 ——
            var createRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var nameBox = new TextBox { PlaceholderText = "合并组名称，例如「多重曝光 1109」", Width = 280 };
            var createButton = new Button { Content = "合并所选", IsEnabled = targets.Count >= 2 };
            createRow.Children.Add(nameBox);
            createRow.Children.Add(createButton);
            panel.Children.Add(createRow);

            var listPanel = new StackPanel { Spacing = 6 };
            panel.Children.Add(listPanel);

            void BuildRow(MergeGroup group)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var box = new TextBox { Text = group.DisplayName, MinHeight = 30 };
                Grid.SetColumn(box, 0);
                row.Children.Add(box);

                var countText = new TextBlock
                {
                    Text = $"{group.AssetCount} 张",
                    Style = (Style)Application.Current.Resources["AppMicroTextStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 52,
                };
                Grid.SetColumn(countText, 1);
                row.Children.Add(countText);

                var openButton = new Button { Content = "打开", MinHeight = 28 };
                openButton.Click += (_, _) => OpenMergeGroupCard(group);
                Grid.SetColumn(openButton, 2);
                row.Children.Add(openButton);

                var defaultCoverButton = new Button { Content = "默认封面", MinHeight = 28 };
                defaultCoverButton.Click += async (_, _) =>
                {
                    await AppServices.Store.SetMergeGroupCoverAsync(group.Id, null);
                    AppEvents.SetStatus($"「{group.DisplayName}」的封面已恢复为第一张");
                    await ViewModel.RefreshMergeGroupsAsync();
                };
                Grid.SetColumn(defaultCoverButton, 3);
                row.Children.Add(defaultCoverButton);

                var deleteButton = new Button { Content = "解散", MinHeight = 28 };
                deleteButton.Click += async (_, _) =>
                {
                    await AppServices.Store.DeleteMergeGroupAsync(group.Id);
                    AppEvents.SetStatus($"已解散合并组「{group.DisplayName}」（素材都还在）");
                    listPanel.Children.Remove(row);
                    await ViewModel.RefreshMergeGroupsAsync();
                };
                Grid.SetColumn(deleteButton, 6);
                row.Children.Add(deleteButton);

                listPanel.Children.Add(row);
                row.Tag = group;
            }

            foreach (var g in groups) BuildRow(g);

            createButton.Click += async (_, _) =>
            {
                if (targets.Count < 2) return;
                var created = await AppServices.Store.CreateMergeGroupAsync(nameBox.Text, targets.Select(t => t.Id));
                nameBox.Text = "";
                AppEvents.SetStatus($"已合并 {created.AssetCount} 张为「{created.Name}」");
                BuildRow(created);
                await ViewModel.RefreshMergeGroupsAsync();
            };

            var dialog = new ContentDialog
            {
                Title = "合并为大文件夹",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "保存名称",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                foreach (var child in listPanel.Children)
                {
                    if (child is not Grid row || row.Tag is not MergeGroup group) continue;
                    if (row.Children[0] is not TextBox box) continue;
                    if (!string.Equals(group.DisplayName, box.Text, StringComparison.Ordinal))
                        await AppServices.Store.RenameMergeGroupAsync(group.Id, box.Text);
                }
                AppEvents.SetStatus("合并组名称已保存");
            }

            await ViewModel.RefreshMergeGroupsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("合并组对话框失败", ex);
            AppEvents.SetError($"合并组操作失败：{ex.Message}");
        }
    }

    // ==================================================================================
    //  影像合集：创建 / 重命名 / 删除 / 加入 / 移出
    // ==================================================================================
    /// <summary>筛选栏的「+」：直接打开合集弹窗（新建 / 管理）。</summary>
    private async void NewCollection_Click(object sender, RoutedEventArgs e)
        => await ShowCollectionsDialogAsync(ViewModel.SelectedItems.Select(i => i.Asset).ToList());

    private async void Collections_Click(object sender, RoutedEventArgs e)
        => await ShowCollectionsDialogAsync(ViewModel.SelectedItems.Select(i => i.Asset).ToList());

    private async Task ShowCollectionsDialogAsync(IReadOnlyList<MediaAsset> targets)
    {
        try
        {
            IReadOnlyList<Collection> collections = await AppServices.Store.GetCollectionsAsync();
            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };

            // —— 新建合集 ——
            var createRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var nameBox = new TextBox { PlaceholderText = "新合集名称，例如「2026 婚礼精选」", Width = 280 };
            var createButton = new Button { Content = "创建合集" };
            createRow.Children.Add(nameBox);
            createRow.Children.Add(createButton);
            panel.Children.Add(new TextBlock
            {
                Text = targets.Count > 0
                    ? $"管理影像合集；当前选中 {targets.Count} 张素材，可直接加入。"
                    : "管理影像合集：新建 / 重命名 / 删除；在素材库选中素材后可一键加入。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["AppCaptionTextStyle"],
            });
            panel.Children.Add(createRow);

            var listPanel = new StackPanel { Spacing = 6 };
            panel.Children.Add(listPanel);

            void BuildRow(Collection collection)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var box = new TextBox { Text = collection.Name, Tag = collection.Id, MinHeight = 30 };
                Grid.SetColumn(box, 0);
                row.Children.Add(box);

                var countText = new TextBlock
                {
                    Text = $"{collection.AssetCount} 张",
                    Style = (Style)Application.Current.Resources["AppMicroTextStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 56,
                };
                Grid.SetColumn(countText, 1);
                row.Children.Add(countText);

                var openButton = new Button { Content = "打开", MinHeight = 28 };
                openButton.Click += (_, _) =>
                {
                    ViewModel.OpenCollection(collection.Id);
                    AppEvents.SetStatus($"已打开影像合集「{collection.DisplayName}」");
                };
                Grid.SetColumn(openButton, 2);
                row.Children.Add(openButton);

                var batchButton = new Button { Content = "批量编辑", MinHeight = 28 };
                batchButton.Click += async (_, _) =>
                {
                    var assets = await ViewModel.LoadCollectionAssetsAsync(collection.Id);
                    if (assets.Count == 0) { AppEvents.SetStatus("这个合集里还没有素材"); return; }
                    BatchEditViewModel.PendingAssets = assets;
                    Frame.Navigate(typeof(BatchEditPage));
                };
                Grid.SetColumn(batchButton, 3);
                row.Children.Add(batchButton);

                var addButton = new Button { Content = "加入所选", IsEnabled = targets.Count > 0, MinHeight = 28 };
                addButton.Click += async (_, _) =>
                {
                    var added = await AppServices.Store.AddToCollectionAsync(collection.Id, targets.Select(t => t.Id));
                    AppEvents.SetStatus($"已把 {added} 张素材加入「{collection.Name}」");
                    await ViewModel.RefreshCollectionsAsync();
                    collection.AssetCount += added;
                    countText.Text = $"{collection.AssetCount} 张";
                };
                Grid.SetColumn(addButton, 4);
                row.Children.Add(addButton);

                var removeButton = new Button { Content = "移出所选", IsEnabled = targets.Count > 0, MinHeight = 28 };
                removeButton.Click += async (_, _) =>
                {
                    var removed = await AppServices.Store.RemoveFromCollectionAsync(collection.Id, targets.Select(t => t.Id));
                    AppEvents.SetStatus($"已从「{collection.Name}」移出 {removed} 张素材");
                    await ViewModel.RefreshCollectionsAsync();
                    collection.AssetCount = Math.Max(0, collection.AssetCount - removed);
                    countText.Text = $"{collection.AssetCount} 张";
                };
                Grid.SetColumn(removeButton, 5);
                row.Children.Add(removeButton);

                var deleteButton = new Button { Content = "删除", MinHeight = 28 };
                deleteButton.Click += async (_, _) =>
                {
                    var confirm = new ContentDialog
                    {
                        Title = "删除合集",
                        Content = $"确定删除「{collection.Name}」吗？\n只删合集归类，不会删除素材文件。",
                        PrimaryButtonText = "删除",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot,
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                    await AppServices.Store.DeleteCollectionAsync(collection.Id);
                    AppEvents.SetStatus($"已删除合集「{collection.Name}」");
                    await ViewModel.RefreshCollectionsAsync();
                    listPanel.Children.Remove(row);
                };
                Grid.SetColumn(deleteButton, 4);
                row.Children.Add(deleteButton);

                listPanel.Children.Add(row);
            }

            foreach (var collection in collections) BuildRow(collection);

            createButton.Click += async (_, _) =>
            {
                var created = await AppServices.Store.CreateCollectionAsync(nameBox.Text);
                nameBox.Text = "";
                AppEvents.SetStatus($"已创建合集「{created.Name}」");
                created.AssetCount = 0;
                BuildRow(created);
                await ViewModel.RefreshCollectionsAsync();
            };

            var dialog = new ContentDialog
            {
                Title = "影像合集",
                Content = new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "保存名称",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                foreach (var child in listPanel.Children)
                {
                    if (child is not Grid row || row.Children.Count == 0) continue;
                    if (row.Children[0] is not TextBox box || box.Tag is not string id) continue;
                    var original = collections.FirstOrDefault(c => c.Id == id);
                    if (original is not null && !string.Equals(original.Name, box.Text, StringComparison.Ordinal))
                        await AppServices.Store.RenameCollectionAsync(id, box.Text);
                }
                AppEvents.SetStatus("合集名称已保存");
            }

            await ViewModel.RefreshCollectionsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("影像合集对话框失败", ex);
            AppEvents.SetError($"影像合集操作失败：{ex.Message}");
        }
    }

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

        if (item.IsMergeGroupCard && item.MergeGroup is { } mergeGroup)
        {
            var openGroup = new MenuFlyoutItem { Text = $"打开合并组「{mergeGroup.DisplayName}」" };
            openGroup.Click += (_, _) => OpenMergeGroupCard(mergeGroup);
            flyout.Items.Add(openGroup);

            var dissolve = new MenuFlyoutItem { Text = "解散合并组（不删素材）" };
            dissolve.Click += async (_, _) => await DissolveMergeGroupAsync(mergeGroup);
            flyout.Items.Add(dissolve);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var mergeSelected = new MenuFlyoutItem { Text = "合并所选为一个大文件夹…" };
        mergeSelected.Click += async (_, _) => await ShowMergeGroupsDialogAsync(TargetAssets(item));
        flyout.Items.Add(mergeSelected);

        if (!item.IsMergeGroupCard)
        {
            var coverItem = new MenuFlyoutItem { Text = "设为合并组封面" };
            coverItem.Click += async (_, _) => await SetMergeCoverAsync(item.Asset);
            flyout.Items.Add(coverItem);

            var leaveGroup = new MenuFlyoutItem { Text = "移出合并组" };
            leaveGroup.Click += async (_, _) => await LeaveMergeGroupAsync(item.Asset);
            flyout.Items.Add(leaveGroup);
        }

        var deleteItem = new MenuFlyoutItem { Text = "删除到回收站（所选）" };
        deleteItem.Click += async (_, _) =>
        {
            var targets = TargetAssets(item);
            if (targets.Count == 0) return;
            var bytes = targets.Sum(a => a.SizeBytes);
            var ok = await AppServices.Dialogs.ConfirmAsync(
                "删除到回收站",
                $"把 {targets.Count} 个文件移入系统回收站（约 {Formatting.FileSize(bytes)}），并从索引库移除。\n\n回收站里可以还原。",
                "移入回收站", "取消");
            if (!ok) return;
            await DeleteAssetsToRecycleBinAsync(targets, "文件");
        };
        flyout.Items.Add(deleteItem);

        var collectionItem = new MenuFlyoutItem { Text = "加入影像合集…" };
        collectionItem.Click += async (_, _) => await ShowCollectionsDialogAsync(TargetAssets(item));
        flyout.Items.Add(collectionItem);

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