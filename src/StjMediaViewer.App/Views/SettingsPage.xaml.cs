using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;
using Windows.Storage.Pickers;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 设置页：AI 接入（核心）/ 设计语言 / 媒体库 / 工具链 / 关于。
/// 所有输入即时写回 <see cref="SettingsViewModel"/> 并防抖保存；页面不做业务判断。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _loadedOnce;
    private bool _suppressUiEvents;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel => SettingsViewModel.Shared;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;

        _suppressUiEvents = true;
        try
        {
            await ViewModel.LoadAsync();
            ApiKeyBox.Password = ViewModel.ApiKey;
            ThemeModeBox.SelectedIndex = ViewModel.ThemeMode switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    // ==================================================================================
    //  AI 接入
    // ==================================================================================
    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ViewModel.ApplyProviderPreset(id);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        ViewModel.SetApiKey(ApiKeyBox.Password);
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e) => await ViewModel.TestConnectionAsync();

    private void Concurrency_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SetInt(args.NewValue, v => ViewModel.Concurrency = v);

    private void Timeout_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SetInt(args.NewValue, v => ViewModel.TimeoutSeconds = v);

    private void MaxImageDimension_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SetInt(args.NewValue, v => ViewModel.MaxImageDimension = v);

    private void JpegQuality_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SetInt(args.NewValue, v => ViewModel.JpegQuality = v);

    private void DailyLimit_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SetInt(args.NewValue, v => ViewModel.DailyCallLimit = v);

    private void SetInt(double value, Action<int> apply)
    {
        if (_suppressUiEvents) return;
        if (double.IsNaN(value)) return;
        apply((int)Math.Round(value));
    }

    // ==================================================================================
    //  外观（明暗；设计语言已收敛为单一 Fluent）
    // ==================================================================================
    private async void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (ThemeModeBox.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        await ViewModel.ApplyThemeModeAsync(mode);
    }

    // ==================================================================================
    //  媒体库
    // ==================================================================================
    private async void AddLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await ViewModel.AddLibraryAsync(folder.Path);
        }
        catch (Exception ex)
        {
            Log.Warn("选择媒体库文件夹失败", ex);
            AppEvents.SetError($"选择文件夹失败：{ex.Message}");
        }
    }

    private async void RescanLibrary_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItemViewModel item)
            await ViewModel.RescanAsync(item);
    }

    private async void PurgeLibrary_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItemViewModel item)
            await ViewModel.PurgeMissingAsync(item);
    }

    private async void RemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryItemViewModel item)
            await ViewModel.RemoveLibraryAsync(item);
    }

    // ==================================================================================
    //  工具链 / 缓存 / 保存
    // ==================================================================================
    private async void ReloadFfmpeg_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadFfmpegAsync();

    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearCacheAsync();

    private void Warmup_Click(object sender, RoutedEventArgs e)
    {
        try { ViewModel.ToggleWarmup(); }
        catch (Exception ex) { Core.Util.Log.Warn("启动缩略图预热失败", ex); }
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel.AppDataPath, "stj-media-viewer.log");

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ViewModel.AppDataPath);

    private static void OpenFolder(string? path, string? selectFile = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            var args = $"\"{path}\"";
            if (!string.IsNullOrWhiteSpace(selectFile))
            {
                var file = Path.Combine(path, selectFile);
                if (File.Exists(file)) args = $"/select,\"{file}\"";
            }
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开目录失败", ex);
            AppEvents.SetError("无法打开目录");
        }
    }
}