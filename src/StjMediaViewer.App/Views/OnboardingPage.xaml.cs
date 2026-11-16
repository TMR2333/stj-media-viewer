using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.UI;

namespace StjMediaViewer.App.Views;

/// <summary>
/// 新用户引导：左侧 WebView2 播放 HTML 动画演示（5 幕），右侧逐步介绍并让用户选好关键设置。
/// 首次启动自动进入；设置页可重新播放。跳过 = 保留默认设置，不算失败。
/// </summary>
public sealed partial class OnboardingPage : Page
{
    private const int StepCount = 5;

    /// <summary>true = 用户从设置里重播引导（结束后恢复原主题，而不是切深色）。</summary>
    public static bool EnterReplay { get; set; }

    private int _step;
    private bool _webReady;
    private bool _finishing;
    private string? _themeBefore;

    private static readonly (string Title, string Subtitle)[] Steps =
    {
        ("欢迎来到 STJ Media Viewer",
         "这是给职业创作者的素材管理工具：本地优先、不联网也能用，所有处理都在你自己的机器上。"),
        ("选一个素材文件夹",
         "扫描会建立索引（类型、时间、地点、设备、主题、影调、后期、Log、评分九个维度）。"),
        ("AI 识别（可选）",
         "接入你自己的模型，让 AI 读图写标签。不配置完全不影响其他功能。"),
        ("几个偏好",
         "这两个开关决定了日常用起来的顺手程度，之后随时能改。"),
        ("都设置好了",
         "下面是你刚才的选择。点击「完成」进入素材库。"),
    };

    public OnboardingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // =====================================================================
    //  启动 / WebView2
    // =====================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoForm();
        ApplyOnboardingTheme();
        Log.Info("[onboarding] 页面已加载");

        // 截图/验收用：STJ_DEMO_ONBOARDING_STEP=1..4 直接停在第 n 步
        var demoStep = Environment.GetEnvironmentVariable("STJ_DEMO_ONBOARDING_STEP");
        ShowStep(int.TryParse(demoStep, out var parsed) ? Math.Clamp(parsed, 0, StepCount - 1) : 0);
        await InitializeWebViewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _webReady = false;

    /// <summary>引导页统一用浅色（演示是浅色扁平风）；重播时结束后恢复用户原来的主题。</summary>
    private void ApplyOnboardingTheme()
    {
        try
        {
            var settings = AppServices.Settings.Current;
            _themeBefore = settings.ThemeMode;
            if (!EnterReplay) settings.ThemeBeforeOnboarding = settings.ThemeMode;

            _ = AppServices.Design.SetThemeModeAsync("light", persist: false);
        }
        catch (Exception ex)
        {
            Log.Warn("应用引导页浅色主题失败", ex);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await Demo.EnsureCoreWebView2Async();

            var core = Demo.CoreWebView2;
            if (core is null)
            {
                ShowDemoFallback("WebView2 初始化失败");
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            Demo.DefaultBackgroundColor = Color.FromArgb(255, 0xF7, 0xF7, 0xF9);

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Onboarding", "onboarding.html");
            string html = File.Exists(htmlPath)
                ? await File.ReadAllTextAsync(htmlPath)
                : "<html><body style='background:#f7f7f9'></body></html>";

            core.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                PushScene(_step);
                _ = DebugCapturePreviewAsync(_step);

                // 自动化验收：STJ_DEMO_ONBOARDING_FINISH=1 时自动走完引导（验证切主题 / 落设置）
                if (Environment.GetEnvironmentVariable("STJ_DEMO_ONBOARDING_FINISH") == "1")
                    _ = AutoFinishAsync();
            };

            Demo.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log.Warn("WebView2 初始化失败，引导退化为静态说明", ex);
            ShowDemoFallback(ex.Message);
        }
    }

    /// <summary>STJ_TRACE_ONBOARDING=1 时把 WebView2 当前画面存成 png（自动化验收用；窗口截图不含 WebView2 合成层）。</summary>
    private async Task DebugCapturePreviewAsync(int step)
    {
        if (Environment.GetEnvironmentVariable("STJ_TRACE_ONBOARDING") != "1") return;
        if (!_webReady || Demo.CoreWebView2 is null) return;
        try
        {
            await Task.Delay(1400);
            var path = Path.Combine(Path.GetTempPath(), $"onboarding-preview-{step}.png");
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(path),
                Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            await Demo.CoreWebView2.CapturePreviewAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
            Log.Info($"[onboarding] 预览已保存 {path}");
        }
        catch (Exception ex)
        {
            Log.Warn("[onboarding] 预览截图失败", ex);
        }
    }

    private async Task AutoFinishAsync()
    {
        try
        {
            await Task.Delay(1200);
            await FinishAsync(startWarmup: false);
        }
        catch (Exception ex)
        {
            Log.Warn("自动化完成引导失败", ex);
        }
    }

    private void ShowDemoFallback(string reason)
    {
        Demo.Visibility = Visibility.Collapsed;
        DemoFallback.Visibility = Visibility.Visible;
        Log.Info($"[onboarding] 动画演示不可用：{reason}");
    }

    // =====================================================================
    //  步骤切换
    // =====================================================================

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);

        StepTitle.Text = Steps[_step].Title;
        StepSubtitle.Text = Steps[_step].Subtitle;

        PanelWelcome.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelLibrary.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelAi.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelPrefs.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelDone.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step switch
        {
            0 => "开始",
            4 => "完成",
            _ => "下一步",
        };
        SkipButton.Content = _step == 4 ? "稍后再说" : "跳过引导";

        var dots = new[] { Dot0, Dot1, Dot2, Dot3, Dot4 };
        for (int i = 0; i < dots.Length; i++)
        {
            bool on = i == _step;
            dots[i].Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                on ? Windows.UI.Color.FromArgb(255, 0x60, 0xCD, 0xFF)
                   : Windows.UI.Color.FromArgb(255, 0x7A, 0x84, 0x97));
            dots[i].Opacity = on ? 1 : 0.5;
            dots[i].Width = on ? 18 : 6;
        }

        if (_step == 4) RefreshDoneSummary();
        PushScene(_step);
        _ = DebugCapturePreviewAsync(step);
    }

    private void PushScene(int step)
    {
        if (!_webReady) return;
        try
        {
            Demo.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "scene", value = step }));
        }
        catch (Exception ex)
        {
            Log.Warn("推送引导场景失败", ex);
        }
    }

    // =====================================================================
    //  表单
    // =====================================================================

    private void LoadSettingsIntoForm()
    {
        var settings = AppServices.Settings.Current;
        AiBaseUrl.Text = settings.Ai.BaseUrl ?? "https://api.openai.com/v1";
        AiModel.Text = settings.Ai.VisionModel ?? "gpt-4o-mini";

        var existingKey = AppServices.Settings.UnprotectSecret(settings.Ai.ProtectedApiKey);
        AiStatus.Text = string.IsNullOrWhiteSpace(existingKey) ? "尚未配置 API Key" : "已配置 API Key（已加密保存）";

        PrefLut.IsOn = settings.AutoAssignLutForSony;
        PrefWarmup.IsOn = settings.Libraries.Count > 0;

        int cache = settings.ThumbnailCacheMb;
        foreach (var item in PrefCache.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var mb) && mb == cache)
            {
                PrefCache.SelectedItem = item;
                break;
            }
        }

        PrefCache.SelectedIndex = PrefCache.SelectedIndex < 0 ? 1 : PrefCache.SelectedIndex;
        RefreshLibrarySummary();
    }

    private void RefreshLibrarySummary()
    {
        var roots = AppServices.Settings.Current.Libraries;
        LibrarySummary.Text = roots.Count == 0
            ? "还没有添加素材库"
            : string.Join("\n", roots.Take(4).Select(r => $"• {r.Name}  ·  {r.Path}")) +
              (roots.Count > 4 ? $"\n… 还有 {roots.Count - 4} 个" : "");
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AddFolderButton.IsEnabled is false) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            AddFolderButton.IsEnabled = false;
            LibraryHint.Text = $"正在扫描 {folder.Path} …（可以继续下一步，扫描在后台进行）";

            var root = await AppServices.Library.AddLibraryAsync(folder.Path);
            AppServices.Settings.Current.Libraries = (await AppServices.Library.GetLibrariesAsync()).ToList();
            await AppServices.Settings.SaveAsync();
            RefreshLibrarySummary();

            var progress = new Progress<ScanProgress>(p =>
            {
                LibraryHint.Text = $"{p.Phase} · {p.Processed}/{p.Total} · {Path.GetFileName(p.CurrentPath ?? "")}";
            });

            var result = await AppServices.Library.ScanLibraryAsync(root, new ScanOptions(), progress);
            LibraryHint.Text = $"扫描完成：{result.MediaFound} 个媒体文件（新增 {result.Added}）";
            AppEvents.RaiseLibraryContentChanged();
            AppEvents.SetStatus($"素材库已建立索引：{result.MediaFound} 个文件");
        }
        catch (Exception ex)
        {
            Log.Warn("引导页添加素材库失败", ex);
            LibraryHint.Text = $"添加失败：{ex.Message}";
        }
        finally
        {
            AddFolderButton.IsEnabled = true;
        }
    }

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AiTestButton.IsEnabled = false;
            AiStatus.Text = "正在测试…";

            var settings = BuildAiSettings();
            var result = await AppServices.AiClient.TestConnectionAsync(settings);
            AiStatus.Text = result.Success
                ? $"连接成功（{result.LatencyMs} ms）"
                : $"连接失败：{result.Message}";
        }
        catch (Exception ex)
        {
            AiStatus.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            AiTestButton.IsEnabled = true;
        }
    }

    private AiSettings BuildAiSettings()
    {
        var current = AppServices.Settings.Current.Ai;
        return new AiSettings
        {
            ProviderId = current.ProviderId,
            BaseUrl = string.IsNullOrWhiteSpace(AiBaseUrl.Text) ? current.BaseUrl : AiBaseUrl.Text.Trim(),
            VisionModel = string.IsNullOrWhiteSpace(AiModel.Text) ? "gpt-4o-mini" : AiModel.Text.Trim(),
            ProtectedApiKey = string.IsNullOrWhiteSpace(AiKey.Password)
                ? current.ProtectedApiKey
                : AppServices.Settings.ProtectSecret(AiKey.Password),
            VisionEnabled = true,
            TimeoutSeconds = current.TimeoutSeconds,
            Temperature = current.Temperature,
            MaxRetries = current.MaxRetries,
            Concurrency = current.Concurrency,
            MaxImageDimension = current.MaxImageDimension,
            JpegQuality = current.JpegQuality,
            UseJsonSchema = current.UseJsonSchema,
        };
    }

    private void RefreshDoneSummary()
    {
        var roots = AppServices.Settings.Current.Libraries;
        DoneLibrary.Text = roots.Count == 0
            ? "尚未添加（可稍后在素材库添加）"
            : $"{roots.Count} 个文件夹 · {roots.Sum(r => r.AssetCount):N0} 个素材";

        bool hasKey = !string.IsNullOrWhiteSpace(AiKey.Password);
        DoneAi.Text = hasKey ? "已配置，之后可在设置里改" : "跳过（之后可在设置里配置）";

        DonePrefs.Text = (PrefLut.IsOn ? "自动匹配 Sony LUT" : "手动选择 LUT") + " · " +
                         (PrefWarmup.IsOn ? "完成后预热缩略图" : "按需生成缩略图") + " · " +
                         $"缓存 {PrefCache.SelectedItem?.ToString() ?? "512 MB"}";
    }

    // =====================================================================
    //  导航 / 完成
    // =====================================================================

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < StepCount - 1)
        {
            ShowStep(_step + 1);
            return;
        }

        _ = FinishAsync(startWarmup: PrefWarmup.IsOn);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => _ = FinishAsync(startWarmup: false);

    private async Task FinishAsync(bool startWarmup)
    {
        if (_finishing) return;
        _finishing = true;

        try
        {
            var settings = AppServices.Settings.Current;
            var ai = settings.Ai;

            ai.BaseUrl = string.IsNullOrWhiteSpace(AiBaseUrl.Text) ? ai.BaseUrl : AiBaseUrl.Text.Trim();
            ai.VisionModel = string.IsNullOrWhiteSpace(AiModel.Text) ? ai.VisionModel : AiModel.Text.Trim();
            if (!string.IsNullOrWhiteSpace(AiKey.Password))
                ai.ProtectedApiKey = AppServices.Settings.ProtectSecret(AiKey.Password);

            settings.AutoAssignLutForSony = PrefLut.IsOn;
            if (PrefCache.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var mb))
                settings.ThumbnailCacheMb = mb;

            settings.OnboardingCompleted = true;

            // 首次引导结束后切深色（演示的浅色只是引导期间）；重播则恢复用户原来的主题。
            var nextTheme = EnterReplay
                ? (settings.ThemeBeforeOnboarding ?? _themeBefore ?? "system")
                : "dark";
            settings.ThemeMode = nextTheme;
            await AppServices.Settings.SaveAsync();
            await AppServices.Design.SetThemeModeAsync(nextTheme, persist: false);
            await AppServices.Luts.ListAsync(forceRefresh: true);

            ShellPage.Current?.LeaveOnboarding();

            if (startWarmup && settings.Libraries.Count > 0)
            {
                AppServices.Warmup.Start();
                AppEvents.SetStatus("已开始后台预热缩略图（可在设置里停止）");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("完成引导失败", ex);
            AppEvents.SetError($"保存引导设置失败：{ex.Message}");
            _finishing = false;
        }
    }
}

