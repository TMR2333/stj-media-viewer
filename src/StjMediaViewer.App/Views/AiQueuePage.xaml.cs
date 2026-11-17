using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>AI 批量分类工作台。</summary>
public sealed partial class AiQueuePage : Page
{
    private bool _suppressUiEvents;

    public AiQueuePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public AiQueueViewModel ViewModel => AiQueueViewModel.Shared;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageLayout.Fit(PageScroll, PageContent);
        _suppressUiEvents = true;
        try
        {
            ViewModel.RefreshGuidance();
            ScopeBox.SelectedIndex = ViewModel.ScopeId switch
            {
                "filtered" => 1,
                "all" => 2,
                "selected" => 3,
                _ => 0,
            };
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (ScopeBox.SelectedItem is ComboBoxItem { Tag: string id }) ViewModel.ScopeId = id;
    }

    private void MaxItems_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressUiEvents || double.IsNaN(args.NewValue)) return;
        ViewModel.MaxItems = (int)Math.Round(args.NewValue);
    }

    private void Concurrency_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressUiEvents || double.IsNaN(args.NewValue)) return;
        ViewModel.Concurrency = (int)Math.Round(args.NewValue);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.StartOrResumeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("启动 AI 批量分类失败", ex);
            AppEvents.SetError($"启动失败：{ex.Message}");
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => ViewModel.Pause();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private async void WriteXmp_Click(object sender, RoutedEventArgs e)
    {
        var count = await ViewModel.WriteXmpAsync();
        AppEvents.SetStatus($"XMP 写入完成（{count} 个）");
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.NavigateToView("settings");
}