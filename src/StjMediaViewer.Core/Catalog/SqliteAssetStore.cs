using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Catalog;

/// <summary>
/// SQLite 素材索引库。设计要点：
/// <list type="bullet">
/// <item>单连接 + <see cref="SemaphoreSlim"/> 串行化：SQLite 单写者模型下最省心的方案，
///       所有公开方法都通过 <see cref="RunAsync{T}"/> 把同步 SQLite 工作放到线程池，避免卡 UI。</item>
/// <item>WAL + synchronous=NORMAL：批量写入吞吐优先，崩溃最多丢最后一个事务。</item>
/// <item>UPSERT 使用 prepared statement（参数对象只建一次，循环里只改值），
///       1000 条一个事务；实测万条 upsert 在 1s 量级。</item>
/// <item>查询条件全部参数化，绝不拼接用户文本。</item>
/// </list>
/// </summary>
public sealed class SqliteAssetStore : IAssetStore, IDisposable
{
    private const int UpsertBatchSize = 1000;
    private const int InClauseChunk = 400;

    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _conn;
    private bool _initialized;
    private bool _disposed;

    /// <summary>数据库文件绝对路径（通常为 {settings.AppDataDirectory}/catalog.db）。</summary>
    public SqliteAssetStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("数据库路径不能为空", nameof(dbPath));
        _dbPath = Path.GetFullPath(dbPath);
    }

    public string DatabasePath => _dbPath;

    // ==================================================================== 生命周期

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Task.Run(() => OpenAndCreate(), ct).ConfigureAwait(false);
            _initialized = true;
            Log.Info($"[catalog] initialized {_dbPath}");
        }
        finally { _gate.Release(); }

        // 预热：小库时把查询/SQL 生成路径 JIT 一遍，避免 UI 首次筛选出现近百毫秒的冷启动毛刺
        await WarmUpAsync(ct).ConfigureAwait(false);
    }

    private async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            if (await CountAllAsync(ct).ConfigureAwait(false) > 2000) return;
            var probe = new AssetQuery { Limit = 1 };
            await QueryAsync(probe, ct).ConfigureAwait(false);
            await CountAsync(probe, ct).ConfigureAwait(false);
            await GetFacetsAsync(probe, ct).ConfigureAwait(false);
            await GetTimelineAsync(probe, ct).ConfigureAwait(false);
            await GetDistinctValuesAsync("format", null, ct).ConfigureAwait(false);
            await LoadLibraryRootsAsync(ct).ConfigureAwait(false);
            await ListAllPathsAsync(null, ct).ConfigureAwait(false);
            await GetByIdsAsync(Array.Empty<string>(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"[catalog] warm-up skipped: {ex.Message}"); }
    }

    private void OpenAndCreate()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 15,
        }.ToString());
        _conn.Open();
        CatalogSchema.Apply(_conn);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _conn?.Dispose(); } catch { }
        _conn = null;
        _gate.Dispose();
    }

    // ==================================================================== 串行化执行

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(Action work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_conn is null || !_initialized)
        {
            // 允许未显式 InitializeAsync 就使用：自动初始化（幂等）
            OpenAndCreate();
            _initialized = true;
        }
    }

    // ==================================================================== 单条读取

    public Task<MediaAsset?> GetAsync(string id, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"SELECT {AssetMapper.SelectList} FROM assets WHERE id=@id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", id ?? "");
            using var r = cmd.ExecuteReader();
            return r.Read() ? AssetMapper.Read(r) : null;
        }, ct);

    public Task<MediaAsset?> GetByPathAsync(string fullPath, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"SELECT {AssetMapper.SelectList} FROM assets WHERE full_path=@p LIMIT 1";
            cmd.Parameters.AddWithValue("@p", fullPath ?? "");
            using var r = cmd.ExecuteReader();
            return r.Read() ? AssetMapper.Read(r) : null;
        }, ct);

    public Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = Clean(ids);
        if (list.Count == 0) return Task.FromResult<IReadOnlyList<MediaAsset>>(Array.Empty<MediaAsset>());
        return RunAsync(() =>
        {
            var result = new List<MediaAsset>(list.Count);
            foreach (var chunk in Chunk(list, InClauseChunk))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = _conn!.CreateCommand();
                var sb = new StringBuilder($"SELECT {AssetMapper.SelectList} FROM assets WHERE id IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@i").Append(i);
                    cmd.Parameters.AddWithValue("@i" + i, chunk[i]);
                }
                sb.Append(')');
                cmd.CommandText = sb.ToString();
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(AssetMapper.Read(r));
            }
            return (IReadOnlyList<MediaAsset>)result;
        }, ct);
    }

    public Task<IReadOnlyList<string>> GetIdsByPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct = default)
    {
        var list = Clean(fullPaths);
        if (list.Count == 0) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        return RunAsync(() =>
        {
            var result = new List<string>(list.Count);
            foreach (var chunk in Chunk(list, InClauseChunk))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = _conn!.CreateCommand();
                var sb = new StringBuilder("SELECT id FROM assets WHERE full_path IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@p").Append(i);
                    cmd.Parameters.AddWithValue("@p" + i, chunk[i]);
                }
                sb.Append(')');
                cmd.CommandText = sb.ToString();
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(r.GetString(0));
            }
            return (IReadOnlyList<string>)result;
        }, ct);
    }

    // ==================================================================== 写入

    public Task UpsertAsync(MediaAsset asset, CancellationToken ct = default) =>
        UpsertManyAsync(new[] { asset }, ct);

    public Task UpsertManyAsync(IEnumerable<MediaAsset> assets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return RunAsync(() =>
        {
            var buffer = new List<MediaAsset>(UpsertBatchSize);
            foreach (var a in assets)
            {
                if (a is null) continue;
                buffer.Add(a);
                if (buffer.Count >= UpsertBatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    WriteBatch(buffer, ct);
                    buffer.Clear();
                }
            }
            if (buffer.Count > 0) WriteBatch(buffer, ct);
        }, ct);
    }

    private void WriteBatch(List<MediaAsset> batch, CancellationToken ct)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = AssetMapper.UpsertSql;
        AssetMapper.PrepareUpsertParameters(cmd);

        using var tx = _conn.BeginTransaction();
        cmd.Transaction = tx;
        int failed = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = batch[i];
            try
            {
                AssetMapper.Bind(cmd.Parameters, a);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                failed++;
                Log.Warn($"[catalog] upsert failed for {a.FullPath}: {ex.Message}");
                TryRepairConflict(a);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"[catalog] upsert failed for {a.FullPath}: {ex.Message}");
            }
        }
        try { tx.Commit(); }
        catch (Exception ex)
        {
            // 事务本身失败（磁盘/锁）：整批回滚，不阻断扫描
            Log.Error($"[catalog] upsert batch commit failed ({batch.Count} rows): {ex.Message}");
        }
        if (failed > 0) Log.Warn($"[catalog] upsert batch had {failed}/{batch.Count} failures");
    }

    /// <summary>id 与 full_path 指向不一致的历史脏行：删掉再让下一轮扫描重建，保证不卡住整批。</summary>
    private void TryRepairConflict(MediaAsset a)
    {
        try
        {
            using var fix = _conn!.CreateCommand();
            fix.CommandText = "DELETE FROM assets WHERE id=@id OR full_path=@p";
            fix.Parameters.AddWithValue("@id", a.Id);
            fix.Parameters.AddWithValue("@p", a.FullPath);
            int n = fix.ExecuteNonQuery();
            if (n > 0) Log.Warn($"[catalog] repaired conflict rows={n} path={a.FullPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[catalog] repair conflict failed: {ex.Message}");
        }
    }

    public Task<int> ApplyBatchEditAsync(IReadOnlyList<string> assetIds, BatchEditPatch patch,
        CancellationToken ct = default)
    {
        if (assetIds is null || assetIds.Count == 0 || patch is null || !patch.HasChanges)
            return Task.FromResult(0);

        var ids = assetIds.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (ids.Count == 0) return Task.FromResult(0);

        int affected = 0;
        return RunAsync(() =>
        {
            using var tx = _conn!.BeginTransaction();

            foreach (var chunk in Chunk(ids, InClauseChunk))
            {
                ct.ThrowIfCancellationRequested();

                // 关键词是 JSON 数组：先读出来合并再写回（只有要求改关键词时才读）
                Dictionary<string, List<string>>? keywords = null;
                if (patch.AddKeywords is { Count: > 0 } || patch.ReplaceKeywords is not null)
                {
                    keywords = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    using var read = _conn.CreateCommand();
                    read.Transaction = tx;
                    var sbRead = new StringBuilder("SELECT id, keywords_json FROM assets WHERE id IN (");
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (i > 0) sbRead.Append(',');
                        sbRead.Append("@k").Append(i);
                        read.Parameters.AddWithValue("@k" + i, chunk[i]);
                    }
                    sbRead.Append(')');
                    read.CommandText = sbRead.ToString();
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        var json = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                        keywords[id] = ParseJsonList(json);
                    }
                }

                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                var sets = new List<string>();
                void Set(string column, object? value)
                {
                    var name = "@p" + sets.Count;
                    sets.Add(column + " = " + name);
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }

                if (patch.CapturedAt is { } captured)
                {
                    var local = captured.ToLocalTime();
                    Set("captured_at", captured.ToString("O"));
                    Set("captured_ms", captured.ToUnixTimeMilliseconds());
                    Set("captured_day", local.ToString("yyyy-MM-dd"));
                    Set("captured_at_source", "manual");
                }
                if (patch.Country is not null) Set("country", NullIfEmpty(patch.Country));
                if (patch.Province is not null) Set("province", NullIfEmpty(patch.Province));
                if (patch.City is not null)
                {
                    Set("city", NullIfEmpty(patch.City));
                    Set("city_source", patch.City.Length == 0 ? null : "manual");
                }
                if (patch.CameraMake is not null) Set("camera_make", NullIfEmpty(patch.CameraMake));
                if (patch.DeviceKey is not null) Set("device_key", NullIfEmpty(patch.DeviceKey));
                if (patch.PostProcess is { } pp) Set("post_process", Math.Clamp(pp, 0, 2));
                if (patch.IsLog is { } isLog) Set("is_log", isLog ? 1 : 0);
                if (patch.LogProfile is not null) Set("log_profile", NullIfEmpty(patch.LogProfile));
                if (patch.Theme is not null) Set("theme", NullIfEmpty(patch.Theme));
                if (patch.Themes is not null)
                {
                    var themes = patch.Themes
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    Set("themes_json", AssetMapper.JsonKeywords(themes));
                    Set("theme", themes.Count > 0 ? themes[0] : null);
                }
                if (patch.Tone is not null) Set("tone", NullIfEmpty(patch.Tone));
                if (patch.Rating is { } rating) Set("rating", Math.Clamp(rating, 0, 5));
                if (patch.ColorLabel is not null) Set("color_label", NullIfEmpty(patch.ColorLabel));
                if (patch.IsPick is { } isPick) Set("is_pick", isPick ? 1 : 0);
                if (patch.Title is not null) Set("title", NullIfEmpty(patch.Title));
                if (patch.Caption is not null) Set("caption", NullIfEmpty(patch.Caption));

                if (sets.Count == 0)
                {
                    ApplyCustomFields(tx, chunk, patch);
                    continue;
                }

                var sb = new StringBuilder("UPDATE assets SET ");
                sb.Append(string.Join(", ", sets));

                // 关键词：按 id 单独更新（每条要合并）
                sb.Append(", keywords_json = CASE id ");
                for (int i = 0; i < chunk.Count; i++)
                {
                    var id = chunk[i];
                    List<string> merged;
                    if (patch.ReplaceKeywords is not null) merged = patch.ReplaceKeywords;
                    else
                    {
                        merged = keywords is not null && keywords.TryGetValue(id, out var old)
                            ? new List<string>(old)
                            : new List<string>();
                        foreach (var kw in patch.AddKeywords ?? new List<string>())
                            if (!merged.Contains(kw, StringComparer.OrdinalIgnoreCase)) merged.Add(kw);
                    }

                    sb.Append(" WHEN @k").Append(i).Append(" THEN @kw").Append(i);
                    cmd.Parameters.AddWithValue("@kw" + i, AssetMapper.JsonList(merged));
                }
                sb.Append(" ELSE keywords_json END, keywords_flat = CASE id ");
                for (int i = 0; i < chunk.Count; i++)
                {
                    List<string> flatSource;
                    if (patch.ReplaceKeywords is not null) flatSource = patch.ReplaceKeywords;
                    else
                    {
                        flatSource = keywords is not null && keywords.TryGetValue(chunk[i], out var old)
                            ? new List<string>(old)
                            : new List<string>();
                        foreach (var kw in patch.AddKeywords ?? new List<string>())
                            if (!flatSource.Contains(kw, StringComparer.OrdinalIgnoreCase)) flatSource.Add(kw);
                    }
                    sb.Append(" WHEN @k").Append(i).Append(" THEN @f").Append(i);
                    cmd.Parameters.AddWithValue("@f" + i, string.Join(' ', flatSource));
                }
                sb.Append(" ELSE keywords_flat END");

                sb.Append(" WHERE id IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@k").Append(i);
                    cmd.Parameters.AddWithValue("@k" + i, chunk[i]);
                }
                sb.Append(')');

                cmd.CommandText = sb.ToString();
                affected += cmd.ExecuteNonQuery();

                ApplyCustomFields(tx, chunk, patch);
            }

            tx.Commit();
        }, ct).ContinueWith(t => affected, ct);
    }

    /// <summary>写入自定义类别：先删该维度旧值，再按需插入新值（空串 = 只删不插）。</summary>
    private void ApplyCustomFields(Microsoft.Data.Sqlite.SqliteTransaction tx, List<string> assetIds, BatchEditPatch patch)
    {
        if (patch.CustomFields is not { Count: > 0 }) return;

        foreach (var (rawDimension, rawValue) in patch.CustomFields)
        {
            if (string.IsNullOrWhiteSpace(rawDimension)) continue;
            var dimension = rawDimension.Trim();
            var value = (rawValue ?? "").Trim();

            using (var delete = _conn!.CreateCommand())
            {
                delete.Transaction = tx;
                var sbDel = new StringBuilder("DELETE FROM asset_custom WHERE dimension = @dim AND asset_id IN (");
                delete.Parameters.AddWithValue("@dim", dimension);
                for (int i = 0; i < assetIds.Count; i++)
                {
                    if (i > 0) sbDel.Append(',');
                    sbDel.Append("@a").Append(i);
                    delete.Parameters.AddWithValue("@a" + i, assetIds[i]);
                }
                sbDel.Append(')');
                delete.CommandText = sbDel.ToString();
                delete.ExecuteNonQuery();
            }

            if (value.Length == 0) continue;

            using var insert = _conn!.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO asset_custom (asset_id, dimension, value) VALUES (@a, @dim, @val) " +
                "ON CONFLICT(asset_id, dimension) DO UPDATE SET value = excluded.value";
            insert.Parameters.AddWithValue("@dim", dimension);
            insert.Parameters.AddWithValue("@val", value);
            for (int i = 0; i < assetIds.Count; i++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@a", assetIds[i]);
                insert.Parameters.AddWithValue("@dim", dimension);
                insert.Parameters.AddWithValue("@val", value);
                insert.ExecuteNonQuery();
            }
        }
    }

    private static object? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static List<string> ParseJsonList(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    public Task<IReadOnlyDictionary<string, string>> GetCustomFieldsAsync(string assetId,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(assetId)) return Task.FromResult<IReadOnlyDictionary<string, string>>(result);

        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT dimension, value FROM asset_custom WHERE asset_id = @id ORDER BY dimension";
            cmd.Parameters.AddWithValue("@id", assetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }, ct).ContinueWith(_ => (IReadOnlyDictionary<string, string>)result, ct);
    }

    public Task<IReadOnlyDictionary<string, string>> GetCustomFieldsBulkAsync(string dimension,
        IEnumerable<string> assetIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = assetIds?.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList()
                  ?? new List<string>();
        if (string.IsNullOrWhiteSpace(dimension) || ids.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, string>>(result);

        var dim = dimension.Trim();
        return RunAsync(() =>
        {
            foreach (var chunk in Chunk(ids, InClauseChunk))
            {
                using var cmd = _conn!.CreateCommand();
                var sb = new StringBuilder("SELECT asset_id, value FROM asset_custom WHERE dimension = @d AND asset_id IN (");
                cmd.Parameters.AddWithValue("@d", dim);
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@i").Append(i);
                    cmd.Parameters.AddWithValue("@i" + i, chunk[i]);
                }
                sb.Append(')');
                cmd.CommandText = sb.ToString();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    result[reader.GetString(0)] = reader.GetString(1);
                }
            }
        }, ct).ContinueWith(_ => (IReadOnlyDictionary<string, string>)result, ct);
    }

    public Task<IReadOnlyList<string>> ListCustomDimensionsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT dimension FROM asset_custom ORDER BY dimension";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                result.Add(reader.GetString(0));
            }
        }, ct).ContinueWith(_ => (IReadOnlyList<string>)result, ct);
    }

    public Task SetCustomFieldAsync(string assetId, string dimension, string? value,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(dimension))
            return Task.CompletedTask;

        var dim = dimension.Trim();
        var val = (value ?? "").Trim();
        return RunAsync(() =>
        {
            using var tx = _conn!.BeginTransaction();

            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM asset_custom WHERE asset_id = @id AND dimension = @dim";
                del.Parameters.AddWithValue("@id", assetId);
                del.Parameters.AddWithValue("@dim", dim);
                del.ExecuteNonQuery();
            }

            if (val.Length > 0)
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO asset_custom (asset_id, dimension, value) VALUES (@id, @dim, @val)";
                ins.Parameters.AddWithValue("@id", assetId);
                ins.Parameters.AddWithValue("@dim", dim);
                ins.Parameters.AddWithValue("@val", val);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }, ct);
    }

    public Task UpdateAssetMetaByPathAsync(string fullPath, string? creator = null, string? origin = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return Task.CompletedTask;
        if (creator is null && origin is null) return Task.CompletedTask;

        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            var sets = new List<string>();
            if (creator is not null) { sets.Add("creator = @c"); cmd.Parameters.AddWithValue("@c", (object?)creator ?? DBNull.Value); }
            if (origin is not null) { sets.Add("origin = @o"); cmd.Parameters.AddWithValue("@o", (object?)origin ?? DBNull.Value); }
            cmd.CommandText = "UPDATE assets SET " + string.Join(", ", sets) + " WHERE full_path = @p";
            cmd.Parameters.AddWithValue("@p", fullPath);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task UpdateImageEditsAsync(string assetId, int rotationDegrees, bool flipHorizontal, bool flipVertical,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return Task.CompletedTask;
        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE assets SET rotation_degrees=@r, flip_h=@h, flip_v=@v WHERE id=@id";
            cmd.Parameters.AddWithValue("@r", ((rotationDegrees % 360) + 360) % 360);
            cmd.Parameters.AddWithValue("@h", flipHorizontal ? 1 : 0);
            cmd.Parameters.AddWithValue("@v", flipVertical ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", assetId);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task UpdateAssetPathAsync(string assetId, string fullPath, string? relativePath,
        IReadOnlyList<string>? sidecars = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(fullPath))
            return Task.CompletedTask;

        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                "UPDATE assets SET full_path = @p, relative_path = @r" +
                (sidecars is null ? "" : ", sidecars_json = @s") +
                " WHERE id = @id";
            cmd.Parameters.AddWithValue("@p", fullPath);
            cmd.Parameters.AddWithValue("@r", (object?)relativePath ?? DBNull.Value);
            if (sidecars is not null) cmd.Parameters.AddWithValue("@s", AssetMapper.JsonList(sidecars.ToList()));
            cmd.Parameters.AddWithValue("@id", assetId);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task SetAssetLutAsync(string assetId, string? lutId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(assetId)) return Task.CompletedTask;
        return RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE assets SET lut_id = $lut WHERE id = $id";
            cmd.Parameters.AddWithValue("$lut", (object?)lutId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", assetId);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = Clean(ids);
        if (list.Count == 0) return Task.CompletedTask;
        return RunAsync(() =>
        {
            using var tx = _conn!.BeginTransaction();
            foreach (var chunk in Chunk(list, InClauseChunk))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                var sb = new StringBuilder("DELETE FROM assets WHERE id IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@i").Append(i);
                    cmd.Parameters.AddWithValue("@i" + i, chunk[i]);
                }
                sb.Append(')');
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();

                // 顺带清掉这些素材的自定义类别
                using var cmdCustom = _conn.CreateCommand();
                cmdCustom.Transaction = tx;
                var sbCustom = new StringBuilder("DELETE FROM asset_custom WHERE asset_id IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sbCustom.Append(',');
                    sbCustom.Append("@c").Append(i);
                    cmdCustom.Parameters.AddWithValue("@c" + i, chunk[i]);
                }
                sbCustom.Append(')');
                cmdCustom.CommandText = sbCustom.ToString();
                cmdCustom.ExecuteNonQuery();
            }
            tx.Commit();
        }, ct);
    }

    public Task DeleteByLibraryAsync(string libraryId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM assets WHERE library_id=@lib";
            cmd.Parameters.AddWithValue("@lib", libraryId ?? "");
            int n = cmd.ExecuteNonQuery();
            Log.Info($"[catalog] deleted {n} assets of library {libraryId}");
        }, ct);

    // ==================================================================== 查询

    public Task<IReadOnlyList<MediaAsset>> QueryAsync(AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(() =>
        {
            var w = BuildWhere(query);
            var sql = new StringBuilder()
                .Append("SELECT ").Append(AssetMapper.SelectList).Append(" FROM assets")
                .Append(w.Sql)
                .Append(OrderSql(query))
                .Append(LimitSql(query));
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql.ToString();
            BindArgs(cmd, w);
            using var r = cmd.ExecuteReader();
            var list = new List<MediaAsset>(Math.Min(query.Limit > 0 ? query.Limit : 512, 4096));
            while (r.Read()) list.Add(AssetMapper.Read(r));
            return (IReadOnlyList<MediaAsset>)list;
        }, ct);
    }

    public Task<int> CountAsync(AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(() =>
        {
            var w = BuildWhere(query);
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM assets" + w.Sql;
            BindArgs(cmd, w);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }, ct);
    }

    public Task<int> CountAllAsync(CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM assets";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }, ct);

    public Task<FacetCounts> GetFacetsAsync(AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(() =>
        {
            var w = BuildWhere(query);
            var f = new FacetCounts();

            // 一次连接内连续执行若干 GROUP BY（每个都是索引扫描，10k 行总耗时 < 20ms）
            (long total, long bytes) = Scalar2(
                "SELECT COUNT(*), COALESCE(SUM(size_bytes),0) FROM assets" + w.Sql, w);
            f.Total = (int)total;
            f.TotalBytes = bytes;

            f.RawCount = (int)Scalar1("SELECT COUNT(*) FROM assets" + w.Sql + AndWhere(w, "raw_format<>0"), w);
            f.LogCount = (int)Scalar1("SELECT COUNT(*) FROM assets" + w.Sql + AndWhere(w, "is_log=1"), w);
            f.GpsCount = (int)Scalar1("SELECT COUNT(*) FROM assets" + w.Sql +
                                      AndWhere(w, "latitude IS NOT NULL AND longitude IS NOT NULL"), w);
            f.OriginUnsetCount = (int)Scalar1(
                "SELECT COUNT(*) FROM assets" + w.Sql + AndWhere(w, "(origin IS NULL OR origin='')"), w);
            f.ThemeUnsetCount = (int)Scalar1(
                "SELECT COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "(theme IS NULL OR theme='') AND (themes_json IS NULL OR themes_json='' OR themes_json='[]')"), w);
            f.ToneUnsetCount = (int)Scalar1(
                "SELECT COUNT(*) FROM assets" + w.Sql + AndWhere(w, "(tone IS NULL OR tone='')"), w);

            f.UnclassifiedCount = (int)Scalar1(
                "SELECT COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "(theme IS NULL OR theme='') AND (tone IS NULL OR tone='')"), w);

            f.ByKind = GroupText("kind", "SELECT kind, COUNT(*) FROM assets" + w.Sql + " GROUP BY kind", w,
                v => ((MediaKind)int.Parse(v)).ToString());
            f.ByFormat = GroupText("format_code",
                "SELECT format_code, COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "format_code IS NOT NULL AND format_code<>''") + " GROUP BY format_code", w);
            f.ByDevice = GroupText("device_key",
                "SELECT device_key, COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "device_key IS NOT NULL AND device_key<>''") + " GROUP BY device_key", w);
            f.ByCity = GroupText("city",
                "SELECT city, COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "city IS NOT NULL AND city<>''") + " GROUP BY city", w);
            // 多主题：主主题 + themes_json 里的附加主题一起计数（同一个值不会重复计同一张）
            var themeSql =
                "SELECT v, COUNT(DISTINCT id) FROM (" +
                "SELECT assets.id AS id, assets.theme AS v FROM assets" + w.Sql +
                AndWhere(w, "assets.theme IS NOT NULL AND assets.theme<>''") +
                " UNION ALL " +
                "SELECT assets.id AS id, je.value AS v FROM assets, json_each(CASE WHEN json_valid(assets.themes_json) THEN assets.themes_json ELSE '[]' END) je" +
                w.Sql + AndWhere(w, "je.value IS NOT NULL AND je.value<>''") +
                ") GROUP BY v";
            f.ByTheme = GroupText("theme", themeSql, w);
            f.ByTone = GroupText("tone",
                "SELECT tone, COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "tone IS NOT NULL AND tone<>''") + " GROUP BY tone", w);
            f.ByYear = GroupText("year",
                "SELECT substr(captured_day,1,4), COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "captured_day IS NOT NULL AND length(captured_day)>=4") +
                " GROUP BY substr(captured_day,1,4)", w);
            f.ByPostProcess = GroupText("post_process",
                "SELECT post_process, COUNT(*) FROM assets" + w.Sql + " GROUP BY post_process", w,
                v => Vocabularies.PostProcessId((PostProcessState)int.Parse(v)));
            // 自定义类别分面：维度 → 值 → 计数（只统计当前筛选条件内的素材）
            var customSql = "SELECT acv.dimension, acv.value, COUNT(*) FROM asset_custom acv " +
                            "WHERE acv.asset_id IN (SELECT assets.id FROM assets" + w.Sql + ") " +
                            "GROUP BY acv.dimension, acv.value";
            f.ByCustom = GroupText2(customSql, w);

            f.ByOrigin = GroupText("origin",
                "SELECT origin, COUNT(*) FROM assets" + w.Sql + AndWhere(w, "origin IS NOT NULL AND origin<>''") +
                " GROUP BY origin", w);

            // 影像合集分面：合集 id → 当前筛选范围内的素材数
            var collectionSql = "SELECT ca.collection_id, COUNT(*) FROM collection_assets ca " +
                                "WHERE ca.asset_id IN (SELECT assets.id FROM assets" + w.Sql + ") " +
                                "GROUP BY ca.collection_id";
            f.ByCollection = GroupText("collection_id", collectionSql, w);
            f.CollectionUnsetCount = (int)Scalar1(
                "SELECT COUNT(*) FROM assets" + w.Sql +
                AndWhere(w, "NOT EXISTS(SELECT 1 FROM collection_assets ca WHERE ca.asset_id = assets.id)"), w);

            // 合并组分面：合并组 id → 组内素材数
            var mergeSql = "SELECT mg.group_id, COUNT(*) FROM merge_group_assets mg " +
                           "WHERE mg.asset_id IN (SELECT assets.id FROM assets" + w.Sql + ") " +
                           "GROUP BY mg.group_id";
            f.ByMergeGroup = GroupText("group_id", mergeSql, w);

            f.ByPick = GroupText("is_pick",
                "SELECT is_pick, COUNT(*) FROM assets" + w.Sql + " GROUP BY is_pick", w,
                v => v switch { "1" => "picked", "0" => "rejected", _ => "unset" });
            return f;
        }, ct);
    }

    public Task<IReadOnlyList<TimelineBucket>> GetTimelineAsync(AssetQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(() =>
        {
            var w = BuildWhere(query);
            var sb = new StringBuilder();
            sb.Append("SELECT captured_day, COUNT(*), ");
            sb.Append("SUM(CASE WHEN kind=1 THEN 1 ELSE 0 END), ");
            sb.Append("SUM(CASE WHEN kind=2 THEN 1 ELSE 0 END), ");
            sb.Append("SUM(CASE WHEN kind=3 THEN 1 ELSE 0 END) ");
            sb.Append("FROM assets").Append(w.Sql);
            sb.Append(w.Sql.Length == 0 ? " WHERE " : " AND ");
            sb.Append("captured_day IS NOT NULL ");
            sb.Append("GROUP BY captured_day ORDER BY captured_day DESC");

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sb.ToString();
            BindArgs(cmd, w);
            using var r = cmd.ExecuteReader();
            var list = new List<TimelineBucket>();
            while (r.Read())
            {
                if (!DateOnly.TryParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day)) continue;
                list.Add(new TimelineBucket
                {
                    Day = day,
                    Count = r.GetInt32(1),
                    ImageCount = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    VideoCount = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    AudioCount = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                });
            }
            return (IReadOnlyList<TimelineBucket>)list;
        }, ct);
    }

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(string field, string? libraryId, CancellationToken ct = default)
    {
        var f = (field ?? "").Trim();
        return RunAsync(() =>
        {
            if (f.Equals("keywords", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("keyword", StringComparison.OrdinalIgnoreCase))
                return DistinctKeywords(libraryId);

            var w = new Where();
            if (!string.IsNullOrWhiteSpace(libraryId))
                w.And("library_id = @w_lib", "@w_lib", libraryId);

            switch (f.ToLowerInvariant())
            {
                case "kind":
                    return (IReadOnlyList<string>)GroupText("kind",
                        "SELECT kind, COUNT(*) FROM assets" + w.Sql + " GROUP BY kind", w,
                        v => ((MediaKind)int.Parse(v)).ToString()).Keys.ToList();
                case "postprocess":
                case "post_process":
                    return (IReadOnlyList<string>)GroupText("post_process",
                        "SELECT post_process, COUNT(*) FROM assets" + w.Sql + " GROUP BY post_process", w,
                        v => Vocabularies.PostProcessId((PostProcessState)int.Parse(v))).Keys.ToList();
                case "year":
                    return (IReadOnlyList<string>)GroupText("year",
                        "SELECT substr(captured_day,1,4), COUNT(*) FROM assets" + w.Sql +
                        AndWhere(w, "captured_day IS NOT NULL AND length(captured_day)>=4") +
                        " GROUP BY substr(captured_day,1,4)", w).Keys.ToList();
            }

            var col = FieldToColumn(f);
            if (col is null) return (IReadOnlyList<string>)Array.Empty<string>();

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {col} FROM assets" + w.Sql +
                              $" AND ({col} IS NOT NULL AND {col}<>'') ORDER BY {col} COLLATE NOCASE LIMIT 5000";
            if (w.Sql.Length == 0)
                cmd.CommandText = $"SELECT DISTINCT {col} FROM assets WHERE {col} IS NOT NULL AND {col}<>'' ORDER BY {col} COLLATE NOCASE LIMIT 5000";
            BindArgs(cmd, w);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            return (IReadOnlyList<string>)list;
        }, ct);
    }

    private IReadOnlyList<string> DistinctKeywords(string? libraryId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn!.CreateCommand();
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            cmd.CommandText = "SELECT keywords_json FROM assets WHERE keywords_json<>'[]'";
        }
        else
        {
            cmd.CommandText = "SELECT keywords_json FROM assets WHERE library_id=@lib AND keywords_json<>'[]'";
            cmd.Parameters.AddWithValue("@lib", libraryId);
        }
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            foreach (var k in AssetMapper.JsonToKeywords(r.GetString(0))) set.Add(k);
        }
        var list = set.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ==================================================================== 合并组

    public Task<IReadOnlyList<MergeGroup>> GetMergeGroupsAsync(CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                SELECT g.id, g.name, g.cover_asset_id, g.created_at, g.sort_order, COUNT(a.asset_id)
                FROM merge_groups g
                LEFT JOIN merge_group_assets a ON a.group_id = g.id
                GROUP BY g.id
                ORDER BY g.sort_order, g.created_at, g.name";
            using var r = cmd.ExecuteReader();
            var list = new List<MergeGroup>();
            while (r.Read())
            {
                list.Add(new MergeGroup
                {
                    Id = r.GetString(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    CoverAssetId = r.IsDBNull(2) ? null : r.GetString(2),
                    CreatedAt = r.IsDBNull(3) || !DateTimeOffset.TryParse(r.GetString(3), out var created)
                        ? DateTimeOffset.Now : created,
                    SortOrder = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    AssetCount = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                });
            }
            return (IReadOnlyList<MergeGroup>)list;
        }, ct);

    public Task<MergeGroup> CreateMergeGroupAsync(string name, IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var ids = (assetIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            var group = new MergeGroup
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = string.IsNullOrWhiteSpace(name) ? $"合并组 {DateTime.Now:MMdd-HHmm}" : name.Trim(),
                CreatedAt = DateTimeOffset.Now,
                AssetCount = ids.Count,
                CoverAssetId = ids.FirstOrDefault(),
            };

            using var tx = _conn!.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO merge_groups(id, name, cover_asset_id, created_at, sort_order)
                    VALUES(@id, @name, @cover, @created,
                           COALESCE((SELECT MAX(sort_order) + 1 FROM merge_groups), 0))";
                cmd.Parameters.AddWithValue("@id", group.Id);
                cmd.Parameters.AddWithValue("@name", group.Name);
                cmd.Parameters.AddWithValue("@cover", (object?)group.CoverAssetId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@created", group.CreatedAt.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO merge_group_assets(group_id, asset_id, position, added_at) VALUES(@g, @a, @p, @t)";
                cmd.Parameters.AddWithValue("@g", group.Id);
                var pAsset = cmd.Parameters.Add("@a", SqliteType.Text);
                var pPos = cmd.Parameters.Add("@p", SqliteType.Integer);
                cmd.Parameters.AddWithValue("@t", DateTimeOffset.Now.ToString("O"));
                for (int i = 0; i < ids.Count; i++)
                {
                    pAsset.Value = ids[i];
                    pPos.Value = i;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return group;
        }, ct);

    public Task RenameMergeGroupAsync(string groupId, string name, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(groupId)) return;
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE merge_groups SET name=@name WHERE id=@id";
            cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(name) ? "未命名合并组" : name.Trim());
            cmd.Parameters.AddWithValue("@id", groupId);
            cmd.ExecuteNonQuery();
        }, ct);

    public Task DeleteMergeGroupAsync(string groupId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(groupId)) return;
            using var tx = _conn!.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM merge_group_assets WHERE group_id=@id";
                del.Parameters.AddWithValue("@id", groupId);
                del.ExecuteNonQuery();
            }
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM merge_groups WHERE id=@id";
                del.Parameters.AddWithValue("@id", groupId);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }, ct);

    public Task SetMergeGroupCoverAsync(string groupId, string? assetId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(groupId)) return;
            using var cmd = _conn!.CreateCommand();
            if (string.IsNullOrWhiteSpace(assetId))
            {
                cmd.CommandText = @"
                    UPDATE merge_groups SET cover_asset_id =
                        (SELECT asset_id FROM merge_group_assets WHERE group_id=@id ORDER BY position, added_at LIMIT 1)
                    WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", groupId);
            }
            else
            {
                cmd.CommandText = @"
                    UPDATE merge_groups SET cover_asset_id=@a
                    WHERE id=@id AND EXISTS(SELECT 1 FROM merge_group_assets WHERE group_id=@id AND asset_id=@a)";
                cmd.Parameters.AddWithValue("@id", groupId);
                cmd.Parameters.AddWithValue("@a", assetId);
            }
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<int> AddToMergeGroupAsync(string groupId, IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(groupId) || assetIds is null) return 0;
            var ids = assetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0) return 0;

            int maxPos;
            using (var read = _conn!.CreateCommand())
            {
                read.CommandText = "SELECT COALESCE(MAX(position), -1) FROM merge_group_assets WHERE group_id=@g";
                read.Parameters.AddWithValue("@g", groupId);
                maxPos = Convert.ToInt32(read.ExecuteScalar() ?? -1);
            }

            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO merge_group_assets(group_id, asset_id, position, added_at) VALUES(@g, @a, @p, @t)";
            cmd.Parameters.AddWithValue("@g", groupId);
            var pAsset = cmd.Parameters.Add("@a", SqliteType.Text);
            var pPos = cmd.Parameters.Add("@p", SqliteType.Integer);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.Now.ToString("O"));
            int added = 0;
            foreach (var id in ids)
            {
                pAsset.Value = id;
                pPos.Value = ++maxPos;
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        }, ct);

    public Task<int> RemoveFromMergeGroupAsync(string groupId, IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(groupId) || assetIds is null) return 0;
            var ids = assetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0) return 0;

            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            var sb = new StringBuilder("DELETE FROM merge_group_assets WHERE group_id=@g AND asset_id IN (");
            cmd.Parameters.AddWithValue("@g", groupId);
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

    public Task<IReadOnlyDictionary<string, string>> GetMergeMembershipAsync(
        IEnumerable<string> assetIds, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (assetIds is null) return (IReadOnlyDictionary<string, string>)map;
            var ids = assetIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count == 0) return (IReadOnlyDictionary<string, string>)map;

            for (int start = 0; start < ids.Count; start += InClauseChunk)
            {
                var chunk = ids.Skip(start).Take(InClauseChunk).ToList();
                using var cmd = _conn!.CreateCommand();
                var sb = new StringBuilder("SELECT asset_id, group_id FROM merge_group_assets WHERE asset_id IN (");
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("@a").Append(i);
                    cmd.Parameters.AddWithValue("@a" + i, chunk[i]);
                }
                sb.Append(')');
                cmd.CommandText = sb.ToString();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0) && !r.IsDBNull(1)) map[r.GetString(0)] = r.GetString(1);
                }
            }
            return (IReadOnlyDictionary<string, string>)map;
        }, ct);

    public Task<IReadOnlyList<string>> GetMergeGroupAssetIdsAsync(string groupId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(groupId)) return (IReadOnlyList<string>)list;
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT asset_id FROM merge_group_assets WHERE group_id=@g ORDER BY position, added_at";
            cmd.Parameters.AddWithValue("@g", groupId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return (IReadOnlyList<string>)list;
        }, ct);

    // ==================================================================== 影像合集

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

    public Task<IReadOnlyList<(string Id, string FullPath, long SizeBytes, DateTimeOffset FileModified)>>
        ListAllPathsAsync(string? libraryId, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(libraryId)
                ? "SELECT id, full_path, size_bytes, file_modified FROM assets"
                : "SELECT id, full_path, size_bytes, file_modified FROM assets WHERE library_id=@lib";
            if (!string.IsNullOrWhiteSpace(libraryId)) cmd.Parameters.AddWithValue("@lib", libraryId);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, long, DateTimeOffset)>();
            while (r.Read())
            {
                list.Add((
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    r.IsDBNull(3) ? DateTimeOffset.MinValue : AssetMapper.ParseDto(r.GetString(3)) ?? DateTimeOffset.MinValue));
            }
            return (IReadOnlyList<(string Id, string FullPath, long SizeBytes, DateTimeOffset FileModified)>)list;
        }, ct);

    // ==================================================================== 库根

    public Task SaveLibraryRootsAsync(IEnumerable<LibraryRoot> roots, CancellationToken ct = default)
    {
        var list = roots?.Where(r => r is not null).ToList() ?? new List<LibraryRoot>();
        return RunAsync(() =>
        {
            using var tx = _conn!.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM libraries";
                del.ExecuteNonQuery();
            }
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT INTO libraries(id, path, name, include_subfolders, watch_for_changes, added_at," +
                " last_scan_at, asset_count, total_bytes, sort_order) VALUES" +
                " (@id,@path,@name,@sub,@watch,@added,@scan,@count,@bytes,@ord)";
            var ps = ins.Parameters;
            foreach (var name in new[] { "@id", "@path", "@name", "@sub", "@watch", "@added", "@scan", "@count", "@bytes", "@ord" })
                ps.Add(new SqliteParameter(name, DBNull.Value));
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                ct.ThrowIfCancellationRequested();
                ps["@id"].Value = r.Id;
                ps["@path"].Value = r.Path ?? "";
                ps["@name"].Value = r.Name ?? Path.GetFileName(r.Path ?? "") ?? "";
                ps["@sub"].Value = r.IncludeSubfolders ? 1 : 0;
                ps["@watch"].Value = r.WatchForChanges ? 1 : 0;
                ps["@added"].Value = AssetMapper.Dto(r.AddedAt);
                ps["@scan"].Value = r.LastScanAt.HasValue ? AssetMapper.Dto(r.LastScanAt.Value) : DBNull.Value;
                ps["@count"].Value = r.AssetCount;
                ps["@bytes"].Value = r.TotalBytes;
                ps["@ord"].Value = i;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }, ct);
    }

    public Task<IReadOnlyList<LibraryRoot>> LoadLibraryRootsAsync(CancellationToken ct = default) =>
        RunAsync(() =>
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                "SELECT id, path, name, include_subfolders, watch_for_changes, added_at, last_scan_at," +
                " asset_count, total_bytes FROM libraries ORDER BY sort_order";
            using var r = cmd.ExecuteReader();
            var list = new List<LibraryRoot>();
            while (r.Read())
            {
                list.Add(new LibraryRoot
                {
                    Id = r.GetString(0),
                    Path = r.IsDBNull(1) ? "" : r.GetString(1),
                    Name = r.IsDBNull(2) ? "" : r.GetString(2),
                    IncludeSubfolders = r.IsDBNull(3) || r.GetInt64(3) != 0,
                    WatchForChanges = r.IsDBNull(4) || r.GetInt64(4) != 0,
                    AddedAt = r.IsDBNull(5) ? DateTimeOffset.Now : AssetMapper.ParseDto(r.GetString(5)) ?? DateTimeOffset.Now,
                    LastScanAt = r.IsDBNull(6) ? null : AssetMapper.ParseDto(r.GetString(6)),
                    AssetCount = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    TotalBytes = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                });
            }
            return (IReadOnlyList<LibraryRoot>)list;
        }, ct);

    // ==================================================================== 条件构造

    /// <summary>WHERE 构造器：子句与命名参数一起累积，最后统一绑定，避免位置错配。</summary>
    private sealed class Where
    {
        public readonly StringBuilder Sql = new();
        public readonly List<KeyValuePair<string, object?>> Args = new();
        private int _inCounter;

        public void And(string clause) =>
            Sql.Append(Sql.Length == 0 ? " WHERE " : " AND ").Append(clause);

        public void And(string clause, string paramName, object? value)
        {
            And(clause);
            Args.Add(new KeyValuePair<string, object?>(paramName, value));
        }

        public string NextInPrefix() => "@w_in" + _inCounter++;

        public string Prefix => Sql.Length == 0 ? " WHERE " : " AND ";
    }

    private static string AndWhere(Where w, string clause) => w.Prefix + clause;

    private Where BuildWhere(AssetQuery q)
    {
        var w = new Where();
        if (!string.IsNullOrWhiteSpace(q.LibraryId)) w.And("library_id = @w_lib", "@w_lib", q.LibraryId);

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var like = "%" + AssetMapper.EscapeLike(q.Text.Trim()) + "%";
            w.And("(file_name LIKE @w_text ESCAPE '\\' OR full_path LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(title,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(caption,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(keywords_flat,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(device_key,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(city,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(creator,'') LIKE @w_text ESCAPE '\\'" +
                  " OR IFNULL(ai_subject,'') LIKE @w_text ESCAPE '\\')", "@w_text", like);
        }

        if (q.Kinds.Count > 0) AddIn(w, "kind", q.Kinds.Cast<object?>().ToList());
        if (q.Formats.Count > 0) AddIn(w, "format_code", q.Formats.Cast<object?>().ToList());
        if (q.Devices.Count > 0) AddIn(w, "device_key", q.Devices.Cast<object?>().ToList());
        if (q.Cities.Count > 0) AddIn(w, "city", q.Cities.Cast<object?>().ToList());
        // 主题/影调支持"未填写"（__unset__）伪选项：选它 = 只要还没分类的素材
        ApplyThemeFilter(w, q.Themes);
        ApplyCollectionFilter(w, q.Collections);
        ApplyMergeGroupFilter(w, q.MergeGroups);
        AddInOrUnset(w, "tone", q.Tones);
        if (q.ColorLabels.Count > 0) AddIn(w, "color_label", q.ColorLabels.Cast<object?>().ToList());
        if (q.RawFormats.Count > 0) AddIn(w, "raw_format", q.RawFormats.Cast<object?>().ToList());

        int kwIndex = 0;
        foreach (var kw in q.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var v = kw.Trim().ToLowerInvariant().Replace('|', ' ');
            w.And("keywords_flat LIKE @w_kw" + kwIndex + " ESCAPE '\\'",
                  "@w_kw" + kwIndex, "%|" + AssetMapper.EscapeLike(v) + "|%");
            kwIndex++;
        }

        // 自定义类别：同一维度内 OR，不同维度之间 AND
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
                var pv = "@w_cv" + w.NextInPrefix().TrimStart('@') + "_" + i;
                sbCustom.Append(pv);
                w.Args.Add(new KeyValuePair<string, object?>(pv, values[i]));
            }
            sbCustom.Append("))");
            w.And(sbCustom.ToString());
        }

        AddInOrUnset(w, "origin", q.Origins);

        if (q.IsRaw is { } isRaw) w.And(isRaw ? "raw_format <> 0" : "raw_format = 0");
        if (q.Colored is { } colored)
        {
            // "调色"包含两种情况：标记为已后期，或已经套用了 LUT
            w.And(colored
                ? "(post_process = 2 OR (lut_id IS NOT NULL AND lut_id <> ''))"
                : "(COALESCE(post_process, 0) <> 2 AND (lut_id IS NULL OR lut_id = ''))");
        }
        if (q.IsLog is { } isLog) w.And(isLog ? "is_log = 1" : "(is_log IS NULL OR is_log = 0)");
        if (q.HasGps is { } gps)
            w.And(gps ? "latitude IS NOT NULL AND longitude IS NOT NULL"
                      : "(latitude IS NULL OR longitude IS NULL)");
        if (q.IsPick is { } pick) w.And(pick ? "is_pick = 1" : "(is_pick IS NULL OR is_pick = 0)");
        if (q.OnlyRejected) w.And("is_pick = 0");
        if (q.PostProcess is { } pp) w.And("post_process = @w_pp", "@w_pp", (int)pp);
        if (q.MinRating is { } minR) w.And("rating >= @w_minr", "@w_minr", minR);
        if (q.MaxRating is { } maxR) w.And("rating <= @w_maxr", "@w_maxr", maxR);
        if (q.CapturedFrom is { } from) w.And("captured_ms >= @w_from", "@w_from", AssetMapper.Ms(from));
        if (q.CapturedTo is { } to) w.And("captured_ms <= @w_to", "@w_to", AssetMapper.Ms(to));
        if (q.OnlyUnclassified)
            w.And("(theme IS NULL OR theme='') AND (tone IS NULL OR tone='')");
        if (q.OnlyProblems)
            w.And("((last_error IS NOT NULL AND last_error<>'') OR metadata_parsed_at IS NULL)");

        // JPG+RAW 分组折叠：网格里一张照片只出现一次（取组代表 = JPG）。
        // 例外：筛「只看 RAW」时必须放行非代表的 RAW 行，否则 RAW 全被折叠掉、结果永远是 0 条。
        if (q.CollapseGroups && q.IsRaw != true) w.And("is_group_cover = 1");
        return w;
    }

    /// <summary>合并组筛选：命中任意选中合并组的素材（多选 = 并集）。</summary>
    private static void ApplyMergeGroupFilter(Where w, List<string> selected)
    {
        if (selected.Count == 0) return;
        var prefix = w.NextInPrefix();
        var sb = new StringBuilder("EXISTS(SELECT 1 FROM merge_group_assets mg WHERE mg.asset_id = assets.id AND mg.group_id IN (");
        for (int i = 0; i < selected.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var name = prefix + "_m" + i;
            sb.Append(name);
            w.Args.Add(new KeyValuePair<string, object?>(name, selected[i]));
        }
        sb.Append("))");
        w.And(sb.ToString());
    }

    /// <summary>
    /// 影像合集筛选：命中任意选中合集的素材（多选 = 并集）；`__unset__` = 没加入任何合集。
    /// </summary>
    private static void ApplyCollectionFilter(Where w, List<string> selected)
    {
        if (selected.Count == 0) return;
        var values = selected.Where(v => v != "__unset__").Cast<object?>().ToList();
        bool withUnset = values.Count != selected.Count;

        if (values.Count == 0)
        {
            if (withUnset) w.And("NOT EXISTS(SELECT 1 FROM collection_assets ca WHERE ca.asset_id = assets.id)");
            return;
        }

        var prefix = w.NextInPrefix();
        var sb = new StringBuilder();
        if (withUnset) sb.Append('(');
        sb.Append("EXISTS(SELECT 1 FROM collection_assets ca WHERE ca.asset_id = assets.id AND ca.collection_id IN (");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var name = prefix + "_c" + i;
            sb.Append(name);
            w.Args.Add(new KeyValuePair<string, object?>(name, values[i]));
        }
        sb.Append("))");
        if (withUnset) sb.Append(" OR NOT EXISTS(SELECT 1 FROM collection_assets ca2 WHERE ca2.asset_id = assets.id)");
        if (withUnset) sb.Append(')');
        w.And(sb.ToString());
    }

    /// <summary>
    /// 主题筛选：命中**主主题**或 `themes_json` 里的任意附加主题；
    /// 选「未填写」= 既没有主主题也没有多主题。
    /// </summary>
    private static void ApplyThemeFilter(Where w, List<string> selected)
    {
        if (selected.Count == 0) return;

        var values = selected.Where(v => v != "__unset__").Cast<object?>().ToList();
        bool withUnset = values.Count != selected.Count;

        if (values.Count == 0)
        {
            if (withUnset)
                w.And("((theme IS NULL OR theme='') AND (themes_json IS NULL OR themes_json='' OR themes_json='[]'))");
            return;
        }

        var prefix = w.NextInPrefix();
        var names = new List<string>();
        var sb = new StringBuilder();
        sb.Append("(theme IN (");
        for (int i = 0; i < values.Count; i++)
        {
            var name = prefix + "_t" + i;
            names.Add(name);
            if (i > 0) sb.Append(',');
            sb.Append(name);
            w.Args.Add(new KeyValuePair<string, object?>(name, values[i]));
        }
        sb.Append(") OR EXISTS(SELECT 1 FROM json_each(CASE WHEN json_valid(themes_json) THEN themes_json ELSE '[]' END) WHERE value IN (");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(names[i]);
        }
        sb.Append(')');
        if (withUnset)
            sb.Append(") OR ((theme IS NULL OR theme='') AND (themes_json IS NULL OR themes_json='' OR themes_json='[]'))");
        else
            sb.Append(')');
        sb.Append(')');
        w.And(sb.ToString());
    }

    /// <summary>IN (...) 之外再并入"该列为空"，用于"未填写"伪选项。</summary>
    private static void AddInOrUnset(Where w, string column, List<string> selected)
    {
        if (selected.Count == 0) return;

        var values = selected.Where(v => v != "__unset__").Cast<object?>().ToList();
        bool withUnset = values.Count != selected.Count;

        if (values.Count == 0)
        {
            if (withUnset)
                w.And("(" + column + " IS NULL OR " + column + "='')");
            return;
        }

        var prefix = w.NextInPrefix();
        var sb = new StringBuilder();
        if (withUnset) sb.Append('(');
        sb.Append(column).Append(" IN (");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var name = prefix + "_" + i;
            sb.Append(name);
            w.Args.Add(new KeyValuePair<string, object?>(name, values[i]));
        }
        sb.Append(')');
        if (withUnset) sb.Append(" OR ").Append(column).Append(" IS NULL OR ").Append(column).Append("='')");
        w.And(sb.ToString());
    }

    private static void AddIn(Where w, string column, List<object?> values)
    {
        if (values.Count == 0) return;
        var prefix = w.NextInPrefix();
        var sb = new StringBuilder(column).Append(" IN (");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var name = prefix + "_" + i;
            sb.Append(name);
            w.Args.Add(new KeyValuePair<string, object?>(name, values[i]));
        }
        sb.Append(')');
        w.And(sb.ToString());
    }

    private static void BindArgs(SqliteCommand cmd, Where w)
    {
        foreach (var kv in w.Args)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
    }

    private static string OrderSql(AssetQuery q)
    {
        string col = (q.SortBy ?? "").ToLowerInvariant() switch
        {
            "filename" or "file_name" => "file_name COLLATE NOCASE",
            "filemodified" or "file_modified" => "file_modified_ms",
            "size" or "sizebytes" => "size_bytes",
            "rating" => "rating",
            "indexedat" or "indexed_at" => "indexed_ms",
            "duration" => "duration_seconds",
            "quality" => "quality_score",
            _ => "captured_ms",
        };
        string dir = q.Descending ? "DESC" : "ASC";
        return $" ORDER BY {col} {dir}, id {dir}";
    }

    private static string LimitSql(AssetQuery q)
    {
        int limit = q.Limit;
        int offset = Math.Max(0, q.Offset);
        if (limit > 0) return $" LIMIT {limit} OFFSET {offset}";
        return offset > 0 ? $" LIMIT -1 OFFSET {offset}" : "";
    }

    private static string? FieldToColumn(string field) => field.ToLowerInvariant() switch
    {
        "format" or "formatcode" or "format_code" => "format_code",
        "device" or "devicekey" or "device_key" => "device_key",
        "city" => "city",
        "province" => "province",
        "country" => "country",
        "theme" => "theme",
        "tone" => "tone",
        "colorlabel" or "color_label" => "color_label",
        "library" or "libraryid" or "library_id" => "library_id",
        "cameramake" or "camera_make" => "camera_make",
        "cameramodel" or "camera_model" => "camera_model",
        "devicemodel" => "camera_model",
        "codec" or "videocodec" or "video_codec" => "video_codec",
        "lens" or "lensmodel" or "lens_model" => "lens_model",
        _ => null,
    };

    // ==================================================================== 分面辅助

    /// <summary>三维分组：dimension → value → count（自定义类别分面用）。</summary>
    private Dictionary<string, Dictionary<string, int>> GroupText2(string sql, Where w)
    {
        var dict = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        BindArgs(cmd, w);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            var dimension = r.GetString(0);
            var value = r.GetString(1);
            if (string.IsNullOrWhiteSpace(dimension) || string.IsNullOrWhiteSpace(value)) continue;
            int count = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));

            if (!dict.TryGetValue(dimension, out var values))
                dict[dimension] = values = new Dictionary<string, int>(StringComparer.Ordinal);
            values.TryGetValue(value, out int prev);
            values[value] = prev + count;
        }
        return dict;
    }

    private Dictionary<string, int> GroupText(string keyName, string sql, Where w, Func<string, string>? mapKey = null)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        BindArgs(cmd, w);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string key;
            if (r.IsDBNull(0))
            {
                if (mapKey is null) continue;
                key = mapKey("");
            }
            else
            {
                key = r.GetValue(0) is long l ? l.ToString() : r.GetString(0);
                if (mapKey is not null) key = mapKey(key);
            }
            if (string.IsNullOrEmpty(key)) continue;
            int count = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
            dict.TryGetValue(key, out int prev);
            dict[key] = prev + count;
        }
        return dict;
    }

    private long Scalar1(string sql, Where w)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        BindArgs(cmd, w);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private (long A, long B) Scalar2(string sql, Where w)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        BindArgs(cmd, w);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0);
        return (r.IsDBNull(0) ? 0 : Convert.ToInt64(r.GetValue(0)),
                r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1)));
    }

    // ==================================================================== misc

    private static List<string> Clean(IEnumerable<string>? values)
    {
        if (values is null) return new List<string>();
        var list = new List<string>();
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v)) list.Add(v);
        return list;
    }

    private static IEnumerable<List<string>> Chunk(List<string> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}