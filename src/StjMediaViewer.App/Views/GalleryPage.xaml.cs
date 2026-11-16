using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;
using Windows.Foundation;
using Windows.System;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 画廊视图：大画布（适应窗口 / 实际像素 / 滚轮缩放 / 拖拽平移）+ 底部胶片带 + 右侧元数据面板。
/// 键盘：←/→ 翻页、Home/End 首尾、0–5 评分、6–9 色标、P 选用、X 废片、F 适应、1 实际像素、I 面板、T 胶片带。
/// </summary>
public sealed partial class GalleryPage : Page
{
    public GalleryViewModel ViewModel => GalleryViewModel.Shared;

    private bool _syncingSelection;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartH, _panStartV;
    private double _fitScale = 1.0;
    private bool _isFit = true;

    public GalleryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ==================================================================================
    //  生命周期
    // ==================================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
            SyncFilmstripSelection();
            Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Log.Warn("画廊页面初始化失败", ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.CurrentIndex))
        {
            SyncFilmstripSelection();
            UpdateZoomLabel();
        }
        else if (e.PropertyName == nameof(GalleryViewModel.CurrentImage))
        {
            _ = DispatcherQueue.TryEnqueue(() => FitToWindow(animated: false));
        }
    }

    private void SyncFilmstripSelection()
    {
        if (ViewModel.CurrentIndex < 0 || ViewModel.CurrentIndex >= Filmstrip.Items.Count) return;
        if (Filmstrip.SelectedIndex == ViewModel.CurrentIndex) return;

        _syncingSelection = true;
        try
        {
            Filmstrip.SelectedIndex = ViewModel.CurrentIndex;
            Filmstrip.ScrollIntoView(Filmstrip.SelectedItem, ScrollIntoViewAlignment.Leading);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private async void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        var index = Filmstrip.SelectedIndex;
        if (index < 0 || index == ViewModel.CurrentIndex) return;
        await ViewModel.ShowIndexAsync(index);
    }

    // ==================================================================================
    //  缩放 / 平移
    // ==================================================================================

    private (double Width, double Height)? ImagePixelSize()
    {
        if (ViewModel.CurrentImage is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            return (bitmap.PixelWidth, bitmap.PixelHeight);

        var asset = ViewModel.CurrentAsset;
        if (asset is { Width: > 0, Height: > 0 }) return (asset.Width!.Value, asset.Height!.Value);
        return null;
    }

    private void CanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFit) FitToWindow(animated: false);
    }

    private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFit) FitToWindow(animated: false);
    }

    private void CanvasImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (_isFit) FitToWindow(animated: false);
        UpdateZoomLabel();
    }

    /// <summary>把图片按"像素 1:1 尺寸 × scale"摆放，返回 scale 为 1 时的自适应倍数。</summary>
    private void ApplyScale(double scale, Point? anchor = null)
    {
        var size = ImagePixelSize();
        if (size is null) return;

        var (pxW, pxH) = size.Value;
        var viewportW = Math.Max(1, CanvasHost.ActualWidth);
        var viewportH = Math.Max(1, CanvasHost.ActualHeight);

        scale = Math.Clamp(scale, 0.05, 8.0);
        double displayW = pxW * scale;
        double displayH = pxH * scale;

        // 记录锚点处的相对位置（用于缩放后保持鼠标下的像素不动）
        double relX = 0.5, relY = 0.5;
        if (anchor is { } pt && displayW > 0 && displayH > 0)
        {
            var offsetX = CanvasScroll.HorizontalOffset;
            var offsetY = CanvasScroll.VerticalOffset;
            relX = Math.Clamp((pt.X + offsetX) / Math.Max(1, CanvasImage.ActualWidth), 0, 1);
            relY = Math.Clamp((pt.Y + offsetY) / Math.Max(1, CanvasImage.ActualHeight), 0, 1);
        }

        CanvasImage.Width = displayW;
        CanvasImage.Height = displayH;

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var targetX = relX * displayW - (anchor?.X ?? viewportW / 2);
            var targetY = relY * displayH - (anchor?.Y ?? viewportH / 2);
            CanvasScroll.ChangeView(
                Math.Clamp(targetX, 0, Math.Max(0, displayW - viewportW)),
                Math.Clamp(targetY, 0, Math.Max(0, displayH - viewportH)),
                null, disableAnimation: true);
            UpdateZoomLabel();
        });
    }

    private void FitToWindow(bool animated)
    {
        var size = ImagePixelSize();
        if (size is null) return;
        var (pxW, pxH) = size.Value;
        var viewportW = CanvasScroll.ViewportWidth > 1 ? CanvasScroll.ViewportWidth : Math.Max(1, CanvasHost.ActualWidth - 4);
        var viewportH = CanvasScroll.ViewportHeight > 1 ? CanvasScroll.ViewportHeight : Math.Max(1, CanvasHost.ActualHeight - 4);

        _fitScale = Math.Min(viewportW / pxW, viewportH / pxH);
        _fitScale = Math.Clamp(_fitScale, 0.02, 1.0);
        _isFit = true;

        CanvasImage.Width = pxW * _fitScale;
        CanvasImage.Height = pxH * _fitScale;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            CanvasScroll.ChangeView(0, 0, null, disableAnimation: !animated);
            UpdateZoomLabel();
        });
    }

    private void UpdateZoomLabel()
    {
        var size = ImagePixelSize();
        if (size is null || _isFit)
        {
            ViewModel.ZoomText = "适应窗口";
            return;
        }
        var scale = CanvasImage.ActualWidth / Math.Max(1, size.Value.Width);
        ViewModel.ZoomText = $"{scale * 100:0}%";
    }

    private void CanvasHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CanvasHost);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var size = ImagePixelSize();
        if (size is null) return;

        var currentScale = _isFit
            ? CanvasImage.ActualWidth / Math.Max(1, size.Value.Width)
            : CanvasImage.ActualWidth / Math.Max(1, size.Value.Width);
        if (currentScale <= 0) return;

        var factor = delta > 0 ? 1.15 : 1 / 1.15;
        _isFit = false;
        ApplyScale(currentScale * factor, point.Position);
        e.Handled = true;
    }

    private void CanvasHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var size = ImagePixelSize();
        if (size is null) return;

        if (_isFit)
        {
            // 双击放大到 100% 实际像素（以双击点为中心）
            _isFit = false;
            ApplyScale(1.0, e.GetPosition(CanvasHost));
        }
        else
        {
            FitToWindow(animated: true);
        }
        e.Handled = true;
    }

    private void CanvasHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CanvasHost);
        if (!point.Properties.IsLeftButtonPressed) return;

        _isPanning = true;
        _panStart = point.Position;
        _panStartH = CanvasScroll.HorizontalOffset;
        _panStartV = CanvasScroll.VerticalOffset;
        CanvasHost.CapturePointer(e.Pointer);
        try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); } catch { }
        e.Handled = true;
    }

    private void CanvasHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        var point = e.GetCurrentPoint(CanvasHost);
        var dx = point.Position.X - _panStart.X;
        var dy = point.Position.Y - _panStart.Y;
        CanvasScroll.ChangeView(
            Math.Max(0, _panStartH - dx),
            Math.Max(0, _panStartV - dy),
            null, disableAnimation: true);
        e.Handled = true;
    }

    private void CanvasHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        CanvasHost.ReleasePointerCapture(e.Pointer);
        try { ProtectedCursor = null; } catch { }
    }

    // ==================================================================================
    //  键盘
    // ==================================================================================

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Left:
                await ViewModel.PrevAsync();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                await ViewModel.NextAsync();
                e.Handled = true;
                break;
            case VirtualKey.Home:
                await ViewModel.FirstAsync();
                e.Handled = true;
                break;
            case VirtualKey.End:
                await ViewModel.LastAsync();
                e.Handled = true;
                break;
            case VirtualKey.Number0: await ViewModel.SetRatingAsync(0); e.Handled = true; break;
            case VirtualKey.Number1 when !ctrl: await ViewModel.SetRatingAsync(1); e.Handled = true; break;
            case VirtualKey.Number2: await ViewModel.SetRatingAsync(2); e.Handled = true; break;
            case VirtualKey.Number3: await ViewModel.SetRatingAsync(3); e.Handled = true; break;
            case VirtualKey.Number4: await ViewModel.SetRatingAsync(4); e.Handled = true; break;
            case VirtualKey.Number5: await ViewModel.SetRatingAsync(5); e.Handled = true; break;
            case VirtualKey.Number6: await ViewModel.SetColorLabelAsync("red"); e.Handled = true; break;
            case VirtualKey.Number7: await ViewModel.SetColorLabelAsync("yellow"); e.Handled = true; break;
            case VirtualKey.Number8: await ViewModel.SetColorLabelAsync("green"); e.Handled = true; break;
            case VirtualKey.Number9: await ViewModel.SetColorLabelAsync("blue"); e.Handled = true; break;
            case VirtualKey.P: await ViewModel.SetPickAsync(true); e.Handled = true; break;
            case VirtualKey.X: await ViewModel.SetPickAsync(false); e.Handled = true; break;
            case VirtualKey.F: FitToWindow(animated: true); e.Handled = true; break;
            case VirtualKey.I: ViewModel.IsInspectorVisible = !ViewModel.IsInspectorVisible; e.Handled = true; break;
            case VirtualKey.T: ViewModel.IsFilmstripVisible = !ViewModel.IsFilmstripVisible; e.Handled = true; break;
            case VirtualKey.Add: ZoomBy(1.25); e.Handled = true; break;
            case VirtualKey.Subtract: ZoomBy(1 / 1.25); e.Handled = true; break;
        }

        // 数字 1 在按住 Ctrl 时不做评分（避免和手势冲突）
        if (e.Key == VirtualKey.Number1 && ctrl) ZoomBy(1.0, absolute: true);
    }

    private void ZoomBy(double factor, bool absolute = false)
    {
        var size = ImagePixelSize();
        if (size is null) return;
        var current = CanvasImage.ActualWidth / Math.Max(1, size.Value.Width);
        _isFit = false;
        ApplyScale(absolute ? factor : current * factor);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await ViewModel.PrevAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await ViewModel.NextAsync();
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow(animated: true);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => ZoomBy(1.0, absolute: true);
    private void ToggleFilmstrip_Click(object sender, RoutedEventArgs e)
        => ViewModel.IsFilmstripVisible = !ViewModel.IsFilmstripVisible;
    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
        => ViewModel.IsInspectorVisible = !ViewModel.IsInspectorVisible;

    private async void Transcode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asset = ViewModel.CurrentAsset;
            if (asset is null || !asset.IsVideo) return;
            var dialog = new TranscodeDialog(asset) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("画廊打开转码失败", ex);
        }
    }

    private void GalleryBack_Click(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LibraryPage));
    }
}