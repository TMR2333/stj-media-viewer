using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.ViewModels;

namespace StjMediaViewer.App.Views;

public sealed partial class OrganizePage : Page
{
    public OrganizeViewModel ViewModel { get; } = new();

    public OrganizePage()
    {
        InitializeComponent();
        Loaded += OnLoadedAsync;
        Loaded += (_, _) => PageLayout.Fit(PageScroll, PageContent);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();

            // 验收用：STJ_DEMO_XMP_REPAIR=1 直接跑「找回未转移的 XMP」并记录结果
            if (Environment.GetEnvironmentVariable("STJ_DEMO_XMP_REPAIR") == "1")
            {
                await ViewModel.RepairXmpCommand.ExecuteAsync(null);
                Core.Util.Log.Info($"[xmp-repair] status={ViewModel.Status} | summary={ViewModel.Summary} | warn={ViewModel.WarningText}");
            }
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("整理页初始化失败", ex);
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await ViewModel.PreviewCommand.ExecuteAsync(null);

    private async void RepairXmp_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "找回未转移的 XMP？",
            Content = "会扫描所有媒体库目录里孤立的 .xmp（旁边已经没有主文件），按文件名匹配索引里的素材，" +
                      "复制到素材现在所在的文件夹。只复制、不删除原文件；目标已存在同名 XMP 时跳过。",
            PrimaryButtonText = "开始找回",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.RepairXmpCommand.ExecuteAsync(null);
    }

    private async void Execute_Click(object sender, RoutedEventArgs e) => await ViewModel.ExecuteCommand.ExecuteAsync(null);

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ViewModel.UndoCommand.ExecuteAsync(null);
}