using System.Globalization;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 展示格式化：所有"给人看"的字符串都从这里出，保证网格 / 检视 / 预览三处口径一致。
/// 不放任何颜色 / 字号，只做文本与数值。
/// </summary>
public static class Formatting
{
    public static string FileSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? string.Create(CultureInfo.InvariantCulture, $"{v:0} {units[i]}")
                      : string.Create(CultureInfo.InvariantCulture, $"{v:0.#} {units[i]}");
    }

    public static string Duration(double? seconds)
    {
        if (seconds is null || seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:D2}");
    }

    public static string Date(DateTimeOffset? at) => at is null ? "—" : at.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset? at) => at is null ? "—" : at.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset? at) => at is null ? "—" : at.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Dimensions(int? w, int? h) => w is > 0 && h is > 0 ? $"{w} × {h}" : "—";

    public static string Megapixels(int? w, int? h)
    {
        if (w is not > 0 || h is not > 0) return "—";
        double mp = w.Value * (double)h.Value / 1_000_000.0;
        return string.Create(CultureInfo.InvariantCulture, $"{mp:0.#} MP");
    }

    public static string AspectRatio(int? w, int? h)
    {
        if (w is not > 0 || h is not > 0) return "—";
        double r = w.Value / (double)h.Value;
        if (Math.Abs(r - 1.0) < 0.02) return "1:1";
        if (Math.Abs(r - 4.0 / 3.0) < 0.02) return "4:3";
        if (Math.Abs(r - 3.0 / 2.0) < 0.02) return "3:2";
        if (Math.Abs(r - 16.0 / 9.0) < 0.02) return "16:9";
        if (Math.Abs(r - 9.0 / 16.0) < 0.02) return "9:16";
        if (Math.Abs(r - 2.0 / 3.0) < 0.02) return "2:3";
        if (Math.Abs(r - 3.0 / 4.0) < 0.02) return "3:4";
        return string.Create(CultureInfo.InvariantCulture, $"{r:0.00}");
    }

    public static string Shutter(double? seconds)
    {
        if (seconds is not > 0) return "—";
        return seconds.Value >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds.Value:0.#} s")
            : string.Create(CultureInfo.InvariantCulture, $"1/{Math.Round(1.0 / seconds.Value)} s");
    }

    public static string Aperture(double? f) => f is > 0 ? string.Create(CultureInfo.InvariantCulture, $"f/{f:0.#}") : "—";

    public static string Focal(double? mm) => mm is > 0 ? string.Create(CultureInfo.InvariantCulture, $"{mm:0.#} mm") : "—";

    public static string Iso(int? iso) => iso is > 0 ? $"ISO {iso}" : "—";

    public static string FrameRate(double? fps) => fps is > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.##} fps") : "—";

    public static string Bitrate(long? bps) => bps is > 0 ? $"{bps.Value / 1_000_000.0:0.#} Mbps" : "—";

    public static string Coord(double? lat, double? lon) =>
        lat is null || lon is null
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{lat.Value:0.00000}, {lon.Value:0.00000}");

    public static string Quality(double? score) => score is null ? "—" : $"{score.Value * 100:0}";

    public static string Percent(double value) => $"{value:0}%";

    public static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value!;

    public static string ThemeName(string? id, string locale = "zh-CN") =>
        string.IsNullOrWhiteSpace(id) ? "—" : Vocabularies.ThemeDisplay(id, locale);

    public static string ToneName(string? id, string locale = "zh-CN") =>
        string.IsNullOrWhiteSpace(id) ? "—" : Vocabularies.ToneDisplay(id, locale);

    public static string ColorLabelName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("none", StringComparison.OrdinalIgnoreCase)) return "无";
        return Vocabularies.DisplayOf(Vocabularies.ColorLabels, id)?.Zh ?? id;
    }

    public static string KindName(MediaKind kind) => kind switch
    {
        MediaKind.Image => "图片",
        MediaKind.Video => "视频",
        MediaKind.Audio => "音频",
        _ => "未知",
    };

    public static string PickName(bool? pick) => pick switch
    {
        true => "选用",
        false => "废片",
        _ => "未定",
    };

    public static string PostProcessName(PostProcessState state) => state switch
    {
        PostProcessState.Processed => "已调色",
        PostProcessState.Unprocessed => "未调色",
        _ => "未标记",
    };

    public static string ProvenanceName(ClassSource source) => source switch
    {
        ClassSource.Manual => "人工",
        ClassSource.Ai => "AI",
        ClassSource.Derived => "元数据",
        ClassSource.Sidecar => "边车",
        _ => "未知",
    };

    public static string SourceBadge(ClassSource source) => source switch
    {
        ClassSource.Manual => "MANUAL",
        ClassSource.Ai => "AI",
        ClassSource.Derived => "META",
        ClassSource.Sidecar => "XMP",
        _ => "",
    };

    /// <summary>评分星字形：实心 + 空心，避免每颗星一个控件。</summary>
    public static string StarsFilled(int rating) => new string('\u2605', Math.Clamp(rating, 0, 5));

    public static string StarsEmpty(int rating) => new string('\u2606', 5 - Math.Clamp(rating, 0, 5));

    /// <summary>设备显示名（与 AssetQuery.Devices 的匹配口径一致，用 DisplayDevice）。</summary>
    public static string Device(MediaAsset asset) => asset.DisplayDevice;

    public static string KindGlyph(MediaKind kind) => kind switch
    {
        MediaKind.Image => "\uE91B",   // Photo
        MediaKind.Video => "\uE8B2",   // Video
        MediaKind.Audio => "\uE8D6",   // Audio
        _ => "\uE8A5",                 // Document
    };
}