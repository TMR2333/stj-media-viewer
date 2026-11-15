namespace StjMediaViewer.Core.Transcode;

/// <summary>
/// 视频编码名的归一化：容器里出现的可能是 `avc1` / `h264` / `x264` / `AVC` 等十几种写法，
/// 界面上统一显示成 H.264 / H.265 / AV1 … 并给出"是否是 HEVC"这类判断。
/// </summary>
public static class VideoCodecNames
{
    /// <summary>把任意编码串（fourcc / ffmpeg 名 / 标签）归一成标准短名，例如 "H.264"。</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var v = raw.Trim().ToLowerInvariant();

        // 去掉括号里的 profile 等附加信息：h264 (high) → h264
        var paren = v.IndexOf('(');
        if (paren > 0) v = v[..paren].Trim();
        v = v.Replace("/", " ").Replace(",", " ");

        if (Matches(v, "h264", "avc1", "avc3", "avc", "x264", "h.264", "h 264", "avc1.640028")) return "H.264";
        if (Matches(v, "h265", "hevc", "hev1", "hvc1", "x265", "h.265", "h 265", "hvc2")) return "H.265";
        if (Matches(v, "av1", "av01", "svtav1", "aom-av1")) return "AV1";
        if (Matches(v, "vp9", "vp09")) return "VP9";
        if (Matches(v, "vp8", "vp08")) return "VP8";
        if (Matches(v, "prores", "ap4h", "ap4x", "apch", "apcn", "apcs", "apco")) return "ProRes";
        if (Matches(v, "dnxhd", "dnxhr", "avdn")) return "DNxHR";
        if (Matches(v, "mpeg4", "mp4v", "xvid", "divx")) return "MPEG-4";
        if (Matches(v, "mpeg2", "mpg2", "mpeg-2")) return "MPEG-2";
        if (Matches(v, "mjpeg", "mjpa", "mjpb")) return "Motion JPEG";
        if (Matches(v, "dvvh", "dvh1", "dvhe", "dolby")) return "Dolby Vision";
        if (Matches(v, "vc1", "vc-1")) return "VC-1";
        if (Matches(v, "theora")) return "Theora";
        if (Matches(v, "rawvideo", "raw")) return "RAW";
        return raw.Trim();
    }

    private static bool Matches(string value, params string[] candidates) =>
        candidates.Any(c => value.StartsWith(c, StringComparison.Ordinal) ||
                            value.Contains(" " + c, StringComparison.Ordinal));

    /// <summary>是否 HEVC/H.265 家族（含 HEVC 的各种别名）。</summary>
    public static bool IsHevc(string? raw) => Normalize(raw) == "H.265";

    /// <summary>是否长 GOP 的中间编码（ProRes / DNxHR / RAW）：压缩收益大，但也常是后期中间片。</summary>
    public static bool IsIntermediate(string? raw)
    {
        var n = Normalize(raw);
        return n is "ProRes" or "DNxHR" or "RAW";
    }

    /// <summary>是否浏览器/剪辑软件普遍能直接播的编码。</summary>
    public static bool IsWidelySupported(string? raw)
    {
        var n = Normalize(raw);
        return n is "H.264" or "H.265" or "AV1" or "VP9" or "VP8" or "MPEG-4";
    }

    /// <summary>显示用标签：H.265 后面补一个 HEVC 别名，方便用户对上相机的说法。</summary>
    public static string DisplayLabel(string? raw)
    {
        var n = Normalize(raw);
        return n switch
        {
            "H.265" => "H.265 / HEVC",
            "H.264" => "H.264 / AVC",
            "Dolby Vision" => "Dolby Vision",
            "" => "未知编码",
            _ => n,
        };
    }

    /// <summary>短徽标文字（卡片右上角用，尽量 ≤ 6 字符）。</summary>
    public static string ShortBadge(string? raw)
    {
        var n = Normalize(raw);
        return n switch
        {
            "H.265" => "H.265",
            "H.264" => "H.264",
            "HEVC" => "H.265",
            "AVC" => "H.264",
            "" => "",
            _ => n.Length <= 8 ? n : n[..8],
        };
    }
}