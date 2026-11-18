using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;
using StjMediaViewer.Core.Watermark;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace StjMediaViewer.App.Views;

/// <summary>水印工作台：品牌字标水印（预览 + 导出 + 批量导出）。</summary>
public sealed partial class WatermarkPage : Page
{
    public WatermarkViewModel ViewModel => WatermarkViewModel.Shared;

    public WatermarkPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var demo = Environment.GetEnvironmentVariable("STJ_DEMO_WATERMARK");
            if (!string.IsNullOrWhiteSpace(demo) && File.Exists(demo))
            {
                await WatermarkViewModel.Shared.LoadAsync(demo);
                // 截图验收用：STJ_DEMO_WATERMARK_BRAND=stjarna|hasselblad|canon|nikon|sony
                var brandName = Environment.GetEnvironmentVariable("STJ_DEMO_WATERMARK_BRAND");
                var brand = WatermarkBrand.Stjarna;
                if (!string.IsNullOrWhiteSpace(brandName) &&
                    Enum.TryParse(brandName, true, out WatermarkBrand parsed))
                {
                    brand = parsed;
                    var option = WatermarkViewModel.Shared.Brands.FirstOrDefault(b => b.Brand == brand);
                    if (option is not null) WatermarkViewModel.Shared.SelectedBrand = option;
                }
                // 截图/验收用：直接导出到指定路径（跳过保存对话框）
                var exportTo = Environment.GetEnvironmentVariable("STJ_DEMO_WATERMARK_EXPORT");
                if (!string.IsNullOrWhiteSpace(exportTo))
                {
                    var summary = Core.Watermark.WatermarkRenderer.RenderToFile(
                        demo, exportTo, new Core.Watermark.WatermarkStyle
                        {
                            Brand = brand,
                            Preset = Core.Watermark.WatermarkPreset.Classic,
                            Size = 20, Opacity = 95, Position = 11,
                        }, Core.Watermark.WatermarkFormat.Jpeg, 92);
                    WatermarkViewModel.Shared.Status = $"演示导出成功：{summary / 1024} KB → {exportTo}";
                }
                return;
            }

            await WatermarkViewModel.Shared.LoadFromLibrarySelectionAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("[watermark] 初始化失败", ex);
        }
    }

    // ==================================================================== 选图

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.ViewMode = PickerViewMode.Thumbnail;
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await ViewModel.LoadAsync(file.Path);
        }
        catch (Exception ex)
        {
            Log.Warn("[watermark] 选择图片失败", ex);
        }
    }

    private async void UseSelection_Click(object sender, RoutedEventArgs e)
        => await ViewModel.LoadFromLibrarySelectionAsync();

    // ==================================================================== 拖放

    private void Preview_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = "松开设为水印素材";
            }
        }
        catch { }
    }

    private async void Preview_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is null) return;
            await ViewModel.LoadAsync(file.Path);
        }
        catch (Exception ex)
        {
            Log.Warn("[watermark] 拖放失败", ex);
        }
    }

    // ==================================================================== 品牌 / 预设 / 墨色

    private void Brand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WatermarkBrand brand }) return;
        var option = ViewModel.Brands.FirstOrDefault(b => b.Brand == brand);
        if (option is not null) ViewModel.SelectedBrand = option;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WatermarkPreset preset }) return;
        var option = ViewModel.Presets.FirstOrDefault(p => p.Preset == preset);
        if (option is not null) ViewModel.SelectedPreset = option;
    }

    private void Ink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WatermarkInk ink }) return;
        var option = ViewModel.Inks.FirstOrDefault(i => i.Ink == ink);
        if (option is not null) ViewModel.SelectedInk = option;
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: WatermarkFormatOption option })
            ViewModel.SelectedFormat = option;
    }

    // ==================================================================== 导出

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await ViewModel.ExportSingleAsync(App.MainWindow);
        ViewModel.Status = message;
        if (ok) AppEvents.SetStatus(message);
    }

    private async void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await ViewModel.ExportSelectionAsync(App.MainWindow);
        ViewModel.Status = message;
        if (ok) AppEvents.SetStatus(message);
        else AppEvents.SetError(message);
    }
}