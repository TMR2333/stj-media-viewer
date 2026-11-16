import io

# 1) 自检：新增"分类筛选/选项刷新"检查
p = 'tools/SelfCheck/Program.cs'
s = io.open(p, encoding='utf-8').read()
if 'CheckFacetRefresh' not in s:
    s = s.replace('            await Section("调色筛选", async () => await CheckColorFilter(s));',
                  '            await Section("调色筛选", async () => await CheckColorFilter(s));\n'
                  '            await Section("分类筛选与选项刷新", async () => await CheckFacetRefresh(s));')
    io.open(p, 'w', encoding='utf-8').write(s)
    print('selfcheck: section')

# 2) 检查实现
p = 'tools/SelfCheck/Program.FacetChecks.cs'
if not __import__('os').path.exists(p):
    io.open(p, 'w', encoding='utf-8').write('''using StjMediaViewer.Core.Models;

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

        // "未填写"伪选项：查询结果必须全是空主题，且条数对得上
        var unsetQuery = new AssetQuery { Limit = 0 };
        unsetQuery.Themes.Add("__unset__");
        var unset = await s.Store.QueryAsync(unsetQuery);
        Check("筛选：未填写主题", unset.Count == emptyTheme && unset.All(a => string.IsNullOrWhiteSpace(a.Theme)),
              $"{unset.Count} 条（应为 {emptyTheme}）");

        // 主题 + 未填写 混选 = 并集
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

        // 关键回归：给一张没有主题的素材填上主题后，分面里要立刻出现这个主题
        var target = all.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Theme));
        if (target is null) return;
        const string newTheme = "city";
        await s.Store.ApplyBatchEditAsync(new[] { target.Id }, new BatchEditPatch { Theme = newTheme });
        var after = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
        Check("分面：新主题立即出现", after.ByTheme.ContainsKey(newTheme),
              string.Join("/", after.ByTheme.Keys));
        Check("分面：未填写计数减一", after.ThemeUnsetCount == Math.Max(0, emptyTheme - 1),
              $"{after.ThemeUnsetCount}（原 {emptyTheme}）");

        var toneFacets = after.ByTone;
        Check("分面：影调未填写计数", after.ToneUnsetCount == all.Count(a => string.IsNullOrWhiteSpace(a.Tone)),
              $"{after.ToneUnsetCount}");
    }
}
''', 'utf-8')
    print('selfcheck: checks file')