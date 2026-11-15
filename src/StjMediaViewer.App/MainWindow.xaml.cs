using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.App.Views;

namespace StjMediaViewer.App;

public sealed partial class MainWindow : Window
{
    public Frame Frame => RootFrame;

    public MainWindow()
    {
        InitializeComponent();
        Title = "STJ Media Viewer";
        AppWindow.Title = "STJ Media Viewer";
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        TrySetSize(1480, 940);
        RootFrame.Navigate(typeof(ShellPage));
    }

    private void TrySetSize(int width, int height)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var w = Math.Min(width, area.WorkArea.Width - 80);
            var h = Math.Min(height, area.WorkArea.Height - 80);
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)w, (int)h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                area.WorkArea.X + (area.WorkArea.Width - (int)w) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - (int)h) / 2));
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("窗口尺寸设置失败", ex);
        }
    }
}

/// <summary>启动失败时的兜底窗口，避免用户只看到闪一下就没了。</summary>
public sealed partial class ErrorWindow : Window
{
    public ErrorWindow(Exception ex)
    {
        Title = "STJ Media Viewer — 启动失败";
        Content = new ScrollViewer
        {
            Padding = new Thickness(28),
            Content = new TextBlock
            {
                Text = ex.ToString(),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsTextSelectionEnabled = true,
            },
        };
    }
}