namespace StjMediaViewer.Core.Models;

/// <summary>媒体库重新定位的结果（用户删了/移动了库目录后修复用）。</summary>
public sealed record LibraryRelocateResult(
    int Updated,
    int Missing,
    string OldPath,
    string NewPath)
{
    public int Total => Updated + Missing;
}

public sealed class LibraryRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;
    public bool WatchForChanges { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastScanAt { get; set; }
    public int AssetCount { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class AiSettings
{
    /// <summary>openai | azure-openai | ollama | custom —— 全部走 OpenAI 兼容协议。</summary>
    public string ProviderId { get; set; } = "openai";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>DPAPI 加密后的密文（Base64）。空表示未配置。</summary>
    public string? ProtectedApiKey { get; set; }
    public string VisionModel { get; set; } = "gpt-4o-mini";
    public string? TextModel { get; set; }
    public bool VisionEnabled { get; set; } = true;
    public bool UseJsonSchema { get; set; } = true;
    public int Concurrency { get; set; } = 3;
    public int MaxImageDimension { get; set; } = 1024;
    public int JpegQuality { get; set; } = 82;
    public int TimeoutSeconds { get; set; } = 90;
    public double Temperature { get; set; } = 0.2;
    public int DailyCallLimit { get; set; } = 2000;
    public int MaxRetries { get; set; } = 2;
    public bool DryRun { get; set; }
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    public string? CustomPrompt { get; set; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ProtectedApiKey);
}

public sealed class GridSettings
{
    public int CardSize { get; set; } = 220;
    public bool ShowFilename { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
    public string Density { get; set; } = "comfortable"; // compact | comfortable | spacious
    public int ThumbnailQuality { get; set; } = 320;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DesignLanguage { get; set; } = DesignLanguageIds.Fluent;
    public string ThemeMode { get; set; } = "system";  // system | light | dark
    public string Locale { get; set; } = "zh-CN";
    public List<LibraryRoot> Libraries { get; set; } = new();
    public string? LastLibraryId { get; set; }
    public AiSettings Ai { get; set; } = new();
    public GridSettings Grid { get; set; } = new();

    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public bool UseFfmpegWhenAvailable { get; set; } = true;
    public bool UseOnlineGeocoding { get; set; }
    public bool WriteXmpOnSave { get; set; } = true;
    public bool AutoClassifyOnImport { get; set; }
    public int ThumbnailCacheMb { get; set; } = 512;

    /// <summary>缩略图缓存根目录；null/空 = 默认 %LOCALAPPDATA%\StjMediaViewer\Cache。</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>自动清理缓存（启动时 + 按间隔）。</summary>
    public bool CacheAutoCleanEnabled { get; set; } = true;

    /// <summary>缓存文件最多保留多少天（0 = 不限）。</summary>
    public int CacheMaxAgeDays { get; set; } = 90;

    /// <summary>自动清理间隔（小时）。</summary>
    public int CacheCleanIntervalHours { get; set; } = 6;
    public int ScanParallelism { get; set; } = 4;
    public bool WatchLibraries { get; set; } = true;

    /// <summary>用户 LUT 目录（放 .cube 文件的文件夹；为空时用默认目录）。</summary>
    public string? LutFolderPath { get; set; }
    /// <summary>识别到 Sony 相机 + Log 素材时自动套用内置基准 LUT。</summary>
    /// <summary>总开关：是否按"设备绑定"给 Log 素材自动套监视 LUT。</summary>
    public bool AutoAssignLutForSony { get; set; } = true;

    /// <summary>设备 ↔ LUT 绑定表（设备键 → LUT id）。只有命中的设备才会自动套。</summary>
    public List<DeviceLutBinding> DeviceLutBindings { get; set; } = new();

    /// <summary>自定义设备名：设备标识（device_key）→ 用户起的名字，例如 "SONY ILCE-7M4" → "我的 A7M4"。</summary>
    public Dictionary<string, string> DeviceAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>云同步目标（坚果云 WebDAV / Nextcloud / 群晖 NAS / 本地挂载目录）。</summary>
    public List<CloudTarget> CloudTargets { get; set; } = new();

    /// <summary>自动同步总开关（单目标另有自己的 AutoSync）。</summary>
    public bool CloudAutoSyncEnabled { get; set; } = true;
    /// <summary>新用户引导是否已完成（false → 启动时进入引导页）。</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>进入引导前的主题（引导演示固定用浅色，结束后恢复/切深色用）。</summary>
    public string? ThemeBeforeOnboarding { get; set; }

    public bool ShowGlassShowcase { get; set; }
    public string? LastViewId { get; set; }
}

/// <summary>
/// 设计语言标识。当前产品只保留一套：Fluent（微软设计语言）。
/// 历史版本尝试过 Material Design 3 与 Liquid Glass 皮肤，因观感不达标已下线；
/// 这里保留 Normalize 收口，保证旧 settings.json 里的 "md3"/"glass" 会安全回落到 Fluent。
/// </summary>
public static class DesignLanguageIds
{
    public const string Fluent = "fluent";

    public static readonly string[] All = { Fluent };

    public static bool IsValid(string? id) => id is Fluent;
    public static string Normalize(string? id) => Fluent;
}