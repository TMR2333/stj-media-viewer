import io
p = 'src/StjMediaViewer.Core/Catalog/SqliteAssetStore.cs'
s = io.open(p, encoding='utf-8').read()

# ---------- 1) BuildWhere：自定义类别筛选（每个维度一个 EXISTS） ----------
if 'q.CustomFilters' not in s:
    anchor = '''        if (q.IsRaw is { } isRaw) w.And(isRaw ? "raw_format <> 0" : "raw_format = 0");'''
    custom = '''        // 自定义类别：同一维度内 OR，不同维度之间 AND
        foreach (var (dimension, values) in q.CustomFilters)
        {
            if (string.IsNullOrWhiteSpace(dimension) || values is null || values.Count == 0) continue;

            var dimParam = "@w_cdim" + w.NextInPrefix().TrimStart('@');
            w.Args.Add(new KeyValuePair<string, object?>(dimParam, dimension));

            var sbCustom = new StringBuilder();
            sbCustom.Append("EXISTS (SELECT 1 FROM asset_custom acv WHERE acv.asset_id = assets.id AND acv.dimension = ")
                     .Append(dimParam).Append(" AND acv.value IN (");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sbCustom.Append(',');
                var p = "@w_cv" + w.NextInPrefix().TrimStart('@') + "_" + i;
                sbCustom.Append(p);
                w.Args.Add(new KeyValuePair<string, object?>(p, values[i]));
            }
            sbCustom.Append("))");
            w.And(sbCustom.ToString());
        }

''' + anchor
    s = s.replace(anchor, custom, 1)
    print('where: custom filters')

# ---------- 2) 分面：ByCustom ----------
if 'f.ByCustom' not in s:
    anchor = '''            f.ByPick = GroupText("is_pick",'''
    bycustom = '''            // 自定义类别分面：维度 → 值 → 计数（只统计当前筛选条件内的素材）
            var customSql = "SELECT acv.dimension, acv.value, COUNT(*) FROM asset_custom acv " +
                            "WHERE acv.asset_id IN (SELECT assets.id FROM assets" + w.Sql + ") " +
                            "GROUP BY acv.dimension, acv.value";
            foreach (var row in QueryGrouped(customSql, w))
            {
                if (!f.ByCustom.TryGetValue(row.Key, out var values))
                    f.ByCustom[row.Key] = values = new Dictionary<string, int>(StringComparer.Ordinal);
                values[row.Value.Key] = row.Value.Value;
            }

''' + anchor
    s = s.replace(anchor, bycustom, 1)
    print('facets: ByCustom declared (helper next)')

io.open(p, 'w', encoding='utf-8').write(s)
print('store patched phase 1')