using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Media;

/// <summary>一次缓存清理的结果。</summary>
public sealed record CacheCleanResult(
    long BytesBefore,
    long BytesAfter,
    int FilesDeletedByAge,
    int FilesDeletedBySize,
    bool Skipped)
{
    public long BytesFreed => Math.Max(0, BytesBefore - BytesAfter);
    public int FilesDeleted => FilesDeletedByAge + FilesDeletedBySize;

    public string Summary => Skipped
        ? "缓存未达清理条件"
        : $"清理了 {FilesDeleted} 个文件（按时间 {FilesDeletedByAge} / 按体积 {FilesDeletedBySize}），" +
          $"释放 {FormatSize(BytesFreed)}（现在 {FormatSize(BytesAfter)}）";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => bytes + " B",
    };
}

/// <summary>
/// 缩略图缓存清理器：
/// 1) 先按时间删（超过最大保留天数的文件）；
/// 2) 再看总体积，超过上限时从最旧的文件开始删，直到降到上限以下。
/// 只处理缓存目录里的文件，不动任何原始素材。
/// </summary>
public static class ThumbnailCacheCleaner
{
    /// <summary>兼容旧接口：只要体积上限。</summary>
    public static Task<CacheCleanResult> CleanAsync(string directory, long maxBytes, CancellationToken ct = default)
        => CleanAsync(directory, maxBytes, maxAgeDays: 0, ct);

    /// <param name="maxBytes">体积上限（字节）；&lt;= 0 表示不限。</param>
    /// <param name="maxAgeDays">保留天数；&lt;= 0 表示不限。</param>
    public static async Task<CacheCleanResult> CleanAsync(
        string directory,
        long maxBytes,
        int maxAgeDays,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new CacheCleanResult(0, 0, 0, 0, Skipped: true);

        var files = await Task.Run(() => EnumerateFiles(directory), ct).ConfigureAwait(false);
        long before = files.Sum(f => f.Size);
        if (files.Count == 0)
            return new CacheCleanResult(0, 0, 0, 0, Skipped: true);

        int deletedByAge = 0, deletedBySize = 0;
        long nowBytes = before;

        // 1) 按时间
        if (maxAgeDays > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-maxAgeDays);
            foreach (var file in files.Where(f => f.LastWriteTime < cutoff).OrderBy(f => f.LastWriteTime))
            {
                ct.ThrowIfCancellationRequested();
                if (TryDelete(file.Path))
                {
                    deletedByAge++;
                    nowBytes -= file.Size;
                }
            }
        }

        // 2) 按体积（从最旧开始）
        if (maxBytes > 0 && nowBytes > maxBytes)
        {
            var remaining = files
                .Where(f => File.Exists(f.Path))
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            foreach (var file in remaining)
            {
                if (nowBytes <= maxBytes) break;
                ct.ThrowIfCancellationRequested();
                if (TryDelete(file.Path))
                {
                    deletedBySize++;
                    nowBytes -= file.Size;
                }
            }
        }

        // 顺手移除空目录（只删空的子目录，不删根）
        TryRemoveEmptyDirectories(directory);

        long after = await Task.Run(() => EnumerateFiles(directory).Sum(f => f.Size), ct).ConfigureAwait(false);
        var result = new CacheCleanResult(before, after, deletedByAge, deletedBySize, Skipped: false);
        Log.Info($"[cache] {result.Summary}");
        return result;
    }

    /// <summary>缓存目录当前占用字节数。</summary>
    public static Task<long> MeasureAsync(string directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Task.FromResult(0L);
        return Task.Run(() => EnumerateFiles(directory).Sum(f => f.Size), ct);
    }

    private static List<(string Path, long Size, DateTimeOffset LastWriteTime)> EnumerateFiles(string directory)
    {
        var list = new List<(string, long, DateTimeOffset)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    list.Add((path, info.Length, info.LastWriteTime));
                }
                catch
                {
                    // 文件刚好被删/被占用，跳过
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举缓存目录失败: {directory}", ex);
        }
        return list;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }
}