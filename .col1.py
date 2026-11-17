import io

# ============ 1) Collection 模型 ============
p = 'src/StjMediaViewer.Core/Models/Collection.cs'
io.open(p, 'w', encoding='utf-8').write('''namespace StjMediaViewer.Core.Models;

/// <summary>
/// 影像合集：用户自建的一组素材（与"媒体库/筛选"独立的多对多归类）。
/// 一张素材可以同时进多个合集，素材本身不会被移动或复制。
/// </summary>
public sealed class Collection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int SortOrder { get; set; }

    /// <summary>合集里的素材数量（查询时带出，便于界面显示）。</summary>
    public int AssetCount { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名合集" : Name;
}
''', encoding='utf-8')
print('model: Collection')

# ============ 2) AssetQuery / FacetCounts ============
p = 'src/StjMediaViewer.Core/Models/AssetQuery.cs'
s = io.open(p, encoding='utf-8').read()
if 'Collections' not in s:
    s = s.replace('''    public List<string> Keywords { get; set; } = new();''','''    public List<string> Keywords { get; set; } = new();
    /// <summary>影像合集筛选：命中任意一个选中合集的素材（多选 = 并集）。</summary>
    public List<string> Collections { get; set; } = new();''', 1)
    s = s.replace('''    public int ThemeUnsetCount { get; set; }''','''    public int ThemeUnsetCount { get; set; }

    /// <summary>影像合集分面：合集 id → 素材数。</summary>
    public Dictionary<string, int> ByCollection { get; set; } = new(StringComparer.Ordinal);

    /// <summary>没有加入任何合集的素材数（"未加入合集"伪选项用）。</summary>
    public int CollectionUnsetCount { get; set; }''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('query: collections')

# ============ 3) 索引库表结构 ============
p = 'src/StjMediaViewer.Core/Catalog/CatalogSchema.cs'
s = io.open(p, encoding='utf-8').read()
if 'TableCollections' not in s:
    s = s.replace('''    public const string TableAssetCustom = "asset_custom";''','''    public const string TableAssetCustom = "asset_custom";
    public const string TableCollections = "collections";
    public const string TableCollectionAssets = "collection_assets";''', 1)
    s = s.replace('''        Execute(conn, DdlAssetCustom);''','''        Execute(conn, DdlAssetCustom);
        Execute(conn, DdlCollections);
        Execute(conn, DdlCollectionAssets);''', 1)

    # DDL 常量追加到文件末尾前
    s = s.rstrip()
    idx = s.rfind('}')
    ddl = '''
    private const string DdlCollections = @"
        CREATE TABLE IF NOT EXISTS collections (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL DEFAULT '',
            description     TEXT,
            created_at      TEXT,
            sort_order      INTEGER NOT NULL DEFAULT 0
        );";

    private const string DdlCollectionAssets = @"
        CREATE TABLE IF NOT EXISTS collection_assets (
            collection_id   TEXT NOT NULL,
            asset_id        TEXT NOT NULL,
            added_at        TEXT,
            PRIMARY KEY (collection_id, asset_id)
        );
        CREATE INDEX IF NOT EXISTS idx_collection_assets_asset ON collection_assets(asset_id);";
'''
    s = s[:idx] + ddl + s[idx:]
    io.open(p, 'w', encoding='utf-8').write(s)
    print('schema: collection tables')