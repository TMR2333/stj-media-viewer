using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 元数据解析共用的底层工具：字节序读取、fourcc、日期转换、文本清洗、ASCII 令牌扫描。
/// 全部为无状态静态方法，可并发调用。
/// </summary>
internal static class MetaUtil
{
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>随机访问用（会 seek 的解析器：ISOBMFF / TIFF）。</summary>
    public static FileStream OpenRandomAccess(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        DefaultBufferSize, FileOptions.RandomAccess);

    /// <summary>顺序访问用（Matroska / 音频）。</summary>
    public static FileStream OpenSequential(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        DefaultBufferSize, FileOptions.SequentialScan);

    // ---------------------------------------------------------------- 字节序

    public static ushort U16BE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.Slice(o, 2)) : (ushort)0;
    public static uint U32BE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4)) : 0u;
    public static ulong U64BE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 8 <= b.Length ? BinaryPrimitives.ReadUInt64BigEndian(b.Slice(o, 8)) : 0ul;

    public static ushort U16LE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2)) : (ushort)0;
    public static uint U32LE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4)) : 0u;
    public static ulong U64LE(ReadOnlySpan<byte> b, int o) =>
        o >= 0 && o + 8 <= b.Length ? BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o, 8)) : 0ul;

    // ---------------------------------------------------------------- fourcc / ASCII

    /// <summary>把 4 字节读成 fourcc（不可打印字节替换为 U+FFFD）。</summary>
    public static string FourCC(ReadOnlySpan<byte> b, int o)
    {
        if (o < 0 || o + 4 > b.Length) return "";
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var v = b[o + i];
            c[i] = v is >= 0x20 and < 0x7F ? (char)v : '\uFFFD';
        }
        return new string(c);
    }

    public static bool FourCCEquals(ReadOnlySpan<byte> b, int o, string fourcc)
    {
        if (fourcc.Length != 4 || o < 0 || o + 4 > b.Length) return false;
        return b[o] == (byte)fourcc[0] && b[o + 1] == (byte)fourcc[1] &&
               b[o + 2] == (byte)fourcc[2] && b[o + 3] == (byte)fourcc[3];
    }

    /// <summary>读取 ASCII 串（到 delimiter 或 len 或 0 结束），清洗控制字符。</summary>
    public static string? Ascii(ReadOnlySpan<byte> b, int o, int len)
    {
        if (o < 0 || o >= b.Length) return null;
        var end = Math.Min(b.Length, o + Math.Max(0, len));
        var sb = new StringBuilder(end - o);
        for (var i = o; i < end; i++)
        {
            var v = b[i];
            if (v == 0) break;
            sb.Append(v is >= 0x20 and < 0x7F ? (char)v : ' ');
        }
        return CleanText(sb.ToString());
    }

    /// <summary>在一段字节里按 UTF-8 解出可读文本（用于 XML/XMP 之外的头部字符串）。</summary>
    public static string? Utf8(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return null;
        try { return CleanText(Encoding.UTF8.GetString(b).TrimEnd('\0')); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 文本清洗

    /// <summary>去掉控制字符 / NUL / 首尾空白，压缩连续空白；超长截断。</summary>
    public static string? CleanText(string? s, int maxLen = 2048)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder(Math.Min(s.Length, maxLen));
        var pendingSpace = false;
        foreach (var ch in s)
        {
            if (ch == '\0') break;                                // NUL = 字符串结束
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (char.IsControl(ch)) continue;
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
            if (sb.Length >= maxLen) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>清洗 EXIF 厂商字符串（SONY / "NIKON CORPORATION" / "OLYMPUS IMAGING CORP." 等）。</summary>
    public static string? CleanMake(string? s)
    {
        var t = CleanText(s, 128);
        if (t is null) return null;
        t = t.Trim().TrimEnd('.', ',', ';').Trim();
        if (t.Length == 0) return null;
        // 把全大写的厂商名转成正常大小写（"SONY" -> "Sony"，但保留 "DJI"/"OM SYSTEM" 等已知形态）
        if (t.All(c => !char.IsLower(c)))
        {
            switch (t.Replace(" ", "").Replace(".", "").Replace(",", ""))
            {
                case "SONY": return "Sony";
                case "CANON": case "CANONINC": return "Canon";
                case "NIKONCORPORATION": case "NIKON": return "Nikon";
                case "FUJIFILMINCORP": case "FUJIFILM": return "Fujifilm";
                case "PANASONIC": return "Panasonic";
                case "OLYMPUSIMAGINGCORP": case "OLYMPUSCORPORATION": case "OLYMPUSIMAGINGCO": return "Olympus";
                case "OMDIGITALSOLUTIONS": case "OMSYSTEM": return "OM System";
                case "RICOHIMAGINGCOMPANYLIMITED": case "RICOHIMAGINGCOMPANY": case "RICOH": return "Ricoh";
                case "PENTAXRICOHIMAGINGCOMPANYLIMITED": return "Pentax";
                case "LEICACAMERAAG": case "LEICA": return "Leica";
                case "HASSELBLAD": return "Hasselblad";
                case "PHASEONE": return "Phase One";
                case "SIGMA": return "Sigma";
                case "SAMSUNG": return "Samsung";
                case "GOOGLE": return "Google";
                case "XIAOMI": return "Xiaomi";
                case "HUAWEI": return "Huawei";
                case "DJI": return "DJI";
                case "GOPRO": return "GoPro";
                case "INSTA360": return "Insta360";
                case "APPLE": return "Apple";
                case "ONEPLUS": return "OnePlus";
                case "OPPO": return "OPPO";
                case "VIVO": return "vivo";
                case "MOTOROLA": return "Motorola";
                case "NOTHING": return "Nothing";
                case "BLACKMAGICDESIGN": return "Blackmagic Design";
                case "RED": return "RED";
                case "ARRI": return "ARRI";
            }
        }
        return t;
    }

    // ---------------------------------------------------------------- 日期

    private static readonly DateTimeOffset MacEpoch = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EbmlEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UnixEpoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>QuickTime / ISOBMFF 的 1904 纪元秒数 → UTC 时间。数值明显不合理时返回 null。</summary>
    public static DateTimeOffset? FromMacEpoch(long seconds)
    {
        if (seconds <= 0) return null;
        // 1904 + 秒；合理范围 1970..2200，避免把未初始化的 0 或 0xFFFFFFFF 当日期
        var min = (long)(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) - MacEpoch).TotalSeconds;
        var max = (long)(new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero) - MacEpoch).TotalSeconds;
        if (seconds < min || seconds > max) return null;
        try { return MacEpoch.AddSeconds(seconds); } catch { return null; }
    }

    /// <summary>Matroska DateUTC：自 2001-01-01 起的纳秒数。</summary>
    public static DateTimeOffset? FromEbmlDate(long nanoseconds)
    {
        if (nanoseconds <= 0) return null;
        try
        {
            var dto = EbmlEpoch.AddTicks(nanoseconds / 100);
            return dto.Year is >= 1970 and <= 2200 ? dto : null;
        }
        catch { return null; }
    }

    public static DateTimeOffset? FromUnixSeconds(long seconds)
    {
        if (seconds <= 0) return null;
        try
        {
            var dto = UnixEpoch.AddSeconds(seconds);
            return dto.Year is >= 1970 and <= 2200 ? dto : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// EXIF 风格日期 + 独立时区偏移（"+09:00"）组合解析。
    /// 注意：不能直接依赖 <c>TimeUtil.ParseFlexible(text, offset)</c> 的某些分支——
    /// 当文本是 "2024:11:03 16:42:10" 这类"冒号日期"时，.NET 的 TryParseExact+AssumeLocal
    /// 会得到 Kind=Local 的 DateTime，再叠加外部 offset 会抛 ArgumentException。
    /// 这里先取"墙上时间"的各个分量，再用外部 offset 重新组装，语义正确且不抛异常。
    /// </summary>
    public static DateTimeOffset? ParseExifDate(string? dateText, string? offsetText = null)
    {
        if (string.IsNullOrWhiteSpace(dateText)) return null;
        var parsed = Util.TimeUtil.ParseFlexible(dateText);
        if (parsed is null) return null;

        var offset = ParseOffset(offsetText);
        if (offset is null) return parsed;

        var t = parsed.Value;
        if (t.Year is < 1900 or > 2200) return parsed;
        try
        {
            return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, offset.Value)
                .AddTicks(t.Ticks % TimeSpan.TicksPerSecond);
        }
        catch
        {
            return parsed;
        }
    }

    /// <summary>"+09:00" / "-04:00" / "Z" → TimeSpan 偏移。</summary>
    public static TimeSpan? ParseOffset(string? offsetText)
    {
        if (string.IsNullOrWhiteSpace(offsetText)) return null;
        var s = offsetText.Trim();
        if (s.Length == 0) return null;
        if (s is "Z" or "z") return TimeSpan.Zero;
        if (s[0] is not ('+' or '-')) return null;
        var sign = s[0] == '-' ? -1 : 1;
        var body = s[1..];
        if (body.Length == 5 && body[2] == ':') body = body.Remove(2, 1);
        if (body.Length != 4 || !int.TryParse(body[..2], out var hh) || !int.TryParse(body[2..], out var mm))
            return null;
        if (hh is < 0 or > 14 || mm is < 0 or > 59) return null;
        return new TimeSpan(sign * hh, sign * mm, 0);
    }

    // ---------------------------------------------------------------- 数值

    public static double? ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(',', '.');
        // "50 mm" / "2.8" / "123/1"
        var slash = s.IndexOf('/');
        if (slash > 0 &&
            double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            d != 0)
            return n / d;
        var num = new string(s.TakeWhile(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string FormatInvariant(double v, string format = "0.###") =>
        v.ToString(format, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- ASCII 令牌扫描（Log 判定用）

    /// <summary>
    /// 在 [start,end) 范围内分块扫描 ASCII 令牌（大小写不敏感），用于在 moov / JPEG APP 段里找
    /// "S-Log3"、"V-Log"、"HLG" 之类线索。不会整体读入文件，内存占用 = chunkSize + 最大令牌长度。
    /// </summary>
    public static void ScanTokens(SafeFileHandle handle, long start, long end, IReadOnlyList<string> tokens,
        ISet<string> found, long maxScanBytes = 64L * 1024 * 1024, CancellationToken ct = default)
    {
        if (tokens.Count == 0 || handle.IsInvalid) return;
        var len = RandomAccess.GetLength(handle);
        start = Math.Max(0, start);
        end = Math.Min(end <= 0 ? len : end, len);
        if (end - start > maxScanBytes) end = start + maxScanBytes;
        if (end <= start) return;

        // 统一成小写 ASCII 字节，扫描时把缓冲转小写（字节级，不分配大字符串）
        var needles = new List<byte[]>(tokens.Count);
        foreach (var t in tokens)
        {
            if (string.IsNullOrEmpty(t)) continue;
            needles.Add(Encoding.ASCII.GetBytes(t.ToLowerInvariant()));
        }
        if (needles.Count == 0) return;
        var overlap = needles.Max(n => n.Length) - 1;
        var chunk = 1 << 20; // 1 MB
        var buf = new byte[chunk + Math.Max(0, overlap)];
        var carry = 0;
        var pos = start;
        var lower = new byte[buf.Length];

        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunk, end - pos);
            var read = RandomAccess.Read(handle, buf.AsSpan(carry, want), pos);
            if (read <= 0) break;
            var total = carry + read;
            // 转小写副本
            for (var i = 0; i < total; i++)
            {
                var v = buf[i];
                lower[i] = v is >= (byte)'A' and <= (byte)'Z' ? (byte)(v + 32) : v;
            }
            var hay = new ReadOnlySpan<byte>(lower, 0, total);
            foreach (var n in needles)
            {
                if (hay.IndexOf(n) < 0) continue;
                // 反查原始 token（保留原始大小写）用于展示
                foreach (var t in tokens)
                {
                    if (t.Length == n.Length && Encoding.ASCII.GetBytes(t.ToLowerInvariant()).AsSpan().SequenceEqual(n))
                        found.Add(t);
                }
            }
            if (read < want) break;
            // 保留尾部 overlap，供跨块匹配
            carry = Math.Min(overlap, total);
            if (carry > 0) buf.AsSpan(total - carry, carry).CopyTo(buf);
            pos += read;
        }
    }
}