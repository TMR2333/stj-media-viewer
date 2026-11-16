namespace StjMediaViewer.Core.Color;

/// <summary>LUT 分类：内置（按官方色彩科学公式生成）、相机官方风格、创意风格。</summary>
public enum LutCategory
{
    /// <summary>内置：由公开的色彩传输函数（S-Log3 / S-Log2 / HLG）与色域矩阵推导生成。</summary>
    BuiltIn = 0,
    /// <summary>官方：文件名匹配厂商/机型风格命名（如 Sony 的 s709、LC-709、S-Gamut）。</summary>
    Official = 1,
    /// <summary>创意：用户自己放进来的风格化 LUT。</summary>
    Creative = 2,
}

/// <summary>一条 LUT 记录（.cube 文件）。</summary>
public sealed class LutEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string FilePath { get; init; } = "";
    /// <summary>3D 尺寸（1D LUT 时为 1D 表长度）。</summary>
    public int Size { get; init; }
    public bool Is1D { get; init; }
    public LutCategory Category { get; init; }
    /// <summary>适配的相机品牌（Sony / Canon / Panasonic / Nikon / DJI / 通用）。</summary>
    public string Brand { get; init; } = "通用";
    /// <summary>适配的 Log 曲线（S-Log3 / S-Log2 / HLG / V-Log / C-Log3 / 通用）。</summary>
    public string LogProfile { get; init; } = "通用";
    public string? Description { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }

    public string CategoryText => Category switch
    {
        LutCategory.BuiltIn => "内置",
        LutCategory.Official => "官方风格",
        _ => "创意风格",
    };

    public string SizeText => Is1D ? $"1D · {Size}" : $"{Size}³";

    /// <summary>展示用副标题：品牌 · Log · 尺寸。</summary>
    public string Subtitle => $"{Brand} · {LogProfile} · {SizeText}";

    public override string ToString() => $"{Name} ({Subtitle})";
}