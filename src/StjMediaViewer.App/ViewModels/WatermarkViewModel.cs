using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using StjMediaViewer.Core.Watermark;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace StjMediaViewer.App.ViewModels;

/// <summary>品牌选择项（带字标缩略图）。</summary>
public sealed class WatermarkBrandOption
{
    public WatermarkBrandOption(WatermarkBrand brand)
    {
        Brand = brand;
        Name = WatermarkPresets.Display(brand);
        try
        {
            var bytes = WatermarkRenderer.GetBrandPreviewPng(brand);
            var image = new BitmapImage();
            _ = image.SetSourceAsync(new MemoryStream(bytes).AsRandomAccessStream());
            Preview = image;
        }
        catch { }
    }

    public WatermarkBrand Brand { get; }
    public string Name { get; }
    public ImageSource? Preview { get; }
}

public sealed class WatermarkPresetOption
{
    public WatermarkPresetOption(WatermarkPreset preset)
    {
        Preset = preset;
        Name = WatermarkPresets.Display(preset);
    }

    public WatermarkPreset Preset { get; }
    public string Name { get; }
}

public sealed class WatermarkInkOption
{
    public WatermarkInkOption(WatermarkInk ink) { Ink = ink; Name = WatermarkPresets.Display(ink); }
    public WatermarkInk Ink { get; }
    public string Name { get; }
}

public sealed class WatermarkFormatOption
{
    public WatermarkFormatOption(WatermarkFormat format) { Format = format; Name = WatermarkPresets.Display(format); }
    public WatermarkFormat Format { get; }
    public string Name { get; }
}

/// <summary>水印工作台：把 Watermark Studio 网页的品牌水印搬进桌面端。</summary>
public sealed partial class WatermarkViewModel : ObservableObject
{
    /// <summary>四档导出质量（对应网页 原始/高/标准/小体积）。</summary>
    public static readonly (string Name, int Quality)[] QualityChoices =
    {
        ("原始 · 100%", 100),
        ("高 · 98%", 98),
        ("标准 · 92%", 92),
        ("小体积 · 82%", 82),
    };

    public static WatermarkViewModel Shared { get; } = new();

    private readonly WatermarkStyle _style = new();
    private CancellationTokenSource? _previewCts;
    private string? _sourcePath;
    private bool _suppressPreview;

    private WatermarkViewModel()
    {
        foreach (var brand in Enum.GetValues<WatermarkBrand>()) Brands.Add(new WatermarkBrandOption(brand));
        foreach (var preset in Enum.GetValues<WatermarkPreset>()) Presets.Add(new WatermarkPresetOption(preset));
        foreach (var ink in Enum.GetValues<WatermarkInk>()) Inks.Add(new WatermarkInkOption(ink));
        foreach (var format in Enum.GetValues<WatermarkFormat>()) Formats.Add(new WatermarkFormatOption(format));

        _selectedBrand = Brands[0];
        _selectedPreset = Presets[0];
        _selectedInk = Inks[0];
        _selectedFormat = Formats[0];
        ApplyPreset(WatermarkPreset.Classic);
    }

    public ObservableCollection<WatermarkBrandOption> Brands { get; } = new();
    public ObservableCollection<WatermarkPresetOption> Presets { get; } = new();
    public ObservableCollection<WatermarkInkOption> Inks { get; } = new();
    public ObservableCollection<WatermarkFormatOption> Formats { get; } = new();

    [ObservableProperty] private WatermarkBrandOption _selectedBrand;
    [ObservableProperty] private WatermarkPresetOption _selectedPreset;
    [ObservableProperty] private WatermarkInkOption _selectedInk;
    [ObservableProperty] private WatermarkFormatOption _selectedFormat;

    [ObservableProperty] private double _sizePercent = 20;
    [ObservableProperty] private double _opacityPercent = 95;
    [ObservableProperty] private double _positionPercent = 11;
    [ObservableProperty] private bool _shadow = true;

    [ObservableProperty] private string _sourceName = "还没有选择照片";
    [ObservableProperty] private string _sourceDetail = "拖入照片，或从素材库选一张";
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private Visibility _previewVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Visible;
    [ObservableProperty] private Visibility _busyVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _exportPathText = "";
    [ObservableProperty] private int _qualityIndex = 1;

    public ObservableCollection<string> QualityOptions { get; } = new(QualityChoices.Select(q => q.Name));

    public bool HasSource => _sourcePath is not null;

    public string SizeText => $"{SizePercent:0}%";
    public string OpacityText => $"{OpacityPercent:0}%";
    public string PositionText => $"{PositionPercent:0}%";

    // ==================================================================== 参数同步

    partial void OnSelectedBrandChanged(WatermarkBrandOption value)
    {
        if (value is null) return;
        _style.Brand = value.Brand;
        // 网页里切到 Canon / Nikon / Sony 会用品牌默认值
        if (WatermarkPresets.BrandDefaults(value.Brand) is { } defaults)
        {
            _suppressPreview = true;
            SizePercent = defaults.Size;
            OpacityPercent = defaults.Opacity;
            PositionPercent = defaults.Position;
            Shadow = defaults.Shadow;
            _suppressPreview = false;
        }
        if (_style.Ink == WatermarkInk.Red && value.Brand != WatermarkBrand.Canon) SelectedInk = Inks[0];
        RefreshPreview();
    }

    partial void OnSelectedPresetChanged(WatermarkPresetOption value)
    {
        if (value is null) return;
        ApplyPreset(value.Preset);
        RefreshPreview();
    }

    partial void OnSelectedInkChanged(WatermarkInkOption value)
    {
        if (value is null) return;
        _style.Ink = value.Ink;
        RefreshPreview();
    }

    partial void OnSelectedFormatChanged(WatermarkFormatOption value)
    {
        if (value is null) return;
        _style.Brand = SelectedBrand.Brand;
    }

    partial void OnSizePercentChanged(double value) { _style.Size = value; OnPropertyChanged(nameof(SizeText)); if (!_suppressPreview) RefreshPreview(); }
    partial void OnOpacityPercentChanged(double value) { _style.Opacity = value; OnPropertyChanged(nameof(OpacityText)); if (!_suppressPreview) RefreshPreview(); }
    partial void OnPositionPercentChanged(double value) { _style.Position = value; OnPropertyChanged(nameof(PositionText)); if (!_suppressPreview) RefreshPreview(); }

    partial void OnShadowChanged(bool value) { _style.Shadow = value; RefreshPreview(); }

    private void ApplyPreset(WatermarkPreset preset)
    {
        var values = WatermarkPresets.Get(preset);
        _suppressPreview = true;
        SizePercent = values.Size;
        OpacityPercent = values.Opacity;
        PositionPercent = values.Position;
        Shadow = values.Shadow;
        _style.Ink = values.Ink;
        SelectedInk = Inks.FirstOrDefault(i => i.Ink == values.Ink) ?? Inks[0];
        _style.Preset = preset;
        _suppressPreview = false;
    }

    // ==================================================================== 选图 / 预览

    public async Task LoadFromLibrarySelectionAsync()
    {
        var asset = LibraryViewModel.Shared.SelectedItems.FirstOrDefault()?.Asset
                    ?? LibraryViewModel.Shared.PrimaryAsset;
        if (asset is null || asset.Kind != MediaKind.Image)
        {
            Status = "先在素材库里选一张照片（视频 / 音频不能加水印）";
            return;
        }
        await LoadAsync(asset.FullPath);
    }

    public async Task LoadAsync(string path)
    {
        if (!File.Exists(path)) { Status = "文件不存在"; return; }
        _sourcePath = path;
        var info = new FileInfo(path);
        SourceName = info.Name;
        SourceDetail = $"{Formatting.FileSize(info.Length)} · {Path.GetDirectoryName(path)}";
        OnPropertyChanged(nameof(HasSource));
        Status = "";
        RefreshPreview();
        await Task.CompletedTask;
    }

    private void RefreshPreview()
    {
        if (_sourcePath is null) return;
        var path = _sourcePath;
        var style = _style.Clone();
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token);   // 拖动滑杆时合并刷新
                var png = WatermarkRenderer.RenderPreviewPng(path, style);
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                            ?? App.MainWindow?.DispatcherQueue;
                if (queue is null) return;
                queue.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(path, _sourcePath)) return;
                    var image = new BitmapImage();
                    _ = image.SetSourceAsync(new MemoryStream(png).AsRandomAccessStream());
                    Preview = image;
                    PreviewVisibility = Visibility.Visible;
                    EmptyVisibility = Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("[watermark] 预览失败", ex);
            }
        });
    }

    // ==================================================================== 导出

    public async Task<(bool Ok, string Message)> ExportSingleAsync(Window? window)
    {
        if (_sourcePath is null) return (false, "先选择一张照片");
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window ?? App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var ext = WatermarkPresets.Extension(SelectedFormat.Format);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_wm";
            picker.FileTypeChoices.Add(SelectedFormat.Name, new List<string> { ext });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return (false, "已取消");

            var quality = QualityChoices[Math.Clamp(QualityIndex, 0, QualityChoices.Length - 1)].Quality;
            var bytes = await Task.Run(() =>
                WatermarkRenderer.RenderToFile(_sourcePath, file.Path, _style, SelectedFormat.Format, quality));

            ExportPathText = file.Path;
            return (true, $"已导出 {file.Name}（{Formatting.FileSize(bytes)}）");
        }
        catch (Exception ex)
        {
            Log.Warn("[watermark] 导出失败", ex);
            return (false, $"导出失败：{ex.Message}");
        }
    }

    public async Task<(bool Ok, string Message)> ExportSelectionAsync(Window? window)
    {
        var assets = LibraryViewModel.Shared.SelectedItems.Select(i => i.Asset)
            .Where(a => a.Kind == MediaKind.Image && File.Exists(a.FullPath))
            .ToList();
        if (assets.Count == 0) return (false, "先在素材库选中要批量导出的照片");

        try
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window ?? App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return (false, "已取消");

            var quality = QualityChoices[Math.Clamp(QualityIndex, 0, QualityChoices.Length - 1)].Quality;
            var style = _style.Clone();
            var format = SelectedFormat.Format;

            BusyVisibility = Visibility.Visible;
            Status = $"正在导出 {assets.Count} 张…";
            var summary = await Task.Run(() => WatermarkRenderer.ExportFolder(
                assets.Select(a => a.FullPath).ToList(), folder.Path, style, format, quality));
            BusyVisibility = Visibility.Collapsed;

            ExportPathText = folder.Path;
            return (summary.Failed == 0, $"{summary.Describe()} → {folder.Path}");
        }
        catch (Exception ex)
        {
            BusyVisibility = Visibility.Collapsed;
            Log.Warn("[watermark] 批量导出失败", ex);
            return (false, $"批量导出失败：{ex.Message}");
        }
    }
}