import io

# ============ 1) 接口 ============
p = 'src/StjMediaViewer.Core/Abstractions/Interfaces.cs'
s = io.open(p, encoding='utf-8').read()
if 'GetCollectionsAsync' not in s:
    s = s.replace('''    Task SaveLibraryRootsAsync(IEnumerable<LibraryRoot> roots, CancellationToken ct = default);''','''    // ---------- 影像合集（用户自建的多对多归类，不动素材文件） ----------
    Task<IReadOnlyList<Collection>> GetCollectionsAsync(CancellationToken ct = default);
    Task<Collection> CreateCollectionAsync(string name, string? description = null, CancellationToken ct = default);
    Task RenameCollectionAsync(string collectionId, string name, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default);
    /// <summary>把素材加进合集（已在里面就跳过），返回新增条数。</summary>
    Task<int> AddToCollectionAsync(string collectionId, IEnumerable<string> assetIds, CancellationToken ct = default);
    /// <summary>把素材移出合集，返回移除条数。</summary>
    Task<int> RemoveFromCollectionAsync(string collectionId, IEnumerable<string> assetIds, CancellationToken ct = default);
    /// <summary>合集 id → 素材数。</summary>
    Task<IReadOnlyDictionary<string, int>> GetCollectionCountsAsync(CancellationToken ct = default);

    Task SaveLibraryRootsAsync(IEnumerable<LibraryRoot> roots, CancellationToken ct = default);''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('iface: collections')

# ============ 2) SqliteAssetStore：CRUD ============
p = 'src/StjMediaViewer.Core/Catalog/SqliteAssetStore.cs'
s = io.open(p, encoding='utf-8').read()
if 'GetCollectionsAsync' not in s:
    anchor = '''    public Task<IReadOnlyList<(string Id, string FullPath, long SizeBytes, DateTimeOffset FileModified)>> ListAllPathsAsync('''
    assert anchor in s
    impl = '''    // ==================================================================== 影像合集

    public Task<IReadOnlyList<Collection>> GetCollectionsAsync(CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                SELECT c.id, c.name, c.description, c.created_at, c.sort_order, COUNT(ca.asset_id)
                FROM collections c
                LEFT JOIN collection_assets ca ON ca.collection_id = c.id
                GROUP BY c.id
                ORDER BY c.sort_order, c.created_at, c.name";
            using var r = cmd.ExecuteReader();
            var list = new List<Collection>();
            while (r.Read())
            {
                list.Add(new Collection
                {
                    Id = r.GetString(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    Description = r.IsDBNull(2) ? null : r.GetString(2),
                    CreatedAt = r.IsDBNull(3) || !DateTimeOffset.TryParse(r.GetString(3), out var created)
                        ? DateTimeOffset.Now : created,
                    SortOrder = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    AssetCount = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                });
            }
            return (IReadOnlyList<Collection>)list;
        }, ct);

    public Task<Collection> CreateCollectionAsync(string name, string? description = null, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var collection = new Collection
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = string.IsNullOrWhiteSpace(name) ? "新合集" : name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim(),
                CreatedAt = DateTimeOffset.Now,
            };

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO collections(id, name, description, created_at, sort_order)
                VALUES(@id, @name, @desc, @created,
                       COALESCE((SELECT MAX(sort_order) + 1 FROM collections), 0))";
            cmd.Parameters.AddWithValue("@id", collection.Id);
            cmd.Parameters.AddWithValue("@name", collection.Name);
            cmd.Parameters.AddWithValue("@desc", (object?)collection.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", collection.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
            return collection;
        }, ct);

    public Task RenameCollectionAsync(string collectionId, string name, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(collectionId)) return;
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE collections SET name=@name WHERE id=@id";
            cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(name) ? "未命名合集" : name.Trim());
            cmd.Parameters.AddWithValue("@id", collectionId);
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(collectionId)) return;
            using var tx = _conn!.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM collection_assets WHERE collection_id=@id";
                del.Parameters.AddWithValue("@id", collectionId);
                del.ExecuteNonQuery();
            }
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM collections WHERE id=@id";
                del.Parameters.AddWithValue("@id", collectionId);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }, ct);

    public Task<int> AddToCollectionAsync(string collectionId, IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(collectionId) || assetIds is null) return 0;
            var ids = assetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0) return 0;

            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO collection_assets(collection_id, asset_id, added_at) VALUES(@c, @a, @t)";
            cmd.Parameters.AddWithValue("@c", collectionId);
            var pAsset = cmd.Parameters.Add("@a", SqliteType.Text);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.Now.ToString("O"));
            int added = 0;
            foreach (var id in ids)
            {
                pAsset.Value = id;
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        }, ct);

    public Task<int> RemoveFromCollectionAsync(string collectionId, IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(collectionId) || assetIds is null) return 0;
            var ids = assetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0) return 0;

            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            var sb = new StringBuilder("DELETE FROM collection_assets WHERE collection_id=@c AND asset_id IN (");
            cmd.Parameters.AddWithValue("@c", collectionId);
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("@a").Append(i);
                cmd.Parameters.AddWithValue("@a" + i, ids[i]);
            }
            sb.Append(')');
            cmd.CommandText = sb.ToString();
            var removed = cmd.ExecuteNonQuery();
            tx.Commit();
            return removed;
        }, ct);

    public Task<IReadOnlyDictionary<string, int>> GetCollectionCountsAsync(CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT collection_id, COUNT(*) FROM collection_assets GROUP BY collection_id";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
            return (IReadOnlyDictionary<string, int>)map;
        }, ct);

''' + anchor
    s = s.replace(anchor, impl, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('sqlite: collection crud')