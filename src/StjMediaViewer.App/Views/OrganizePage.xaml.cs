using Microsoft.UI.Xaml;
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
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("整理页初始化失败", ex);
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await ViewModel.PreviewCommand.ExecuteAsync(null);

    private async void Execute_Click(object sender, RoutedEventArgs e) => await ViewModel.ExecuteCommand.ExecuteAsync(null);

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ViewModel.UndoCommand.ExecuteAsync(null);
}