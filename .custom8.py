import io, os

# 1) 注册 section
p = 'tools/SelfCheck/Program.cs'
s = io.open(p, encoding='utf-8').read()
if 'CheckCustomCategories' not in s:
    s = s.replace('            await Section("分类筛选与选项刷新", async () => await CheckFacetRefresh(s));',
                  '            await Section("分类筛选与选项刷新", async () => await CheckFacetRefresh(s));\n'
                  '            await Section("自定义类别", async () => await CheckCustomCategories(s));')
    io.open(p, 'w', encoding='utf-8').write(s)
    print('selfcheck: section')

# 2) 检查实现
p = 'tools/SelfCheck/Program.CustomChecks.cs'
if not os.path.exists(p):
    io.open(p, 'w', encoding='utf-8').write('''using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>自定义类别：用户自建维度（项目/客户/场地），可写、可筛选、进分面、可删。</summary>
    private static async Task CheckCustomCategories(Services s)
    {
        var asset = (await s.Store.QueryAsync(new AssetQuery { Limit = 5 })).FirstOrDefault();
        Check("自定义类别：有可操作素材", asset is not null);
        if (asset is null) return;

        // 单条写入 / 读取
        await s.Store.SetCustomFieldAsync(asset.Id, "项目", "山河");
        var fields = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("自定义类别：写入并读回", fields.TryGetValue("项目", out var project) && project == "山河",
              project);

        var dims = await s.Store.ListCustomDimensionsAsync();
        Check("自定义类别：维度列表", dims.Contains("项目"), string.Join("/", dims));

        // 按自定义类别筛选
        var query = new AssetQuery { Limit = 0 };
        query.CustomFilters["项目"] = new List<string> { "山河" };
        var rows = await s.Store.QueryAsync(query);
        Check("自定义类别：筛选命中", rows.Count == 1 && rows[0].Id == asset.Id, $"{rows.Count} 条");

        // 空值筛选不命中
        var miss = new AssetQuery { Limit = 0 };
        miss.CustomFilters["项目"] = new List<string> { "不存在的值" };
        Check("自定义类别：筛选不匹配为空", (await s.Store.QueryAsync(miss)).Count == 0);

        // 批量填写（只带自定义类别，没有其他字段）
        await s.Store.ApplyBatchEditAsync(new[] { asset.Id }, new BatchEditPatch
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["客户"] = "某品牌" },
        });
        var after = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("批量填写：自定义类别", after.TryGetValue("客户", out var client) && client == "某品牌", client);

        // 分面里出现维度与值
        var facets = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
        Check("分面：自定义类别",
              facets.ByCustom.TryGetValue("项目", out var projects) && projects.ContainsKey("山河"),
              string.Join("/", facets.ByCustom.Keys));

        // 删除某个维度
        await s.Store.SetCustomFieldAsync(asset.Id, "项目", null);
        var cleared = await s.Store.GetCustomFieldsAsync(asset.Id);
        Check("自定义类别：删除维度", !cleared.ContainsKey("项目"), string.Join("/", cleared.Keys));

        // 批量填写空值 = 清空该维度
        await s.Store.ApplyBatchEditAsync(new[] { asset.Id }, new BatchEditPatch
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["客户"] = "" },
        });
        Check("批量填写：空值清空自定义类别",
              !(await s.Store.GetCustomFieldsAsync(asset.Id)).ContainsKey("客户"));

        // 清理，避免影响后续检查
        await s.Store.SetCustomFieldAsync(asset.Id, "客户", null);
    }
}
''', 'utf-8')
    print('selfcheck: checks file')