using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Settings;

/// <summary>
/// 应用数据目录约定。默认 <c>%LOCALAPPDATA%\StjMediaViewer</c>；
/// 设置环境变量 <c>STJMEDIA_APPDATA</c> 可重定向（便携模式 / 自动化测试隔离）。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "StjMediaViewer";
    public const string SettingsFileName = "settings.json";
    public const string LogFileName = "stj-media-viewer.log";

    /// <summary>可通过 <c>STJMEDIA_APPDATA</c> 重定向根目录（测试用）。</summary>
    public const string AppDataOverrideVariable = "STJMEDIA_APPDATA";

    private static readonly string RootDirectory = ResolveRoot();

    /// <summary>应用数据根目录：<c>%LOCALAPPDATA%\StjMediaViewer</c>。</summary>
    public static string AppDataDirectory => RootDirectory;

    public static string SettingsFilePath => Path.Combine(RootDirectory, SettingsFileName);

    public static string LogDirectory => RootDirectory;

    public static string LogFilePath => Path.Combine(RootDirectory, LogFileName);

    public static string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    public static string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "Thumbnails");

    public static string AiCacheDirectory => Path.Combine(CacheDirectory, "Ai");

    private static string ResolveRoot()
    {
        try
        {
            var over = Environment.GetEnvironmentVariable(AppDataOverrideVariable);
            if (!string.IsNullOrWhiteSpace(over)) return PathUtil.Normalize(over);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                local = Path.Combine(profile, "AppData", "Local");
            }
            return Path.Combine(local, AppFolderName);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), AppFolderName);
        }
    }

    /// <summary>幂等地创建运行时需要的目录；失败只记日志不抛。</summary>
    public static void EnsureCreated()
    {
        foreach (var dir in new[] { RootDirectory, CacheDirectory, ThumbnailCacheDirectory, AiCacheDirectory })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Log.Warn($"无法创建目录: {dir}", ex); }
        }
    }
}