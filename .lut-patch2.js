const fs = require('fs');

// ---------- 1) Schema：assets 增加 lut_id ----------
{
    const p = 'src/StjMediaViewer.Core/Catalog/CatalogSchema.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('lut_id')) {
        s = s.replace(
            '        EnsureColumn(conn, TableAssets, "companions_json", "TEXT NOT NULL DEFAULT \'[]\'");',
            '        EnsureColumn(conn, TableAssets, "companions_json", "TEXT NOT NULL DEFAULT \'[]\'");\n' +
            '        // LUT：素材选用的调色 LUT（空 = 自动匹配内置基准 LUT）\n' +
            '        EnsureColumn(conn, TableAssets, "lut_id", "TEXT");');
        s = s.replace(
            '            companions_json         TEXT NOT NULL DEFAULT \'[]\',',
            '            companions_json         TEXT NOT NULL DEFAULT \'[]\',\n            lut_id                  TEXT,');
        fs.writeFileSync(p, s, 'utf8');
        console.log('schema: lut_id');
    }
}

// ---------- 2) AssetMapper：读写 lut_id ----------
{
    const p = 'src/StjMediaViewer.Core/Catalog/AssetMapper.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('"lut_id"')) {
        s = s.replace(
            '        "group_id", "is_group_cover", "group_has_raw", "group_has_jpeg", "companions_json",',
            '        "group_id", "is_group_cover", "group_has_raw", "group_has_jpeg", "companions_json", "lut_id",');
        s = s.replace(
            '        "companions_json" => JsonList(a.CompanionPaths),',
            '        "companions_json" => JsonList(a.CompanionPaths),\n        "lut_id" => a.LutId,');
        s = s.replace(
            '        a.CompanionPaths = JsonToList(GetS(r, "companions_json"));',
            '        a.CompanionPaths = JsonToList(GetS(r, "companions_json"));\n        a.LutId = GetS(r, "lut_id");');
        fs.writeFileSync(p, s, 'utf8');
        console.log('mapper: lut_id');
    }
}

// ---------- 3) Store：按 id 只更新 lut_id（避免整行 Upsert 覆盖并发编辑） ----------
{
    const p = 'src/StjMediaViewer.Core/Abstractions/Interfaces.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('SetAssetLutAsync')) {
        s = s.replace(
            '    Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default);',
            '    /// <summary>只更新某条素材选用的 LUT（null = 恢复自动匹配）。</summary>\n' +
            '    Task SetAssetLutAsync(string assetId, string? lutId, CancellationToken ct = default);\n' +
            '    Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default);');
        fs.writeFileSync(p, s, 'utf8');
        console.log('interface: SetAssetLutAsync');
    }
}

// ---------- 4) SqliteAssetStore：实现 ----------
{
    const p = 'src/StjMediaViewer.Core/Catalog/SqliteAssetStore.cs';
    let s = fs.readFileSync(p, 'utf8');
    if (!s.includes('SetAssetLutAsync')) {
        const anchor = '    public async Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)';
        if (!s.includes(anchor)) throw new Error('store anchor');
        s = s.replace(anchor,
            '    public async Task SetAssetLutAsync(string assetId, string? lutId, CancellationToken ct = default)\n' +
            '    {\n' +
            '        await using var conn = await OpenAsync(ct).ConfigureAwait(false);\n' +
            '        await using var cmd = conn.CreateCommand();\n' +
            '        cmd.CommandText = "UPDATE assets SET lut_id = $lut WHERE id = $id";\n' +
            '        cmd.Parameters.AddWithValue("$lut", (object?)lutId ?? DBNull.Value);\n' +
            '        cmd.Parameters.AddWithValue("$id", assetId);\n' +
            '        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);\n' +
            '        _cache.Remove(assetId);\n' +
            '    }\n\n' + anchor);
        fs.writeFileSync(p, s, 'utf8');
        console.log('store: SetAssetLutAsync impl');
    }
}
console.log('done');