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
    public bool ShowGlassShowcase { get; set; }
    public string? LastViewId { get; set; }
}

public static class DesignLanguageIds
{
    public const string Fluent = "fluent";
    public const string Md3 = "md3";
    public const string Glass = "glass";

    public static readonly string[] All = { Fluent, Md3, Glass };

    public static bool IsValid(string? id) => id is Fluent or Md3 or Glass;
    public static string Normalize(string? id) => IsValid(id) ? id! : Fluent;
}