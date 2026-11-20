using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.App.ViewModels;

namespace StjMediaViewer.App;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    /// <summary>FirstChanceException 的递归保护（写日志失败时不能再次进入）。</summary>
    [ThreadStatic] private static bool _firstChanceHandling;
    private static int _firstChanceSeen;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FirstChanceCounts = new();

    /// <summary>日志/IO 类异常：常见且无害（文件被占用），绝不能在其处理器里再写日志。</summary>
    private static bool IsLoggingOrIoNoise(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is UnauthorizedAccessException or IOException or System.Security.SecurityException) return true;
            var stack = cur.StackTrace ?? "";
            if (stack.Contains("StjMediaViewer.Core.Util.Log", StringComparison.Ordinal)) return true;
            if (stack.Contains("System.IO.File.AppendAllText", StringComparison.Ordinal)) return true;
        }
        return false;
    }

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
            // 关键：异常处理器里再写日志，一旦写日志本身失败（文件被占用/权限被改），
            // 就会无限递归 FirstChance → 栈溢出（0xC00000FD），表现为"双击打不开"。
            if (IsLoggingOrIoNoise(e.Exception)) return;
            if (_firstChanceHandling) return;

            // 同一种异常（尤其 COMException）一次刷新里会成百上千次：
            // 每次都在 UI 线程同步写日志会放大卡顿，这里按 (类型+HRESULT) 限流。
            var key = e.Exception.GetType().FullName + "|" + e.Exception.HResult.ToString("X8");
            var seen = Interlocked.Increment(ref _firstChanceSeen);
            var count = FirstChanceCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (seen > 2000 || (count > 20 && count % 200 != 0)) return;

            _firstChanceHandling = true;
            try
            {
                Core.Util.Log.Warn($"[first-chance] {e.Exception.GetType().Name} hr=0x{e.Exception.HResult:X8} " +
                                   $"thread={Environment.CurrentManagedThreadId} msg={e.Exception.Message} " +
                                   $"occurrence={count}\n{e.Exception.StackTrace}");
            }
            finally
            {
                _firstChanceHandling = false;
            }
        };

        Services.CrashGuard.Install();

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
    /// <summary>启动后清理失效索引（只处理当前可访问的媒体库根目录）。</summary>
    private static async Task PurgeMissingAssetsAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var libraries = await AppServices.Library.GetLibrariesAsync();
            int removed = 0;
            foreach (var library in libraries)
            {
                if (string.IsNullOrWhiteSpace(library.Path) || !Directory.Exists(library.Path)) continue;
                removed += await AppServices.Library.PurgeMissingAsync(library.Id);
            }
            if (removed > 0)
            {
                Core.Util.Log.Info($"[startup] 已清理失效素材记录 {removed} 条（文件已不在磁盘上）");
                MainWindow?.DispatcherQueue.TryEnqueue(() => AppEvents.RaiseLibraryContentChanged());
            }
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("清理失效素材记录失败", ex);
        }
    }

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

            // 云同步后台调度：目标就绪后开始轮询 + 监听本地变更（自动同步只有目标开了 AutoSync 才真正跑）
            try
            {
                AppServices.CloudAuto.RefreshTargets();
                if (AppServices.Settings.Current.CloudAutoSyncEnabled) AppServices.CloudAuto.Start();
            }
            catch (Exception cloudEx)
            {
                Core.Util.Log.Warn("启动云同步调度失败", cloudEx);
            }

            ScreenCapture.ScheduleIfRequested(MainWindow);

            // 后台清理"索引里有、磁盘上已经没了"的记录（比如在别的软件里移动/删除过文件）。
            // 库根目录当前不可访问时会整库跳过，避免移动硬盘没插就误删。
            _ = Task.Run(PurgeMissingAssetsAsync);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Error("启动失败", ex);
            MainWindow = new ErrorWindow(ex);
            MainWindow.Activate();
        }
    }
}