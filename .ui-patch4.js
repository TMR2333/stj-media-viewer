const fs = require('fs');

// 自检：只看未调色 / 只看已调色
{
    const p = 'tools/SelfCheck/Program.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('CheckColorFilter')) {
        s = s.replace(
            '            await Section("批量填写信息", async () => await CheckBatchEdit(s));',
            '            await Section("批量填写信息", async () => await CheckBatchEdit(s));\n' +
            '            await Section("调色筛选", async () => await CheckColorFilter(s));');
        fs.writeFileSync(p, s, 'utf8');
        console.log('selfcheck: colored section');
    }
}

{
    const p = 'tools/SelfCheck/Program.BatchChecks.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('CheckColorFilter')) {
        s = s.TrimEnd();
        if (s.EndsWith('}')) s = s[..^1];   // 去掉命名空间/类结尾的最后一个 }
        // 该文件尾部结构：类结尾 } —— 重新拼装
        s = s.TrimEnd();
        if (!s.EndsWith('}')) throw new Error('unexpected tail');
        s = s[..^1];   // 去掉类结尾
        s += `
    /// <summary>"只看未调色 / 只看已调色"筛选：两集互斥、并集不超过全量，且标记后期后归入已调色。</summary>
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
        Check("调色筛选：并集不超过全量",
              colored.Count + uncolored.Count <= all.Count,
              $"{colored.Count}+{uncolored.Count} <= {all.Count}");

        // 把 2 条未调色素材标记为已后期 → 应当从"未调色"挪到"已调色"
        var targets = uncolored.Take(2).ToList();
        if (targets.Count == 0) return;

        var ids = targets.Select(a => a.Id).ToList();
        await s.Store.ApplyBatchEditAsync(ids, new BatchEditPatch { PostProcess = 2 });

        var colored2 = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = true });
        var uncolored2 = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = false });
        Check("标记已后期后计入已调色",
              colored2.Count >= colored.Count + ids.Count,
              $"{colored.Count} → {colored2.Count}");
        Check("标记已后期后离开未调色",
              uncolored2.Count <= uncolored.Count - ids.Count,
              $"{uncolored.Count} → {uncolored2.Count}");

        // 已套 LUT 的素材也算已调色
        var withLut = all.FirstOrDefault(a => a.Kind == MediaKind.Video);
        if (withLut is not null)
        {
            await s.Store.SetAssetLutAsync(withLut.Id, "builtin:sony-slog3-cine-709");
            var lutColored = await s.Store.QueryAsync(new AssetQuery { Limit = 0, Colored = true });
            Check("套用 LUT 后计入已调色",
                  lutColored.Any(a => a.Id == withLut.Id),
                  withLut.FileName);
            await s.Store.SetAssetLutAsync(withLut.Id, null);
        }
    }
}
`;
        fs.writeFileSync(p, s, 'utf8');
        console.log('selfcheck: color filter checks');
    }
}
console.log('done');