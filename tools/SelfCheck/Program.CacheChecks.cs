using StjMediaViewer.Core.Media;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>缓存清理：按保留天数删旧文件、按体积上限从最旧删到上限以下，且不误删新文件。</summary>
    private static async Task CheckCacheCleanup(Services s, string tempData)
    {
        var cacheDir = Path.Combine(tempData, "cache-clean", "thumbs");
        Directory.CreateDirectory(cacheDir);

        async Task<string> WriteFile(string name, int size, DateTimeOffset written)
        {
            var path = Path.Combine(cacheDir, name);
            await File.WriteAllBytesAsync(path, new byte[size]);
            File.SetLastWriteTime(path, written.LocalDateTime);
            return path;
        }

        var now = DateTimeOffset.Now;
        var old1 = await WriteFile("old-1.jpg", 100_000, now.AddDays(-120));
        var old2 = await WriteFile("old-2.jpg", 100_000, now.AddDays(-100));
        var mid = await WriteFile("mid.jpg", 100_000, now.AddDays(-10));
        var fresh = await WriteFile("fresh.jpg", 100_000, now);

        var before = await ThumbnailCacheCleaner.MeasureAsync(cacheDir);
        Check("缓存：目录体积统计", before == 400_000, $"{before} 字节");

        // 1) 只按天数清理（保留 90 天）：两个 100+ 天的被删，mid/fresh 保留
        var ageResult = await ThumbnailCacheCleaner.CleanAsync(cacheDir, maxBytes: 0, maxAgeDays: 90);
        Check("缓存：按天数删旧文件", ageResult.FilesDeletedByAge == 2,
              $"删了 {ageResult.FilesDeletedByAge} 个");
        Check("缓存：旧文件确实删除", !File.Exists(old1) && !File.Exists(old2));
        Check("缓存：新文件保留", File.Exists(mid) && File.Exists(fresh));

        // 2) 再有体积上限：目前 200KB，上限 120KB → 从最旧的开始删，只剩 fresh
        var sizeResult = await ThumbnailCacheCleaner.CleanAsync(cacheDir, maxBytes: 120_000, maxAgeDays: 0);
        Check("缓存：按体积清理", sizeResult.FilesDeletedBySize >= 1,
              $"删了 {sizeResult.FilesDeletedBySize} 个");
        var after = await ThumbnailCacheCleaner.MeasureAsync(cacheDir);
        Check("缓存：清理后不超过上限", after <= 120_000, $"{after} 字节");
        Check("缓存：最新的保留", File.Exists(fresh));

        // 3) 空目录 / 不存在目录不抛异常
        var emptyDir = Path.Combine(tempData, "cache-empty");
        Directory.CreateDirectory(emptyDir);
        var emptyResult = await ThumbnailCacheCleaner.CleanAsync(emptyDir, 1000, 30);
        Check("缓存：空目录安全", emptyResult.FilesDeleted == 0);
        var missingResult = await ThumbnailCacheCleaner.CleanAsync(Path.Combine(tempData, "nope"), 1000, 30);
        Check("缓存：不存在的目录安全", missingResult.Skipped);

        _ = s;
    }
}