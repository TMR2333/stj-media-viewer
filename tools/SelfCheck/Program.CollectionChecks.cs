using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 影像合集：自建合集、加/移素材、计数、筛选（多选并集 + "未加入合集"）、重命名、删除清理。
    /// </summary>
    private static async Task CheckCollections(Services s, string tempData)
    {
        var dir = Path.Combine(tempData, "collections");
        Directory.CreateDirectory(dir);

        var assets = new List<MediaAsset>();
        for (int i = 0; i < 4; i++)
        {
            var path = Path.Combine(dir, $"c{i}.jpg");
            File.WriteAllBytes(path, new byte[32]);
            assets.Add(NewAsset($"col-{i}", "col-lib", path, $"c{i}.jpg"));
        }
        await s.Store.UpsertManyAsync(assets);

        var album = await s.Store.CreateCollectionAsync("婚礼精选");
        var travel = await s.Store.CreateCollectionAsync("旅行");
        Check("合集：创建", album.Name == "婚礼精选" && travel.Id.Length > 0, $"{album.Name}/{travel.Name}");

        var added = await s.Store.AddToCollectionAsync(album.Id, new[] { "col-0", "col-1", "col-2" });
        var addedAgain = await s.Store.AddToCollectionAsync(album.Id, new[] { "col-0" });
        await s.Store.AddToCollectionAsync(travel.Id, new[] { "col-2", "col-3" });
        Check("合集：加入素材（重复加入不重复计数）", added == 3 && addedAgain == 0,
              $"新增 {added}，重复加入 {addedAgain}");

        var list = await s.Store.GetCollectionsAsync();
        var albumLoaded = list.FirstOrDefault(c => c.Id == album.Id);
        Check("合集：列表带素材数", albumLoaded?.AssetCount == 3, $"{albumLoaded?.AssetCount} 张");

        // 筛选：单选
        var inAlbum = await s.Store.QueryAsync(new AssetQuery { LibraryId = "col-lib", Collections = { album.Id }, Limit = 0 });
        Check("合集：按单个合集筛选", inAlbum.Count == 3, $"{inAlbum.Count} 条");

        // 多选 = 并集
        var union = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "col-lib",
            Collections = { album.Id, travel.Id },
            Limit = 0,
        });
        Check("合集：多选筛选是并集", union.Count == 4, $"{union.Count} 条");

        // 未加入合集
        var unsetAssets = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "col-lib",
            Collections = { "__unset__" },
            Limit = 0,
        });
        Check("合集：未加入合集筛选", unsetAssets.Count == 0, $"{unsetAssets.Count} 条");

        // 分面
        var facets = await s.Store.GetFacetsAsync(new AssetQuery { LibraryId = "col-lib", Limit = 0 });
        var albumCount = facets.ByCollection.TryGetValue(album.Id, out var a1) ? a1 : -1;
        var travelCount = facets.ByCollection.TryGetValue(travel.Id, out var t1) ? t1 : -1;
        Check("合集：分面计数",
              albumCount == 3 && travelCount == 2 && facets.CollectionUnsetCount == 0,
              $"album={albumCount} travel={travelCount} unset={facets.CollectionUnsetCount}");

        // 移出
        var removed = await s.Store.RemoveFromCollectionAsync(album.Id, new[] { "col-1", "col-2" });
        var afterRemove = await s.Store.QueryAsync(new AssetQuery { LibraryId = "col-lib", Collections = { album.Id }, Limit = 0 });
        Check("合集：移出素材", removed == 2 && afterRemove.Count == 1,
              $"移除 {removed}，剩余 {afterRemove.Count}");

        // 重命名
        await s.Store.RenameCollectionAsync(travel.Id, "旅行 2026");
        var renamed = (await s.Store.GetCollectionsAsync()).FirstOrDefault(c => c.Id == travel.Id);
        Check("合集：重命名", renamed?.Name == "旅行 2026", renamed?.Name ?? "(无)");

        // 删除：合集成员关系一起清掉，其他合集不受影响
        await s.Store.DeleteCollectionAsync(album.Id);
        var afterDelete = await s.Store.GetCollectionsAsync();
        var orphan = await s.Store.QueryAsync(new AssetQuery { LibraryId = "col-lib", Collections = { album.Id }, Limit = 0 });
        Check("合集：删除后列表与筛选都清空",
              afterDelete.All(c => c.Id != album.Id) && orphan.Count == 0,
              $"剩 {afterDelete.Count} 个合集");

        var unsetAfter = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "col-lib",
            Collections = { "__unset__" },
            Limit = 0,
        });
        Check("合集：删除合集后素材回到未加入", unsetAfter.Count == 2, $"{unsetAfter.Count} 条");
    }
}