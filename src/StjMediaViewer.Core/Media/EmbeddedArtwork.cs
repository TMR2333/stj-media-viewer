using System.Buffers.Binary;
using System.Text;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 内嵌图像提取：RAW 里的 JPEG 预览、音频文件里的封面。
/// 全部手写解析（ID3v2 APIC / FLAC PICTURE / MP4 covr / Ogg base64），
/// 不依赖额外库；失败一律返回 null，由上层降级。
/// </summary>
internal static class EmbeddedArtwork
{
    private const int ScanChunk = 1 << 20;         // 1MB
    private const int MinPreviewBytes = 2 * 1024;  // 小于 2KB 的“预览”没有意义

    // ================================================================ RAW：内嵌 JPEG

    /// <summary>
    /// RAW 内嵌预览。先按 TIFF/EXIF 标准解析 IFD1 的 JPEGInterchangeFormat(0x0201)/Length(0x0202)，
    /// 这是 CR2/NEF/ARW/DNG/TIFF 最可靠的路径；解析不到（CR3/RAF 等非 TIFF 布局）再退回
    /// 「全文件扫描候选 JPEG 段 + 结构校验」，只接受能完整走完 marker 链的段。
    /// </summary>
    public static byte[]? FindLargestJpeg(
        string fullPath, CancellationToken ct = default, long maxScanBytes = 256L * 1024 * 1024)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.RandomAccess);
            long fileLen = fs.Length;

            // 1) TIFF 家族：直接按 IFD 指针取预览（精确、无猜测）
            var tiff = TryTiffThumbnail(fs, fileLen, ct);
            if (tiff is { Length: > MinPreviewBytes } && IsValidJpeg(TrimToEoi(tiff))) return TrimToEoi(tiff);

            // 2) 非 TIFF 布局：扫描候选段并做结构校验
            return ScanForJpeg(fs, fileLen, Math.Min(fileLen, maxScanBytes), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>遍历 TIFF IFD 链，取第一个带 JPEGInterchangeFormat/Length 的缩略图。</summary>
    private static byte[]? TryTiffThumbnail(FileStream fs, long fileLen, CancellationToken ct)
    {
        if (fileLen < 16) return null;
        var head = new byte[8];
        fs.Seek(0, SeekOrigin.Begin);
        if (fs.Read(head, 0, 8) != 8) return null;

        bool little;
        if (head[0] == 'I' && head[1] == 'I') little = true;
        else if (head[0] == 'M' && head[1] == 'M') little = false;
        else return null;
        if (ReadU16(head, 2, little) != 42) return null;

        long ifdOffset = ReadU32(head, 4, little);
        long thumbOffset = -1, thumbLength = -1;

        for (int depth = 0; depth < 8 && ifdOffset > 0 && ifdOffset + 2 <= fileLen; depth++)
        {
            ct.ThrowIfCancellationRequested();
            fs.Seek(ifdOffset, SeekOrigin.Begin);
            var countBuf = new byte[2];
            if (fs.Read(countBuf, 0, 2) != 2) break;
            int entries = little ? countBuf[0] | countBuf[1] << 8 : countBuf[0] << 8 | countBuf[1];
            if (entries <= 0 || entries > 4096) break;

            long entriesStart = ifdOffset + 2;
            var entry = new byte[12];
            for (int i = 0; i < entries; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (entriesStart + i * 12 + 12 > fileLen) return null;
                fs.Seek(entriesStart + i * 12, SeekOrigin.Begin);
                if (fs.Read(entry, 0, 12) != 12) return null;

                int tag = ReadU16(entry, 0, little);
                if (tag == 0x0201) thumbOffset = ReadU32(entry, 8, little);
                else if (tag == 0x0202) thumbLength = ReadU32(entry, 8, little);
            }

            if (thumbOffset > 0 && thumbLength > MinPreviewBytes &&
                thumbOffset + thumbLength <= fileLen && thumbLength <= 64 * 1024 * 1024)
            {
                var data = new byte[thumbLength];
                fs.Seek(thumbOffset, SeekOrigin.Begin);
                int got = 0;
                while (got < data.Length)
                {
                    int n = fs.Read(data, got, data.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == data.Length && IsValidJpeg(TrimToEoi(data))) return TrimToEoi(data);
            }

            // IFD 链的下一个 IFD（IFD1 通常就是缩略图 IFD）
            long nextPos = entriesStart + (long)entries * 12;
            if (nextPos + 4 > fileLen) break;
            var nextBuf = new byte[4];
            fs.Seek(nextPos, SeekOrigin.Begin);
            if (fs.Read(nextBuf, 0, 4) != 4) break;
            long next = ReadU32(nextBuf, 0, little);
            if (next <= ifdOffset) break;
            ifdOffset = next;
        }
        return null;
    }

    /// <summary>流式扫描所有 SOI..EOI 候选段，按大小降序做结构校验，返回最大的合法 JPEG。</summary>
    private static byte[]? ScanForJpeg(FileStream fs, long fileLen, long scanLimit, CancellationToken ct)
    {
        var spans = new List<(long Start, long Length)>(16);
        var buffer = new byte[ScanChunk + 2];
        int carry = 0;
        long consumed = 0;
        long candidateStart = -1;

        while (consumed < scanLimit)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(ScanChunk, scanLimit - consumed);
            int read = fs.Read(buffer, carry, want);
            if (read <= 0) break;
            int len = carry + read;
            long baseOffset = consumed - carry;

            for (int i = 0; i + 1 < len; i++)
            {
                if (buffer[i] != 0xFF) continue;
                byte next = buffer[i + 1];
                if (next == 0xD8)
                {
                    if (candidateStart < 0) candidateStart = baseOffset + i;
                    i++;
                    continue;
                }
                if (next == 0xD9 && candidateStart >= 0)
                {
                    spans.Add((candidateStart, baseOffset + i + 2 - candidateStart));
                    candidateStart = -1;
                    i++;
                }
            }

            if (len >= 2)
            {
                buffer[0] = buffer[len - 2];
                buffer[1] = buffer[len - 1];
                carry = 2;
            }
            consumed += read;
        }
        if (candidateStart >= 0) spans.Add((candidateStart, fileLen - candidateStart));   // 文件尾截断

        foreach (var span in spans.OrderByDescending(s => s.Length).Take(48))
        {
            ct.ThrowIfCancellationRequested();
            if (span.Length <= MinPreviewBytes || span.Length > 64 * 1024 * 1024) continue;
            var data = new byte[span.Length];
            fs.Seek(span.Start, SeekOrigin.Begin);
            int got = 0;
            while (got < data.Length)
            {
                int n = fs.Read(data, got, data.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == data.Length && IsValidJpeg(TrimToEoi(data))) return TrimToEoi(data);
        }
        return null;
    }

    /// <summary>
    /// 结构校验：从 SOI 起逐个 marker 走段长度，必须能走到 SOS，且以 EOI 结尾。
    /// 这样能排除 RAW 数据里偶然出现的 0xFFD8…0xFFD9 假阳性。
    /// </summary>
    public static bool IsValidJpeg(byte[] d)
    {
        if (d.Length < 512 || d[0] != 0xFF || d[1] != 0xD8) return false;
        if (d[^2] != 0xFF || d[^1] != 0xD9) return false;

        int i = 2;
        bool sawSos = false;
        while (i + 1 < d.Length)
        {
            if (d[i] != 0xFF) return false;
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }               // 填充字节
            if (marker == 0xD9) return sawSos && i + 2 >= d.Length - 1;
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }  // 无长度段的 marker
            if (i + 4 > d.Length) return false;
            int segLen = d[i + 2] << 8 | d[i + 3];
            if (segLen < 2 || i + 2 + segLen > d.Length) return false;
            if (marker == 0xDA) { sawSos = true; break; }
            i += 2 + segLen;
        }
        if (!sawSos) return false;
        // 熵编码数据直接扫到最后一个 EOI（前面已确认末尾是 FFD9）
        return true;
    }

    // ================================================================ 音频封面

    public static byte[]? TryGetAudioCover(string fullPath, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => FromId3v2(fullPath, ct),
                ".flac" => FromFlac(fullPath, ct),
                ".m4a" or ".mp4" or ".m4v" or ".aac" or ".alac" => FromMp4Atom(fullPath, ct),
                ".ogg" or ".oga" or ".opus" => FromOggPicture(fullPath, ct),
                ".wav" or ".aif" or ".aiff" or ".wma" or ".dsf" or ".dff" or ".caf" => null,
                _ => FromId3v2(fullPath, ct) ?? FromFlac(fullPath, ct) ?? FromMp4Atom(fullPath, ct),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>ID3v2.3/2.4 的 APIC 帧（也支持 v2.2 的 PIC）。</summary>
    private static byte[]? FromId3v2(string fullPath, CancellationToken ct)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[10];
        if (fs.Read(head, 0, 10) != 10) return null;
        if (head[0] != 'I' || head[1] != 'D' || head[2] != '3') return null;

        int ver = head[3];
        int flags = head[5];
        int tagSize = SyncSafe(head, 6, 4);
        long tagEnd = 10 + tagSize;
        if (tagSize <= 0 || tagSize > 64 * 1024 * 1024) return null;

        long pos = 10;
        if ((flags & 0x40) != 0 && ver >= 3)
        {
            var ext = new byte[4];
            fs.Seek(pos, SeekOrigin.Begin);
            if (fs.Read(ext, 0, 4) != 4) return null;
            pos += 4 + SyncSafe(ext, 0, 4);
        }

        byte[]? best = null;
        int guard = 0;
        while (pos + 10 <= tagEnd && guard++ < 4096)
        {
            ct.ThrowIfCancellationRequested();
            fs.Seek(pos, SeekOrigin.Begin);
            var fh = new byte[10];
            if (fs.Read(fh, 0, 10) != 10) break;
            if (fh[0] == 0) break;                                  // 填充开始

            string id = ver == 2 ? Encoding.ASCII.GetString(fh, 0, 3) : Encoding.ASCII.GetString(fh, 0, 4);
            int frameSize = ver switch
            {
                2 => (fh[3] << 16) | (fh[4] << 8) | fh[5],
                4 => SyncSafe(fh, 4, 4),
                _ => BinaryPrimitives.ReadInt32BigEndian(fh.AsSpan(4, 4)),
            };
            long headerLen = ver == 2 ? 6 : 10;
            if (frameSize <= 0 || pos + headerLen + frameSize > tagEnd) break;

            if (id is "APIC" or "PIC")
            {
                var body = new byte[frameSize];
                fs.Seek(pos + headerLen, SeekOrigin.Begin);
                int got = 0;
                while (got < frameSize)
                {
                    int n = fs.Read(body, got, frameSize - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == frameSize)
                {
                    var data = ParseApic(body, ver == 2);
                    if (data is { Length: > 512 } && (best is null || data.Length > best.Length)) best = data;
                }
            }
            pos += headerLen + frameSize;
        }
        return best;
    }

    private static byte[]? ParseApic(byte[] body, bool v22)
    {
        int i = 0;
        if (body.Length < 4) return null;
        byte encoding = body[i++];

        if (v22)
        {
            // 3 字节图片格式（如 "JPG"）
            if (body.Length < i + 3) return null;
            i += 3;
        }
        else
        {
            // 以 0 结尾的 MIME 字符串（Latin-1）
            int mimeEnd = Array.IndexOf(body, (byte)0, i);
            if (mimeEnd < 0) return null;
            i = mimeEnd + 1;
        }

        if (i >= body.Length) return null;
        i++; // picture type

        // 描述：encoding 1/2 为 UTF-16（2 字节对齐），其余为单字节
        if (encoding is 1 or 2)
        {
            while (i + 1 < body.Length)
            {
                if (body[i] == 0 && body[i + 1] == 0) { i += 2; break; }
                i += 2;
            }
        }
        else
        {
            int descEnd = Array.IndexOf(body, (byte)0, i);
            if (descEnd < 0) return null;
            i = descEnd + 1;
        }

        if (i >= body.Length) return null;
        var data = new byte[body.Length - i];
        Array.Copy(body, i, data, 0, data.Length);
        return LooksLikeImage(data) ? data : null;
    }

    /// <summary>FLAC 的 PICTURE 元数据块。</summary>
    private static byte[]? FromFlac(string fullPath, CancellationToken ct)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var magic = new byte[4];
        if (fs.Read(magic, 0, 4) != 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return null;

        byte[]? best = null;
        int guard = 0;
        while (guard++ < 256)
        {
            ct.ThrowIfCancellationRequested();
            var h = new byte[4];
            if (fs.Read(h, 0, 4) != 4) break;
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            if (len < 0 || len > 64 * 1024 * 1024) break;

            if (type == 6 && len > 32)
            {
                var body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = fs.Read(body, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == len)
                {
                    var data = ParseFlacPicture(body);
                    if (data is { Length: > 512 } && (best is null || data.Length > best.Length)) best = data;
                }
            }
            else
            {
                fs.Seek(len, SeekOrigin.Current);
            }
            if (last) break;
        }
        return best;
    }

    private static byte[]? ParseFlacPicture(byte[] body)
    {
        int i = 0;
        if (body.Length < 32) return null;
        i += 4;                                                  // picture type
        if (i + 4 > body.Length) return null;
        int mimeLen = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i)); i += 4;
        if (mimeLen < 0 || i + mimeLen > body.Length) return null;
        i += mimeLen;
        if (i + 4 > body.Length) return null;
        int descLen = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i)); i += 4;
        if (descLen < 0 || i + descLen > body.Length) return null;
        i += descLen;
        i += 16;                                                 // width/height/depth/colors
        if (i + 4 > body.Length) return null;
        int dataLen = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(i)); i += 4;
        if (dataLen <= 0 || i + dataLen > body.Length) return null;
        var data = new byte[dataLen];
        Array.Copy(body, i, data, 0, dataLen);
        return LooksLikeImage(data) ? data : null;
    }

    /// <summary>MP4/M4A 的 covr atom（可能出现在文件头或文件尾的 moov 里）。</summary>
    private static byte[]? FromMp4Atom(string fullPath, CancellationToken ct)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = fs.Length;
        const int window = 8 * 1024 * 1024;
        var regions = new List<(long start, int size)>();
        if (len <= window * 2)
        {
            regions.Add((0, (int)len));
        }
        else
        {
            regions.Add((0, window));
            regions.Add((len - window, window));
        }

        byte[]? best = null;
        foreach (var (start, size) in regions)
        {
            ct.ThrowIfCancellationRequested();
            var buf = new byte[size];
            fs.Seek(start, SeekOrigin.Begin);
            int got = 0;
            while (got < size)
            {
                int n = fs.Read(buf, got, size - got);
                if (n <= 0) break;
                got += n;
            }
            var data = ScanCovr(buf, got);
            if (data is { Length: > 512 } && (best is null || data.Length > best.Length)) best = data;
        }
        return best;
    }

    private static byte[]? ScanCovr(byte[] buf, int len)
    {
        byte[]? best = null;
        for (int i = 0; i + 8 <= len; i++)
        {
            if (buf[i] != 'c' || buf[i + 1] != 'o' || buf[i + 2] != 'v' || buf[i + 3] != 'r') continue;
            // covr 之后紧跟一个 'data' 子 box：size(4) 'data'(4) version+flags(4) payload
            int p = i + 4;
            for (int k = 0; k < 3 && p + 16 <= len; k++)
            {
                if (buf[p + 4] == 'd' && buf[p + 5] == 'a' && buf[p + 6] == 't' && buf[p + 7] == 'a')
                {
                    int boxSize = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(p));
                    // 标准 iTunes covr 的 data 头是 16 字节（version+flags(4) + locale(4)），
                    // 但有些封装只写 12 或 8 字节；三个偏移都试，认图片魔数的那个。
                    foreach (int headerBytes in new[] { 16, 12, 8 })
                    {
                        int payloadStart = p + headerBytes;
                        int payloadLen = boxSize - headerBytes;
                        if (payloadLen <= 512 || payloadStart + payloadLen > len) continue;
                        var data = new byte[payloadLen];
                        Array.Copy(buf, payloadStart, data, 0, payloadLen);
                        if (LooksLikeImage(data) && (best is null || data.Length > best.Length)) best = data;
                    }
                    break;
                }
                int inner = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(p));
                if (inner <= 8) break;
                p += inner;
            }
        }
        return best;
    }

    /// <summary>Ogg Vorbis/Opus 注释里的 METADATA_BLOCK_PICTURE（base64 的 FLAC PICTURE 块）。</summary>
    private static byte[]? FromOggPicture(string fullPath, CancellationToken ct)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int max = 2 * 1024 * 1024;
        var buf = new byte[(int)Math.Min(max, fs.Length)];
        int got = 0;
        while (got < buf.Length)
        {
            int n = fs.Read(buf, got, buf.Length - got);
            if (n <= 0) break;
            got += n;
        }
        ct.ThrowIfCancellationRequested();
        var needle = Encoding.ASCII.GetBytes("METADATA_BLOCK_PICTURE=");
        int idx = IndexOf(buf, got, needle);
        if (idx < 0) return null;

        int p = idx + needle.Length;
        int start = p;
        while (p < got && IsBase64Char(buf[p])) p++;
        if (p - start < 64) return null;
        try
        {
            var raw = Convert.FromBase64String(Encoding.ASCII.GetString(buf, start, p - start));
            return ParseFlacPicture(raw);
        }
        catch { return null; }
    }

    // ================================================================ 工具

    private static bool LooksLikeImage(byte[] d)
    {
        if (d.Length < 4) return false;
        if (d[0] == 0xFF && d[1] == 0xD8) return true;                                  // JPEG
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return true;      // PNG
        if (d[0] == 'B' && d[1] == 'M') return true;                                    // BMP
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return true;                     // GIF
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return true;
        if (d.Length >= 12 && d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p') return true;
        return false;
    }

    private static int SyncSafe(byte[] b, int offset, int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++) v = (v << 7) | (b[offset + i] & 0x7F);
        return v;
    }

    private static int ReadU16(byte[] b, int offset, bool little) =>
        little ? b[offset] | b[offset + 1] << 8 : b[offset] << 8 | b[offset + 1];

    private static long ReadU32(byte[] b, int offset, bool little)
    {
        if (little)
            return (uint)(b[offset] | b[offset + 1] << 8 | b[offset + 2] << 16 | b[offset + 3] << 24);
        return (uint)(b[offset] << 24 | b[offset + 1] << 16 | b[offset + 2] << 8 | b[offset + 3]);
    }

    /// <summary>去掉 EOI 之后的填充尾巴（部分 RAW 的 IFD 长度包含对齐填充）。</summary>
    private static byte[] TrimToEoi(byte[] data)
    {
        if (data.Length >= 2 && data[^2] == 0xFF && data[^1] == 0xD9) return data;
        int limit = Math.Max(0, data.Length - 256);
        for (int i = data.Length - 2; i >= limit; i--)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                var trimmed = new byte[i + 2];
                Array.Copy(data, trimmed, i + 2);
                return trimmed;
            }
        }
        return data;
    }

    private static int IndexOf(byte[] haystack, int len, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= len; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static bool IsBase64Char(byte b) =>
        (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '+' || b == '/' || b == '=';
}