using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Metadata;

/// <summary>RAW/容器内嵌预览图的位置与尺寸。</summary>
public sealed class EmbeddedPreviewInfo
{
    public bool Found { get; init; }
    public long Offset { get; init; }
    public int Length { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>发现方式：tiff-ifd / scan。</summary>
    public string Method { get; init; } = "";
    public long Area => (long)Width * Height;
}

/// <summary>
/// RAW 内嵌预览提取。两条路径：
/// 1) DNG/CR2 等 TIFF 系文件优先读 IFD 的 JPEGInterchangeFormat(513)/Length(514)（含 SubIFD），零扫描；
/// 2) 其它 RAW/CR3 用分块流式扫描整个文件，找"最大且完整"的 JPEG（SOI 0xFFD8 → 匹配 EOI 0xFFD9），
///    绝不整体 Load 文件，内存峰值约 1MB。
/// </summary>
public static class RawPreviewExtractor
{
    private const int ScanChunk = 1 << 20;          // 1 MB
    private const long MaxJpegBytes = 128L * 1024 * 1024;
    private static readonly byte[] SoiPattern = { 0xFF, 0xD8, 0xFF };

    /// <summary>TIFF 系 RAW（以及所有以 II*\0 / MM\0* 开头的文件）的扩展名集合。</summary>
    public static bool IsTiffBased(RawFormat raw) => raw is RawFormat.Dng or RawFormat.Cr2 or RawFormat.Nef or RawFormat.Arw
        or RawFormat.Orf or RawFormat.Rw2 or RawFormat.Pef or RawFormat.Srw or RawFormat.Other;

    /// <summary>找最大内嵌预览。失败返回 <see cref="EmbeddedPreviewInfo"/>（Found=false），不抛异常。</summary>
    public static EmbeddedPreviewInfo Find(string fullPath, RawFormat raw = RawFormat.None, CancellationToken ct = default)
    {
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            var handle = fs.SafeFileHandle;
            var length = fs.Length;
            if (length < 64) return new EmbeddedPreviewInfo();

            // 1) TIFF IFD 快路径
            if (IsTiffBased(raw) || LooksLikeTiff(handle, length))
            {
                var fromIfd = FindViaTiff(fullPath, handle, length, ct);
                if (fromIfd is { Found: true }) return fromIfd;
            }

            // 2) 全文件流式扫描
            return ScanLargestJpeg(handle, length, ct);
        }
        catch (OperationCanceledException)
        {
            return new EmbeddedPreviewInfo();
        }
        catch (Exception ex)
        {
            Util.Log.Warn($"[RawPreviewExtractor] 预览探测失败: {fullPath}", ex);
            return new EmbeddedPreviewInfo();
        }
    }

    /// <summary>按已找到的位置把 JPEG 字节读出来（用于解码器）。</summary>
    public static byte[]? Extract(string fullPath, EmbeddedPreviewInfo info, CancellationToken ct = default)
    {
        if (info is not { Found: true, Length: > 0 }) return null;
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            var handle = fs.SafeFileHandle;
            if (info.Offset < 0 || info.Offset + info.Length > fs.Length) return null;
            var buf = new byte[info.Length];
            var read = 0;
            while (read < buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                var n = RandomAccess.Read(handle, buf.AsSpan(read), info.Offset + read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }
        catch (Exception ex)
        {
            Util.Log.Warn($"[RawPreviewExtractor] 预览读取失败: {fullPath}", ex);
            return null;
        }
    }

    // ============================================================ TIFF IFD 路径

    private static EmbeddedPreviewInfo? FindViaTiff(string path, SafeFileHandle handle, long length, CancellationToken ct)
    {
        var tiff = TiffParser.Parse(path);
        if (tiff is null) return null;

        EmbeddedPreviewInfo? best = null;
        foreach (var ifd in tiff.AllIfds())
        {
            ct.ThrowIfCancellationRequested();
            var offset = ifd.GetLong(513);
            var size = ifd.GetLong(514);
            if (offset is null || size is null || offset <= 8 || size <= 256) continue;
            if (offset.Value + size.Value > length || size.Value > MaxJpegBytes) continue;
            if (!IsJpegAt(handle, offset.Value)) continue;

            var dims = ReadJpegSizeAt(handle, offset.Value, Math.Min(size.Value, 4096));
            var cand = new EmbeddedPreviewInfo
            {
                Found = true,
                Offset = offset.Value,
                Length = (int)size.Value,
                Width = dims.Width,
                Height = dims.Height,
                Method = "tiff-ifd",
            };
            if (best is null || cand.Area > best.Area) best = cand;
        }
        return best;
    }

    private static bool LooksLikeTiff(SafeFileHandle handle, long length)
    {
        if (length < 8) return false;
        Span<byte> head = stackalloc byte[4];
        if (!ReadExact(handle, 0, head)) return false;
        return (head[0] == (byte)'I' && head[1] == (byte)'I' && head[2] == 42 && head[3] == 0) ||
               (head[0] == (byte)'M' && head[1] == (byte)'M' && head[2] == 0 && head[3] == 42);
    }

    // ============================================================ 流式扫描路径

    private static EmbeddedPreviewInfo ScanLargestJpeg(SafeFileHandle handle, long length, CancellationToken ct)
    {
        EmbeddedPreviewInfo? best = null;
        var buffer = new byte[ScanChunk + SoiPattern.Length];
        var carry = 0;
        long pos = 0;

        while (pos < length)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(ScanChunk, length - pos);
            var read = RandomAccess.Read(handle, buffer.AsSpan(carry, want), pos);
            if (read <= 0) break;
            var total = carry + read;
            var baseOffset = pos - carry;
            var span = new ReadOnlySpan<byte>(buffer, 0, total);

            var idx = 0;
            var skipUntil = -1; // baseOffset 内的跳过位置（找到 JPEG 后跳过其全部数据）
            while (idx < span.Length - 2)
            {
                if (idx < skipUntil)
                {
                    if (skipUntil >= span.Length - 2)
                    {
                        idx = span.Length;
                        break;
                    }
                    idx = (int)skipUntil;
                    continue;
                }
                var rel = span[idx..].IndexOf(SoiPattern);
                if (rel < 0) break;
                idx += rel;
                var candidateOffset = baseOffset + idx;
                var info = TryReadJpeg(handle, candidateOffset, length, ct);
                if (info is not null)
                {
                    if (best is null || info.Area > best.Area ||
                        (info.Area == best.Area && info.Length > best.Length))
                        best = info;
                    skipUntil = (int)Math.Min(int.MaxValue, (candidateOffset + info.Length) - baseOffset);
                    idx++;
                    continue;
                }
                idx++;
            }

            if (read < want) break;
            carry = Math.Min(SoiPattern.Length - 1, total);
            if (carry > 0) span[(total - carry)..].CopyTo(buffer);
            pos += read;
        }

        return best ?? new EmbeddedPreviewInfo();
    }

    /// <summary>验证 offset 处是一个结构合法、有 EOI 的 JPEG，返回其长度与尺寸；不是则 null。</summary>
    private static EmbeddedPreviewInfo? TryReadJpeg(SafeFileHandle handle, long offset, long fileLength, CancellationToken ct)
    {
        if (offset < 0 || offset + 4 > fileLength) return null;
        Span<byte> head = stackalloc byte[4];
        if (!ReadExact(handle, offset, head)) return null;
        if (head[0] != 0xFF || head[1] != 0xD8 || head[2] != 0xFF) return null;
        var firstMarker = head[3];
        // SOI 之后的第一个标记必须是合法段起始（APP0..APP15 / DQT / DHT / SOF / DAC / COM / SOS / DRI）
        if (!IsValidMarker(firstMarker) || firstMarker is 0xD8 or 0xD9) return null;

        var result = WalkJpeg(handle, offset, fileLength, ct);
        if (result is null) return null;
        var (len, w, h) = result.Value;
        if (len < 256 || w < 16 || h < 16) return null;
        return new EmbeddedPreviewInfo
        {
            Found = true,
            Offset = offset,
            Length = len,
            Width = w,
            Height = h,
            Method = "scan",
        };
    }

    private static (int Length, int Width, int Height)? WalkJpeg(SafeFileHandle handle, long start, long fileLength, CancellationToken ct)
    {
        var pos = start + 2;
        var width = 0;
        var height = 0;
        var end = Math.Min(fileLength, start + MaxJpegBytes);
        Span<byte> two = stackalloc byte[2];
        Span<byte> sof = stackalloc byte[5];   // precision(1) + height(2) + width(2)
        var guard = 0;

        while (pos + 2 <= end && guard++ < 4096)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadExact(handle, pos, two)) return null;
            if (two[0] != 0xFF) return null;          // 扫描段外必须严格对齐到标记
            var marker = two[1];
            // 填充 0xFF（0xFF00 是字节填充，不应出现在段间）
            while (marker == 0xFF)
            {
                pos++;
                if (!ReadExact(handle, pos, two)) return null;
                marker = two[1];
            }
            pos += 2;

            if (marker == 0xD9) return null;          // 还没看到 SOS 就 EOI：不是完整图像
            if (marker is >= 0xD0 and <= 0xD7 || marker == 0x01) continue;
            if (marker == 0xD8) return null;

            if (!ReadExact(handle, pos, two)) return null;
            var segLen = (two[0] << 8) | two[1];
            if (segLen < 2 || pos + segLen > end) return null;

            if (IsStartOfFrame(marker))
            {
                if (segLen >= 7 && ReadExact(handle, pos + 2, sof))
                {
                    // precision(1) height(2) width(2)
                    height = BinaryPrimitives.ReadUInt16BigEndian(sof[1..3]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(sof[3..5]);
                }
                pos += segLen;
                continue;
            }

            if (marker == 0xDA) // SOS：跳过扫描参数，进入熵编码数据找 EOI
            {
                pos += segLen;
                var eoi = FindEntropyEnd(handle, pos, end, ct, out var nextMarkerPos);
                if (eoi < 0) return null;
                if (nextMarkerPos >= 0)
                {
                    pos = nextMarkerPos;   // 渐进式还有下一个 SOS / 段
                    continue;
                }
                return ((int)(eoi - start), width, height);
            }

            if (marker is 0xDB or 0xC4 or 0xCC or 0xDD or 0xFE || IsAppMarker(marker))
            {
                pos += segLen;
                continue;
            }

            // 其它未知标记：保守跳过其段长（若结构不对会在后面失败）
            pos += segLen;
        }
        return null;
    }

    /// <summary>在熵编码数据里找 EOI(FFD9)；跳过 FF00 填充与 RST 标记。返回 EOI 结束偏移。</summary>
    private static long FindEntropyEnd(SafeFileHandle handle, long pos, long end, CancellationToken ct, out long nextMarkerPos)
    {
        nextMarkerPos = -1;
        var chunk = 1 << 16;
        var buf = new byte[chunk + 1];
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunk, end - pos);
            var read = RandomAccess.Read(handle, buf.AsSpan(0, want), pos);
            if (read <= 0) return -1;
            var span = new ReadOnlySpan<byte>(buf, 0, read);
            var idx = 0;
            while (idx < span.Length - 1)
            {
                var rel = span[idx..].IndexOf((byte)0xFF);
                if (rel < 0) { idx = span.Length; break; }
                idx += rel;
                if (idx + 1 >= span.Length) break;
                var next = span[idx + 1];
                if (next == 0x00) { idx += 2; continue; }              // 填充字节
                if (next is >= 0xD0 and <= 0xD7) { idx += 2; continue; } // RST
                if (next == 0xD9) return pos + idx + 2;
                // 熵编码里出现的其它标记 = 下一个段（渐进式 JPEG 的多个 SOS / 扫描结束）
                nextMarkerPos = pos + idx;
                return -1;
            }
            if (read < want) return -1;
            pos += read - 1; // 保留最后 1 字节（可能是 0xFF）
        }
        return -1;
    }

    // ============================================================ JPEG 头尺寸读取

    /// <summary>从 JPEG 起始的少量字节里读 SOF 尺寸（不验证整张图）。</summary>
    public static bool TryReadJpegDimensions(ReadOnlySpan<byte> head, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8) return false;
        var i = 2;
        while (i + 3 < head.Length)
        {
            if (head[i] != 0xFF) return false;
            var marker = head[i + 1];
            if (marker == 0xFF) { i++; continue; }
            i += 2;
            if (marker == 0xD8 || marker == 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (i + 1 >= head.Length) return false;
            var len = (head[i] << 8) | head[i + 1];
            if (len < 2) return false;
            if (IsStartOfFrame(marker) && i + 6 < head.Length)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(head[(i + 3)..(i + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(head[(i + 5)..(i + 7)]);
                return width > 0 && height > 0;
            }
            if (marker == 0xDA) return false;
            i += len;
        }
        return false;
    }

    // ============================================================ 工具

    private static (int Width, int Height) ReadJpegSizeAt(SafeFileHandle handle, long offset, long maxBytes)
    {
        var size = (int)Math.Min(4096, maxBytes);
        var buf = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = RandomAccess.Read(handle, buf.AsSpan(read), offset + read);
            if (n <= 0) break;
            read += n;
        }
        return TryReadJpegDimensions(buf.AsSpan(0, read), out var w, out var h) ? (w, h) : (0, 0);
    }

    private static bool IsJpegAt(SafeFileHandle handle, long offset)
    {
        Span<byte> b = stackalloc byte[3];
        return ReadExact(handle, offset, b) && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    }

    private static bool IsValidMarker(byte m) =>
        IsAppMarker(m) || IsStartOfFrame(m) || m is 0xDB or 0xC4 or 0xCC or 0xDD or 0xFE or 0xDA or 0xC8;

    private static bool IsAppMarker(byte m) => m is >= 0xE0 and <= 0xEF;

    private static bool IsStartOfFrame(byte m) =>
        m is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

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
}