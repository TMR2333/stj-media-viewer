using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Models;
using Windows.System;

namespace StjMediaViewer.App.Views;

/// <summary>右侧检视面板。ViewModel 是单例，页面重建（换皮肤）后状态不丢。</summary>
public sealed partial class InspectorPanel : UserControl
{
    private bool _subscribed;

    public InspectorPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateLabelDots();
    }

    public InspectorViewModel ViewModel => InspectorViewModel.Shared;

    /// <summary>点击大图 → 请求 Shell 打开全屏预览。</summary>
    public event EventHandler<MediaAsset>? PreviewRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateLabelDots();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InspectorViewModel.ColorLabel) or null or "")
            UpdateLabelDots();
    }

    /// <summary>当前色标用"描边环"标出来，颜色仍然全部由 token 提供。</summary>
    private void UpdateLabelDots()
    {
        var current = ViewModel.ColorLabel ?? "none";
        DotRed.BorderThickness = Ring("red");
        DotYellow.BorderThickness = Ring("yellow");
        DotGreen.BorderThickness = Ring("green");
        DotBlue.BorderThickness = Ring("blue");
        DotPurple.BorderThickness = Ring("purple");
        return;

        Thickness Ring(string id) => string.Equals(current, id, StringComparison.OrdinalIgnoreCase)
            ? new Thickness(2)
            : new Thickness(0);
    }

    // ==================================================================================
    //  操作
    // ==================================================================================
    private async void Save_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();

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
            StjMediaViewer.Core.Util.Log.Warn("打开转码对话框失败", ex);
        }
    }

    private async void Ai_Click(object sender, RoutedEventArgs e) => await ViewModel.AnalyzeAsync();

    private async void Xmp_Click(object sender, RoutedEventArgs e) => await ViewModel.WriteXmpAsync();

    private void PreviewHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.CurrentAsset is { } asset) PreviewRequested?.Invoke(this, asset);
    }

    // ==================================================================================
    //  编辑
    // ==================================================================================
    private void RatingStars_RatingChanged(object? sender, int rating) => ViewModel.SetRating(rating);

    private void LabelRed_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("red");
    private void LabelYellow_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("yellow");
    private void LabelGreen_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("green");
    private void LabelBlue_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("blue");
    private void LabelPurple_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("purple");
    private void LabelNone_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorLabel("none");

    private void Pick_Click(object sender, RoutedEventArgs e) => ViewModel.SetPick(true);
    private void Reject_Click(object sender, RoutedEventArgs e) => ViewModel.SetPick(false);
    private void PickClear_Click(object sender, RoutedEventArgs e) => ViewModel.SetPick(null);

    private void CityBox_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SetCity(CityBox.Text);

    private void CityBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ViewModel.SetCity(CityBox.Text);
        e.Handled = true;
    }
}