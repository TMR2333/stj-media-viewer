using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;

namespace StjMediaViewer.App;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        Instance = this;
        InitializeComponent();

        // 诊断钩子：记录"首次异常"的完整堆栈（跳过取消异常这种噪声）。
        // XAML 里的 stowed exception（0xC000027B 闪退）不会走 UnhandledException，
        // 有这条日志才能在用户机器上定位到底是哪一步炸的。
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is OperationCanceledException) return;
            if (IsWinUiProbeNoise(e.Exception)) return;

            Core.Util.Log.Warn("[first-chance] " + e.Exception.GetType().Name + ": " +
                               e.Exception.Message + "\n" + (e.Exception.StackTrace ?? ""));
        };

        UnhandledException += (_, e) =>
        {
            Core.Util.Log.Error("未处理异常（UI 线程）", e.Exception);
            e.Handled = true;
        };

        // 后台线程 / 线程池里的未处理异常同样会终止进程（0xC000027B 常见来源），先把现场写进日志。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Util.Log.Error("未处理异常（后台线程）", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Core.Util.Log.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = LaunchAsync();
    }

    /// <summary>
    /// WinUI 启动时会反射探一下 <c>ApplicationData.Current</c>：非打包应用必然抛
    /// InvalidOperationException（"Operation is not valid due to the current state of the object"），
    /// 外层常被包成 TargetInvocationException。这是正常噪声，不进日志。
    /// </summary>
    private static bool IsWinUiProbeNoise(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if ((cur.StackTrace ?? "").Contains("ApplicationData", StringComparison.Ordinal)) return true;
            if ((cur.Message ?? "").Contains("Operation is not valid due to the current state", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private async Task LaunchAsync()
    {
        try
        {
            await AppServices.InitializeAsync();
            MainWindow = new MainWindow();
            MainWindow.Activate();
            if (MainWindow is global::StjMediaViewer.App.MainWindow mw) mw.PositionWindow();
            AppServices.WindowChrome.Configure(MainWindow);
            await AppServices.Design.ApplySavedAsync();

            ScreenCapture.ScheduleIfRequested(MainWindow);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Error("启动失败", ex);
            MainWindow = new ErrorWindow(ex);
            MainWindow.Activate();
        }
    }
}