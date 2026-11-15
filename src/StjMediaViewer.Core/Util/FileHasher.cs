using System.Security.Cryptography;
using System.Text;

namespace StjMediaViewer.Core.Util;

/// <summary>文件指纹。快速指纹用于重复检测的粗筛，完整指纹用于精确判定。</summary>
public static class FileHasher
{
    private const int ChunkSize = 512 * 1024;

    /// <summary>快速指纹 = 大小 + 头尾各 512KB 的 FNV-1a 64。足够区分绝大多数不同文件。</summary>
    public static string QuickHash(string fullPath, CancellationToken ct = default)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        long len = fs.Length;
        var hasher = Fnv1a64.Create();
        hasher.Append(BitConverter.GetBytes(len));

        var head = new byte[Math.Min(ChunkSize, len)];
        ReadExactly(fs, head, ct);
        hasher.Append(head);

        if (len > ChunkSize * 2L)
        {
            fs.Seek(-ChunkSize, SeekOrigin.End);
            var tail = new byte[ChunkSize];
            ReadExactly(fs, tail, ct);
            hasher.Append(tail);
        }
        return "q1:" + hasher.Value.ToString("x16");
    }

    /// <summary>完整 SHA-256（大写十六进制）。</summary>
    public static string FullHash(string fullPath, CancellationToken ct = default)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>字符串稳定哈希（用于缓存键）。</summary>
    public static string StableHash(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static void ReadExactly(Stream s, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = s.Read(buffer, offset, buffer.Length - offset);
            if (n <= 0) break;
            offset += n;
        }
    }
}

/// <summary>极简 FNV-1a 64 位累加器。</summary>
public struct Fnv1a64
{
    private ulong _hash;

    public static Fnv1a64 Create() => new() { _hash = 14695981039346656037UL };

    public void Append(ReadOnlySpan<byte> data)
    {
        ulong h = _hash;
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        _hash = h;
    }

    public readonly ulong Value => _hash;
}