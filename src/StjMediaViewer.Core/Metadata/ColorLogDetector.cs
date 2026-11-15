using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>Log / 色彩判定的输入信号。全部字段可空，判定器只根据有的信号下结论。</summary>
public sealed class ColorSignals
{
    public MediaKind Kind { get; set; } = MediaKind.Video;
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? FormatCode { get; set; }
    public string? FileName { get; set; }

    /// <summary>ISOBMFF colr(nclx) / Matroska Colour 的 CICP 码。</summary>
    public int? CicpPrimaries { get; set; }
    public int? CicpTransfer { get; set; }
    public int? CicpMatrix { get; set; }

    /// <summary>ICC profile 名称（Sony S-Gamut3.Cine/S-Log3 等会写在这里）。</summary>
    public string? IccProfileName { get; set; }

    /// <summary>从容器/XML/文件名里扫到或读到的文本线索（S-Log3 / V-Log / HLG …）。</summary>
    public IEnumerable<string>? TextHints { get; set; }

    public string? XmpCameraProfile { get; set; }
    public string? XmpLookName { get; set; }
    public string? XmpColorMode { get; set; }
    public string? XmpWhiteBalance { get; set; }
    public double? XmpTemperatureK { get; set; }
}

/// <summary>Log / 色彩判定结果。</summary>
public sealed class ColorLogInfo
{
    public bool? IsLog { get; set; }
    public string? LogProfile { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? TransferCharacteristics { get; set; }
    public string? ColorMatrix { get; set; }
    public string? ColorProfileName { get; set; }
}

/// <summary>
/// Log / HDR 色彩检测：
/// 1) 权威信号：CICP（colr nclx / Matroska Colour）的 transfer characteristics；
/// 2) 显式文本：XMP crs:CameraProfile / ICC profile 名 / 容器元数据字符串 / 文件名里的 S-Log3、V-Log、C-Log3、Apple Log、D-Log、N-Log、HLG、PQ；
/// 3) 色域线索：S-Gamut3.Cine、V-Gamut、Cinema Gamut、D-Gamut、N-Gamut、Rec.2020。
/// 没有证据时 IsLog 保持 null（不臆测），由规则分类器/文件名启发式兜底。
/// </summary>
public static class ColorLogDetector
{
    /// <summary>用于在容器元数据区扫描 ASCII 的令牌（不含 2 字符短词，避免误报）。</summary>
    public static readonly IReadOnlyList<string> ScanTokens = new[]
    {
        "S-Log3", "S-Log2", "S-Gamut3.Cine", "S-Gamut3", "S-Gamut",
        "V-Log L", "V-Log", "V-Gamut", "Cinema Gamut",
        "C-Log3", "C-Log", "Canon Log 3", "Canon Log",
        "Apple Log", "AppleLog",
        "D-Log M", "D-Log", "D-Gamut",
        "N-Log", "N-Gamut",
        "F-Log2", "F-Log",
        "HLG", "ARIB-STD-B67", "ST 2084", "SMPTE 2084", "BT.2100",
        "Rec.2020", "Rec.2100", "BT.2020",
    };

    /// <summary>文件名/路径里常见的工作流命名词（例如 A7S3_SLOG3_001.MP4）。</summary>
    public static readonly IReadOnlyList<string> FileNameTokens = new[]
    {
        "SLOG3", "SLOG2", "S_LOG3", "S_LOG2", "S-LOG3", "S-LOG2",
        "VLOG", "V-LOG", "CLOG3", "C-LOG3", "CLOG", "C-LOG",
        "ALOG", "APPLELOG", "DLOG", "D-LOG", "NLOG", "N-LOG", "FLOG", "F-LOG",
        "HLG", "PQ",
    };

    // ============================================================ 公共判定

    public static ColorLogInfo Detect(ColorSignals signals)
    {
        var info = new ColorLogInfo();
        if (signals is null) return info;

        // ---- 1) CICP 基础映射（最权威的容器级色彩描述） ----
        if (signals.CicpPrimaries is { } pri) info.ColorPrimaries = MapPrimaries(pri);
        if (signals.CicpTransfer is { } trc) info.TransferCharacteristics = MapTransfer(trc);
        if (signals.CicpMatrix is { } mat) info.ColorMatrix = MapMatrix(mat);

        // ---- 2) 收集文本线索 ----
        var hints = new List<string>(8);
        if (signals.TextHints is not null)
            foreach (var h in signals.TextHints) if (!string.IsNullOrWhiteSpace(h)) hints.Add(h);
        if (!string.IsNullOrWhiteSpace(signals.XmpCameraProfile)) hints.Add(signals.XmpCameraProfile);
        if (!string.IsNullOrWhiteSpace(signals.XmpLookName)) hints.Add(signals.XmpLookName);
        if (!string.IsNullOrWhiteSpace(signals.IccProfileName)) hints.Add(signals.IccProfileName);
        if (!string.IsNullOrWhiteSpace(signals.XmpColorMode)) hints.Add(signals.XmpColorMode);

        var logProfile = MatchLogProfile(hints);
        var hdr = MatchHdrTransfer(hints);
        var gamut = MatchGamut(hints);

        // ---- 3) 判定 ----
        if (logProfile is not null)
        {
            info.IsLog = true;
            info.LogProfile = logProfile;
            info.TransferCharacteristics = logProfile;   // Log 曲线就是 transfer 的语义
            if (gamut is not null) info.ColorPrimaries = gamut;
            info.ColorProfileName = gamut is not null ? $"{gamut}/{logProfile}" : logProfile;
            return info;
        }

        if (hdr is not null)
        {
            info.IsLog = false;                          // HLG/PQ 是 HDR 传输曲线，不是 Log
            info.TransferCharacteristics = hdr;
            info.ColorPrimaries ??= "BT.2020";
            info.ColorProfileName ??= $"Rec.2020 {hdr}";
            if (gamut is not null) info.ColorPrimaries = gamut;
            return info;
        }

        // ---- 4) 只有普通 transfer：可确定"不是 Log" ----
        if (signals.CicpTransfer is { } t && MapTransfer(t) is { } mapped && mapped is not "Unspecified")
        {
            info.IsLog = false;
            info.ColorProfileName ??= info.ColorPrimaries is not null && info.ColorPrimaries != "BT.709"
                ? $"{info.ColorPrimaries} / {mapped}"
                : mapped;
            return info;
        }

        // ---- 5) 相机型号/其它线索都无 → 保持 null（未知） ----
        if (gamut is not null)
        {
            info.ColorPrimaries = gamut;
            info.ColorProfileName ??= gamut;
        }
        return info;
    }

    /// <summary>把检测结果写入 MediaMetadata（只在目标为空/未知时写）。</summary>
    public static void ApplyTo(ColorLogInfo info, MediaMetadata m)
    {
        if (info is null || m is null) return;
        m.LogProfile ??= info.LogProfile;
        m.IsLog ??= info.IsLog;
        m.ColorPrimaries ??= info.ColorPrimaries;
        m.TransferCharacteristics ??= info.TransferCharacteristics;
        m.ColorMatrix ??= info.ColorMatrix;
        m.ColorProfileName ??= info.ColorProfileName;
    }

    // ============================================================ 文本匹配

    /// <summary>把一堆文本线索归到规范 Log 名称；无匹配返回 null。顺序敏感（V-Log L 先于 V-Log）。</summary>
    public static string? MatchLogProfile(IEnumerable<string>? hints)
    {
        if (hints is null) return null;
        var corpus = BuildCorpus(hints);
        if (corpus.Length == 0) return null;

        // 注意顺序：更具体的放前面
        if (ContainsAny(corpus, "s-log3", "slog3", "s log3", "s-log 3")) return "S-Log3";
        if (ContainsAny(corpus, "s-log2", "slog2", "s log2", "s-log 2")) return "S-Log2";
        if (ContainsAny(corpus, "v-log l", "vlog l", "v-log-l")) return "V-Log L";
        if (ContainsAny(corpus, "v-log", "vlog")) return "V-Log";
        if (ContainsAny(corpus, "c-log3", "clog3", "canon log 3", "canonlog3")) return "C-Log3";
        if (ContainsAny(corpus, "c-log", "clog", "canon log", "canonlog")) return "C-Log";
        if (ContainsAny(corpus, "apple log", "applelog", "apple-log")) return "Apple Log";
        if (ContainsAny(corpus, "d-log m", "dlogm", "d log m")) return "D-Log M";
        if (ContainsAny(corpus, "d-log", "dlog")) return "D-Log";
        if (ContainsAny(corpus, "n-log", "nlog")) return "N-Log";
        if (ContainsAny(corpus, "f-log2", "flog2")) return "F-Log2";
        if (ContainsAny(corpus, "f-log", "flog")) return "F-Log";
        return null;
    }

    /// <summary>识别 HLG / PQ 传输曲线（区分大小写不敏感）。</summary>
    public static string? MatchHdrTransfer(IEnumerable<string>? hints)
    {
        if (hints is null) return null;
        var corpus = BuildCorpus(hints);
        if (corpus.Length == 0) return null;
        if (ContainsAny(corpus, "arib-std-b67", "hlg", "bt.2100 hlg", "rec.2100 hlg")) return "HLG";
        if (ContainsAny(corpus, "st 2084", "st2084", "smpte 2084", "smpte2084", "pq", "hdr10", "dolby vision")) return "PQ (ST 2084)";
        return null;
    }

    /// <summary>识别相机 Log 色域名。</summary>
    public static string? MatchGamut(IEnumerable<string>? hints)
    {
        if (hints is null) return null;
        var corpus = BuildCorpus(hints);
        if (corpus.Length == 0) return null;
        if (ContainsAny(corpus, "s-gamut3.cine", "s gamut3 cine", "sgamut3cine")) return "S-Gamut3.Cine";
        if (ContainsAny(corpus, "s-gamut3", "sgamut3")) return "S-Gamut3";
        if (ContainsAny(corpus, "s-gamut", "sgamut")) return "S-Gamut";
        if (ContainsAny(corpus, "v-gamut", "vgamut")) return "V-Gamut";
        if (ContainsAny(corpus, "cinema gamut", "cinemagamut")) return "Cinema Gamut";
        if (ContainsAny(corpus, "d-gamut", "dgamut")) return "D-Gamut";
        if (ContainsAny(corpus, "n-gamut", "ngamut")) return "N-Gamut";
        if (ContainsAny(corpus, "f-gamut", "fgamut")) return "F-Gamut";
        if (ContainsAny(corpus, "rec.2020", "rec2020", "bt.2020", "bt2020")) return "BT.2020";
        return null;
    }

    private static string BuildCorpus(IEnumerable<string> hints)
    {
        var sb = new System.Text.StringBuilder(128);
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            sb.Append(' ').Append(h.ToLowerInvariant());
        }
        return sb.ToString();
    }

    private static bool ContainsAny(string corpus, params string[] needles)
    {
        foreach (var n in needles)
            if (corpus.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    // ============================================================ CICP 映射

    public static string? MapPrimaries(int code) => code switch
    {
        1 => "BT.709",
        2 => null,                    // Unspecified
        4 => "BT.470M",
        5 => "BT.601 (PAL)",
        6 => "BT.601 (NTSC)",
        7 => "SMPTE 240M",
        8 => "Film",
        9 => "BT.2020",
        10 => "XYZ",
        11 => "DCI-P3",
        12 => "P3-D65",
        22 => "BT.2020",
        _ => $"CICP primaries {code}",
    };

    public static string? MapTransfer(int code) => code switch
    {
        1 => "BT.709",
        2 => null,                    // Unspecified
        4 => "Gamma 2.2",
        5 => "Gamma 2.8",
        6 => "SMPTE 170M",
        7 => "SMPTE 240M",
        8 => "Linear",
        9 => "Log (100:1)",
        10 => "Log (316:1)",
        11 => "IEC 61966-2-4",
        12 => "BT.1361",
        13 => "sRGB",
        14 => "BT.2020 (10-bit)",
        15 => "BT.2020 (12-bit)",
        16 => "PQ (ST 2084)",
        17 => "SMPTE 428-1",
        18 => "HLG",
        _ => $"CICP transfer {code}",
    };

    public static string? MapMatrix(int code) => code switch
    {
        0 => "Identity (GBR)",
        1 => "BT.709",
        2 => null,
        4 => "FCC",
        5 => "BT.601",
        6 => "SMPTE 170M",
        7 => "SMPTE 240M",
        8 => "YCgCo",
        9 => "BT.2020 non-constant",
        10 => "BT.2020 constant",
        11 => "SMPTE 2085",
        12 => "Chromaticity-derived non-constant",
        13 => "Chromaticity-derived constant",
        14 => "ICtCp",
        _ => $"CICP matrix {code}",
    };

    /// <summary>从 CICP 三码直接构造检测结果（IsoBmffParser / MatroskaParser 用）。</summary>
    public static ColorLogInfo FromCicp(int? primaries, int? transfer, int? matrix, string? iccName = null,
        IEnumerable<string>? textHints = null, string? fileName = null, string? cameraProfile = null)
    {
        return Detect(new ColorSignals
        {
            CicpPrimaries = primaries,
            CicpTransfer = transfer,
            CicpMatrix = matrix,
            IccProfileName = iccName,
            TextHints = textHints,
            FileName = fileName,
            XmpCameraProfile = cameraProfile,
        });
    }

    /// <summary>日志诊断用。</summary>
    public static void LogResult(string path, ColorLogInfo info)
    {
        if (info?.LogProfile is null) return;
        Log.Info($"[ColorLogDetector] {Path.GetFileName(path)} → {info.LogProfile} (primaries={info.ColorPrimaries})");
    }
}