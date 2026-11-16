using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 筛选面板的可选值必须跟着数据走：
    ///  - 主题为空时能用「未填写」筛选；
    ///  - 新写入的主题/影调要立刻出现在分面里（之前只建一次选项，导致"拍摄主题不能筛选"）。
    /// </summary>
    private static async Task CheckFacetRefresh(Services s)
    {
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 0 });
        var facets = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });

        int emptyTheme = all.Count(a => string.IsNullOrWhiteSpace(a.Theme));
        Check("分面：主题未填写计数", facets.ThemeUnsetCount == emptyTheme,
              $"{facets.ThemeUnsetCount} vs {emptyTheme}");

        var unsetQuery = new AssetQuery { Limit = 0 };
        unsetQuery.Themes.Add("__unset__");
        var unset = await s.Store.QueryAsync(unsetQuery);
        Check("筛选：未填写主题", unset.Count == emptyTheme && unset.All(a => string.IsNullOrWhiteSpace(a.Theme)),
              $"{unset.Count} 条（应为 {emptyTheme}）");

        var withTheme = all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Theme));
        if (withTheme is not null)
        {
            var mixed = new AssetQuery { Limit = 0 };
            mixed.Themes.Add("__unset__");
            mixed.Themes.Add(withTheme.Theme!);
            var rows = await s.Store.QueryAsync(mixed);
            var expect = emptyTheme + all.Count(a => a.Theme == withTheme.Theme);
            Check("筛选：主题与未填写并集", rows.Count == expect, $"{rows.Count} 条（应为 {expect}）");
        }

        var target = all.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Theme));
        if (target is null) return;
        const string newTheme = "city";
        await s.Store.ApplyBatchEditAsync(new[] { target.Id }, new BatchEditPatch { Theme = newTheme });
        var after = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
        Check("分面：新主题立即出现", after.ByTheme.ContainsKey(newTheme),
              string.Join("/", after.ByTheme.Keys));
        Check("分面：未填写计数减一", after.ThemeUnsetCount == Math.Max(0, emptyTheme - 1),
              $"{after.ThemeUnsetCount}（原 {emptyTheme}）");
        Check("分面：影调未填写计数", after.ToneUnsetCount == all.Count(a => string.IsNullOrWhiteSpace(a.Tone)),
              $"{after.ToneUnsetCount}");
    }
}
