using System.Buffers.Binary;
using System.Text;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>Matroska / WebM 解析结果。</summary>
public sealed class MatroskaInfo
{
    public string? DocType { get; set; }
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

    public string? Title { get; set; }
    public string? MuxingApp { get; set; }
    public string? WritingApp { get; set; }
    public DateTimeOffset? CreationDate { get; set; }

    public List<string> Warnings { get; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// 自研 Matroska/WebM（EBML）解析器：只读 EBML 头 + Segment/Info/Tracks，
/// 拿到 Duration / TimecodeScale / PixelWidth/Height / CodecID / DefaultDuration / 音频参数 /
/// Colour（CICP）/ DateUTC。拿到 Info+Tracks 后立即停止，不扫描 Cluster，因此大文件也很快。
/// </summary>
public static class MatroskaParser
{
    private const ulong IdEbml = 0x1A45DFA3;
    private const ulong IdSegment = 0x18538067;
    private const ulong IdInfo = 0x1549A966;
    private const ulong IdTracks = 0x1654AE6B;
    private const ulong IdCluster = 0x1F43B675;
    private const ulong IdCues = 0x1C53BB6B;
    private const ulong IdSeekHead = 0x114D9B74;
    private const ulong IdTags = 0x1254C367;
    private const ulong IdAttachments = 0x1941A469;
    private const ulong IdChapters = 0x1043A770;
    private const ulong IdVoid = 0xEC;

    public static MatroskaInfo Parse(string fullPath, CancellationToken ct = default)
    {
        var info = new MatroskaInfo();
        try
        {
            using var stream = MetaUtil.OpenSequential(fullPath);
            var reader = new EbmlReader(stream);
            var fileLength = stream.Length;

            // EBML 头
            if (!reader.TryReadElement(out var id, out var size, out var unknown) || id != IdEbml)
            {
                info.Warnings.Add("不是有效的 EBML 文件头");
                return info;
            }
            ParseEbmlHeader(reader, unknown ? 0 : size, info);   // 内部已消费到头部末尾，不能再 Skip

            // 顶层：寻找 Segment（前面可能有 Void/其它）
            var guard = 0;
            while (guard++ < 64)
            {
                ct.ThrowIfCancellationRequested();
                var pos = reader.Position;
                if (!reader.TryReadElement(out id, out size, out unknown)) break;
                if (id == IdSegment)
                {
                    var segStart = reader.Position;
                    var segEnd = unknown ? fileLength : Math.Min(fileLength, segStart + size);
                    ParseSegment(reader, segStart, segEnd, info, ct);
                    break;
                }
                if (unknown) break;
                if (pos + size > fileLength) break;
                reader.Skip(size);
            }

            if (info.DurationSeconds is > 0 && info.BitrateBps is null)
            {
                try { info.BitrateBps = (long)(fileLength * 8 / info.DurationSeconds.Value); }
                catch { /* 忽略 */ }
            }
            return info;
        }
        catch (OperationCanceledException)
        {
            info.Warnings.Add("Matroska 解析被取消");
            return info;
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
            info.Warnings.Add("Matroska 解析失败: " + ex.Message);
            Log.Warn($"[MatroskaParser] 解析失败: {fullPath}", ex);
            return info;
        }
    }

    private static void ParseEbmlHeader(EbmlReader reader, long size, MatroskaInfo info)
    {
        var end = reader.Position + size;
        while (reader.Position < end)
        {
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var eSize, out var unknown)) break;
            if (unknown) break;
            var payload = reader.Position;
            if (payload + eSize > end) break;
            switch (id)
            {
                case 0x4282: info.DocType = ReadString(reader, eSize); break;   // DocType
            }
            reader.SeekTo(payload + eSize);
            if (reader.Position <= pos) break;
        }
        reader.SeekTo(end);
    }

    private static void ParseSegment(EbmlReader reader, long start, long end, MatroskaInfo info, CancellationToken ct)
    {
        reader.SeekTo(start);
        var gotInfo = false;
        var gotTracks = false;
        var guard = 0;
        while (reader.Position < end && guard++ < 512)
        {
            ct.ThrowIfCancellationRequested();
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var size, out var unknown)) break;
            var payload = reader.Position;

            if (id == IdInfo && !unknown)
            {
                var infoEnd = Math.Min(end, payload + size);
                ParseInfo(reader, infoEnd, info);
                reader.SeekTo(infoEnd);
                gotInfo = true;
            }
            else if (id == IdTracks && !unknown)
            {
                var tracksEnd = Math.Min(end, payload + size);
                ParseTracks(reader, tracksEnd, info, ct);
                reader.SeekTo(tracksEnd);
                gotTracks = true;
            }
            else if (id == IdTags && !unknown)
            {
                var tagsEnd = Math.Min(end, payload + size);
                ParseTags(reader, tagsEnd, info);
                reader.SeekTo(tagsEnd);
            }
            else if (id == IdCluster)
            {
                // Cluster 数据量大且对元数据无用；Info/Tracks 都在前面，直接结束
                if (!gotInfo || !gotTracks) { /* 继续找后面的已知元素 */ }
                if (unknown) break;
                if (gotInfo && gotTracks) break;
                reader.Skip(size);
            }
            else if (id is IdCues or IdSeekHead or IdAttachments or IdChapters or IdVoid)
            {
                if (unknown) break;
                reader.Skip(size);
            }
            else
            {
                if (unknown) break;
                if (payload + size > end) break;
                reader.Skip(size);
            }

            if (reader.Position <= pos) break;
        }
    }

    private static void ParseInfo(EbmlReader reader, long end, MatroskaInfo info)
    {
        long timecodeScale = 1_000_000;
        double? duration = null;
        while (reader.Position < end)
        {
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var size, out var unknown) || unknown) break;
            switch (id)
            {
                case 0x2AD7B1: // TimecodeScale
                    timecodeScale = ReadUInt(reader, size);
                    break;
                case 0x4489: // Duration (float, 以 TimecodeScale 为单位)
                    duration = ReadFloat(reader, size);
                    break;
                case 0x7BA9: // Title
                    info.Title = MetaUtil.CleanText(ReadString(reader, size), 512);
                    break;
                case 0x4D80: // MuxingApp
                    info.MuxingApp = MetaUtil.CleanText(ReadString(reader, size), 256);
                    break;
                case 0x5741: // WritingApp
                    info.WritingApp = MetaUtil.CleanText(ReadString(reader, size), 256);
                    break;
                case 0x4461: // DateUTC（纳秒，自 2001-01-01）
                {
                    var raw = ReadLong(reader, size);
                    var dto = MetaUtil.FromEbmlDate(raw);
                    if (dto is not null) info.CreationDate = dto;
                    break;
                }
                default:
                    SkipValue(reader, size);
                    break;
            }
            if (reader.Position > end) break;
            if (reader.Position <= pos) break;
        }
        if (duration is > 0) info.DurationSeconds = Math.Round(duration.Value * timecodeScale / 1e9, 3);
    }

    private static void ParseTracks(EbmlReader reader, long end, MatroskaInfo info, CancellationToken ct)
    {
        while (reader.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var size, out var unknown) || unknown) break;
            if (id == 0xAE) // TrackEntry
            {
                var entryEnd = Math.Min(end, reader.Position + size);
                ParseTrackEntry(reader, entryEnd, info, ct);
                reader.SeekTo(entryEnd);
            }
            else
            {
                reader.Skip(size);
            }
            if (reader.Position <= pos) break;
        }
    }

    private static void ParseTrackEntry(EbmlReader reader, long end, MatroskaInfo info, CancellationToken ct)
    {
        long trackType = 0;
        string? codecId = null;
        long? defaultDurationNs = null;
        int? width = null, height = null, channels = null, sampleRate = null, bitDepth = null;
        int? primaries = null, transfer = null, matrix = null;

        while (reader.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var size, out var unknown) || unknown) break;
            switch (id)
            {
                case 0x83: trackType = ReadUInt(reader, size); break;
                case 0x86: codecId = ReadString(reader, size); break;
                case 0x23E383: defaultDurationNs = ReadUInt(reader, size); break;
                case 0xE0: // Video
                {
                    var vEnd = reader.Position + size;
                    while (reader.Position < vEnd)
                    {
                        var vPos = reader.Position;
                        if (!reader.TryReadElement(out var vid, out var vsize, out var vUnknown) || vUnknown) break;
                        switch (vid)
                        {
                            case 0xB0: width = (int)ReadUInt(reader, vsize); break;
                            case 0xBA: height = (int)ReadUInt(reader, vsize); break;
                            case 0x55B0: // Colour
                            {
                                var cEnd = reader.Position + vsize;
                                while (reader.Position < cEnd)
                                {
                                    var cPos = reader.Position;
                                    if (!reader.TryReadElement(out var cid, out var csize, out var cUnknown) || cUnknown) break;
                                    switch (cid)
                                    {
                                        case 0x55B1: matrix = (int)ReadUInt(reader, csize); break;
                                        case 0x55BA: transfer = (int)ReadUInt(reader, csize); break;
                                        case 0x55BB: primaries = (int)ReadUInt(reader, csize); break;
                                        default: reader.Skip(csize); break;
                                    }
                                    if (reader.Position <= cPos) break;
                                }
                                reader.SeekTo(cEnd);
                                break;
                            }
                            default: reader.Skip(vsize); break;
                        }
                        if (reader.Position <= vPos) break;
                    }
                    reader.SeekTo(vEnd);
                    break;
                }
                case 0xE1: // Audio
                {
                    var aEnd = reader.Position + size;
                    while (reader.Position < aEnd)
                    {
                        var aPos = reader.Position;
                        if (!reader.TryReadElement(out var aid, out var asize, out var aUnknown) || aUnknown) break;
                        switch (aid)
                        {
                            case 0xB5: sampleRate = (int)Math.Round(ReadFloat(reader, asize) ?? 0); break;
                            case 0x78B5: sampleRate = (int)Math.Round(ReadFloat(reader, asize) ?? sampleRate ?? 0); break;
                            case 0x9F: channels = (int)ReadUInt(reader, asize); break;
                            case 0x6264: bitDepth = (int)ReadUInt(reader, asize); break;
                            default: reader.Skip(asize); break;
                        }
                        if (reader.Position <= aPos) break;
                    }
                    reader.SeekTo(aEnd);
                    break;
                }
                default:
                    reader.Skip(size);
                    break;
            }
            if (reader.Position <= pos) break;
        }

        var isVideo = trackType == 1 || width is > 0;
        var isAudio = trackType == 2 || channels is > 0;

        if (isVideo)
        {
            info.Width ??= width;
            info.Height ??= height;
            if (codecId is not null) info.VideoCodec ??= MapVideoCodec(codecId) ?? codecId;
            if (defaultDurationNs is > 0) info.FrameRate ??= Math.Round(1e9 / defaultDurationNs.Value, 3);
            info.CicpPrimaries ??= primaries;
            info.CicpTransfer ??= transfer;
            info.CicpMatrix ??= matrix;
        }
        if (isAudio)
        {
            info.HasAudio = true;
            if (codecId is not null) info.AudioCodec ??= MapAudioCodec(codecId) ?? codecId;
            info.AudioChannels ??= channels;
            info.AudioSampleRate ??= sampleRate;
            info.AudioBitDepth ??= bitDepth;
        }
    }

    private static void ParseTags(EbmlReader reader, long end, MatroskaInfo info)
    {
        // 只取 Title 等简单字符串标签，够用即可
        while (reader.Position < end)
        {
            var pos = reader.Position;
            if (!reader.TryReadElement(out var id, out var size, out var unknown) || unknown) break;
            if (id == 0x7373) // Tag
            {
                var tagEnd = Math.Min(end, reader.Position + size);
                reader.SeekTo(tagEnd);
            }
            else
            {
                reader.Skip(size);
            }
            if (reader.Position <= pos) break;
        }
    }

    // ============================================================ 值读取

    private static string? ReadString(EbmlReader reader, long size)
    {
        if (size <= 0 || size > 1024 * 1024) { reader.Skip(size); return null; }
        var buf = new byte[size];
        if (!reader.ReadBytes(buf, (int)size)) return null;
        var text = Encoding.UTF8.GetString(buf).TrimEnd('\0');
        return MetaUtil.CleanText(text, 2048);
    }

    private static long ReadUInt(EbmlReader reader, long size)
    {
        if (size is <= 0 or > 8) { reader.Skip(size); return 0; }
        Span<byte> buf = stackalloc byte[8];
        if (!reader.ReadBytes(buf[..(int)size], (int)size)) return 0;
        long value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | buf[i];
        return value;
    }

    private static long ReadLong(EbmlReader reader, long size)
    {
        if (size is <= 0 or > 8) { reader.Skip(size); return 0; }
        var value = ReadUInt(reader, size);
        // 符号扩展
        if (size < 8 && ((value >> (int)(size * 8 - 1)) & 1) != 0)
            value |= ~((1L << (int)(size * 8)) - 1);
        return value;
    }

    private static double? ReadFloat(EbmlReader reader, long size)
    {
        if (size is not (4 or 8)) { reader.Skip(size); return null; }
        Span<byte> buf = stackalloc byte[8];
        if (!reader.ReadBytes(buf[..(int)size], (int)size)) return null;
        return size == 4
            ? BinaryPrimitives.ReadSingleBigEndian(buf)
            : BinaryPrimitives.ReadDoubleBigEndian(buf);
    }

    private static void SkipValue(EbmlReader reader, long size) => reader.Skip(size);

    // ============================================================ 编解码映射

    private static string? MapVideoCodec(string codecId) => codecId.ToUpperInvariant() switch
    {
        "V_MPEG4/ISO/AVC" => "H.264",
        "V_MPEGH/ISO/HEVC" => "HEVC",
        "V_AV1" => "AV1",
        "V_VP9" => "VP9",
        "V_VP8" => "VP8",
        "V_THEORA" => "Theora",
        "V_PRORES" => "ProRes",
        "V_FFV1" => "FFV1",
        "V_MPEG4/ISO/ASP" => "MPEG-4 Video",
        "V_MPEG4/ISO/SP" => "MPEG-4 Video",
        "V_MPEG2" => "MPEG-2",
        "V_MPEG1" => "MPEG-1",
        "V_MS/VFW/FOURCC" => "VFW",
        "V_UNCOMPRESSED" => "Uncompressed",
        _ => null,
    };

    private static string? MapAudioCodec(string codecId) => codecId.ToUpperInvariant() switch
    {
        "A_AAC" => "AAC",
        "A_AAC/MPEG4/LC" => "AAC-LC",
        "A_FLAC" => "FLAC",
        "A_OPUS" => "Opus",
        "A_VORBIS" => "Vorbis",
        "A_MPEG/L3" => "MP3",
        "A_MPEG/L2" => "MP2",
        "A_AC3" => "AC-3",
        "A_EAC3" => "E-AC-3",
        "A_DTS" => "DTS",
        "A_TRUEHD" => "TrueHD",
        "A_PCM/INT/LIT" => "PCM",
        "A_PCM/INT/BIG" => "PCM",
        "A_PCM/FLOAT/IEEE" => "PCM Float",
        "A_ALAC" => "ALAC",
        "A_WAVPACK4" => "WavPack",
        _ => null,
    };

    // ============================================================ EBML 底层读取器

    private sealed class EbmlReader
    {
        private readonly Stream _stream;
        public long Position => _stream.Position;

        public EbmlReader(Stream stream) => _stream = stream;

        /// <summary>读取一个 EBML 元素头（ID + 长度）。</summary>
        public bool TryReadElement(out ulong id, out long size, out bool unknownSize)
        {
            id = 0;
            size = 0;
            unknownSize = false;

            var first = _stream.ReadByte();
            if (first < 0) return false;

            var idLength = VintLength((byte)first);
            if (idLength is 0 or > 8) return false;
            id = (ulong)first; // ID 保留 marker 位
            for (var i = 1; i < idLength; i++)
            {
                var b = _stream.ReadByte();
                if (b < 0) return false;
                id = (id << 8) | (byte)b;
            }

            var sizeFirst = _stream.ReadByte();
            if (sizeFirst < 0) return false;
            var sizeLength = VintLength((byte)sizeFirst);
            if (sizeLength is 0 or > 8) return false;

            ulong value = (ulong)((byte)sizeFirst & ((0x80 >> (sizeLength - 1)) - 1));
            for (var i = 1; i < sizeLength; i++)
            {
                var b = _stream.ReadByte();
                if (b < 0) return false;
                value = (value << 8) | (byte)b;
            }

            var allOnes = (1UL << (7 * sizeLength)) - 1;
            if (value == allOnes)
            {
                unknownSize = true;
                size = 0;
                return true;
            }
            if (value > long.MaxValue) return false;
            size = (long)value;
            return true;
        }

        private static int VintLength(byte first)
        {
            if (first == 0) return 0;
            var len = 1;
            var mask = 0x80;
            while ((first & mask) == 0)
            {
                len++;
                mask >>= 1;
                if (len > 8) return 0;
            }
            return len;
        }

        public bool ReadBytes(Span<byte> buffer, int length)
        {
            var read = 0;
            while (read < length)
            {
                var n = _stream.Read(buffer[read..length]);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Skip(long count)
        {
            if (count <= 0) return;
            if (_stream.CanSeek)
            {
                var target = Math.Min(_stream.Length, _stream.Position + count);
                _stream.Position = target;
            }
            else
            {
                var buf = new byte[64 * 1024];
                var remaining = count;
                while (remaining > 0)
                {
                    var n = _stream.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0) break;
                    remaining -= n;
                }
            }
        }

        public void SeekTo(long position)
        {
            if (_stream.CanSeek && position >= 0 && position <= _stream.Length) _stream.Position = position;
        }
    }
}