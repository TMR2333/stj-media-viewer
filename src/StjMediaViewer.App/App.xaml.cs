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
        UnhandledException += (_, e) =>
        {
            Core.Util.Log.Error("未处理异常", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = LaunchAsync();
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
        }
        catch (Exception ex)
        {
            Core.Util.Log.Error("启动失败", ex);
            MainWindow = new ErrorWindow(ex);
            MainWindow.Activate();
        }
    }
}