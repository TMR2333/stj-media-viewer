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
            AppServices.WindowChrome.Configure(MainWindow);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Error("启动失败", ex);
            MainWindow = new ErrorWindow(ex);
            MainWindow.Activate();
        }
    }
}