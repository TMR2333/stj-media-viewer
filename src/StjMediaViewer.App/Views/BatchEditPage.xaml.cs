using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>批量填写信息：左侧挑文件、右侧勾字段填值。</summary>
public sealed partial class BatchEditPage : Page
{
    public BatchEditViewModel ViewModel => BatchEditViewModel.Shared;

    public BatchEditPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导入页跳过来时带着刚导入的素材 id
            if (BatchEditViewModel.PendingIds.Count > 0)
            {
                var ids = BatchEditViewModel.PendingIds.ToList();
                BatchEditViewModel.PendingIds.Clear();
                var assets = await AppServices.Store.GetByIdsAsync(ids);
                await ViewModel.LoadAsync(assets);
            }
            else if (int.TryParse(Environment.GetEnvironmentVariable("STJ_DEMO_BATCH"), out var demoCount) && demoCount > 0)
            {
                // 截图验收用：STJ_DEMO_BATCH=24 直接载入最近 24 条
                var assets = await AppServices.Store.QueryAsync(new Core.Models.AssetQuery { Limit = demoCount });
                await ViewModel.LoadAsync(assets);
            }
            else
            {
                await ViewModel.LoadFromLibrarySelectionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("批量填写页初始化失败", ex);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var all = ViewModel.Items.All(i => i.IsChecked);
        ViewModel.SetAllChecked(!all);
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.Items) item.IsChecked = !item.IsChecked;
        ViewModel.OnItemCheckedChanged();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = await ViewModel.ApplyAsync();
            if (n == 0) AppEvents.SetError("没有可写入的素材或字段");
        }
        catch (Exception ex)
        {
            Log.Warn("批量写入失败", ex);
        }
    }
}