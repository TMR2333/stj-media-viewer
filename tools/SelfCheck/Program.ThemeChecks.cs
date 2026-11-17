using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 多主题：一张素材可以同时属于多个主题；第一个是主主题；筛选/分面/批量写入都要认全部主题。
    /// </summary>
    private static async Task CheckMultiTheme(Services s, string tempData)
    {
        var dir = Path.Combine(tempData, "multi-theme");
        Directory.CreateDirectory(dir);

        var assets = new List<MediaAsset>();
        for (int i = 0; i < 3; i++)
        {
            var path = Path.Combine(dir, $"t{i}.jpg");
            File.WriteAllBytes(path, new byte[48]);
            var asset = NewAsset($"theme-{i}", "theme-lib", path, $"t{i}.jpg");
            if (i == 0) asset.SetThemes(new[] { "nature", "night" });
            if (i == 1) asset.SetThemes(new[] { "night" });
            if (i == 2) asset.Theme = null;   // 未分类
            assets.Add(asset);
        }
        await s.Store.UpsertManyAsync(assets);

        var first = await s.Store.GetAsync("theme-0");
        Check("多主题：写入并读回", first is not null && first.Themes.Count == 2 && first.Theme == "nature",
              first is null ? "(无)" : $"{first.Theme} / [{string.Join(",", first.Themes)}]");
        Check("多主题：主主题 = 列表第一个", first?.PrimaryTheme == "nature", first?.PrimaryTheme ?? "(无)");

        // 次要主题也能筛出来
        var night = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "theme-lib",
            Themes = { "night" },
            Limit = 0,
        });
        Check("多主题：按次要主题筛选", night.Count == 2, $"{night.Count} 条");

        var nature = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "theme-lib",
            Themes = { "nature" },
            Limit = 0,
        });
        Check("多主题：按主主题筛选", nature.Count == 1, $"{nature.Count} 条");

        // 分面计数把次要主题也算上
        var facets = await s.Store.GetFacetsAsync(new AssetQuery { LibraryId = "theme-lib", Limit = 0 });
        var nightCount = facets.ByTheme.TryGetValue("night", out var n1) ? n1 : 0;
        var natureCount = facets.ByTheme.TryGetValue("nature", out var n2) ? n2 : 0;
        Check("多主题：分面包含次要主题",
              nightCount == 2 && natureCount == 1 && facets.ThemeUnsetCount == 1,
              $"night={nightCount}, nature={natureCount}, unset={facets.ThemeUnsetCount}");

        // 批量写入多主题（多选分类走的就是这条路）
        var ids = new List<string> { "theme-0", "theme-1", "theme-2" };
        var affected = await s.Store.ApplyBatchEditAsync(ids, new BatchEditPatch
        {
            Themes = new List<string> { "city", "architecture" },
        });
        var reloaded = await s.Store.GetByIdsAsync(ids);
        Check("多主题：批量写入三张",
              affected == 3 && reloaded.Count == 3 &&
              reloaded.All(a => a.Themes.Contains("city") && a.Themes.Contains("architecture") &&
                                a.Theme == "city"),
              $"affected={affected}");

        // 「未填写」筛选：多主题有值的不算未填写
        var unset = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "theme-lib",
            Themes = { "__unset__" },
            Limit = 0,
        });
        Check("多主题：未填写筛选正确", unset.Count == 0, $"{unset.Count} 条");
    }
}