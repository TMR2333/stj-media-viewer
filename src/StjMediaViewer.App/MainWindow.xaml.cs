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
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        ApplyWindowIcon();
        RootFrame.Navigate(typeof(ShellPage));
    }

    /// <summary>标题栏 / 任务栏图标：用 Assets/AppIcon/app.ico（品牌图标：SD + CF 卡）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "app.ico");
            if (System.IO.File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("设置窗口图标失败", ex);
        }
    }

    /// <summary>在窗口 Activate 之后调用，避免 AppWindow 还没拿到显示器信息。</summary>
    public void PositionWindow()
    {
        try
        {
            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;

            // 截图/验收用：STJ_WINDOW_SIZE=1800x1000 指定逻辑尺寸（DIP）
            var sizeOverride = Environment.GetEnvironmentVariable("STJ_WINDOW_SIZE");
            if (!string.IsNullOrWhiteSpace(sizeOverride))
            {
                var parts = sizeOverride.Split('x', 'X');
                if (parts.Length == 2 && int.TryParse(parts[0], out var ow) && int.TryParse(parts[1], out var oh))
                {
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                        wa.X + (wa.Width - (int)(ow * scale)) / 2,
                        wa.Y + (wa.Height - (int)(oh * scale)) / 2,
                        Math.Min((int)(ow * scale), wa.Width),
                        Math.Min((int)(oh * scale), wa.Height)));
                    Core.Util.Log.Info($"窗口尺寸（演示）{ow}x{oh} DIP");
                    return;
                }
            }

            // 目标逻辑尺寸 1500x900 DIP；AppWindow 用物理像素，所以乘缩放。
            // 再与工作区做 clamp，保证一定完整可见（含任务栏）。
            int margin = (int)(80 * scale);
            int w = Math.Min((int)(1500 * scale), Math.Max(800, wa.Width - margin));
            int h = Math.Min((int)(900 * scale), Math.Max(600, wa.Height - margin));
            int x = wa.X + (wa.Width - w) / 2;
            int y = wa.Y + (wa.Height - h) / 2;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
            Core.Util.Log.Info($"窗口定位 scale={scale:0.##} workArea={wa.Width}x{wa.Height}@{wa.X},{wa.Y} → {w}x{h}@{x},{y}");
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