using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>视频 LUT 管理页：内置 LUT / 用户 LUT / 素材预览与分配。</summary>
public sealed partial class LutPage : Page
{
    public LutViewModel ViewModel => LutViewModel.Shared;

    private bool _syncingToggle;

    public LutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _syncingToggle = true;
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("LUT 页面初始化失败", ex);
        }
        finally
        {
            _syncingToggle = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshAsync(forceRefresh: true); }
        catch (Exception ex) { Log.Warn("刷新 LUT 失败", ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".cube");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            if (App.MainWindow is not null)
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await ViewModel.ImportAsync(files.Select(f => f.Path).ToList());
        }
        catch (Exception ex)
        {
            Log.Warn("导入 LUT 失败", ex);
            AppEvents.SetError($"导入 LUT 失败：{ex.Message}");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = ViewModel.UserFolderText;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开 LUT 目录失败", ex);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = await ViewModel.ApplyToSelectionAsync();
            if (n == 0) AppEvents.SetError("先在图库里选中素材，再套用 LUT");
        }
        catch (Exception ex) { Log.Warn("应用 LUT 失败", ex); }
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ClearSelectionAsync(); }
        catch (Exception ex) { Log.Warn("恢复自动匹配失败", ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await ViewModel.DeleteSelectedAsync();
            if (!ok) AppEvents.SetError("内置 LUT 不能删除");
        }
        catch (Exception ex) { Log.Warn("删除 LUT 失败", ex); }
    }

    private async void AutoAssign_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;
        try { await ViewModel.SetAutoAssignAsync(ViewModel.AutoAssignForSony); }
        catch (Exception ex) { Log.Warn("切换自动套用失败", ex); }
    }
}