using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StjMediaViewer.Core.Metadata;

/// <summary>一个 TIFF 标签条目。</summary>
public sealed class TiffEntry
{
    public int Tag { get; init; }
    public ushort Type { get; init; }
    public long Count { get; init; }
    /// <summary>标签数据（内联或从文件读出的原始字节）。太大而放弃加载时为 null。</summary>
    public byte[]? Data { get; init; }
    public long DataOffset { get; init; }
}

/// <summary>一个 TIFF IFD（可能来自 IFD0 链、SubIFD、ExifIFD、GPS IFD）。</summary>
public sealed class TiffIfd
{
    public long Offset { get; init; }
    public List<TiffEntry> Entries { get; } = new();
    public TiffIfd? Next { get; internal set; }
    public List<TiffIfd> Children { get; } = new();

    public bool Has(int tag) => Entries.Any(e => e.Tag == tag);

    public TiffEntry? Find(int tag) => Entries.FirstOrDefault(e => e.Tag == tag);

    public string? GetAscii(int tag)
    {
        var e = Find(tag);
        if (e?.Data is null || e.Data.Length == 0) return null;
        var s = e.Type == 2
            ? Encoding.ASCII.GetString(e.Data).TrimEnd('\0')
            : Encoding.UTF8.GetString(e.Data).TrimEnd('\0');
        return MetaUtil.CleanText(s, 2048);
    }

    public long? GetLong(int tag)
    {
        var e = Find(tag);
        if (e?.Data is null) return null;
        var d = e.Data;
        try
        {
            return e.Type switch
            {
                3 => d.Length >= 2 ? (IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d) : BinaryPrimitives.ReadUInt16LittleEndian(d)) : null,
                4 => d.Length >= 4 ? (IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d)) : null,
                13 => d.Length >= 4 ? (IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d)) : null,
                8 => d.Length >= 2 ? (IsBigEndian ? BinaryPrimitives.ReadInt16BigEndian(d) : BinaryPrimitives.ReadInt16LittleEndian(d)) : null,
                9 => d.Length >= 4 ? (IsBigEndian ? BinaryPrimitives.ReadInt32BigEndian(d) : BinaryPrimitives.ReadInt32LittleEndian(d)) : null,
                _ => null,
            };
        }
        catch { return null; }
    }

    public int? GetInt(int tag) => GetLong(tag) is { } v && v is >= int.MinValue and <= int.MaxValue ? (int)v : null;

    public double? GetDouble(int tag)
    {
        var e = Find(tag);
        if (e?.Data is null) return null;
        var d = e.Data;
        try
        {
            switch (e.Type)
            {
                case 5 when d.Length >= 8:
                {
                    var n = IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d);
                    var den = IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
                    return den == 0 ? null : (double)n / den;
                }
                case 10 when d.Length >= 8:
                {
                    var n = IsBigEndian ? BinaryPrimitives.ReadInt32BigEndian(d) : BinaryPrimitives.ReadInt32LittleEndian(d);
                    var den = IsBigEndian ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)) : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
                    return den == 0 ? null : (double)n / den;
                }
                case 11 when d.Length >= 4:
                    return IsBigEndian ? BinaryPrimitives.ReadSingleBigEndian(d) : BinaryPrimitives.ReadSingleLittleEndian(d);
                case 12 when d.Length >= 8:
                    return IsBigEndian ? BinaryPrimitives.ReadDoubleBigEndian(d) : BinaryPrimitives.ReadDoubleLittleEndian(d);
                default:
                    return GetLong(tag);
            }
        }
        catch { return null; }
    }

    public int[] GetInts(int tag)
    {
        var e = Find(tag);
        if (e?.Data is null) return Array.Empty<int>();
        var result = new List<int>();
        var d = e.Data;
        var size = e.Type switch { 1 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 13 => 4, _ => 0 };
        if (size == 0 || d.Length < size) return Array.Empty<int>();
        for (var i = 0; i + size <= d.Length; i += size)
        {
            result.Add(size switch
            {
                1 => d[i],
                2 => IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i)),
                4 => (int)(IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i))),
                _ => 0,
            });
        }
        return result.ToArray();
    }

    public bool IsBigEndian { get; internal set; }
}

/// <summary>解析后的 TIFF 结构（RAW / DNG / CR2 / CMT 内嵌 TIFF 共用）。</summary>
public sealed class TiffFile
{
    public bool LittleEndian { get; init; }
    public long BaseOffset { get; init; }
    public List<TiffIfd> Ifds { get; } = new();
    /// <summary>全部 IFD（含 SubIFD / ExifIFD / GPS IFD，深度优先展开）。</summary>
    public IEnumerable<TiffIfd> AllIfds()
    {
        var stack = new Stack<TiffIfd>(Ifds.AsEnumerable().Reverse());
        var seen = new HashSet<long>();
        while (stack.Count > 0)
        {
            var ifd = stack.Pop();
            if (!seen.Add(ifd.Offset)) continue;
            yield return ifd;
            foreach (var child in ifd.Children.AsEnumerable().Reverse()) stack.Push(child);
            if (ifd.Next is not null) stack.Push(ifd.Next);
        }
    }

    public string? Error { get; internal set; }
}

/// <summary>
/// 极简但完整的基线 TIFF 读取器：只解析 IFD 结构与常用标签值，用于
/// 1) RAW 内嵌预览偏移（JPEGInterchangeFormat 513 / Length 514，含 SubIFD）
/// 2) RAW 全尺寸宽高（SubIFD 的 ImageWidth/ImageLength）
/// 3) CR3 的 CMT 块、以及 MetadataExtractor 解析失败时的 EXIF 兜底。
/// 不解析像素数据、不做解码；大数组（如 StripOffsets）只读取有界大小。
/// </summary>
public static class TiffParser
{
    private const int MaxIfdCount = 32;
    private const int MaxEntriesPerIfd = 4096;
    private const long MaxLoadBytes = 1024 * 1024;  // 单个标签最多读 1MB 数据

    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4 };

    public static TiffFile? Parse(string path)
    {
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(path);
            return ParseAt(fs.SafeFileHandle, 0, fs.Length);
        }
        catch (Exception ex)
        {
            return new TiffFile { LittleEndian = true, Error = ex.Message };
        }
    }

    /// <summary>从文件绝对偏移 <paramref name="baseOffset"/> 处解析一个 TIFF 结构（可嵌在其它容器里）。</summary>
    public static TiffFile? ParseAt(SafeFileHandle handle, long baseOffset, long length)
    {
        if (handle.IsInvalid || length < 8) return null;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (!ReadExact(handle, baseOffset, header)) return null;

            bool le;
            if (header[0] == (byte)'I' && header[1] == (byte)'I') le = true;
            else if (header[0] == (byte)'M' && header[1] == (byte)'M') le = false;
            else return null;

            var magic = le ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            if (magic == 43) return new TiffFile { LittleEndian = le, BaseOffset = baseOffset, Error = "BigTIFF 不支持" };
            if (magic != 42) return null;

            var ifd0 = le ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var file = new TiffFile { LittleEndian = le, BaseOffset = baseOffset };
            if (ifd0 == 0 || ifd0 >= length) return file;

            var visited = new HashSet<long>();
            var root = ParseIfd(handle, baseOffset, length, ifd0, le, visited, depth: 0);
            if (root is null) return file;

            file.Ifds.Add(root);
            // IFD0 -> next 链
            var cursor = root;
            var guard = 0;
            while (cursor.Next is not null && guard++ < MaxIfdCount)
            {
                var claimed = cursor.Next;
                var next = ParseIfd(handle, baseOffset, length, claimed.Offset, le, visited, depth: 0);
                if (next is null)
                {
                    cursor.Next = null;
                    break;
                }
                cursor.Next = next;
                file.Ifds.Add(next);
                cursor = next;
            }
            return file;
        }
        catch (Exception ex)
        {
            return new TiffFile { LittleEndian = true, BaseOffset = baseOffset, Error = ex.Message };
        }
    }

    private static TiffIfd? ParseIfd(SafeFileHandle handle, long baseOffset, long length, long relativeOffset,
        bool le, HashSet<long> visited, int depth)
    {
        if (depth > 4 || !visited.Add(relativeOffset)) return null;
        if (relativeOffset < 8 || relativeOffset + 2 > length) return null;

        Span<byte> countBuf = stackalloc byte[2];
        if (!ReadExact(handle, baseOffset + relativeOffset, countBuf)) return null;
        var count = le ? BinaryPrimitives.ReadUInt16LittleEndian(countBuf) : BinaryPrimitives.ReadUInt16BigEndian(countBuf);
        if (count == 0 || count > MaxEntriesPerIfd) return null;

        var entriesBytes = new byte[count * 12 + 4];
        if (!ReadExact(handle, baseOffset + relativeOffset + 2, entriesBytes)) return null;

        var ifd = new TiffIfd { Offset = relativeOffset, IsBigEndian = !le };
        for (var i = 0; i < count; i++)
        {
            var o = i * 12;
            var tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(entriesBytes.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(entriesBytes.AsSpan(o));
            var type = le ? BinaryPrimitives.ReadUInt16LittleEndian(entriesBytes.AsSpan(o + 2)) : BinaryPrimitives.ReadUInt16BigEndian(entriesBytes.AsSpan(o + 2));
            var n = le ? BinaryPrimitives.ReadUInt32LittleEndian(entriesBytes.AsSpan(o + 4)) : BinaryPrimitives.ReadUInt32BigEndian(entriesBytes.AsSpan(o + 4));
            var valueOrOffset = le ? BinaryPrimitives.ReadUInt32LittleEndian(entriesBytes.AsSpan(o + 8)) : BinaryPrimitives.ReadUInt32BigEndian(entriesBytes.AsSpan(o + 8));

            var typeSize = type < TypeSizes.Length ? TypeSizes[type] : 0;
            if (typeSize == 0 || n > int.MaxValue / Math.Max(1, typeSize))
            {
                ifd.Entries.Add(new TiffEntry { Tag = tag, Type = type, Count = n });
                continue;
            }

            var total = (long)n * typeSize;
            byte[]? data;
            long dataOffset;
            if (total <= 4)
            {
                data = entriesBytes.AsSpan(o + 8, typeSize).ToArray();
                dataOffset = relativeOffset + 2 + o + 8;
            }
            else
            {
                dataOffset = valueOrOffset;
                if (dataOffset < 0 || dataOffset + total > length || total > MaxLoadBytes)
                {
                    ifd.Entries.Add(new TiffEntry { Tag = tag, Type = type, Count = n, DataOffset = dataOffset });
                    continue;
                }
                data = new byte[total];
                if (!ReadExact(handle, baseOffset + dataOffset, data)) data = null;
            }

            ifd.Entries.Add(new TiffEntry { Tag = tag, Type = type, Count = n, Data = data, DataOffset = dataOffset });
        }

        var nextOffset = le
            ? BinaryPrimitives.ReadUInt32LittleEndian(entriesBytes.AsSpan(count * 12))
            : BinaryPrimitives.ReadUInt32BigEndian(entriesBytes.AsSpan(count * 12));
        if (nextOffset > 0 && depth == 0 && ifd.Next is null)
        {
            // 只记录指针，由上层决定是否继续解析，避免递归过深
            var placeholder = new TiffIfd { Offset = nextOffset, IsBigEndian = !le };
            ifd.Next = placeholder;
        }

        // 子 IFD：SubIFDs(330) / ExifIFD(34665) / GPS IFD(34853)
        foreach (var childTag in new[] { 330, 34665, 34853 })
        {
            var entry = ifd.Find(childTag);
            if (entry?.Data is null) continue;
            var offsets = childTag == 330 ? ReadOffsets(entry, le) : new[] { ReadOffset(entry, le) };
            foreach (var off in offsets)
            {
                if (off <= 0 || off >= length) continue;
                var child = ParseIfd(handle, baseOffset, length, off, le, visited, depth + 1);
                if (child is not null) ifd.Children.Add(child);
            }
        }

        return ifd;
    }

    private static long ReadOffset(TiffEntry e, bool le) => e.Data is { Length: >= 4 } d
        ? (le ? BinaryPrimitives.ReadUInt32LittleEndian(d) : BinaryPrimitives.ReadUInt32BigEndian(d))
        : 0;

    private static IEnumerable<long> ReadOffsets(TiffEntry e, bool le)
    {
        if (e.Data is not { Length: >= 4 } d) yield break;
        var step = e.Type switch { 3 => 2, 4 or 13 => 4, _ => 0 };
        if (step == 0) yield break;
        for (var i = 0; i + step <= d.Length; i += step)
        {
            yield return step switch
            {
                2 => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i)),
                _ => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i)),
            };
        }
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
}