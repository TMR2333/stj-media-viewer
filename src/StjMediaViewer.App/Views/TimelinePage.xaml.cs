using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>时间线：左侧 年/月/日 轴，右侧当天缩略图墙。</summary>
public sealed partial class TimelinePage : Page
{
    private bool _loadedOnce;
    private string _density = "medium";

    public TimelinePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public TimelineViewModel ViewModel => TimelineViewModel.Shared;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        ApplyDensity(_density);
        await ViewModel.LoadAsync();
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) ApplyDensity(tag);
    }

    private void ApplyDensity(string tag)
    {
        _density = tag;
        ViewModel.SetDensity(tag);
        DensitySmall.IsChecked = tag == "small";
        DensityMedium.IsChecked = tag == "medium";
        DensityLarge.IsChecked = tag == "large";
        Wall.ItemsPanel = (ItemsPanelTemplate)Resources[tag switch
        {
            "small" => "WallPanelSmall",
            "large" => "WallPanelLarge",
            _ => "WallPanelMedium",
        }];
    }

    private async void Day_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TimelineDayViewModel day) return;
        try
        {
            await ViewModel.SelectDayAsync(day);
        }
        catch (Exception ex)
        {
            Log.Warn("切换时间线日期失败", ex);
        }
    }

    private async void Year_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int year }) return;
        try
        {
            await ViewModel.SelectYearAsync(year);
        }
        catch (Exception ex)
        {
            Log.Warn("切换时间线年份失败", ex);
        }
    }

    private void Wall_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Wall.SelectedItem is not AssetCardItem item) return;
        var list = ViewModel.Items.Select(i => i.Asset).ToList();
        var index = ViewModel.Items.IndexOf(item);
        ShellPage.Current?.ShowPreview(list, Math.Max(0, index));
    }
}