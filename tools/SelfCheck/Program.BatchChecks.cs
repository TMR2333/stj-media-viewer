using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>批量填写：一次写入分类 / 日期 / 城市 / 评分 / 关键词。</summary>
    private static async Task CheckBatchEdit(Services s)
    {
        var assets = (await s.Store.QueryAsync(new AssetQuery { Limit = 3 })).ToList();
        Check("批量填写：有可操作素材", assets.Count >= 2, $"{assets.Count} 条");
        if (assets.Count < 2) return;

        var ids = assets.Select(a => a.Id).ToList();
        var when = new DateTimeOffset(2024, 5, 18, 9, 30, 0, TimeSpan.FromHours(8));

        var patch = new BatchEditPatch
        {
            CapturedAt = when,
            Country = "法国",
            Province = "法兰西岛",
            City = "巴黎",
            CameraMake = "Sony",
            DeviceKey = "Sony A7 IV",
            Theme = "街头",
            Tone = "低调",
            PostProcess = 2,      // Processed
            IsLog = true,
            LogProfile = "S-Log3",
            Rating = 4,
            ColorLabel = "yellow",
            IsPick = true,
            Title = "批量标题",
            Caption = "批量说明",
            AddKeywords = new List<string> { "街拍", "夜景" },
        };

        Check("批量填写：patch 有内容", patch.HasChanges, patch.Describe());

        var affected = await s.Store.ApplyBatchEditAsync(ids, patch);
        Check("批量填写：写入行数正确", affected == ids.Count, $"{affected}/{ids.Count}");

        var reloaded = await s.Store.GetByIdsAsync(ids);
        var a0 = reloaded.FirstOrDefault();
        Check("批量填写：城市写入", a0?.City == "巴黎", a0?.City);
        Check("批量填写：拍摄时间写入",
              a0?.CapturedAt is { } ct && Math.Abs((ct - when).TotalMinutes) < 1,
              a0?.CapturedAt?.ToString("yyyy-MM-dd HH:mm"));
        Check("批量填写：设备写入", a0?.DeviceKey == "Sony A7 IV", a0?.DeviceKey);
        Check("批量填写：主题/影调写入", a0?.Theme == "街头" && a0?.Tone == "低调", $"{a0?.Theme}/{a0?.Tone}");
        Check("批量填写：评分写入", a0?.Rating == 4, a0?.Rating.ToString());
        Check("批量填写：色标写入", a0?.ColorLabel == "yellow", a0?.ColorLabel);
        Check("批量填写：选用写入", a0?.IsPick == true, a0?.IsPick?.ToString());
        Check("批量填写：追加关键词", a0?.Keywords.Contains("街拍") == true, string.Join('/', a0?.Keywords ?? new List<string>()));

        // 部分字段写入：没写的不能被清空
        var partial = new BatchEditPatch { Rating = 2 };
        await s.Store.ApplyBatchEditAsync(ids, partial);
        var again = (await s.Store.GetByIdsAsync(ids)).FirstOrDefault();
        Check("批量填写：未勾选字段保持不变",
              again?.City == "巴黎" && again?.Rating == 2,
              $"{again?.City} / {again?.Rating}");

        // 空字符串 = 清空
        await s.Store.ApplyBatchEditAsync(ids, new BatchEditPatch { City = "", ColorLabel = "" });
        var cleared = (await s.Store.GetByIdsAsync(ids)).FirstOrDefault();
        Check("批量填写：空值清空字段", cleared?.City is null && cleared?.ColorLabel is null,
              $"{cleared?.City ?? "null"} / {cleared?.ColorLabel ?? "null"}");

        // 拍摄地点"未分类"伪选项：分面计数 + __unset__ 过滤
        var unsetFacets = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
        Check("拍摄地点：未分类分面计数", unsetFacets.CityUnsetCount >= ids.Count,
              $"未分类 {unsetFacets.CityUnsetCount} 条");

        var unsetQuery = await s.Store.QueryAsync(new AssetQuery
        {
            Cities = new List<string> { "__unset__" },
            Limit = 0,
        });
        Check("拍摄地点：只看未分类", unsetQuery.Count == unsetFacets.CityUnsetCount,
              $"{unsetQuery.Count} 条");

        var parisBefore = (await s.Store.QueryAsync(new AssetQuery { Cities = new List<string> { "巴黎" }, Limit = 0 })).Count;
        await s.Store.ApplyBatchEditAsync(ids.Take(1).ToList(), new BatchEditPatch { City = "巴黎" });
        var parisAfter = (await s.Store.QueryAsync(new AssetQuery { Cities = new List<string> { "巴黎" }, Limit = 0 })).Count;
        var unsetAfter = (await s.Store.QueryAsync(new AssetQuery { Cities = new List<string> { "__unset__" }, Limit = 0 })).Count;
        Check("拍摄地点：未分类不含已填写", parisAfter == parisBefore + 1 && unsetAfter == unsetFacets.CityUnsetCount - 1,
              $"巴黎 {parisBefore}→{parisAfter} / 未分类 {unsetAfter}");

        Check("批量填写：空 patch 不写库",
              await s.Store.ApplyBatchEditAsync(ids, new BatchEditPatch()) == 0, null);
    }
    /// <summary>"只看未调色 / 只看已调色"：两集互斥、并集不超过全量；标记后期或套 LUT 后归入已调色。</summary>
    private static async Task CheckColorFilter(Services s)
    {
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 0 });
        var colored = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = true });
        var uncolored = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = false });

        Check("调色筛选：已调色 / 未调色都有结果", colored.Count > 0 && uncolored.Count > 0,
              $"已调色 {colored.Count} / 未调色 {uncolored.Count} / 共 {all.Count}");
        Check("调色筛选：两集互斥",
              !colored.Select(a => a.Id).Intersect(uncolored.Select(a => a.Id), StringComparer.Ordinal).Any(),
              null);
        Check("调色筛选：并集不超过全量", colored.Count + uncolored.Count <= all.Count,
              $"{colored.Count}+{uncolored.Count} <= {all.Count}");

        var targets = uncolored.Take(2).ToList();
        if (targets.Count == 0) return;
        var ids = targets.Select(a => a.Id).ToList();

        await s.Store.ApplyBatchEditAsync(ids, new BatchEditPatch { PostProcess = 2 });
        var colored2 = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = true });
        var uncolored2 = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = false });
        Check("标记已后期后计入已调色", colored2.Count >= colored.Count + ids.Count,
              $"{colored.Count} → {colored2.Count}");
        Check("标记已后期后离开未调色", uncolored2.Count <= uncolored.Count - ids.Count,
              $"{uncolored.Count} → {uncolored2.Count}");

        var withLut = all.FirstOrDefault(a => a.Kind == MediaKind.Video);
        if (withLut is not null)
        {
            await s.Store.SetAssetLutAsync(withLut.Id, "builtin:sony-slog3-cine-709");
            var lutColored = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = true });
            Check("套用 LUT 后计入已调色", lutColored.Any(a => a.Id == withLut.Id), withLut.FileName);
            await s.Store.SetAssetLutAsync(withLut.Id, null);
        }
    }

}
