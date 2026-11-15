using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>ISOBMFF（MP4 / MOV / M4A / HEIC / AVIF / CR3）解析结果。</summary>
public sealed class IsoBmffInfo
{
    public string? MajorBrand { get; set; }
    public string? CompatibleBrands { get; set; }
    /// <summary>建议的格式代码：MP4 / MOV / M4A / M4V / HEIC / HEIF / AVIF / CR3。</summary>
    public string? FormatCode { get; set; }
    public MediaKind KindHint { get; set; } = MediaKind.Unknown;

    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? FrameRate { get; set; }
    public long? BitrateBps { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public int? AudioBitDepth { get; set; }
    public bool HasAudio { get; set; }

    public int? CicpPrimaries { get; set; }
    public int? CicpTransfer { get; set; }
    public int? CicpMatrix { get; set; }
    public string? IccProfileName { get; set; }

    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Software { get; set; }
    public string? CreationDateString { get; set; }
    public DateTimeOffset? CreationDate { get; set; }
    public string? LocationIso6709 { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Altitude { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Comment { get; set; }
    public string? Copyright { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }

    /// <summary>内嵌 XMP 包原始字节（uuid 或 meta 里找到的）。</summary>
    public byte[]? XmpBytes { get; set; }

    /// <summary>容器里读到的文本元数据（供 Log 判定/调试）。</summary>
    public List<string> MetadataStrings { get; } = new();

    /// <summary>在 moov 区域扫描到的 Log/色彩令牌（S-Log3 / V-Log / HLG …）。</summary>
    public HashSet<string> TextHints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Warnings { get; } = new();
    public string? Error { get; set; }

    internal bool IsHeifLike { get; set; }
    internal byte[]? ExifTiffBytes { get; set; }
}

/// <summary>
/// 自研 ISOBMFF 盒子解析器（不依赖第三方库）。只按盒子结构 seek/读头部，永远跳过 mdat，
/// 因此对几百 MB 的视频/RAW 也能在毫秒级拿到：尺寸、时长、帧率、编解码器、creationdate、
/// make/model、GPS ISO6709、colr(nclx/ICC)、QuickTime keys+ilst、uuid XMP、CR3 CMT EXIF。
/// </summary>
public static class IsoBmffParser
{
    private static readonly Guid XmpUuid = new("BE7ACFCB-97A9-42E8-9C71-999491E3AFAC");
    private static readonly byte[] TiffLe = { (byte)'I', (byte)'I', 42, 0 };
    private static readonly byte[] TiffBe = { (byte)'M', (byte)'M', 0, 42 };

    private sealed class Track
    {
        public string Handler = "";
        public long Timescale;
        public long Duration;
        public int? Width;
        public int? Height;
        public string? Codec;
        public long SampleCount;
        public long SampleDurationTicks;
        public int? AudioChannels;
        public int? AudioSampleRate;
        public int? AudioBitDepth;
        public long? AvgBitrate;
        public int? Rotation;
        public string? IccName;
        public int? Primaries, Transfer, Matrix;
    }

    private sealed class Ctx
    {
        public SafeFileHandle Handle = null!;
        public long FileLength;
        public readonly IsoBmffInfo Info = new();
        public readonly List<Track> Tracks = new();
        public Track? CurrentTrack;
        public long MovieTimescale;
        public long MovieDuration;
        public long MoovStart = -1, MoovEnd = -1;
        public readonly List<string> MetaKeys = new();
        public readonly Dictionary<string, string> MetaValues = new(StringComparer.OrdinalIgnoreCase);
        public string? CurrentItemKey;
        public int HeicWidth, HeicHeight;
        public string? GlobalIccName;
        public int? GlobalPrimaries, GlobalTransfer, GlobalMatrix;
        public int Depth;
    }

    public static IsoBmffInfo Parse(string fullPath, CancellationToken ct = default)
    {
        var info = new IsoBmffInfo();
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            var handle = fs.SafeFileHandle;
            var ctx = new Ctx { Handle = handle, FileLength = fs.Length };
            if (!ReadBoxes(ctx, 0, ctx.FileLength, ct))
                ctx.Info.Warnings.Add("ISOBMFF 顶层盒子结构不完整");

            CopyTo(ctx.Info, ctx, fullPath, ct);

            // 兜底：HEIC/AVIF 的 XMP 常放在 mdat 的 item 里，直接按特征扫描
            if (ctx.Info.XmpBytes is null && ctx.Info.IsHeifLike)
                ctx.Info.XmpBytes = TryScanForXmp(handle, ctx.FileLength, ct);

            // 兜底：HEIC 的 EXIF item（iinf/iloc 解析成本高，直接找 "Exif\0\0" TIFF 头）
            if (ctx.Info.Make is null && ctx.Info.IsHeifLike)
                TryScanForExif(ctx, ct);

            // 在 moov 区域扫描 Log/色彩令牌（通常 moov < 几 MB）
            if (ctx.MoovStart >= 0)
            {
                try
                {
                    MetaUtil.ScanTokens(handle, ctx.MoovStart, ctx.MoovEnd, ColorLogDetector.ScanTokens,
                        ctx.Info.TextHints, 32L * 1024 * 1024, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ctx.Info.Warnings.Add("moov 令牌扫描失败: " + ex.Message); }
            }

            foreach (var s in ctx.Info.MetadataStrings)
                if (!string.IsNullOrWhiteSpace(s)) ctx.Info.TextHints.Add(s);
            foreach (var kv in ctx.MetaValues)
                if (!string.IsNullOrWhiteSpace(kv.Value)) ctx.Info.MetadataStrings.Add($"{kv.Key}={kv.Value}");

            return ctx.Info;
        }
        catch (OperationCanceledException)
        {
            info.Warnings.Add("ISOBMFF 解析被取消");
            return info;
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
            info.Warnings.Add("ISOBMFF 解析失败: " + ex.Message);
            Log.Warn($"[IsoBmffParser] 解析失败: {fullPath}", ex);
            return info;
        }
    }

    // ============================================================ 结果归并

    private static void CopyTo(IsoBmffInfo info, Ctx ctx, string path, CancellationToken ct)
    {
        info.MajorBrand = ctx.Info.MajorBrand;
        info.CompatibleBrands = ctx.Info.CompatibleBrands;
        info.FormatCode = ctx.Info.FormatCode;
        info.KindHint = ctx.Info.KindHint;
        info.Make = ctx.Info.Make;
        info.Model = ctx.Info.Model;
        info.Software = ctx.Info.Software;
        info.CreationDateString = ctx.Info.CreationDateString;
        info.CreationDate = ctx.Info.CreationDate;
        info.LocationIso6709 = ctx.Info.LocationIso6709;
        info.XmpBytes = ctx.Info.XmpBytes;

        // 视频轨：优先 handler=vide，其次面积最大
        var video = ctx.Tracks.Where(t => t.Handler is "vide" || t.Width is > 0 || t.Height is > 0)
            .OrderByDescending(t => (long)(t.Width ?? 0) * (t.Height ?? 0)).FirstOrDefault();
        var audio = ctx.Tracks.FirstOrDefault(t => t.Handler == "soun" || t.AudioChannels is > 0);

        if (video is not null)
        {
            info.Width = video.Width;
            info.Height = video.Height;
            info.VideoCodec = video.Codec;
            info.CicpPrimaries = video.Primaries ?? ctx.Info.CicpPrimaries;
            info.CicpTransfer = video.Transfer ?? ctx.Info.CicpTransfer;
            info.CicpMatrix = video.Matrix ?? ctx.Info.CicpMatrix;
            info.IccProfileName = video.IccName ?? ctx.Info.IccProfileName ?? ctx.GlobalIccName;
            if (video.Timescale > 0 && video.SampleCount > 0)
            {
                double fps;
                if (video.SampleDurationTicks > 0)
                    fps = video.SampleCount * (double)video.Timescale / video.SampleDurationTicks;
                else if (video.Duration > 0)
                    fps = video.SampleCount * (double)video.Timescale / video.Duration;
                else
                    fps = 0;
                if (fps is > 0.5 and < 1000) info.FrameRate = Math.Round(fps, 3);
            }
            // 旋转矩阵：竖屏素材把显示宽高转正
            if (video.Rotation is 90 or 270 && info.Width is > 0 && info.Height is > 0 && info.Width > info.Height)
                (info.Width, info.Height) = (info.Height, info.Width);
            info.BitrateBps = video.AvgBitrate;
        }

        if (info.Width is null && ctx.HeicWidth > 0 && ctx.HeicHeight > 0)
        {
            info.Width = ctx.HeicWidth;
            info.Height = ctx.HeicHeight;
        }
        if (info.CicpPrimaries is null) info.CicpPrimaries = ctx.GlobalPrimaries;
        if (info.CicpTransfer is null) info.CicpTransfer = ctx.GlobalTransfer;
        if (info.CicpMatrix is null) info.CicpMatrix = ctx.GlobalMatrix;
        info.IccProfileName ??= ctx.GlobalIccName;

        if (audio is not null)
        {
            info.HasAudio = true;
            info.AudioCodec = audio.Codec;
            info.AudioChannels = audio.AudioChannels;
            info.AudioSampleRate = audio.AudioSampleRate;
            info.AudioBitDepth = audio.AudioBitDepth;
            info.BitrateBps ??= audio.AvgBitrate;
        }

        if (info.HasAudio)
        {
            info.KindHint = info.Width is > 0 || info.VideoCodec is not null ? MediaKind.Video : MediaKind.Audio;
        }
        else if (info.Width is > 0 || ctx.Info.IsHeifLike ||
                 ctx.Info.FormatCode is "HEIC" or "HEIF" or "AVIF" or "CR3")
        {
            info.KindHint = MediaKind.Image;
        }
        else
        {
            info.KindHint = MediaKind.Video;
        }

        // 时长
        if (ctx.MovieTimescale > 0 && ctx.MovieDuration > 0)
            info.DurationSeconds = Math.Round((double)ctx.MovieDuration / ctx.MovieTimescale, 3);
        else
        {
            var d = ctx.Tracks.Where(t => t.Timescale > 0 && t.Duration > 0)
                .Select(t => (double)t.Duration / t.Timescale).DefaultIfEmpty(0).Max();
            if (d > 0) info.DurationSeconds = Math.Round(d, 3);
        }

        // 码率兜底：文件大小 / 时长
        if (info.BitrateBps is null or <= 0 && info.DurationSeconds is > 0)
        {
            try
            {
                var size = new FileInfo(path).Length;
                info.BitrateBps = (long)(size * 8 / info.DurationSeconds.Value);
            }
            catch { /* 忽略 */ }
        }

        // 拍摄时间：优先带时区的元数据字符串，其次 mvhd 的 1904 纪元
        if (!string.IsNullOrWhiteSpace(info.CreationDateString))
        {
            var parsed = TimeUtil.ParseFlexible(info.CreationDateString!.TrimEnd('\0'));
            if (parsed is not null)
            {
                info.CreationDate = parsed;
                info.CreationDateString = null; // 已消费
            }
        }

        // ISO6709 GPS
        if (info.LocationIso6709 is not null) ApplyIso6709(info);
    }

    // ============================================================ 盒子遍历

    private static bool ReadBoxes(Ctx ctx, long start, long end, CancellationToken ct)
    {
        if (ctx.Depth > 24) return false;
        ctx.Depth++;
        try
        {
            var pos = start;
            var guard = 0;
            while (pos + 8 <= end && guard++ < 200_000)
            {
                ct.ThrowIfCancellationRequested();
                Span<byte> head = stackalloc byte[16];
                if (!ReadExact(ctx.Handle, pos, head[..8])) break;

                var size32 = BinaryPrimitives.ReadUInt32BigEndian(head);
                var type = RawFourCC(head[4..8]);
                long boxSize;
                long headerSize = 8;
                if (size32 == 1)
                {
                    if (!ReadExact(ctx.Handle, pos + 8, head[8..16])) break;
                    boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    boxSize = end - pos;
                }
                else
                {
                    boxSize = size32;
                }

                if (boxSize < headerSize) break;
                var payload = pos + headerSize;
                var boxEnd = pos + boxSize;
                if (boxEnd > end + 1) boxEnd = end;

                if (!Dispatch(ctx, type, payload, boxEnd, ct)) break;
                pos += boxSize;
            }
            return true;
        }
        finally
        {
            ctx.Depth--;
        }
    }

    private static bool Dispatch(Ctx ctx, string type, long payload, long end, CancellationToken ct)
    {
        switch (type)
        {
            case "ftyp":
                ParseFtyp(ctx, payload, end);
                return true;
            case "moov":
                ctx.MoovStart = payload;
                ctx.MoovEnd = end;
                return ReadBoxes(ctx, payload, end, ct);
            case "trak":
            {
                var track = new Track();
                ctx.Tracks.Add(track);
                var prev = ctx.CurrentTrack;
                ctx.CurrentTrack = track;
                var ok = ReadBoxes(ctx, payload, end, ct);
                ctx.CurrentTrack = prev;
                return ok;
            }
            case "mdia":
            case "minf":
            case "stbl":
            case "edts":
            case "dinf":
            case "mvex":
            case "udta":
            case "tref":
            case "wave":
                return ReadBoxes(ctx, payload, end, ct);
            case "meta":
                return ReadMetaBox(ctx, payload, end, ct);
            case "ilst":
                ParseIlst(ctx, payload, end);
                return true;
            case "keys":
                ParseKeys(ctx, payload, end);
                return true;
            case "mvhd":
                ParseMvhd(ctx, payload, end);
                return true;
            case "tkhd":
                ParseTkhd(ctx, payload, end);
                return true;
            case "mdhd":
                ParseMdhd(ctx, payload, end);
                return true;
            case "hdlr":
                ParseHdlr(ctx, payload, end);
                return true;
            case "stsd":
                ParseStsd(ctx, payload, end, ct);
                return true;
            case "stts":
                ParseStts(ctx, payload, end);
                return true;
            case "stsz":
                ParseStsz(ctx, payload, end);
                return true;
            case "btrt":
                if (ctx.CurrentTrack is not null && end - payload >= 12)
                    ctx.CurrentTrack.AvgBitrate = BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(ctx, payload + 8, 4));
                return true;
            case "colr":
                ParseColr(ctx, payload, end);
                return true;
            case "esds":
                ParseEsds(ctx, payload, end);
                return true;
            case "uuid":
                ParseUuid(ctx, payload, end, ct);
                return true;
            case "iprp":
            case "ipco":
            case "iinf":
            case "iref":
            case "grpl":
            case "dref":
            case "mfra":
                return ReadBoxes(ctx, payload, end, ct);
            case "ispe":
                if (end - payload >= 12)
                {
                    var w = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(ctx, payload + 4, 4));
                    var h = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(ctx, payload + 8, 4));
                    if ((long)w * h > (long)ctx.HeicWidth * ctx.HeicHeight)
                    {
                        ctx.HeicWidth = w;
                        ctx.HeicHeight = h;
                    }
                }
                return true;
            case "CMT1":
            case "CMT2":
            case "CMT3":
            case "CMT4":
            case "CMT5":
            case "CMP1":
                ApplyTiffBlock(ctx, payload, end);
                return true;
            case "data":
                ParseDataBox(ctx, payload, end);
                return true;
            case "©mak":
            case "©mod":
            case "©swr":
            case "©too":
            case "©day":
            case "©xyz":
            case "©nam":
            case "©ART":
            case "©cmt":
            case "©cpy":
            case "©alb":
            case "©gen":
            case "©des":
            case "©aut":
            case "©wrt":
            case "©enc":
            case "©inf":
            case "©prd":
            case "©chp":
                ParseQuickTimeTextAtom(ctx, type, payload, end);
                return true;
            case "----":
                return ReadBoxes(ctx, payload, end, ct);
            case "mean":
            case "name":
                return true; // 仅用于 ---- 项，键名已由 uuid/keys 决定
            case "mdat":
            case "free":
            case "skip":
            case "wide":
            case "styp":
            case "sidx":
            case "ssix":
            case "subs":
            case "ctts":
            case "cslg":
            case "sdtp":
            case "saiz":
            case "saio":
            case "tfra":
            case "trex":
            case "mehd":
            case "leva":
                return true;
            default:
                return true; // 未知盒子一律按长度跳过
        }
    }

    private static bool ReadMetaBox(Ctx ctx, long payload, long end, CancellationToken ct)
    {
        // ISO meta 是 FullBox（4 字节 version/flags）；QuickTime 旧写法没有。
        if (end - payload < 8) return true;
        Span<byte> probe = stackalloc byte[8];
        if (!ReadExact(ctx.Handle, payload, probe)) return true;
        var childSize = BinaryPrimitives.ReadUInt32BigEndian(probe);
        var childType = RawFourCC(probe[4..8]);
        var hasFullBoxHeader = childSize is < 8 or > 0x7FFFFFFF || !IsKnownMetaChild(childType);
        var start = payload + (hasFullBoxHeader ? 4 : 0);
        return ReadBoxes(ctx, start, end, ct);
    }

    private static bool IsKnownMetaChild(string t) => t is "hdlr" or "keys" or "ilst" or "iinf" or "iprp"
        or "ipco" or "pitm" or "iloc" or "iref" or "idat" or "dinf" or "free" or "mhdr" or "xml " or "ipro";

    // ============================================================ 具体盒子

    private static void ParseFtyp(Ctx ctx, long payload, long end)
    {
        if (end - payload < 8) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 256));
        ctx.Info.MajorBrand = RawFourCC(buf);
        var compat = new List<string>();
        for (var i = 8; i + 4 <= buf.Length; i += 4)
        {
            var b = RawFourCC(buf[i..]);
            if (!string.IsNullOrWhiteSpace(b)) compat.Add(b.Trim());
        }
        ctx.Info.CompatibleBrands = compat.Count > 0 ? string.Join(",", compat) : null;

        var brand = ctx.Info.MajorBrand;
        if (brand is "crx ")
        {
            ctx.Info.FormatCode = "CR3";
            ctx.Info.KindHint = MediaKind.Image;
        }
        else if (brand is "avif" or "avis" || compat.Contains("avif"))
        {
            ctx.Info.IsHeifLike = true;
            ctx.Info.FormatCode = "AVIF";
            ctx.Info.KindHint = MediaKind.Image;
        }
        else if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" or "heif")
        {
            ctx.Info.IsHeifLike = true;
            ctx.Info.FormatCode = brand is "mif1" or "msf1" ? "HEIF" : "HEIC";
            ctx.Info.KindHint = MediaKind.Image;
        }
        else if (brand is "M4A " or "M4B ")
        {
            ctx.Info.FormatCode = "M4A";
            ctx.Info.KindHint = MediaKind.Audio;
        }
        else if (brand is "M4V " or "M4VH" or "M4VP")
        {
            ctx.Info.FormatCode = "M4V";
            ctx.Info.KindHint = MediaKind.Video;
        }
        else if (brand is "qt  ")
        {
            ctx.Info.FormatCode = "MOV";
            ctx.Info.KindHint = MediaKind.Video;
        }
        else
        {
            ctx.Info.FormatCode = "MP4";
            ctx.Info.KindHint = MediaKind.Video;
        }
    }

    private static void ParseMvhd(Ctx ctx, long payload, long end)
    {
        if (end - payload < 24) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 64));
        var version = buf[0];
        long creation, timescale, duration;
        if (version == 1)
        {
            if (buf.Length < 32) return;
            creation = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[4..12]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[20..24]);
            duration = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[24..32]);
        }
        else
        {
            creation = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[12..16]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(buf[16..20]);
        }
        ctx.MovieTimescale = timescale;
        ctx.MovieDuration = duration;
        if (ctx.Info.CreationDate is null)
            ctx.Info.CreationDate = MetaUtil.FromMacEpoch(creation);
    }

    private static void ParseTkhd(Ctx ctx, long payload, long end)
    {
        if (ctx.CurrentTrack is null || end - payload < 84) return;
        var len = (int)Math.Min(end - payload, 256);
        var buf = ReadSpan(ctx, payload, len);
        var version = buf[0];
        long duration;
        if (version == 1)
        {
            if (buf.Length < 36) return;
            duration = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[28..36]);
        }
        else
        {
            duration = BinaryPrimitives.ReadUInt32BigEndian(buf[20..24]);
        }
        ctx.CurrentTrack.Duration = duration;

        // 宽高是盒子最后 8 字节（16.16 定点）
        ReadOnlySpan<byte> tail = len >= 8 ? buf.AsSpan(len - 8) : ReadOnlySpan<byte>.Empty;
        if (tail.Length >= 8)
        {
            var w = BinaryPrimitives.ReadUInt32BigEndian(tail) / 65536.0;
            var h = BinaryPrimitives.ReadUInt32BigEndian(tail[4..]) / 65536.0;
            if (w is > 0 and < 200000) ctx.CurrentTrack.Width = (int)Math.Round(w);
            if (h is > 0 and < 200000) ctx.CurrentTrack.Height = (int)Math.Round(h);
        }

        // 旋转矩阵在宽高之前的 36 字节
        if (len >= 8 + 36)
        {
            var m = buf[(len - 8 - 36)..(len - 8)];
            static int Fix(Span<byte> b, int o) => (int)(BinaryPrimitives.ReadInt32BigEndian(b[o..]) / 65536.0);
            var a = Fix(m, 0);
            var b = Fix(m, 4);
            var c = Fix(m, 12);
            var d = Fix(m, 16);
            if (a == 0 && b == 1 && c == -1 && d == 0) ctx.CurrentTrack.Rotation = 90;
            else if (a == 0 && b == -1 && c == 1 && d == 0) ctx.CurrentTrack.Rotation = 270;
        }
    }

    private static void ParseMdhd(Ctx ctx, long payload, long end)
    {
        if (ctx.CurrentTrack is null || end - payload < 24) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 64));
        var version = buf[0];
        if (version == 1)
        {
            if (buf.Length < 32) return;
            ctx.CurrentTrack.Timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[20..24]);
            ctx.CurrentTrack.Duration = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[24..32]);
        }
        else
        {
            ctx.CurrentTrack.Timescale = BinaryPrimitives.ReadUInt32BigEndian(buf[12..16]);
            ctx.CurrentTrack.Duration = BinaryPrimitives.ReadUInt32BigEndian(buf[16..20]);
        }
    }

    private static void ParseHdlr(Ctx ctx, long payload, long end)
    {
        if (ctx.CurrentTrack is null || end - payload < 12) return;
        var buf = ReadSpan(ctx, payload, 12);
        ctx.CurrentTrack.Handler = RawFourCC(buf[8..12]).Trim();
    }

    private static void ParseStsd(Ctx ctx, long payload, long end, CancellationToken ct)
    {
        if (end - payload < 8) return;
        var header = ReadSpan(ctx, payload, 8);
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        var pos = payload + 8;
        for (var i = 0; i < entryCount && pos + 8 <= end; i++)
        {
            ct.ThrowIfCancellationRequested();
            Span<byte> eh = stackalloc byte[8];
            if (!ReadExact(ctx.Handle, pos, eh)) break;
            var size = BinaryPrimitives.ReadUInt32BigEndian(eh);
            var entryType = RawFourCC(eh[4..8]);
            if (size < 8) break;
            var entryEnd = Math.Min(pos + size, end);
            ParseSampleEntry(ctx, entryType, pos + 8, entryEnd, ct);
            pos += size;
        }
    }

    private static void ParseSampleEntry(Ctx ctx, string type, long payload, long end, CancellationToken ct)
    {
        var track = ctx.CurrentTrack;
        var codec = MapVideoCodec(type) ?? MapAudioCodec(type);
        if (codec is not null && track is not null) track.Codec = codec;

        if (MapVideoCodec(type) is not null)
        {
            // VisualSampleEntry: 6 reserved + 2 data_ref + 16 pre_defined/reserved + 2 w + 2 h + ... = 78
            if (end - payload >= 78)
            {
                var buf = ReadSpan(ctx, payload, 78);
                var w = BinaryPrimitives.ReadUInt16BigEndian(buf[24..26]);
                var h = BinaryPrimitives.ReadUInt16BigEndian(buf[26..28]);
                if (w > 0 && h > 0 && track is not null)
                {
                    track.Width ??= w;
                    track.Height ??= h;
                }
                ReadBoxes(ctx, payload + 78, end, ct);
            }
        }
        else if (MapAudioCodec(type) is not null)
        {
            // AudioSampleEntry: 8 + 8 + 2 channels + 2 sampleSize + 2 predefined + 2 reserved + 4 sampleRate(16.16)
            if (end - payload >= 28)
            {
                var buf = ReadSpan(ctx, payload, 28);
                var version = BinaryPrimitives.ReadUInt16BigEndian(buf[8..10]);
                var channels = BinaryPrimitives.ReadUInt16BigEndian(buf[16..18]);
                var sampleSize = BinaryPrimitives.ReadUInt16BigEndian(buf[18..20]);
                var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(buf[24..28]) >> 16;
                if (track is not null)
                {
                    if (channels > 0) track.AudioChannels = channels;
                    if (sampleSize > 0) track.AudioBitDepth = sampleSize;
                    if (sampleRate > 0) track.AudioSampleRate = (int)sampleRate;
                }
                var childStart = payload + 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
                if (childStart < end) ReadBoxes(ctx, childStart, end, ct);
            }
        }
        else
        {
            // 未知 sample entry：尝试按通用盒子递归（有些私有 entry 里嵌 avcC/hvcC）
            if (end - payload > 16) ReadBoxes(ctx, payload + 16, end, ct);
        }
    }

    private static void ParseStts(Ctx ctx, long payload, long end)
    {
        if (ctx.CurrentTrack is null || end - payload < 8) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 8 + 8 * 4096));
        var count = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        if (count > 4096) count = 4096;
        var pos = 8;
        long samples = 0, durationTicks = 0;
        for (var i = 0; i < count && pos + 8 <= buf.Length; i++, pos += 8)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(buf[pos..]);
            var delta = BinaryPrimitives.ReadUInt32BigEndian(buf[(pos + 4)..]);
            samples += n;
            durationTicks += (long)n * delta;
        }
        ctx.CurrentTrack.SampleCount = samples;
        ctx.CurrentTrack.SampleDurationTicks = durationTicks;
    }

    private static void ParseStsz(Ctx ctx, long payload, long end)
    {
        if (ctx.CurrentTrack is null || end - payload < 12) return;
        var buf = ReadSpan(ctx, payload, 12);
        var sampleSize = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(buf[8..12]);
        if (sampleSize > 0 && ctx.CurrentTrack.SampleCount == 0) ctx.CurrentTrack.SampleCount = count;
    }

    private static void ParseColr(Ctx ctx, long payload, long end)
    {
        if (end - payload < 4) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 4 + 1 << 20));
        var colrType = RawFourCC(buf);
        if (colrType is "nclx" or "nclc" && buf.Length >= 10)   // ISO 用 nclx，QuickTime/MOV 用 nclc，布局一致
        {
            var p = BinaryPrimitives.ReadUInt16BigEndian(buf[4..6]);
            var t = BinaryPrimitives.ReadUInt16BigEndian(buf[6..8]);
            var m = BinaryPrimitives.ReadUInt16BigEndian(buf[8..10]);
            // 规范里 nclx 还有 1 字节 full_range_flag；ffmpeg 等实现常省略，故按可选处理
            if (ctx.CurrentTrack is not null)
            {
                ctx.CurrentTrack.Primaries = p;
                ctx.CurrentTrack.Transfer = t;
                ctx.CurrentTrack.Matrix = m;
            }
            else
            {
                ctx.GlobalPrimaries = p;
                ctx.GlobalTransfer = t;
                ctx.GlobalMatrix = m;
            }
        }
        else if (colrType is "prof" or "rICC")
        {
            var icc = buf[4..];
            var name = ImageMetadataReader.IccProfileName(icc);
            if (name is not null)
            {
                if (ctx.CurrentTrack is not null) ctx.CurrentTrack.IccName = name;
                else ctx.GlobalIccName = name;
            }
        }
    }

    private static void ParseEsds(Ctx ctx, long payload, long end)
    {
        var track = ctx.CurrentTrack;
        if (track is null || end - payload < 8) return;
        var len = (int)Math.Min(end - payload, 64 * 1024);
        var buf = ReadSpan(ctx, payload, len);
        var idx = 4; // version/flags
        var guard = 0;
        while (idx + 2 < buf.Length && guard++ < 16)
        {
            var tag = buf[idx++];
            var size = 0;
            var shift = 0;
            while (idx < buf.Length && shift < 28)
            {
                var b = buf[idx++];
                size = (size << 7) | (b & 0x7F);
                shift += 7;
                if ((b & 0x80) == 0) break;
            }

            if (tag == 0x03) // ES_Descriptor
            {
                if (idx + 3 > buf.Length) break;
                var flags = buf[idx + 2];
                idx += 3;
                if ((flags & 0x80) != 0) idx += 2;   // dependsOn_ES_ID
                if ((flags & 0x40) != 0)
                {
                    if (idx >= buf.Length) break;
                    idx += 1 + buf[idx];             // URL
                }
                if ((flags & 0x20) != 0) idx += 2;   // OCR_ES_ID
                continue;
            }
            if (tag == 0x04) // DecoderConfigDescriptor
            {
                if (idx + 13 > buf.Length) break;
                var avgBitrate = BinaryPrimitives.ReadUInt32BigEndian(buf[(idx + 9)..(idx + 13)]);
                if (avgBitrate > 0) track.AvgBitrate = avgBitrate;
                idx += 13;
                continue;
            }
            if (tag == 0x05) // DecoderSpecificInfo（AudioSpecificConfig）
            {
                if (idx < buf.Length) ParseAudioSpecificConfig(track, buf.AsSpan(idx, Math.Min(size, buf.Length - idx)));
                break;
            }
            if (idx + size > buf.Length) break;
            idx += size;
        }
    }

    private static readonly int[] AacSampleRates =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350,
    };

    private static void ParseAudioSpecificConfig(Track track, ReadOnlySpan<byte> asc)
    {
        if (asc.Length < 2) return;
        var bits = new BitReader(asc);
        var objectType = bits.Read(5);
        if (objectType == 31) bits.Read(6);
        var freqIndex = bits.Read(4);
        int sampleRate;
        if (freqIndex == 15) sampleRate = bits.Read(24);
        else sampleRate = freqIndex < AacSampleRates.Length ? AacSampleRates[freqIndex] : 0;
        var channelConfig = bits.Read(4);
        if (sampleRate > 0) track.AudioSampleRate = sampleRate;
        if (channelConfig is > 0 and <= 8)
        {
            track.AudioChannels = channelConfig == 7 ? 8 : channelConfig;
        }
    }

    private static void ParseKeys(Ctx ctx, long payload, long end)
    {
        if (end - payload < 8) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 256 * 1024));
        var count = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        if (count > 4096) count = 4096;
        var pos = 8;
        for (var i = 0; i < count && pos + 8 <= buf.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(buf[pos..]);
            if (size < 8 || pos + size > buf.Length) break;
            // 4 字节命名空间 + key 文本
            var keyBytes = buf[(pos + 8)..(pos + (int)size)];
            var nul = keyBytes.IndexOf((byte)0);
            if (nul >= 0) keyBytes = keyBytes[..nul];
            var key = Encoding.UTF8.GetString(keyBytes).Trim();
            ctx.MetaKeys.Add(key);
            pos += (int)size;
        }
    }

    private static void ParseIlst(Ctx ctx, long payload, long end)
    {
        var pos = payload;
        var guard = 0;
        while (pos + 8 <= end && guard++ < 4096)
        {
            Span<byte> head = stackalloc byte[8];
            if (!ReadExact(ctx.Handle, pos, head)) break;
            var size = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (size < 8) break;
            var itemEnd = Math.Min(pos + size, end);
            var itemKey = ResolveIlstKey(ctx, head[4..8]);
            var prev = ctx.CurrentItemKey;
            ctx.CurrentItemKey = itemKey;
            ReadBoxes(ctx, pos + 8, itemEnd, default);
            ctx.CurrentItemKey = prev;
            pos += size;
        }
    }

    private static string ResolveIlstKey(Ctx ctx, ReadOnlySpan<byte> fourccBytes)
    {
        var raw = RawFourCC(fourccBytes);
        // QuickTime keys 模式下，item 类型是 1 起的数字索引
        if (fourccBytes[0] == 0 && fourccBytes[1] == 0 && fourccBytes[2] == 0)
        {
            var idx = (int)fourccBytes[3];
            if (idx >= 1 && idx <= ctx.MetaKeys.Count) return ctx.MetaKeys[idx - 1];
        }
        return raw.Trim();
    }

    private static void ParseDataBox(Ctx ctx, long payload, long end)
    {
        var key = ctx.CurrentItemKey;
        if (string.IsNullOrEmpty(key) || end - payload < 8) return;
        var header = ReadSpan(ctx, payload, 8);
        var typeIndicator = BinaryPrimitives.ReadUInt32BigEndian(header);
        var dataLen = (int)Math.Min(end - payload - 8, 4 * 1024 * 1024);
        if (dataLen <= 0) return;
        var data = ReadSpan(ctx, payload + 8, dataLen);
        string? value = typeIndicator switch
        {
            1 => Encoding.UTF8.GetString(data).TrimEnd('\0'),
            2 => Encoding.BigEndianUnicode.GetString(data).TrimEnd('\0'),
            21 => BinaryPrimitives.ReadInt32BigEndian(data[..Math.Min(4, data.Length)]).ToString(),
            22 => BinaryPrimitives.ReadUInt32BigEndian(data[..Math.Min(4, data.Length)]).ToString(),
            _ => null,
        };
        if (value is null) return;
        value = MetaUtil.CleanText(value, 8192);
        if (string.IsNullOrEmpty(value)) return;
        ctx.MetaValues[key] = value;
        MapQuickTimeValue(ctx, key, value);
    }

    /// <summary>
    /// QuickTime 经典文本原子（udta 下的 ©mak/©mod/©xyz/©day/©cmt …）：
    /// payload = 2 字节文本长度 + 2 字节语言码 + 文本。
    /// </summary>
    private static void ParseQuickTimeTextAtom(Ctx ctx, string type, long payload, long end)
    {
        if (end - payload < 4) return;
        var buf = ReadSpan(ctx, payload, (int)Math.Min(end - payload, 8192));
        var textLen = (int)BinaryPrimitives.ReadUInt16BigEndian(buf);
        string? text;
        if (textLen > 0 && 4 + textLen <= buf.Length)
            text = Encoding.UTF8.GetString(buf, 4, textLen).TrimEnd('\0');
        else
            text = Encoding.UTF8.GetString(buf).TrimEnd('\0');
        text = MetaUtil.CleanText(text, 2048);
        if (string.IsNullOrEmpty(text)) return;
        MapQuickTimeValue(ctx, type, text);
        if (text.Length <= 256) ctx.Info.MetadataStrings.Add($"{type}={text}");
    }

    private static void MapQuickTimeValue(Ctx ctx, string key, string value)
    {
        var info = ctx.Info;
        var k = key.TrimStart('©').Trim();
        switch (key.ToLowerInvariant())
        {
            case "com.apple.quicktime.make":
                info.Make ??= value;
                return;
            case "com.apple.quicktime.model":
                info.Model ??= value;
                return;
            case "com.apple.quicktime.software":
            case "com.apple.quicktime.software.version":
                info.Software ??= value;
                return;
            case "com.apple.quicktime.creationdate":
                info.CreationDateString ??= value;
                return;
            case "com.apple.quicktime.location.iso6709":
                info.LocationIso6709 ??= value;
                return;
            case "com.apple.quicktime.title":
                info.Title ??= value;
                return;
            case "com.apple.quicktime.artist":
            case "com.apple.quicktime.author":
            case "com.apple.quicktime.creator":
                info.Artist ??= value;
                return;
            case "com.apple.quicktime.comment":
            case "com.apple.quicktime.description":
                info.Comment ??= value;
                return;
            case "com.apple.quicktime.copyright":
                info.Copyright ??= value;
                return;
            case "com.apple.quicktime.album":
                info.Album ??= value;
                return;
            case "com.apple.quicktime.genre":
                info.Genre ??= value;
                return;
        }

        switch (k.ToLowerInvariant())
        {
            case "mak": info.Make ??= value; break;
            case "mod": info.Model ??= value; break;
            case "swr": info.Software ??= value; break;
            case "too": info.Software ??= value; break;
            case "day": info.CreationDateString ??= value; break;
            case "xyz": info.LocationIso6709 ??= value; break;
            case "nam": info.Title ??= value; break;
            case "des": info.Comment ??= value; break;
            case "cmt": info.Comment ??= value; break;
            case "cpy": info.Copyright ??= value; break;
            case "art": info.Artist ??= value; break;
            case "aut": info.Artist ??= value; break;
            case "alb": info.Album ??= value; break;
            case "gen": info.Genre ??= value; break;
            case "wrt": info.Artist ??= value; break;
        }
    }

    /// <summary>
    /// 判定 uuid 是否是 Adobe XMP 的固定 UUID。注意：MP4 里写的是 UUID 的"网络字节序"
    /// （BE7ACFCB-97A9-42E8-9C71-999491E3AFAC），而 Guid.ToByteArray() 是混合端序；
    /// 两种写法都要认。
    /// </summary>
    private static bool IsXmpUuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return false;
        if (bytes[..16].SequenceEqual(XmpUuid.ToByteArray())) return true;
        // 规范文本字节序：前 3 段是大端
        Span<byte> be = stackalloc byte[16];
        XmpUuid.TryWriteBytes(be, bigEndian: true, out _);
        return bytes[..16].SequenceEqual(be);
    }

    private static void ParseUuid(Ctx ctx, long payload, long end, CancellationToken ct)
    {
        if (end - payload < 16) return;
        var uuidBytes = ReadSpan(ctx, payload, 16);
        var bodyStart = payload + 16;
        if (IsXmpUuid(uuidBytes))
        {
            var len = (int)Math.Min(end - bodyStart, 16L * 1024 * 1024);
            if (len > 16) ctx.Info.XmpBytes ??= ReadSpan(ctx, bodyStart, len).ToArray();
            return;
        }

        // CR3：Canon 的 uuid 里嵌 CMT1..CMT4（每个都是一个小盒子，payload 是 TIFF）
        if (LooksLikeTiff(ctx, bodyStart, 8))
        {
            ApplyTiffBlock(ctx, bodyStart, end);
            return;
        }
        // 尝试把 body 当作盒子集合
        var found = false;
        var pos = bodyStart;
        var guard = 0;
        while (pos + 8 <= end && guard++ < 16)
        {
            Span<byte> head = stackalloc byte[8];
            if (!ReadExact(ctx.Handle, pos, head)) break;
            var size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = RawFourCC(head[4..8]);
            if (size < 8 || pos + size > end) break;
            if (type is "CMT1" or "CMT2" or "CMT3" or "CMT4" or "CMP1" or "CMT5")
            {
                ApplyTiffBlock(ctx, pos + 8, pos + size);
                found = true;
            }
            pos += size;
        }
        if (found) return;

        // 最后兜底：payload 前 256 字节里搜 TIFF 头
        var probe = ReadSpan(ctx, bodyStart, (int)Math.Min(end - bodyStart, 256));
        for (var i = 0; i + 8 < probe.Length; i++)
        {
            if (!IsTiffHeader(probe[i..])) continue;
            ApplyTiffBlock(ctx, bodyStart + i, end);
            return;
        }
        _ = ct;
    }

    private static bool LooksLikeTiff(Ctx ctx, long offset, int probeLen)
    {
        if (offset + 4 > ctx.FileLength) return false;
        Span<byte> head = stackalloc byte[4];
        return ReadExact(ctx.Handle, offset, head) && IsTiffHeader(head);
    }

    private static bool IsTiffHeader(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && (b[..4].SequenceEqual(TiffLe) || b[..4].SequenceEqual(TiffBe));

    /// <summary>把一段 TIFF（CR3 CMT / HEIC Exif item / uuid 内嵌）映射到结果。</summary>
    private static void ApplyTiffBlock(Ctx ctx, long start, long end)
    {
        try
        {
            var tiff = TiffParser.ParseAt(ctx.Handle, start, end - start);
            if (tiff is null) return;
            var info = ctx.Info;
            foreach (var ifd in tiff.AllIfds())
            {
                info.Make ??= MetaUtil.CleanMake(ifd.GetAscii(0x010F));
                info.Model ??= MetaUtil.CleanText(ifd.GetAscii(0x0110), 128);
                info.Software ??= MetaUtil.CleanText(ifd.GetAscii(0x0131), 128);
                info.Artist ??= MetaUtil.CleanText(ifd.GetAscii(0x013B), 256);
                info.Copyright ??= MetaUtil.CleanText(ifd.GetAscii(0x8298), 512);
                info.Title ??= MetaUtil.CleanText(ifd.GetAscii(0x010E), 512);

                var date = ifd.GetAscii(0x9003) ?? ifd.GetAscii(0x9004) ?? ifd.GetAscii(0x0132);
                var offset = ifd.GetAscii(0x9011) ?? ifd.GetAscii(0x9010);
                if (info.CreationDate is null)
                {
                    var parsed = MetaUtil.ParseExifDate(date, offset);
                    if (parsed is not null) info.CreationDate = parsed;
                }

                var w = ifd.GetInt(0xA002) ?? ifd.GetInt(0x0100);
                var h = ifd.GetInt(0xA003) ?? ifd.GetInt(0x0101);
                if (w is > 0 && h is > 0 && (long)w * h > (long)(info.Width ?? 0) * (info.Height ?? 0))
                {
                    info.Width = w;
                    info.Height = h;
                }

                // GPS IFD
                var gpsIfd = ifd.Children.FirstOrDefault(c => c.Offset != 0 && c.Has(0x0001) && c.Has(0x0002));
                if (gpsIfd is not null && info.Latitude is null)
                {
                    var lat = ReadGpsCoord(gpsIfd, 0x0002, gpsIfd.GetAscii(0x0001));
                    var lon = ReadGpsCoord(gpsIfd, 0x0004, gpsIfd.GetAscii(0x0003));
                    if (lat is not null && lon is not null)
                    {
                        info.Latitude = lat;
                        info.Longitude = lon;
                        var alt = gpsIfd.GetDouble(0x0006);
                        if (alt is not null) info.Altitude = gpsIfd.GetInt(0x0005) == 1 ? -alt : alt;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Info.Warnings.Add("内嵌 TIFF 解析失败: " + ex.Message);
        }
    }

    private static double? ReadGpsCoord(TiffIfd ifd, int tag, string? refText)
    {
        var e = ifd.Find(tag);
        if (e?.Data is null || e.Type != 5 || e.Data.Length < 24) return null;
        var d = e.Data;
        var deg = RationalAt(d, 0);
        var min = RationalAt(d, 1);
        var sec = RationalAt(d, 2);
        var value = Math.Abs(deg) + Math.Abs(min) / 60.0 + Math.Abs(sec) / 3600.0;
        var negative = refText?.Trim().ToUpperInvariant() is "S" or "W";
        return negative ? -value : value;
    }

    private static double RationalAt(byte[] d, int index)
    {
        var o = index * 8;
        if (o + 8 > d.Length) return 0;
        var n = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        var den = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 4));
        return den == 0 ? 0 : (double)n / den;
    }

    // ============================================================ 兜底扫描

    private static byte[]? TryScanForXmp(SafeFileHandle handle, long length, CancellationToken ct)
    {
        try
        {
            var probe = new byte[64 * 1024];
            long pos = 0;
            var limit = Math.Min(length, 64L * 1024 * 1024);
            while (pos < limit)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(probe.Length, limit - pos);
                var read = RandomAccess.Read(handle, probe.AsSpan(0, want), pos);
                if (read <= 0) break;
                var span = probe.AsSpan(0, read);
                var idx = span.IndexOf("<x:xmpmeta"u8);
                if (idx < 0) idx = span.IndexOf("<xmpmeta"u8);
                if (idx >= 0)
                {
                    // 向后读，直到 </x:xmpmeta>（最多 4MB）
                    var absolute = pos + idx;
                    var tailLen = (int)Math.Min(4L * 1024 * 1024, length - absolute);
                    var packet = new byte[tailLen];
                    var got = 0;
                    while (got < packet.Length)
                    {
                        var n = RandomAccess.Read(handle, packet.AsSpan(got), absolute + got);
                        if (n <= 0) break;
                        got += n;
                    }
                    var text = Encoding.UTF8.GetString(packet, 0, got);
                    var close = text.IndexOf("</x:xmpmeta>", StringComparison.Ordinal);
                    if (close < 0) close = text.IndexOf("</xmpmeta>", StringComparison.Ordinal);
                    if (close >= 0) return Encoding.UTF8.GetBytes(text[..(close + 13)]);
                    return Encoding.UTF8.GetBytes(text);
                }
                pos += Math.Max(1, read - 12);
                if (read < 16) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略 */ }
        return null;
    }

    private static void TryScanForExif(Ctx ctx, CancellationToken ct)
    {
        try
        {
            var probe = new byte[64 * 1024];
            long pos = 0;
            var limit = Math.Min(ctx.FileLength, 64L * 1024 * 1024);
            while (pos < limit)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(probe.Length, limit - pos);
                var read = RandomAccess.Read(ctx.Handle, probe.AsSpan(0, want), pos);
                if (read <= 0) break;
                var idx = probe.AsSpan(0, read).IndexOf("Exif\0\0"u8);
                if (idx >= 0)
                {
                    var tiffStart = pos + idx + 6;
                    ApplyTiffBlock(ctx, tiffStart, ctx.FileLength);
                    return;
                }
                pos += Math.Max(1, read - 8);
                if (read < 12) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略 */ }
    }

    // ============================================================ 工具

    private static readonly Dictionary<string, string> VideoCodecs = new(StringComparer.Ordinal)
    {
        ["avc1"] = "H.264", ["avc3"] = "H.264", ["avc2"] = "H.264", ["avc4"] = "H.264",
        ["hvc1"] = "HEVC", ["hev1"] = "HEVC", ["hvc2"] = "HEVC", ["dvhe"] = "HEVC (Dolby Vision)",
        ["dvh1"] = "HEVC (Dolby Vision)", ["dav1"] = "AV1 (Dolby Vision)", ["av01"] = "AV1",
        ["vp08"] = "VP8", ["vp09"] = "VP9", ["vp10"] = "VP10",
        ["ap4h"] = "ProRes 4444", ["ap4x"] = "ProRes 4444 XQ",
        ["apcn"] = "ProRes 422", ["apcs"] = "ProRes 422 LT", ["apco"] = "ProRes 422 Proxy",
        ["apch"] = "ProRes 422 HQ", ["aprn"] = "ProRes RAW", ["aprN"] = "ProRes RAW",
        ["jpeg"] = "Motion JPEG", ["mjpa"] = "Motion JPEG", ["mjpb"] = "Motion JPEG",
        ["mp4v"] = "MPEG-4 Video", ["s263"] = "H.263", ["cvid"] = "Cinepak",
        ["rle "] = "QuickTime RLE", ["raw "] = "Raw Video", ["yuv2"] = "YUV 4:2:2",
        ["v210"] = "Uncompressed 10-bit 4:2:2", ["2vuy"] = "Uncompressed 8-bit 4:2:2",
        ["btrt"] = "", ["encv"] = "Encrypted Video",
        ["mjp2"] = "JPEG 2000", ["mjp2"] = "JPEG 2000",
    };

    private static readonly Dictionary<string, string> AudioCodecs = new(StringComparer.Ordinal)
    {
        ["mp4a"] = "AAC", ["ac-3"] = "Dolby Digital (AC-3)", ["ec-3"] = "Dolby Digital Plus (E-AC-3)",
        ["ac-4"] = "Dolby AC-4", ["alac"] = "ALAC", ["fLaC"] = "FLAC", ["Opus"] = "Opus",
        ["lpcm"] = "PCM", ["sowt"] = "PCM", ["twos"] = "PCM", ["in24"] = "PCM 24-bit",
        ["in32"] = "PCM 32-bit", ["fl32"] = "PCM Float 32", ["fl64"] = "PCM Float 64",
        ["ulaw"] = "μ-Law", ["alaw"] = "A-Law", ["samr"] = "AMR-NB", ["sawb"] = "AMR-WB",
        ["dtsc"] = "DTS", ["dtsh"] = "DTS-HD", ["dtsl"] = "DTS-HD MA", ["dtse"] = "DTS Express",
        ["enca"] = "Encrypted Audio", ["mp3 "] = "MP3", [".mp3"] = "MP3", ["ima4"] = "IMA ADPCM",
        ["QDM2"] = "QDesign 2", ["QDM "]= "QDesign",
    };

    private static string? MapVideoCodec(string type) => VideoCodecs.TryGetValue(type, out var v) && v.Length > 0 ? v : null;

    private static string? MapAudioCodec(string type) => AudioCodecs.TryGetValue(type, out var v) ? v : null;

    private static void ApplyIso6709(IsoBmffInfo info)
    {
        try
        {
            var s = info.LocationIso6709!.Trim().TrimEnd('/');
            var matches = System.Text.RegularExpressions.Regex.Matches(s, @"([+-]\d{1,3}(?:\.\d+)?)");
            if (matches.Count < 2) return;
            var lat = double.Parse(matches[0].Value, System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(matches[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (lat is < -90 or > 90 || lon is < -180 or > 180) return;
            info.Latitude = lat;
            info.Longitude = lon;
            if (matches.Count >= 3)
            {
                var alt = double.Parse(matches[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (alt is > -500 and < 10000) info.Altitude = alt;
            }
        }
        catch { /* 忽略 */ }
    }

    private static byte[] ReadSpan(Ctx ctx, long offset, int length)
    {
        var buf = new byte[Math.Max(0, length)];
        if (buf.Length == 0) return buf;
        var read = 0;
        while (read < buf.Length)
        {
            var n = RandomAccess.Read(ctx.Handle, buf.AsSpan(read), offset + read);
            if (n <= 0) break;
            read += n;
        }
        return read == buf.Length ? buf : buf[..read];
    }

    private static bool ReadExact(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer[read..], offset + read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>fourcc 原文（保留 © 0xA9 等非 ASCII 字节）。</summary>
    private static string RawFourCC(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return "";
        return string.Create(4, b[..4].ToArray(), (span, bytes) =>
        {
            for (var i = 0; i < 4; i++)
            {
                var v = bytes[i];
                span[i] = v switch
                {
                    0xA9 => '©',
                    0xAE => '®',
                    0 => ' ',
                    >= 0x20 and < 0x7F => (char)v,
                    _ => '\uFFFD',
                };
            }
        });
    }

    /// <summary>AudioSpecificConfig 的极简位读取器。</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;
        public BitReader(ReadOnlySpan<byte> data) { _data = data; _bit = 0; }

        public int Read(int bits)
        {
            var value = 0;
            for (var i = 0; i < bits; i++)
            {
                var byteIndex = _bit >> 3;
                if (byteIndex >= _data.Length) return value << (bits - i);
                var bitIndex = 7 - (_bit & 7);
                value = (value << 1) | ((_data[byteIndex] >> bitIndex) & 1);
                _bit++;
            }
            return value;
        }
    }
}