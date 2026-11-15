using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        if (_loadedOnce) return;
        _loadedOnce = true;
        await ViewModel.InitializeAsync();
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
}