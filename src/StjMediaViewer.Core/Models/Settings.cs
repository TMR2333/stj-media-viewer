namespace StjMediaViewer.Core.Models;

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
    public int ScanParallelism { get; set; } = 4;
    public bool WatchLibraries { get; set; } = true;

    /// <summary>云同步目标（坚果云 WebDAV / Nextcloud / 群晖 NAS / 本地挂载目录）。</summary>
    public List<CloudTarget> CloudTargets { get; set; } = new();

    /// <summary>自动同步总开关（单目标另有自己的 AutoSync）。</summary>
    public bool CloudAutoSyncEnabled { get; set; } = true;
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