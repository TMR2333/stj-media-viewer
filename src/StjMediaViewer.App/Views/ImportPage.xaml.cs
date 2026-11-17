using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;
using Windows.Storage.Pickers;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 导入页：设备 → 计划 → 执行 → 报告 → 导出核验。
/// 页面只做「取事件 → 转给 <see cref="ImportViewModel.Shared"/> → 系统对话框 / 资源管理器」，
/// 所有业务与异步状态都在 ViewModel 里，避免页面被 Frame 重建时丢状态。
/// </summary>
public sealed partial class ImportPage : Page
{
    private bool _loadedOnce;

    public ImportPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ImportViewModel ViewModel => ImportViewModel.Shared;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageLayout.Fit(PageScroll, PageContent);

        // 宽窗口把「设备区 / 导入设置」并排，窄窗口堆叠
        HookTopLayout();
        if (_loadedOnce) return;
        _loadedOnce = true;
        await ViewModel.InitializeAsync();

        // 截图/验收用：STJ_DEMO_IMPORT_GEO=1 时把子目录模板切到「按细分位置」
        if (Environment.GetEnvironmentVariable("STJ_DEMO_IMPORT_GEO") == "1")
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var index = TemplateBox.Items
                    .Select((item, i) => (item, i))
                    .Where(x => x.item is ComboBoxItem { Tag: "{geo}" })
                    .Select(x => x.i)
                    .DefaultIfEmpty(-1)
                    .First();
                if (index >= 0) TemplateBox.SelectedIndex = index;

                // 展开「导入设置」并滚到子目录模板附近，方便截图验收
                await Task.Delay(600);
                var expander = FindAncestor<Expander>(TemplateBox);
                if (expander is not null) expander.IsExpanded = true;
                await Task.Delay(400);
                if (FindDescendant<ScrollViewer>(this) is { } scroll)
                    scroll.ChangeView(null, 620, null);
            });
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.CancelThumbnails();

    // ==================================================================================
    //  ① 设备区
    // ==================================================================================
    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshDevicesAsync();

    private async void RefreshDevices_Action(object? sender, EventArgs e) => await ViewModel.RefreshDevicesAsync();

    private async void ScanDevice_Click(object sender, RoutedEventArgs e)
    {
        // ItemsRepeater 的 DataTemplate 里用 {x:Bind} 把整项塞进 CommandParameter，
        // 比依赖 DataContext 更稳（不随虚拟化容器的复用状态变化）。
        var card = (sender as Button)?.CommandParameter as ImportDeviceCard
                   ?? (sender as FrameworkElement)?.DataContext as ImportDeviceCard;
        if (card is null) return;
        // 相机卡优先扫 DCIM 子目录（更聚焦，少扫无关文件），否则扫盘根。
        await ViewModel.BuildPlanForPathAsync(card.ScanRoot);
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("选择要导入的文件夹");
        if (!string.IsNullOrEmpty(folder)) await ViewModel.BuildPlanForPathAsync(folder);
    }

    // ==================================================================================
    //  ② 计划区
    // ==================================================================================
    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNone();

    private void SelectPairs_Click(object sender, RoutedEventArgs e) => ViewModel.SelectPairsOnly();

    private void SelectRawOnly_Click(object sender, RoutedEventArgs e) => ViewModel.SelectRawOnly();

    // ==================================================================================
    //  ③ 设置区
    // ==================================================================================
    private async void ChangeDestination_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("选择导入目标目录");
        if (!string.IsNullOrEmpty(folder)) ViewModel.SetDestinationRoot(folder);
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            ViewModel.SubfolderTemplate = tag;
        }
    }

    // ==================================================================================
    //  ④ 执行 / 报告
    // ==================================================================================
    private async void StartImport_Click(object sender, RoutedEventArgs e) => await ViewModel.StartImportAsync();

    private void CancelImport_Click(object sender, RoutedEventArgs e) => ViewModel.CancelImport();

    private void OpenDestination_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel.LastImportDestination);

    private async void Reimport_Click(object sender, RoutedEventArgs e) => await ViewModel.ReimportAsync();

    /// <summary>把这批刚导入的素材带去批量填写页。</summary>
    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        var paths = ImportViewModel.Shared.LastImportedPaths.ToList();
        if (paths.Count == 0) return;
        _ = LoadPendingIdsAsync(paths);
        Frame.Navigate(typeof(BatchEditPage));
    }

    private static async Task LoadPendingIdsAsync(List<string> paths)
    {
        try
        {
            var ids = await AppServices.Store.GetIdsByPathsAsync(paths);
            BatchEditViewModel.PendingIds.Clear();
            BatchEditViewModel.PendingIds.AddRange(ids);
        }
        catch { /* 页面会回落到图库选择 */ }
    }

    // ==================================================================================
    //  ⑤ 核验导出
    // ==================================================================================
    private async void VerifySource_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("选择核验的源目录");
        if (!string.IsNullOrEmpty(folder)) ViewModel.SetVerifySourceRoot(folder);
    }

    private async void VerifyDestination_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("选择核验的目标目录");
        if (!string.IsNullOrEmpty(folder)) ViewModel.SetVerifyDestinationRoot(folder);
    }

    private async void StartVerify_Click(object sender, RoutedEventArgs e) => await ViewModel.StartVerifyAsync();

    private void CancelVerify_Click(object sender, RoutedEventArgs e) => ViewModel.CancelVerify();

    // ==================================================================================
    //  公共
    // ==================================================================================
    /// <summary>FolderPicker 必须显式关联窗口句柄（WinUI 3 未打包应用）。</summary>
    private static async Task<string?> PickFolderAsync(string caption)
    {
        try
        {
            if (App.MainWindow is null) return null;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Log.Warn($"{caption}失败", ex);
            AppEvents.SetError($"选择文件夹失败：{ex.Message}");
            return null;
        }
    }

    private static void OpenFolder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("[import] 打开目标目录失败", ex);
            AppEvents.SetError("无法打开目标目录");
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        var current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            if (current is T match) return match;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private bool _topLayoutHooked;

    /// <summary>顶部两栏的布局钩子：窗口尺寸 / 首次布局 / 延迟一拍都要重算一次。</summary>
    private void HookTopLayout()
    {
        if (_topLayoutHooked) return;
        _topLayoutHooked = true;
        PageScroll.SizeChanged += (_, _) => ApplyImportTopLayout();
        SizeChanged += (_, _) => ApplyImportTopLayout();
        LayoutUpdated += OnTopLayoutUpdatedOnce;
        DispatcherQueue.TryEnqueue(() => ApplyImportTopLayout());
    }

    private void OnTopLayoutUpdatedOnce(object? sender, object e)
    {
        LayoutUpdated -= OnTopLayoutUpdatedOnce;
        ApplyImportTopLayout();
    }

    /// <summary>导入页顶部两栏自适应：宽窗口左右并排，窄窗口上下堆叠。</summary>
    private void ApplyImportTopLayout()
    {
        try
        {
            var width = Math.Max(PageScroll.ActualWidth, ActualWidth);
            if (width <= 0) return;
            bool wide = width >= 1180;

            var wasWide = ImportTopRightColumn.Width.Value > 0.5;
            if (wide)
            {
                ImportTopRightColumn.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(SettingsCard, 0);
                Grid.SetColumn(SettingsCard, 1);
            }
            else
            {
                ImportTopRightColumn.Width = new GridLength(0);
                Grid.SetRow(SettingsCard, 1);
                Grid.SetColumn(SettingsCard, 0);
            }

            // 跨过断点时让布局变化不那么生硬
            if (wasWide != wide) PageLayout.FadeIn(ImportTopGrid, 160);
        }
        catch { }
    }

}