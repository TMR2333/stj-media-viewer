using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 音频元数据读取器：WAV(RF64) / MP3(ID3v2.2-2.4 + ID3v1) / FLAC(STREAMINFO + VorbisComment) /
/// OGG(Vorbis/Opus/FLAC) / AIFF(AIFC)。M4A 由 <see cref="IsoBmffParser"/> 负责。
/// 只读文件头与标签区，不读音频数据；所有异常内部吞掉。
/// </summary>
public static class AudioMetadataReader
{
    public static void Read(string fullPath, MediaMetadata m, CancellationToken ct = default)
    {
        try
        {
            Span<byte> head = stackalloc byte[12];
            using (var fs = MetaUtil.OpenSequential(fullPath))
            {
                if (!ReadExact(fs, head)) { m.Warnings.Add("音频文件为空或过短"); return; }
            }

            var magic = Encoding.ASCII.GetString(head[..4]);
            if (magic == "RIFF" || magic == "RF64")
            {
                var form = Encoding.ASCII.GetString(head[8..12]);
                if (form == "WAVE")
                {
                    ReadWav(fullPath, m, ct);
                    return;
                }
            }
            if (magic == "fLaC") { ReadFlac(fullPath, m, ct); return; }
            if (magic == "OggS") { ReadOgg(fullPath, m, ct); return; }
            if (magic == "FORM") { ReadAiff(fullPath, m, ct); return; }
            if (magic == "ID3 " || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
            {
                ReadMp3(fullPath, m, ct);
                return;
            }
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                ReadMp3(fullPath, m, ct);
                return;
            }

            m.Warnings.Add("未识别的音频容器（扩展名为音频但魔数不匹配）");
        }
        catch (OperationCanceledException)
        {
            m.Warnings.Add("音频解析被取消");
        }
        catch (Exception ex)
        {
            m.Warnings.Add("音频元数据解析失败: " + ex.Message);
            Log.Warn($"[AudioMetadataReader] 解析失败: {fullPath}", ex);
        }
    }

    // ============================================================ WAV

    private static void ReadWav(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        m.HasAudio = true;
        m.ContainerName = "RIFF";
        using var fs = MetaUtil.OpenSequential(fullPath);
        var fileLen = fs.Length;

        Span<byte> head = stackalloc byte[12];
        if (!ReadExact(fs, head)) return;
        var isRf64 = Encoding.ASCII.GetString(head[..4]) == "RF64";

        long dataSize = -1;
        long sampleCount = -1;
        long byteRate = 0;
        int channels = 0, sampleRate = 0, bits = 0, formatTag = 0;
        var codecSet = false;

        Span<byte> chunkHead = stackalloc byte[8];
        var guard = 0;
        while (fs.Position + 8 <= fileLen && guard++ < 4096)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadExact(fs, chunkHead)) break;
            var id = Encoding.ASCII.GetString(chunkHead[..4]);
            var size32 = BinaryPrimitives.ReadUInt32LittleEndian(chunkHead[4..8]);
            var size = (long)size32;
            var payload = fs.Position;
            if (id == "data" && size32 == 0xFFFFFFFF && dataSize > 0) size = dataSize;
            if (id == "data" && size32 == 0xFFFFFFFF) size = Math.Max(0, fileLen - payload - 8);
            if (payload + size > fileLen) size = Math.Max(0, fileLen - payload);

            switch (id)
            {
                case "data":
                    dataSize = size;
                    break;
                case "ds64":
                {
                    var buf = new byte[Math.Min(size, 28)];
                    if (ReadExact(fs, buf))
                    {
                        if (buf.Length >= 16) dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8));
                        if (buf.Length >= 24) sampleCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(16));
                    }
                    break;
                }
                case "fmt ":
                {
                    var buf = new byte[Math.Min(size, 64)];
                    if (ReadExact(fs, buf) && buf.Length >= 16)
                    {
                        formatTag = BinaryPrimitives.ReadUInt16LittleEndian(buf);
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
                        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
                        byteRate = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
                        bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(14));
                        if (formatTag == 0xFFFE && buf.Length >= 26)
                        {
                            // WAVE_FORMAT_EXTENSIBLE：真实格式在 SubFormat GUID 前 2 字节
                            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(24));
                        }
                        codecSet = true;
                    }
                    break;
                }
                case "LIST":
                {
                    var typeBuf = new byte[4];
                    if (ReadExact(fs, typeBuf) && Encoding.ASCII.GetString(typeBuf) == "INFO")
                        ParseRiffInfo(fs, payload + 4, payload + size, m);
                    break;
                }
                case "bext":
                {
                    var buf = new byte[Math.Min(size, 602)];
                    if (ReadExact(fs, buf) && buf.Length >= 338)
                    {
                        var desc = MetaUtil.Ascii(buf, 0, 256);
                        if (!string.IsNullOrWhiteSpace(desc)) m.Caption ??= desc;
                        var originator = MetaUtil.Ascii(buf, 256, 32);
                        if (!string.IsNullOrWhiteSpace(originator)) m.Creator ??= originator;
                        var date = MetaUtil.Ascii(buf, 320, 10);
                        var time = MetaUtil.Ascii(buf, 330, 8);
                        if (m.CapturedAt is null && !string.IsNullOrWhiteSpace(date))
                        {
                            var parsed = TimeUtil.ParseFlexible($"{date} {time}".Trim());
                            if (parsed is not null) { m.CapturedAt = parsed; m.CapturedAtSource = "bext"; }
                        }
                    }
                    break;
                }
                case "id3 ":
                case "ID3 ":
                {
                    var tagBytes = new byte[Math.Min(size, 8 * 1024 * 1024)];
                    if (ReadExact(fs, tagBytes))
                        ParseId3v2Bytes(tagBytes, m);
                    break;
                }
            }

            fs.Position = payload + size + (size & 1);
        }

        m.AudioSampleRate ??= sampleRate > 0 ? sampleRate : null;
        m.AudioChannels ??= channels > 0 ? channels : null;
        m.BitDepth ??= bits > 0 ? bits : null;
        m.AudioCodec ??= MapWaveFormat(formatTag);
        if (!codecSet) m.Warnings.Add("WAV 未找到 fmt 块");
        if (byteRate <= 0 && sampleRate > 0 && channels > 0 && bits > 0)
            byteRate = (long)sampleRate * channels * bits / 8;
        if (byteRate > 0) m.BitrateBps ??= byteRate * 8;

        if (sampleCount > 0 && sampleRate > 0)
            m.DurationSeconds ??= Math.Round((double)sampleCount / sampleRate, 3);
        else if (dataSize > 0 && byteRate > 0)
            m.DurationSeconds ??= Math.Round((double)dataSize / byteRate, 3);
        else if (dataSize > 0 && sampleRate > 0 && channels > 0 && bits > 0)
            m.DurationSeconds ??= Math.Round((double)dataSize / (sampleRate * channels * bits / 8.0), 3);
    }

    private static void ParseRiffInfo(Stream fs, long start, long end, MediaMetadata m)
    {
        var pos = start;
        Span<byte> head = stackalloc byte[8];
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (!ReadExact(fs, head)) break;
            var id = Encoding.ASCII.GetString(head[..4]);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]), 1024 * 1024);
            if (size < 0 || pos + 8 + size > end) break;
            var buf = new byte[size];
            if (!ReadExact(fs, buf)) break;
            var value = MetaUtil.Utf8(buf);
            if (!string.IsNullOrWhiteSpace(value))
            {
                switch (id)
                {
                    case "INAM": m.Title ??= value; break;
                    case "IART": m.Creator ??= value; break;
                    case "ICOP": m.Copyright ??= value; break;
                    case "ICMT": m.Caption ??= value; break;
                    case "ICRD":
                        if (m.CapturedAt is null)
                        {
                            var parsed = TimeUtil.ParseFlexible(value);
                            if (parsed is not null) { m.CapturedAt = parsed; m.CapturedAtSource = "RIFF-INFO"; }
                        }
                        break;
                    case "IKEY":
                        foreach (var kw in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var clean = MetaUtil.CleanText(kw, 128);
                            if (clean is not null && !m.Keywords.Contains(clean, StringComparer.OrdinalIgnoreCase))
                                m.Keywords.Add(clean);
                        }
                        break;
                    case "IGNR":
                        if (!string.IsNullOrWhiteSpace(value) && !m.Keywords.Contains(value, StringComparer.OrdinalIgnoreCase))
                            m.Keywords.Add(value);
                        break;
                }
            }
            pos += 8 + size + (size & 1);
        }
    }

    private static string? MapWaveFormat(int formatTag) => formatTag switch
    {
        1 => "PCM",
        2 => "ADPCM",
        3 => "PCM Float",
        6 => "A-Law",
        7 => "μ-Law",
        0x11 => "IMA ADPCM",
        0x55 => "MP3",
        0x2000 => "AC-3",
        0x2001 => "DTS",
        0xFFFE => "PCM",
        _ => formatTag > 0 ? $"WAV({formatTag:X4})" : null,
    };

    // ============================================================ MP3 / ID3

    private static void ReadMp3(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        m.HasAudio = true;
        m.ContainerName = "MPEG-Audio";
        m.AudioCodec = "MP3";

        using var fs = MetaUtil.OpenSequential(fullPath);
        var fileLen = fs.Length;

        long audioStart = 0;
        Span<byte> id3Head = stackalloc byte[10];
        if (fileLen >= 10)
        {
            fs.Position = 0;
            if (ReadExact(fs, id3Head) && id3Head[0] == 'I' && id3Head[1] == 'D' && id3Head[2] == '3')
            {
                // ID3v2：10 字节头 + syncsafe 大小 (+footer)
                var size = SyncSafe(id3Head[6], id3Head[7], id3Head[8], id3Head[9]);
                var hasFooter = (id3Head[5] & 0x10) != 0;
                var tagTotal = 10L + size + (hasFooter ? 10 : 0);
                audioStart = Math.Min(fileLen, tagTotal);

                var tagBytes = new byte[Math.Min(tagTotal, 16 * 1024 * 1024)];
                fs.Position = 0;
                if (ReadExact(fs, tagBytes))
                    ParseId3v2Bytes(tagBytes, m);
            }
        }

        // 找到第一个 MPEG 帧
        var frame = FindFirstMpegFrame(fs, audioStart, fileLen, ct);
        if (frame is null)
        {
            m.Warnings.Add("MP3 未找到有效 MPEG 帧");
            ParseId3v1(fs, fileLen, m);
            return;
        }

        m.AudioSampleRate ??= frame.Value.SampleRate;
        m.AudioChannels ??= frame.Value.Channels;
        if (frame.Value.Layer == 3) m.AudioCodec = "MP3";

        // Xing/Info（VBR 头）优先给总帧数
        var duration = TryXingDuration(fs, frame.Value, fileLen);
        if (duration is null)
        {
            var bitrate = frame.Value.BitrateKbps;
            if (bitrate > 0)
            {
                var audioBytes = Math.Max(0, fileLen - audioStart);
                duration = audioBytes * 8.0 / (bitrate * 1000.0);
                m.BitrateBps ??= (long)bitrate * 1000;
            }
        }
        else
        {
            // VBR：用文件大小/时长反推平均码率
            if (duration > 0) m.BitrateBps ??= (long)(Math.Max(0, fileLen - audioStart) * 8 / duration.Value);
        }
        if (duration is > 0) m.DurationSeconds ??= Math.Round(duration.Value, 3);

        ParseId3v1(fs, fileLen, m);
    }

    private readonly record struct MpegFrame(int SampleRate, int Channels, int Layer, int BitrateKbps, long Offset, int SamplesPerFrame);

    private static readonly int[,] BitrateTable =
    {
        // [versionGroup, layerIndex] -> 14 个 kbps 档位；versionGroup 0 = MPEG1, 1 = MPEG2/2.5
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };

    private static readonly int[] SampleRateTableMpeg1 = { 44100, 48000, 32000 };
    private static readonly int[] SampleRateTableMpeg2 = { 22050, 24000, 16000 };
    private static readonly int[] SampleRateTableMpeg25 = { 11025, 12000, 8000 };

    private static MpegFrame? FindFirstMpegFrame(Stream fs, long start, long fileLen, CancellationToken ct)
    {
        var maxScan = Math.Min(fileLen, start + 2 * 1024 * 1024);
        var buf = new byte[64 * 1024 + 1];
        var pos = start;
        while (pos < maxScan)
        {
            ct.ThrowIfCancellationRequested();
            fs.Position = pos;
            var want = (int)Math.Min(buf.Length, maxScan - pos);
            var read = fs.Read(buf, 0, want);
            if (read <= 0) break;
            for (var i = 0; i + 4 <= read; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                if (!TryParseMpegHeader(buf.AsSpan(i), out var frame)) continue;
                return frame with { Offset = pos + i };
            }
            if (read < 4) break;
            pos += read - 3;
        }
        return null;
    }

    private static bool TryParseMpegHeader(ReadOnlySpan<byte> b, out MpegFrame frame)
    {
        frame = default;
        if (b.Length < 4) return false;
        var b1 = b[1];
        var versionBits = (b1 >> 3) & 0x03;   // 0=2.5 1=reserved 2=2 3=1
        var layerBits = (b1 >> 1) & 0x03;     // 0=reserved 1=III 2=II 3=I
        if (versionBits == 1 || layerBits == 0) return false;

        var b2 = b[2];
        var bitrateIndex = (b2 >> 4) & 0x0F;
        var sampleRateIndex = (b2 >> 2) & 0x03;
        if (bitrateIndex is 0 or 15 || sampleRateIndex == 3) return false;

        var mpeg1 = versionBits == 3;
        int[] rates = versionBits switch
        {
            3 => SampleRateTableMpeg1,
            2 => SampleRateTableMpeg2,
            _ => SampleRateTableMpeg25,
        };
        var sampleRate = rates[sampleRateIndex];

        var layer = layerBits switch { 3 => 1, 2 => 2, _ => 3 };
        var tableRow = mpeg1 ? layer - 1 : (layer == 1 ? 3 : 4);
        if (tableRow < 0 || tableRow >= BitrateTable.GetLength(0)) return false;
        var bitrate = BitrateTable[tableRow, bitrateIndex];

        var mode = (b[3] >> 6) & 0x03;
        var channels = mode == 3 ? 1 : 2;
        var samplesPerFrame = layer switch
        {
            1 => 384,
            2 => 1152,
            _ => mpeg1 ? 1152 : 576,
        };
        frame = new MpegFrame(sampleRate, channels, layer, bitrate, 0, samplesPerFrame);
        return true;
    }

    private static double? TryXingDuration(Stream fs, MpegFrame frame, long fileLen)
    {
        try
        {
            // 在第一个帧头后 200 字节内找 Xing/Info 标签
            var probe = new byte[256];
            fs.Position = frame.Offset;
            var read = fs.Read(probe, 0, probe.Length);
            if (read < 16) return null;
            for (var i = 4; i + 12 <= read; i++)
            {
                var isXing = probe[i] == 'X' && probe[i + 1] == 'i' && probe[i + 2] == 'n' && probe[i + 3] == 'g';
                var isInfo = probe[i] == 'I' && probe[i + 1] == 'n' && probe[i + 2] == 'f' && probe[i + 3] == 'o';
                if (!isXing && !isInfo) continue;
                var flags = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(i + 4));
                if ((flags & 0x01) == 0 || i + 12 > read) return null;
                var frames = (long)BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(i + 8));
                if (frames <= 0) return null;
                return frames * (double)frame.SamplesPerFrame / frame.SampleRate;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    private static void ParseId3v1(Stream fs, long fileLen, MediaMetadata m)
    {
        if (fileLen < 128) return;
        try
        {
            fs.Position = fileLen - 128;
            var buf = new byte[128];
            if (!ReadExact(fs, buf) || buf[0] != 'T' || buf[1] != 'A' || buf[2] != 'G') return;
            m.Title ??= DecodeLatin1(buf, 3, 30);
            m.Creator ??= DecodeLatin1(buf, 33, 30);
            var year = DecodeLatin1(buf, 93, 4);
            if (m.CapturedAt is null && year is not null && int.TryParse(year, out var y) && y is > 1900 and < 2200)
            {
                m.CapturedAt = new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero);
                m.CapturedAtSource = "id3v1";
            }
        }
        catch { /* 忽略 */ }
    }

    private static string? DecodeLatin1(byte[] buf, int offset, int len)
    {
        var end = offset + len;
        var actual = offset;
        while (actual < end && buf[actual] != 0) actual++;
        return MetaUtil.CleanText(Encoding.Latin1.GetString(buf, offset, actual - offset), 256);
    }

    /// <summary>
    /// ID3v2.2 / 2.3 / 2.4 帧解析。输入为完整 ID3 标签字节（含 "ID3" 头）；没有头时按 2.3 裸帧处理。
    /// </summary>
    private static void ParseId3v2Bytes(ReadOnlySpan<byte> data, MediaMetadata m)
    {
        try
        {
            if (data.Length < 6) return;
            byte major = 3;
            byte flags = 0;
            var payload = data;
            if (data.Length >= 10 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
            {
                major = data[3];
                flags = data[5];
                var size = SyncSafe(data[6], data[7], data[8], data[9]);
                var avail = Math.Max(0, Math.Min(size, data.Length - 10));
                payload = data.Slice(10, avail);
            }
            if (major is < 2 or > 4) major = 3;
            if ((flags & 0x80) != 0) payload = DeUnsynchronize(payload);

            var idx = 0;
            if ((flags & 0x40) != 0 && major == 3 && payload.Length >= 4)
                idx = 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
            else if ((flags & 0x40) != 0 && major == 4 && payload.Length >= 4)
                idx = 4 + SyncSafe(payload[0], payload[1], payload[2]);
            if (idx < 0 || idx >= payload.Length) return;

            var guard = 0;
            while (idx + (major == 2 ? 6 : 10) <= payload.Length && guard++ < 10000)
            {
                var frameId = major == 2
                    ? Encoding.ASCII.GetString(payload.Slice(idx, 3))
                    : Encoding.ASCII.GetString(payload.Slice(idx, 4));
                if (frameId.Length == 0 || frameId[0] == '\0') break;

                int frameSize;
                int headerSize;
                if (major == 2)
                {
                    frameSize = (payload[idx + 3] << 16) | (payload[idx + 4] << 8) | payload[idx + 5];
                    headerSize = 6;
                }
                else if (major == 3)
                {
                    frameSize = (int)BinaryPrimitives.ReadUInt32BigEndian(payload[(idx + 4)..]);
                    headerSize = 10;
                }
                else
                {
                    frameSize = SyncSafe(payload[idx + 4], payload[idx + 5], payload[idx + 6], payload[idx + 7]);
                    headerSize = 10;
                }
                if (frameSize <= 0 || idx + headerSize + frameSize > payload.Length) break;

                var frame = payload.Slice(idx + headerSize, frameSize);
                ApplyId3Frame(frameId, frame, m);
                idx += headerSize + frameSize;
            }
        }
        catch (Exception ex)
        {
            m.Warnings.Add("ID3 解析失败: " + ex.Message);
        }
    }

    private static ReadOnlySpan<byte> DeUnsynchronize(ReadOnlySpan<byte> src)
    {
        var buf = new byte[src.Length];
        var j = 0;
        for (var i = 0; i < src.Length; i++)
        {
            buf[j++] = src[i];
            if (src[i] == 0xFF && i + 1 < src.Length && src[i + 1] == 0x00) i++;
        }
        return buf.AsSpan(0, j);
    }

    private static void ApplyId3Frame(string frameId, ReadOnlySpan<byte> frame, MediaMetadata m)
    {
        // v2.2 → v2.3/2.4 常用 ID 统一
        frameId = frameId switch
        {
            "TT2" => "TIT2",
            "TP1" => "TPE1",
            "TAL" => "TALB",
            "TYE" => "TYER",
            "TCO" => "TCON",
            "TCR" => "TCOP",
            "TCM" => "TCOM",
            "TP2" => "TPE2",
            "TEN" => "TENC",
            "TSS" => "TSSE",
            _ => frameId,
        };

        switch (frameId)
        {
            case "TIT2": m.Title ??= ReadId3Text(frame); break;
            case "TPE1": m.Creator ??= ReadId3Text(frame); break;
            case "TPE2": m.Creator ??= ReadId3Text(frame); break;
            case "TCOP": m.Copyright ??= ReadId3Text(frame); break;
            case "TDRC":
            case "TYER":
            case "TDAT":
                if (m.CapturedAt is null)
                {
                    var text = ReadId3Text(frame);
                    var parsed = TimeUtil.ParseFlexible(text);
                    if (parsed is null && text is { Length: 4 } && int.TryParse(text, out var year) && year is > 1900 and < 2200)
                        parsed = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    if (parsed is not null)
                    {
                        m.CapturedAt = parsed;
                        m.CapturedAtSource ??= "id3";
                    }
                }
                break;
            case "TCON":
            {
                var genre = ReadId3Text(frame);
                if (!string.IsNullOrWhiteSpace(genre) && !m.Keywords.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    m.Keywords.Add(genre);
                break;
            }
            case "COMM":
            {
                var text = ReadId3Comment(frame);
                if (!string.IsNullOrWhiteSpace(text)) m.Caption ??= text;
                break;
            }
            case "TIT1":
                m.Caption ??= ReadId3Text(frame);
                break;
        }
    }

    private static string? ReadId3Text(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1) return null;
        var encoding = frame[0];
        var body = frame[1..];
        var text = DecodeId3String(body, encoding);
        return text;
    }

    private static string? ReadId3Comment(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return null;
        var encoding = frame[0];
        var body = frame[4..];   // 跳过编码 + 3 字节语言
        var terminator = encoding is 1 or 2 ? 2 : 1;
        var text = DecodeId3String(body, encoding);
        if (text is null) return null;
        // 去掉短描述（描述与正文之间用 NUL 分隔）
        var idx = text.IndexOf('\0');
        if (idx >= 0 && idx + 1 < text.Length) text = text[(idx + 1)..];
        _ = terminator;
        return MetaUtil.CleanText(text.TrimEnd('\0'), 2048);
    }

    private static string? DecodeId3String(ReadOnlySpan<byte> body, byte encoding)
    {
        try
        {
            string text;
            switch (encoding)
            {
                case 0:
                    text = Encoding.Latin1.GetString(body);
                    break;
                case 1:
                    if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
                        text = Encoding.Unicode.GetString(body[2..]);
                    else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
                        text = Encoding.BigEndianUnicode.GetString(body[2..]);
                    else
                        text = Encoding.Unicode.GetString(body);
                    break;
                case 2:
                    text = Encoding.BigEndianUnicode.GetString(body);
                    break;
                default:
                    text = Encoding.UTF8.GetString(body);
                    break;
            }
            return MetaUtil.CleanText(text.TrimEnd('\0'), 4096);
        }
        catch { return null; }
    }

    // ============================================================ FLAC

    private static void ReadFlac(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        m.HasAudio = true;
        m.ContainerName = "FLAC";
        m.AudioCodec = "FLAC";
        using var fs = MetaUtil.OpenSequential(fullPath);
        var fileLen = fs.Length;

        Span<byte> magic = stackalloc byte[4];
        if (!ReadExact(fs, magic)) return;

        long totalSamples = -1;
        int sampleRate = 0, channels = 0, bits = 0;
        var metadataEnd = fs.Position;
        var guard = 0;
        while (guard++ < 1024)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[4];
            if (!ReadExact(fs, header)) break;
            var isLast = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var size = (header[1] << 16) | (header[2] << 8) | header[3];
            var payload = fs.Position;
            if (payload + size > fileLen) break;

            if (type == 0 && size >= 34) // STREAMINFO
            {
                var buf = new byte[34];
                if (ReadExact(fs, buf))
                {
                    sampleRate = ((buf[10] & 0xFF) << 12) | ((buf[11] & 0xFF) << 4) | ((buf[12] & 0xF0) >> 4);
                    channels = ((buf[12] >> 1) & 0x07) + 1;
                    bits = (((buf[12] & 0x01) << 4) | ((buf[13] & 0xF0) >> 4)) + 1;
                    totalSamples = ((long)(buf[13] & 0x0F) << 32) |
                                   ((long)buf[14] << 24) | ((long)buf[15] << 16) | ((long)buf[16] << 8) | buf[17];
                }
            }
            else if (type == 4) // VORBIS_COMMENT
            {
                var buf = new byte[Math.Min(size, 8 * 1024 * 1024)];
                if (ReadExact(fs, buf)) ParseVorbisComment(buf, m);
            }

            metadataEnd = payload + size;
            fs.Position = metadataEnd;
            if (isLast) break;
        }

        m.AudioSampleRate ??= sampleRate > 0 ? sampleRate : null;
        m.AudioChannels ??= channels > 0 ? channels : null;
        m.BitDepth ??= bits > 0 ? bits : null;
        if (totalSamples > 0 && sampleRate > 0)
            m.DurationSeconds ??= Math.Round((double)totalSamples / sampleRate, 3);
        if (m.DurationSeconds is > 0)
        {
            var audioBytes = Math.Max(0, fileLen - metadataEnd);
            m.BitrateBps ??= (long)(audioBytes * 8 / m.DurationSeconds.Value);
        }
    }

    private static void ParseVorbisComment(ReadOnlySpan<byte> data, MediaMetadata m)
    {
        try
        {
            var idx = 0;
            if (idx + 4 > data.Length) return;
            var vendorLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[idx..]);
            idx += 4 + vendorLen;
            if (idx + 4 > data.Length) return;
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[idx..]);
            idx += 4;
            if (count is < 0 or > 100000) return;

            for (var i = 0; i < count && idx + 4 <= data.Length; i++)
            {
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[idx..]);
                idx += 4;
                if (len < 0 || idx + len > data.Length) break;
                var entry = Encoding.UTF8.GetString(data.Slice(idx, len));
                idx += len;
                var eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var key = entry[..eq].Trim().ToUpperInvariant();
                var value = MetaUtil.CleanText(entry[(eq + 1)..], 4096);
                if (string.IsNullOrWhiteSpace(value)) continue;
                switch (key)
                {
                    case "TITLE": m.Title ??= value; break;
                    case "ARTIST": m.Creator ??= value; break;
                    case "ALBUMARTIST": m.Creator ??= value; break;
                    case "COPYRIGHT": m.Copyright ??= value; break;
                    case "DESCRIPTION":
                    case "COMMENT": m.Caption ??= value; break;
                    case "DATE":
                    case "YEAR":
                        if (m.CapturedAt is null)
                        {
                            var parsed = TimeUtil.ParseFlexible(value);
                            if (parsed is null && value.Length == 4 && int.TryParse(value, out var y) && y is > 1900 and < 2200)
                                parsed = new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero);
                            if (parsed is not null) { m.CapturedAt = parsed; m.CapturedAtSource = "vorbis"; }
                        }
                        break;
                    case "GENRE":
                    case "KEYWORDS":
                        foreach (var kw in value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var clean = MetaUtil.CleanText(kw, 128);
                            if (clean is not null && !m.Keywords.Contains(clean, StringComparer.OrdinalIgnoreCase))
                                m.Keywords.Add(clean);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            m.Warnings.Add("VorbisComment 解析失败: " + ex.Message);
        }
    }

    // ============================================================ OGG / Opus

    private static void ReadOgg(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        m.HasAudio = true;
        m.ContainerName = "Ogg";
        using var fs = MetaUtil.OpenSequential(fullPath);
        var fileLen = fs.Length;

        var headLen = (int)Math.Min(fileLen, 256 * 1024);
        var head = new byte[headLen];
        if (!ReadExact(fs, head)) return;

        string? codec = null;
        int sampleRate = 0, channels = 0, preSkip = 0;
        long nominalBitrate = 0;

        var packet = ExtractFirstPacket(head);
        if (packet.Length >= 8)
        {
            if (packet.Length >= 19 && packet.StartsWith("OpusHead"u8))
            {
                codec = "Opus";
                channels = packet[9];
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]);
                sampleRate = 48000;
                if (channels <= 0) channels = 2;
            }
            else if (packet.Length >= 28 && packet[0] == 0x01 && packet[1..].StartsWith("vorbis"u8))
            {
                codec = "Vorbis";
                channels = packet[11];
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
                nominalBitrate = BinaryPrimitives.ReadInt32LittleEndian(packet[20..]);
            }
            else if (packet.StartsWith("Speex   "u8))
            {
                codec = "Speex";
            }
            else if (packet.Length >= 5 && packet[0] == 0x7F && packet[1..5].SequenceEqual("FLAC"u8))
            {
                codec = "FLAC";
            }
        }

        m.AudioCodec ??= codec ?? "Ogg";
        m.AudioSampleRate ??= sampleRate > 0 ? sampleRate : null;
        m.AudioChannels ??= channels > 0 ? channels : null;
        if (nominalBitrate > 0) m.BitrateBps ??= nominalBitrate;

        // 文本标签：在第一页之后找 comment packet
        ParseOggTags(head, m);

        // 时长：找最后一个 OggS 页的 granule position
        var granule = FindLastGranule(fs, fileLen);
        if (granule is > 0)
        {
            if (codec == "Opus" && sampleRate == 48000)
                m.DurationSeconds ??= Math.Round(Math.Max(0, granule.Value - preSkip) / 48000.0, 3);
            else if (sampleRate > 0)
                m.DurationSeconds ??= Math.Round((double)granule.Value / sampleRate, 3);
        }
        if (m.DurationSeconds is > 0 && m.BitrateBps is null)
            m.BitrateBps = (long)(fileLen * 8 / m.DurationSeconds.Value);
        ct.ThrowIfCancellationRequested();
    }

    private static ReadOnlySpan<byte> ExtractFirstPacket(ReadOnlySpan<byte> page)
    {
        if (page.Length < 28 || !page.StartsWith("OggS"u8)) return default;
        var segments = page[26];
        var payloadStart = 27 + segments;
        if (payloadStart >= page.Length) return default;
        var size = 0;
        for (var i = 0; i < segments; i++)
        {
            size += page[27 + i];
            if (i == 0) break; // 首个包可能跨段，这里只估算头一页足够识别 codec
        }
        size = 0;
        for (var i = 0; i < segments; i++) size += page[27 + i];
        return page.Slice(payloadStart, Math.Min(size, page.Length - payloadStart));
    }

    private static void ParseOggTags(ReadOnlySpan<byte> buffer, MediaMetadata m)
    {
        try
        {
            // Vorbis：\x03vorbis + VorbisComment
            var idx = buffer.IndexOf("\x03vorbis"u8);
            if (idx >= 0) ParseVorbisComment(buffer[(idx + 7)..], m);

            // Opus：OpusTags + VorbisComment
            var opus = buffer.IndexOf("OpusTags"u8);
            if (opus >= 0) ParseVorbisComment(buffer[(opus + 8)..], m);
        }
        catch (Exception ex)
        {
            m.Warnings.Add("Ogg 标签解析失败: " + ex.Message);
        }
    }

    private static long? FindLastGranule(Stream fs, long fileLen)
    {
        try
        {
            var window = (int)Math.Min(fileLen, 256 * 1024);
            if (window < 28) return null;
            var buf = new byte[window];
            fs.Position = fileLen - window;
            if (!ReadExact(fs, buf)) return null;
            for (var i = buf.Length - 28; i >= 0; i--)
            {
                if (buf[i] != 'O' || buf[i + 1] != 'g' || buf[i + 2] != 'g' || buf[i + 3] != 'S') continue;
                var granule = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i + 6));
                if (granule > 0) return granule;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    // ============================================================ AIFF

    private static void ReadAiff(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        m.HasAudio = true;
        m.ContainerName = "AIFF";
        using var fs = MetaUtil.OpenSequential(fullPath);
        var fileLen = fs.Length;

        Span<byte> head = stackalloc byte[12];
        if (!ReadExact(fs, head)) return;
        var formType = Encoding.ASCII.GetString(head[8..12]);

        long numFrames = -1;
        int channels = 0, bits = 0, sampleRate = 0;
        string? codec = formType == "AIFC" ? null : "PCM";

        Span<byte> chunkHead = stackalloc byte[8];
        var guard = 0;
        while (fs.Position + 8 <= fileLen && guard++ < 4096)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadExact(fs, chunkHead)) break;
            var id = Encoding.ASCII.GetString(chunkHead[..4]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(chunkHead[4..8]);
            var payload = fs.Position;
            if (payload + size > fileLen) break;

            switch (id)
            {
                case "COMM":
                {
                    var buf = new byte[Math.Min(size, 64)];
                    if (ReadExact(fs, buf) && buf.Length >= 18)
                    {
                        channels = BinaryPrimitives.ReadUInt16BigEndian(buf);
                        numFrames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(2));
                        bits = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
                        sampleRate = (int)Math.Round(ReadExtended80(buf.AsSpan(8, 10)));
                        if (formType == "AIFC" && buf.Length >= 26)
                        {
                            var compression = Encoding.ASCII.GetString(buf, 18, 4);
                            codec = MapAifcCompression(compression);
                        }
                    }
                    break;
                }
                case "NAME":
                case "AUTH":
                case "(c) ":
                case "ANNO":
                {
                    var buf = new byte[Math.Min(size, 1024 * 1024)];
                    if (ReadExact(fs, buf))
                    {
                        var value = MetaUtil.Utf8(buf);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            switch (id)
                            {
                                case "NAME": m.Title ??= value; break;
                                case "AUTH": m.Creator ??= value; break;
                                case "(c) ": m.Copyright ??= value; break;
                                case "ANNO": m.Caption ??= value; break;
                            }
                        }
                    }
                    break;
                }
                case "ID3 ":
                {
                    var tagBytes = new byte[Math.Min(size, 8 * 1024 * 1024)];
                    if (ReadExact(fs, tagBytes))
                        ParseId3v2Bytes(tagBytes, m);
                    break;
                }
            }

            fs.Position = payload + size + (size & 1);
        }

        m.AudioCodec ??= codec ?? "PCM";
        m.AudioSampleRate ??= sampleRate > 0 ? sampleRate : null;
        m.AudioChannels ??= channels > 0 ? channels : null;
        m.BitDepth ??= bits > 0 ? bits : null;
        if (numFrames > 0 && sampleRate > 0) m.DurationSeconds ??= Math.Round((double)numFrames / sampleRate, 3);
        if (m.BitrateBps is null && sampleRate > 0 && channels > 0 && bits > 0)
            m.BitrateBps = (long)sampleRate * channels * bits;
    }

    private static string? MapAifcCompression(string compression) => compression switch
    {
        "NONE" or "twos" or "sowt" or "in24" or "in32" => "PCM",
        "fl32" or "FL32" => "PCM Float",
        "fl64" => "PCM Float 64",
        "alaw" or "ALAW" => "A-Law",
        "ulaw" or "ULAW" => "μ-Law",
        "ima4" => "IMA ADPCM",
        "MAC3" => "MACE 3",
        "MAC6" => "MACE 6",
        _ => compression.Trim(),
    };

    /// <summary>AIFF 的 80 位 IEEE 754 extended 浮点。</summary>
    private static double ReadExtended80(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10) return 0;
        var exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = 0;
        for (var i = 2; i < 10; i++) mantissa = (mantissa << 8) | b[i];
        if (exponent == 0 && mantissa == 0) return 0;
        if (exponent == 0x7FFF) return 0;
        var sign = (b[0] & 0x80) != 0 ? -1 : 1;
        return sign * (double)mantissa * Math.Pow(2, exponent - 16383 - 63);
    }

    // ============================================================ 工具

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static bool ReadExact(Stream stream, byte[] buffer) => ReadExact(stream, buffer.AsSpan());

    private static int SyncSafe(params byte[] bytes)
    {
        var value = 0;
        foreach (var b in bytes) value = (value << 7) | (b & 0x7F);
        return value;
    }

    private static int SyncSafe(byte a, byte b, byte c) => ((a & 0x7F) << 14) | ((b & 0x7F) << 7) | (c & 0x7F);
}