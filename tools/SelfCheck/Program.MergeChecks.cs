using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 合并组：把多重曝光/星轨/演示视频这类"一次拍一串"的素材合成一个大文件夹。
    /// 校验：默认封面=第一张、自定义封面、成员增删、筛选、分面、解散清理。
    /// </summary>
    private static async Task CheckMergeGroups(Services s, string tempData)
    {
        var dir = Path.Combine(tempData, "merge-groups");
        Directory.CreateDirectory(dir);

        var assets = new List<MediaAsset>();
        for (int i = 0; i < 3; i++)
        {
            var path = Path.Combine(dir, $"star{i}.jpg");
            File.WriteAllBytes(path, new byte[16]);
            assets.Add(NewAsset($"mg-{i}", "mg-lib", path, $"star{i}.jpg"));
        }
        await s.Store.UpsertManyAsync(assets);

        var group = await s.Store.CreateMergeGroupAsync("星轨 1109", new[] { "mg-0", "mg-1", "mg-2" });
        Check("合并组：创建并默认封面=第一张",
              group.AssetCount == 3 && group.CoverAssetId == "mg-0",
              $"{group.AssetCount} 张 / 封面 {group.CoverAssetId}");

        var reloaded = (await s.Store.GetMergeGroupsAsync()).FirstOrDefault(g => g.Id == group.Id);
        Check("合并组：列表带数量与封面",
              reloaded is not null && reloaded.AssetCount == 3 && reloaded.CoverAssetId == "mg-0");

        var membership = await s.Store.GetMergeMembershipAsync(new[] { "mg-0", "mg-1", "mg-2", "other" });
        Check("合并组：成员反查", membership.Count == 3 && membership["mg-1"] == group.Id);

        await s.Store.SetMergeGroupCoverAsync(group.Id, "mg-1");
        var coverChanged = (await s.Store.GetMergeGroupsAsync()).First(g => g.Id == group.Id);
        Check("合并组：自定义封面", coverChanged.CoverAssetId == "mg-1", coverChanged.CoverAssetId ?? "(空)");

        // 非成员不能当封面
        await s.Store.SetMergeGroupCoverAsync(group.Id, "other");
        var coverStill = (await s.Store.GetMergeGroupsAsync()).First(g => g.Id == group.Id);
        Check("合并组：非成员不能设为封面", coverStill.CoverAssetId == "mg-1", coverStill.CoverAssetId ?? "(空)");

        // 恢复默认封面 = 组内第一张
        await s.Store.SetMergeGroupCoverAsync(group.Id, null);
        var coverDefault = (await s.Store.GetMergeGroupsAsync()).First(g => g.Id == group.Id);
        Check("合并组：恢复默认封面", coverDefault.CoverAssetId == "mg-0", coverDefault.CoverAssetId ?? "(空)");

        var inGroup = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "mg-lib",
            MergeGroups = { group.Id },
            Limit = 0,
        });
        Check("合并组：按组合并筛选", inGroup.Count == 3, $"{inGroup.Count} 条");

        var facets = await s.Store.GetFacetsAsync(new AssetQuery { LibraryId = "mg-lib", Limit = 0 });
        var facetCount = facets.ByMergeGroup.TryGetValue(group.Id, out var fc) ? fc : -1;
        Check("合并组：分面计数", facetCount == 3, $"{facetCount}");

        var removed = await s.Store.RemoveFromMergeGroupAsync(group.Id, new[] { "mg-2" });
        var afterRemove = await s.Store.QueryAsync(new AssetQuery { LibraryId = "mg-lib", MergeGroups = { group.Id }, Limit = 0 });
        Check("合并组：移出成员", removed == 1 && afterRemove.Count == 2, $"移除 {removed}，剩 {afterRemove.Count}");

        var added = await s.Store.AddToMergeGroupAsync(group.Id, new[] { "mg-2" });
        var afterAdd = await s.Store.QueryAsync(new AssetQuery { LibraryId = "mg-lib", MergeGroups = { group.Id }, Limit = 0 });
        Check("合并组：再加回成员", added == 1 && afterAdd.Count == 3, $"新增 {added}，共 {afterAdd.Count}");

        await s.Store.DeleteMergeGroupAsync(group.Id);
        var afterDelete = await s.Store.GetMergeGroupsAsync();
        var memberAfterDelete = await s.Store.GetMergeMembershipAsync(new[] { "mg-0", "mg-1", "mg-2" });
        Check("合并组：解散后组与成员关系都清掉",
              afterDelete.All(g => g.Id != group.Id) && memberAfterDelete.Count == 0,
              $"剩 {afterDelete.Count} 组 / {memberAfterDelete.Count} 条关系");
    }
}