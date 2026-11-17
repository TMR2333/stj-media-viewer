using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 删除废片：废片查询 + 移入系统回收站（文件从原路径消失，返回成功数量）。
    /// </summary>
    private static async Task CheckRecycleBinDelete(Services s, string tempData)
    {
        var dir = Path.Combine(tempData, "rejects");
        Directory.CreateDirectory(dir);

        var assets = new List<MediaAsset>();
        for (int i = 0; i < 2; i++)
        {
            var path = Path.Combine(dir, $"reject{i}.jpg");
            await File.WriteAllBytesAsync(path, new byte[128]);
            var asset = NewAsset($"rej-{i}", "rej-lib", path, $"reject{i}.jpg");
            asset.IsPick = false;
            assets.Add(asset);
        }
        var keep = Path.Combine(dir, "keep.jpg");
        await File.WriteAllBytesAsync(keep, new byte[64]);
        var keepAsset = NewAsset("rej-keep", "rej-lib", keep, "keep.jpg");
        keepAsset.IsPick = true;
        assets.Add(keepAsset);
        await s.Store.UpsertManyAsync(assets);

        // 未标记（IsPick=null）的素材不该被当成废片
        var unmarked = Path.Combine(dir, "unmarked.jpg");
        await File.WriteAllBytesAsync(unmarked, new byte[32]);
        var unmarkedAsset = NewAsset("rej-unmarked", "rej-lib", unmarked, "unmarked.jpg");
        await s.Store.UpsertManyAsync(new[] { unmarkedAsset });

        var rejects = await s.Store.QueryAsync(new AssetQuery { LibraryId = "rej-lib", OnlyRejected = true, Limit = 0 });
        Check("删除废片：查询只返回废片", rejects.Count == 2, $"{rejects.Count} 条");

        var notPicked = await s.Store.QueryAsync(new AssetQuery { LibraryId = "rej-lib", IsPick = false, Limit = 0 });
        Check("删除废片：未标记素材不算废片", notPicked.Count == 3, $"{notPicked.Count} 条");

        var victims = rejects.ToList();
        var deleted = await s.Operations.DeleteToRecycleBinAsync(victims);
        Check("删除废片：文件移入回收站（原路径消失）",
              deleted == 2 && victims.All(a => !File.Exists(a.FullPath)),
              $"删除 {deleted} / {victims.Count}");

        Check("删除废片：非废片文件不受影响", File.Exists(keep), keep);

        // 不存在的文件不应报成功（也不应抛异常）
        var ghost = NewAsset("rej-ghost", "rej-lib", Path.Combine(dir, "ghost.jpg"), "ghost.jpg");
        var ghostDeleted = await s.Operations.DeleteToRecycleBinAsync(new[] { ghost });
        Check("删除废片：不存在的文件跳过", ghostDeleted == 0, deleted.ToString());

        // 索引清理：UI 走的是"文件没了就删索引"，这里模拟同样路径
        var goneIds = victims.Where(a => !File.Exists(a.FullPath)).Select(a => a.Id).ToList();
        await s.Store.DeleteAsync(goneIds);
        var remaining = await s.Store.QueryAsync(new AssetQuery { LibraryId = "rej-lib", Limit = 0 });
        var keepIds = remaining.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Check("删除废片：索引记录已移除",
              keepIds.Contains("rej-keep") && keepIds.Contains("rej-unmarked") &&
              !keepIds.Contains("rej-0") && !keepIds.Contains("rej-1"),
              $"{remaining.Count} 条：{string.Join(",", keepIds)}");
    }
}