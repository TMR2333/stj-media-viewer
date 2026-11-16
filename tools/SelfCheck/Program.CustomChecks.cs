using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>自定义类别：用户自建维度（项目/客户/场地），可写、可筛选、进分面、可删。</summary>
    private static async Task CheckCustomCategories(Services s)
    {
        var asset = (await s.Store.QueryAsync(new AssetQuery { Limit = 5 })).FirstOrDefault();
        Check("自定义类别：有可操作素材", asset is not null);
        if (asset is null) return;

        await s.Store.SetCustomFieldAsync(asset.Id, "项目", "山河");
        var fields = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("自定义类别：写入并读回", fields.TryGetValue("项目", out var project) && project == "山河",
              project);

        var dims = await s.Store.ListCustomDimensionsAsync();
        Check("自定义类别：维度列表", dims.Contains("项目"), string.Join("/", dims));

        var query = new AssetQuery { Limit = 0 };
        query.CustomFilters["项目"] = new List<string> { "山河" };
        var rows = await s.Store.QueryAsync(query);
        Check("自定义类别：筛选命中", rows.Count == 1 && rows[0].Id == asset.Id, $"{rows.Count} 条");

        var miss = new AssetQuery { Limit = 0 };
        miss.CustomFilters["项目"] = new List<string> { "不存在的值" };
        Check("自定义类别：筛选不匹配为空", (await s.Store.QueryAsync(miss)).Count == 0);

        await s.Store.ApplyBatchEditAsync(new[] { asset.Id }, new BatchEditPatch
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["客户"] = "某品牌" },
        });
        var after = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("批量填写：自定义类别", after.TryGetValue("客户", out var client) && client == "某品牌", client);

        var facets = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
        Check("分面：自定义类别",
              facets.ByCustom.TryGetValue("项目", out var projects) && projects.ContainsKey("山河"),
              string.Join("/", facets.ByCustom.Keys));

        await s.Store.SetCustomFieldAsync(asset.Id, "项目", null);
        var cleared = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("自定义类别：删除维度", !cleared.ContainsKey("项目"), string.Join("/", cleared.Keys));

        await s.Store.ApplyBatchEditAsync(new[] { asset.Id }, new BatchEditPatch
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["客户"] = "" },
        });
        Check("批量填写：空值清空自定义类别",
              !(await s.Store.GetCustomFieldsAsync(asset.Id)).ContainsKey("客户"));

        await s.Store.SetCustomFieldAsync(asset.Id, "客户", null);
    }
}
