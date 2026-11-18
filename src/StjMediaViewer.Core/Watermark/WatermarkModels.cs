namespace StjMediaViewer.Core.Watermark;

/// <summary>水印品牌（来自 Watermark Studio 网页里的 5 个品牌标记）。</summary>
public enum WatermarkBrand
{
    Stjarna = 0,
    Hasselblad = 1,
    Canon = 2,
    Nikon = 3,
    Sony = 4,
}

/// <summary>风格预设：对应网页里的 classic / soft / dark / film / gallery / blackGallery。</summary>
public enum WatermarkPreset
{
    Classic = 0,
    Soft = 1,
    Dark = 2,
    Film = 3,
    Gallery = 4,
    BlackGallery = 5,
}

/// <summary>水印墨色（Canon 的 red 对应网页 canon 的红色原标）。</summary>
public enum WatermarkInk
{
    White = 0,
    Black = 1,
    Red = 2,
}

public enum WatermarkFormat
{
    Jpeg = 0,
    Png = 1,
    Webp = 2,
}

/// <summary>当前水印参数（与网页 03 / ADJUST 一一对应）。</summary>
public sealed class WatermarkStyle
{
    public WatermarkBrand Brand { get; set; } = WatermarkBrand.Stjarna;
    public WatermarkPreset Preset { get; set; } = WatermarkPreset.Classic;

    /// <summary>水印宽度 = 画面宽度 × Size%（网页 12%–40%）。</summary>
    public double Size { get; set; } = 20;

    /// <summary>不透明度 %。</summary>
    public double Opacity { get; set; } = 95;

    /// <summary>垂直位置 %（水印顶边在画面高度中的位置）。</summary>
    public double Position { get; set; } = 11;

    public WatermarkInk Ink { get; set; } = WatermarkInk.White;

    /// <summary>是否加投影（网页 classic 默认开）。</summary>
    public bool Shadow { get; set; } = true;

    public WatermarkStyle Clone() => new()
    {
        Brand = Brand,
        Preset = Preset,
        Size = Size,
        Opacity = Opacity,
        Position = Position,
        Ink = Ink,
        Shadow = Shadow,
    };
}

/// <summary>预设参数表（数值与网页保持一致）。</summary>
public static class WatermarkPresets
{
    public sealed record Values(double Size, double Opacity, double Position, WatermarkInk Ink, bool Shadow);

    public static Values Get(WatermarkPreset preset) => preset switch
    {
        WatermarkPreset.Classic => new(20, 95, 11, WatermarkInk.White, true),
        WatermarkPreset.Soft => new(20, 48, 76, WatermarkInk.White, false),
        WatermarkPreset.Dark => new(20, 62, 95, WatermarkInk.Black, false),
        WatermarkPreset.Film => new(20, 78, 87, WatermarkInk.White, false),
        WatermarkPreset.Gallery => new(20, 93, 93, WatermarkInk.Black, false),
        WatermarkPreset.BlackGallery => new(20, 93, 93, WatermarkInk.White, false),
        _ => new(20, 95, 11, WatermarkInk.White, true),
    };

    /// <summary>品牌专属默认值（网页里切到 Canikony 时会覆盖大小/透明度/位置）。</summary>
    public static Values? BrandDefaults(WatermarkBrand brand) => brand switch
    {
        WatermarkBrand.Canon => new(12, 83, 8, WatermarkInk.White, false),
        WatermarkBrand.Nikon => new(5, 78, 91, WatermarkInk.White, false),
        WatermarkBrand.Sony => new(12, 83, 89, WatermarkInk.White, false),
        _ => null,
    };

    public static string Display(WatermarkPreset preset) => preset switch
    {
        WatermarkPreset.Classic => "经典",
        WatermarkPreset.Soft => "柔淡",
        WatermarkPreset.Dark => "暗调",
        WatermarkPreset.Film => "胶片",
        WatermarkPreset.Gallery => "白框画廊",
        WatermarkPreset.BlackGallery => "黑框画廊",
        _ => preset.ToString(),
    };

    public static string Display(WatermarkBrand brand) => brand switch
    {
        WatermarkBrand.Stjarna => "STJARNA",
        WatermarkBrand.Hasselblad => "HASSELBLAD",
        WatermarkBrand.Canon => "Canon",
        WatermarkBrand.Nikon => "Nikon",
        WatermarkBrand.Sony => "SONY",
        _ => brand.ToString(),
    };

    public static string Display(WatermarkInk ink) => ink switch
    {
        WatermarkInk.White => "白色",
        WatermarkInk.Black => "黑色",
        WatermarkInk.Red => "红色（Canon）",
        _ => ink.ToString(),
    };

    public static string Display(WatermarkFormat format) => format switch
    {
        WatermarkFormat.Jpeg => "JPG",
        WatermarkFormat.Png => "PNG",
        _ => "WEBP",
    };

    public static string Extension(WatermarkFormat format) => format switch
    {
        WatermarkFormat.Jpeg => ".jpg",
        WatermarkFormat.Png => ".png",
        _ => ".webp",
    };
}

/// <summary>批量导出结果。</summary>
public sealed class WatermarkExportSummary
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public List<string> Errors { get; } = new();

    public string Describe() => Failed == 0
        ? $"导出 {Succeeded} 张水印图"
        : $"导出 {Succeeded} 张，失败 {Failed} 张";
}