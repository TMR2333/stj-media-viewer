using System.Text.Json.Serialization;

namespace StjMediaViewer.Core.Exchange;

/// <summary>
/// 跨平台交换文件（`stj-media-viewer/exchange` v1）的模型。
/// 与 macOS 版 `Sources/STJCore/SettingsExchange.swift` 共用同一契约；
/// 只放"界面偏好 + 用户编辑成果 + 合集结构"，**绝不含密钥/口令/缓存路径**。
/// </summary>
public sealed class ExchangeDocument
{
    public const string FormatId = "stj-media-viewer/exchange";
    public const int CurrentFormatVersion = 1;

    public string Format { get; set; } = FormatId;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ExchangeAppInfo App { get; set; } = new();
    public ExchangeSettings Settings { get; set; } = new();
    public List<ExchangeAsset> Assets { get; set; } = new();
    public List<ExchangeCollection> Collections { get; set; } = new();
}

public sealed class ExchangeAppInfo
{
    public string Name { get; set; } = "STJ Media Viewer";
    public string Version { get; set; } = "";
    /// <summary>windows / macos</summary>
    public string Platform { get; set; } = "windows";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>只含可跨平台的部分；未列出的 Windows 设置一律不进文件。</summary>
public sealed class ExchangeSettings
{
    /// <summary>system / light / dark</summary>
    public string? ThemeMode { get; set; }
    /// <summary>small / medium / large（Windows 的 list 密度忽略）</summary>
    public string? Density { get; set; }
    public ExchangeSort? Sort { get; set; }
    public ExchangePanels? Panels { get; set; }
    public bool? FoldRawPairs { get; set; }
}

public sealed class ExchangeSort
{
    /// <summary>captured / fileName / modified / size / rating / indexed</summary>
    public string? Key { get; set; }
    public bool Ascending { get; set; }
}

public sealed class ExchangePanels
{
    public bool Sidebar { get; set; } = true;
    public bool Inspector { get; set; } = true;
}

/// <summary>一条素材的编辑成果；只写非默认字段（省略 = 未设置）。</summary>
public sealed class ExchangeAsset
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>仅当"同名同大小"多行时用于消歧；正斜杠。</summary>
    public string? RelativePath { get; set; }

    public int? Rating { get; set; }
    /// <summary>中文色标名：红 / 橙 / 黄 / 绿 / 蓝 / 紫。</summary>
    public string? ColorLabel { get; set; }
    /// <summary>三态：1 选用 / 0 废片 / null 未标记。</summary>
    public int? IsPick { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    /// <summary>多主题，第一个是主主题。</summary>
    public List<string>? Themes { get; set; }
    public string? Tone { get; set; }
    public List<string>? Keywords { get; set; }
    public string? Title { get; set; }
    /// <summary>0/1 = 未调色，>1 = 已调色（导出只在 >1 时写）。</summary>
    public int? PostProcess { get; set; }

    [JsonIgnore]
    public bool HasAnyEdit =>
        Rating is not null || ColorLabel is not null || IsPick is not null ||
        !string.IsNullOrWhiteSpace(Country) || !string.IsNullOrWhiteSpace(Province) ||
        !string.IsNullOrWhiteSpace(City) || Themes is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(Tone) || Keywords is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(Title) || PostProcess is not null;
}

/// <summary>合集：导入时按名字匹配，存在则补成员，不存在则新建。</summary>
public sealed class ExchangeCollection
{
    public string Name { get; set; } = "";
    public List<ExchangeAssetKey> Assets { get; set; } = new();
}

/// <summary>素材身份（fileName 小写 + sizeBytes；relativePath 仅用于消歧）。</summary>
public sealed class ExchangeAssetKey
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? RelativePath { get; set; }
}