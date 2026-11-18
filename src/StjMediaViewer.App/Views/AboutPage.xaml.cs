using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 关于页：品牌（Stjarna PERSPECTIVE 出品 · co-worked with DeepSeek Harness）+ 版本环境 + 项目表情包 + 技术栈。
/// 静态内容为主，代码里只做版本号与依赖清单的填充。
/// </summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageLayout.Fit(PageScroll, PageContent);
        try
        {
            var asm = typeof(App).Assembly;
            var version = asm.GetName().Version;
            VersionText.Text = $"stj-media-viewer {version?.Major}.{version?.Minor}.{version?.Build}";

            var os = Environment.OSVersion.Version;
            RuntimeText.Text = $".NET {Environment.Version} · Windows {os.Major}.{os.Minor}.{os.Build} · {(Environment.Is64BitProcess ? "x64" : "x86")}";

            BuildText.Text = $"构建 {BuildTimestamp(asm.Location)}";

            CopyrightText.Text = $"© {DateTimeOffset.Now.Year} Stjarna PERSPECTIVE · 本项目按 MIT 许可分发（第三方组件版权归各自作者）";
        }
        catch (Exception ex)
        {
            Log.Warn("关于页信息填充失败", ex);
        }

        FillStack();

        // 截图用：STJ_DEMO_ABOUT_SCROLL=1 滚到作者区
        if (Environment.GetEnvironmentVariable("STJ_DEMO_ABOUT_SCROLL") == "1")
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1500);
                try { FindScrollViewer(this)?.ChangeView(null, 520, null, true); } catch { }
            });
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private static string BuildTimestamp(string assemblyPath)
    {
        try
        {
            return File.Exists(assemblyPath)
                ? File.GetLastWriteTime(assemblyPath).ToString("yyyy-MM-dd")
                : DateTimeOffset.Now.ToString("yyyy-MM-dd");
        }
        catch
        {
            return DateTimeOffset.Now.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>点作者卡片：打开 TMR 的 B 站主页。</summary>
    private async void TmrAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://space.bilibili.com/632568585"));
        }
        catch (Exception ex)
        {
            Log.Warn("打开作者主页失败", ex);
        }
    }

    /// <summary>点「一个货成」：打开抖音主页。</summary>
    private async void HuoChengAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(
                "https://www.douyin.com/user/MS4wLjABAAAAaAohr1ln9GMhbZ21KSG3KvdbMPMfW_d5mXkZvSfRUDY"));
        }
        catch (Exception ex)
        {
            Log.Warn("打开一个货成主页失败", ex);
        }
    }

    /// <summary>点「一只马子」：打开 B 站主页。</summary>
    private async void MaZiAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://space.bilibili.com/1236557779"));
        }
        catch (Exception ex)
        {
            Log.Warn("打开一只马子主页失败", ex);
        }
    }

    private void FillStack()
    {
        var items = new (string Name, string Detail)[]
        {
            ("WinUI 3 / Windows App SDK", "原生窗口、Mica 背板、Fluent 组件与资源体系"),
            (".NET 10", "Core 与 UI 分层，self-contained 部署，免装运行时"),
            ("SQLite (WAL)", "84 列素材索引 + 分面 / 时间线 / 文本检索"),
            ("FFmpeg", "视频抽帧、代理转码、H.264/H.265/AV1 压缩"),
            ("MetadataExtractor + 自研解析器", "EXIF / IPTC / XMP / ISOBMFF / Matroska / RAW 预览"),
            ("GeoNames cities15000", "离线反向地理编码（31,701 城市，含中文名）"),
        };

        foreach (var (name, detail) in items)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)Application.Current.Resources["AccentBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(dot);

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock
            {
                Text = name,
                Style = (Style)Application.Current.Resources["AppCaptionTextStyle"],
            });
            text.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)Application.Current.Resources["AppMicroTextStyle"],
                Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            StackItems.Items.Add(row);
        }
    }
}
