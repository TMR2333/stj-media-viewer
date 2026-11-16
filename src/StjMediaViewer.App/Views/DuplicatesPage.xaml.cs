using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>重复项页：查重 → 按组展示 → 标记保留 / 勾选删除（回收站）。</summary>
public sealed partial class DuplicatesPage : Page
{
    public DuplicatesPage()
    {
        InitializeComponent();
        AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPageKeyDown), true);
    }

    private void OnPageKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LibraryPage));
    }

    public DuplicatesViewModel ViewModel => DuplicatesViewModel.Shared;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.FindAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("查重失败", ex);
            AppEvents.SetError($"查重失败：{ex.Message}");
        }
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DuplicateItemViewModel item) return;
        var group = ViewModel.Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (group is null) return;
        ViewModel.SetKeep(group, item);
    }

    private void DeleteCheck_Changed(object sender, RoutedEventArgs e)
    {
        // CheckBox 的 TwoWay 绑定先写回 item.IsSelectedForDelete，这里只刷新汇总计数。
        ViewModel.UpdateSelectionSummary();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.DeleteSelectedAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("删除重复项失败", ex);
            AppEvents.SetError($"删除失败：{ex.Message}");
        }
    }

    private void DuplicatesBack_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(LibraryPage));
    }
}