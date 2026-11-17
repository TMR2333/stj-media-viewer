using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.System;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 全屏预览覆盖层：图片缩放/平移、视频播放、左右翻页、评分与色标快捷操作。
/// 由 <see cref="ShellPage"/> 挂在整个窗口最上层，因此能盖住顶栏与状态栏。
/// </summary>
public sealed partial class PreviewOverlay : UserControl
{
    private bool _dragging;
    private Windows.Foundation.Point _dragStartPoint;
    private double _dragStartHorizontal;
    private double _dragStartVertical;
    private bool _suppressZoomText;

    public PreviewOverlay()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // 系统解码器（Media Foundation）不支持 4:2:2 / 10-bit / HEVC 时，
        // MediaPlayerElement 会抛 MediaFailed：这时切到 ffmpeg 生成的预览代理。
        VideoPlayer.Loaded += (_, _) => HookMediaPlayer();
        // ScrollViewer 自带 ZoomMode 会把滚轮当成滚动/缩放处理，这里用 handledEventsToo 抢在前面，
        // 保证"滚轮 = 以指针为中心缩放"这一条交互契约稳定生效。
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Overlay_WheelZoom), true);
    }

    public PreviewViewModel ViewModel => PreviewViewModel.Shared;

    // ==================================================================================
    //  打开 / 关闭
    // ==================================================================================
    public void Show(IReadOnlyList<MediaAsset> assets, int index)
    {
        ViewModel.Open(assets, index);
        DispatcherQueue.TryEnqueue(() =>
        {
            ResetZoom();
            // 把焦点放到关闭按钮上：Esc / ← / → 等按键才能沿视觉树冒泡到 OverlayRoot。
            try { CloseButton.Focus(FocusState.Programmatic); }
            catch { /* 焦点设置失败不影响可用性 */ }
        });
    }

    public void Close()
    {
        try { VideoPlayer.MediaPlayer?.Pause(); } catch { /* 播放器未就绪时忽略 */ }
        _dragging = false;
        ViewModel.Close();
    }

    private Windows.Media.Playback.MediaPlayer? _hookedPlayer;

    private void HookMediaPlayer()
    {
        try
        {
            var player = VideoPlayer.MediaPlayer;
            if (player is null || ReferenceEquals(player, _hookedPlayer)) return;
            _hookedPlayer = player;
            player.MediaFailed += (playerSender, args) =>
            {
                Log.Info($"[preview] MediaFailed：{args.ErrorMessage}（{args.Error}）→ 尝试 ffmpeg 预览代理");
                // MediaFailed 在后台线程触发，ViewModel 的属性会触发 x:Bind 更新 → 必须切回 UI 线程
                DispatcherQueue.TryEnqueue(() => _ = ViewModel.SwitchToProxyAsync());
            };
        }
        catch (Exception ex)
        {
            Log.Warn("挂接视频播放失败事件失败", ex);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e) => ViewModel.OpenInExternalPlayer();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.VideoSource):
                HookMediaPlayer();
                break;
            case nameof(PreviewViewModel.Image):
                AudioGlyph.Visibility = ViewModel.Image is null ? Visibility.Visible : Visibility.Collapsed;
                ResetZoom();
                break;
            case nameof(PreviewViewModel.Current):
            case null:
            case "":
                ResetZoom();
                break;
        }
    }

    // ==================================================================================
    //  缩放 / 平移
    // ==================================================================================
    private void ResetZoom()
    {
        _suppressZoomText = true;
        try
        {
            ZoomScroll.ChangeView(0, 0, 1.0f, true);
            ViewModel.ZoomText = "适应";
        }
        catch
        {
            // 布局尚未完成时 ChangeView 可能抛，忽略。
        }
        finally
        {
            _suppressZoomText = false;
        }
    }

    private void ZoomScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_suppressZoomText) return;
        var zoom = ZoomScroll.ZoomFactor;
        ViewModel.ZoomText = Math.Abs(zoom - 1.0) < 0.01 ? "适应" : $"{zoom * 100:0}%";
    }

    private void Overlay_WheelZoom(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsOpen || ViewModel.Image is null) return;
        var point = e.GetCurrentPoint(ZoomScroll);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var factor = ZoomScroll.ZoomFactor;
        var target = (float)Math.Clamp(delta > 0 ? factor * 1.15 : factor / 1.15, 0.1, 8.0);
        if (Math.Abs(target - factor) < 0.0001) { e.Handled = true; return; }

        // 以鼠标位置为锚点缩放：先算出指针处的相对位置，缩放后再把偏移补回去。
        var pos = point.Position;
        var extentW = ZoomScroll.ExtentWidth;
        var extentH = ZoomScroll.ExtentHeight;
        var relX = extentW > 0 ? (ZoomScroll.HorizontalOffset + pos.X) / extentW : 0.5;
        var relY = extentH > 0 ? (ZoomScroll.VerticalOffset + pos.Y) / extentH : 0.5;

        ZoomScroll.ChangeView(null, null, target, true);
        ZoomScroll.UpdateLayout();
        var newExtentW = ZoomScroll.ExtentWidth;
        var newExtentH = ZoomScroll.ExtentHeight;
        ZoomScroll.ChangeView(
            relX * newExtentW - pos.X,
            relY * newExtentH - pos.Y,
            null,
            true);

        ViewModel.ZoomText = $"{target * 100:0}%";
        e.Handled = true;
    }

    private void ZoomScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ZoomScroll);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStartPoint = point.Position;
        _dragStartHorizontal = ZoomScroll.HorizontalOffset;
        _dragStartVertical = ZoomScroll.VerticalOffset;
        ZoomScroll.CapturePointer(e.Pointer);
    }

    private void ZoomScroll_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetCurrentPoint(ZoomScroll).Position;
        var dx = pos.X - _dragStartPoint.X;
        var dy = pos.Y - _dragStartPoint.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
        ZoomScroll.ChangeView(_dragStartHorizontal - dx, _dragStartVertical - dy, null, true);
    }

    private void ZoomScroll_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ZoomScroll.ReleasePointerCapture(e.Pointer);
    }

    private void ZoomScroll_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var asset = ViewModel.Current;
        if (asset is null) return;

        if (ZoomScroll.ZoomFactor > 1.05)
        {
            ResetZoom();
            return;
        }

        // 适应 → 1:1：按"画面实际像素 / 当前显示像素"求缩放倍率。
        var viewportW = ZoomScroll.ViewportWidth;
        var viewportH = ZoomScroll.ViewportHeight;
        if (viewportW <= 0 || viewportH <= 0 || asset.Width is not > 0) return;

        double naturalW = asset.Width!.Value;
        double naturalH = asset.Height is > 0 ? asset.Height!.Value : naturalW;
        var ar = naturalW / naturalH;
        var drawnW = Math.Min(viewportW, viewportH * ar);
        if (drawnW <= 0) return;

        var target = (float)Math.Clamp(naturalW / drawnW, 0.1, 8.0);
        ZoomScroll.ChangeView(null, null, target, false);
        ViewModel.ZoomText = $"{target * 100:0}%";
        e.Handled = true;
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void ZoomScroll_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 点画布空白处（不是图片本身）关闭预览。
        if (e.OriginalSource is Image) return;
        Close();
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.Previous();

    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.Next();

    // ==================================================================================
    //  键盘
    // ==================================================================================
    private void Overlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                ViewModel.Previous();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                ViewModel.Next();
                e.Handled = true;
                break;
            case VirtualKey.Space:
                TogglePlayback();
                e.Handled = true;
                break;
        }
    }

    private void TogglePlayback()
    {
        try
        {
            var player = VideoPlayer.MediaPlayer;
            if (player is null) return;
            if (player.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                player.Pause();
            else
                player.Play();
        }
        catch
        {
            // 未接入播放器时忽略。
        }
    }

    // ==================================================================================
    //  快速评分 / 色标 / 选用
    // ==================================================================================
    private async void PreviewStars_RatingChanged(object? sender, int rating)
    {
        var asset = ViewModel.Current;
        if (asset is null) return;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a =>
        {
            a.Rating = Math.Clamp(rating, 0, 5);
            a.Provenance[ClassDimension.Rating] = FieldProvenance.Of(ClassSource.Manual);
        });
        ViewModel.RefreshCurrent();
    }

    private async void LabelRed_Click(object sender, RoutedEventArgs e) => await SetColorLabelAsync("red");
    private async void LabelYellow_Click(object sender, RoutedEventArgs e) => await SetColorLabelAsync("yellow");
    private async void LabelGreen_Click(object sender, RoutedEventArgs e) => await SetColorLabelAsync("green");
    private async void LabelBlue_Click(object sender, RoutedEventArgs e) => await SetColorLabelAsync("blue");
    private async void LabelNone_Click(object sender, RoutedEventArgs e) => await SetColorLabelAsync(null);

    private async Task SetColorLabelAsync(string? label)
    {
        var asset = ViewModel.Current;
        if (asset is null) return;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.ColorLabel = label);
        ViewModel.RefreshCurrent();
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var asset = ViewModel.Current;
        if (asset is null) return;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.IsPick = a.IsPick == true ? null : true);
        ViewModel.RefreshCurrent();
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        var asset = ViewModel.Current;
        if (asset is null) return;
        await InspectorViewModel.Shared.ApplyQuickEditAsync(asset, a => a.IsPick = a.IsPick == false ? null : false);
        ViewModel.RefreshCurrent();
    }
}