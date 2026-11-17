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
        Loaded += (_, _) => ApplyDemoScroll();
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

    private async void LoadDeviceAliases_Click(object sender, RoutedEventArgs e)
        => await ViewModel.LoadDeviceAliasesAsync();

    private async void SaveDeviceAliases_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SaveDeviceAliasesAsync();

    private async void LoadDeviceLuts_Click(object sender, RoutedEventArgs e)
        => await ViewModel.LoadDeviceLutBindingsAsync();

    private void SuggestSonyLuts_Click(object sender, RoutedEventArgs e) => ViewModel.SuggestSonyLuts();

    private async void SaveDeviceLuts_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SaveDeviceLutBindingsAsync();

    private void ResetDeviceLuts_Click(object sender, RoutedEventArgs e) => ViewModel.ResetDeviceLutBindings();

    private void ResetDeviceAliases_Click(object sender, RoutedEventArgs e)
        => ViewModel.ResetDeviceAliases();

    private async void RelocateLibrary_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LibraryItemViewModel item) return;
        try
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await ViewModel.RelocateLibraryAsync(item, folder.Path);
        }
        catch (Exception ex)
        {
            Log.Warn("选择新目录失败", ex);
            AppEvents.SetError($"选择目录失败：{ex.Message}");
        }
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

    /// <summary>截图/验收用：STJ_DEMO_SETTINGS_CACHE=1 滚到缓存区；STJ_DEMO_SETTINGS_DEVICES=1 滚到设备名称区。</summary>
    private void ApplyDemoScroll()
    {
        var cache = Environment.GetEnvironmentVariable("STJ_DEMO_SETTINGS_CACHE") == "1";
        var devices = Environment.GetEnvironmentVariable("STJ_DEMO_SETTINGS_DEVICES") == "1";
        var luts = Environment.GetEnvironmentVariable("STJ_DEMO_SETTINGS_LUT") == "1";
        if (!cache && !devices && !luts) return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1600);
            if (FindScrollViewer(this) is not { } scroll) return;
            if (cache)
            {
                scroll.ChangeView(null, scroll.ScrollableHeight, null);
                return;
            }

            // 设备名称卡片：按"设备"标题找到它在滚动内容里的位置
            if (FindTextBlock(this, luts ? "设备 ↔ LUT 绑定" : "设备名称") is { } heading && scroll.Content is UIElement content)
            {
                try
                {
                    var transform = heading.TransformToVisual(content);
                    var offset = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    scroll.ChangeView(null, Math.Max(0, offset - 40), null, true);
                }
                catch { }
            }
        });
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && tb.Text == text) return tb;
            if (FindTextBlock(child, text) is { } found) return found;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearCacheAsync();

    private async void PickCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ViewModel.CachePath = folder.Path;
            ViewModel.ScheduleSave();
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("选择缓存目录失败", ex);
        }
    }

    private void ResetCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetCachePath();
        ViewModel.ScheduleSave();
    }

    private async void CleanCacheNow_Click(object sender, RoutedEventArgs e) => await ViewModel.CleanCacheNowAsync();

    private void ReplayOnboarding_Click(object sender, RoutedEventArgs e) => ViewModel.ReplayOnboarding();

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