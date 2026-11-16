using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Scanning;

/// <summary>
/// 库管理 + 扫描编排：扫描 → 批量 upsert → 清理已删除文件 → 库统计写回。
/// 库根列表持久化在 asset store（libraries 表），并在 settings.Libraries 保持一份镜像，
/// 这样 App 启动时无论从哪边先读到都能恢复。
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private const int UpsertChunk = 500;

    private readonly IAssetStore _store;
    private readonly ILibraryScanner _scanner;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<LibraryRoot> _roots = Array.Empty<LibraryRoot>();
    private bool _initialized;

    public LibraryService(IAssetStore store, ILibraryScanner scanner, ISettingsService settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // ==================================================================== 初始化 / 读取

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _store.InitializeAsync(ct).ConfigureAwait(false);

            var roots = await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false);
            if (roots.Count == 0)
            {
                var fromSettings = SafeSettingsLibraries();
                if (fromSettings.Count > 0)
                {
                    await _store.SaveLibraryRootsAsync(fromSettings, ct).ConfigureAwait(false);
                    roots = fromSettings;
                    Log.Info($"[library] seeded {roots.Count} roots from settings");
                }
            }
            _roots = roots;
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LibraryRoot>> GetLibrariesAsync(CancellationToken ct = default)
    {
        try
        {
            var roots = await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false);
            if (roots.Count > 0)
            {
                _roots = roots;
                return roots;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[library] load roots failed: {ex.Message}");
        }
        return SafeSettingsLibraries();
    }

    // ==================================================================== 增删

    public async Task<LibraryRoot> AddLibraryAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("媒体库路径不能为空", nameof(folderPath));

        var path = PathUtil.Normalize(folderPath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"目录不存在：{path}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var roots = (await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false)).ToList();

            var existing = roots.FirstOrDefault(r => PathUtil.Normalize(r.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            var root = new LibraryRoot
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Path = path,
                Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                IncludeSubfolders = true,
                WatchForChanges = SafeSettings().WatchLibraries,
                AddedAt = DateTimeOffset.Now,
            };
            if (string.IsNullOrWhiteSpace(root.Name)) root.Name = path;

            roots.Add(root);
            await _store.SaveLibraryRootsAsync(roots, ct).ConfigureAwait(false);
            _roots = roots;
            SyncSettings(roots);
            Log.Info($"[library] added {root.Name} ({path})");
            return root;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var roots = (await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false)).ToList();
            roots.RemoveAll(r => string.Equals(r.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            await _store.DeleteByLibraryAsync(libraryId, ct).ConfigureAwait(false);
            await _store.SaveLibraryRootsAsync(roots, ct).ConfigureAwait(false);
            _roots = roots;
            SyncSettings(roots);
            Log.Info($"[library] removed {libraryId}");
        }
        finally { _gate.Release(); }
    }

    public async Task<LibraryRelocateResult> RelocateLibraryAsync(
        string libraryId,
        string newFolderPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
            throw new ArgumentException("媒体库 id 不能为空", nameof(libraryId));

        var newPath = PathUtil.Normalize(newFolderPath);
        if (!Directory.Exists(newPath))
            throw new DirectoryNotFoundException($"新目录不存在：{newPath}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var roots = (await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false)).ToList();
            var root = roots.FirstOrDefault(r => string.Equals(r.Id, libraryId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("找不到这个媒体库");

            var oldPath = PathUtil.Normalize(root.Path);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return new LibraryRelocateResult(0, 0, oldPath, newPath);

            var oldPrefix = oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

            var entries = await _store.ListAllPathsAsync(libraryId, ct).ConfigureAwait(false);
            int updated = 0, missing = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var full = PathUtil.Normalize(entry.FullPath);
                    var relative = full.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
                        ? full[oldPrefix.Length..]
                        : Path.GetFileName(full);

                    var candidate = Path.Combine(newPath, relative);
                    if (!File.Exists(candidate))
                    {
                        // 相对结构变了（比如用户在子目录里整理过）：按文件名兜底找一次
                        var fallback = FindFileByName(newPath, Path.GetFileName(full));
                        if (fallback is null)
                        {
                            missing++;
                            continue;
                        }
                        candidate = fallback;
                    }

                    var newRelative = Path.GetRelativePath(newPath, candidate).Replace('\\', '/');
                    await _store.UpdateAssetPathAsync(entry.Id, candidate, newRelative, null, ct).ConfigureAwait(false);
                    updated++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    missing++;
                    Log.Warn($"[library] relocate {entry.FullPath} failed: {ex.Message}");
                }
            }

            root.Path = newPath;
            var name = Path.GetFileName(newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name)) root.Name = name;
            await _store.SaveLibraryRootsAsync(roots, ct).ConfigureAwait(false);
            _roots = roots;
            SyncSettings(roots);

            Log.Info($"[library] relocated {libraryId}: {oldPath} → {newPath}（修复 {updated}，缺失 {missing}）");
            return new LibraryRelocateResult(updated, missing, oldPath, newPath);
        }
        finally { _gate.Release(); }
    }

    private static string? FindFileByName(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                return file;
        }
        catch
        {
            // 权限/IO 问题就当作找不到
        }
        return null;
    }

    // ==================================================================== 扫描入库

    public async Task<ScanProgress> ScanLibraryAsync(
        LibraryRoot root,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new ScanOptions();

        var final = new ScanProgress { Phase = ScanPhase.Preparing, CurrentPath = root.Path };
        void Report(ScanProgress p) => SafeReport(progress, p);

        try
        {
            Report(new ScanProgress { Phase = ScanPhase.Preparing, CurrentPath = root.Path, Message = "准备扫描" });

            // ---------- 1) 扫描（不落库） ----------
            var assets = await _scanner.CollectAsync(root, options, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // ---------- 2) 入库（分块事务 upsert） ----------
            // 关键：重扫不能抹掉用户/AI 已经做的分类。扫描产出的是"元数据视图"，
            // 分类字段（主题/影调/评分/色标/选废/关键词/AI 描述/标签来源）属于"人的视图"，
            // 必须从旧记录合并过来；元数据字段仍然以本次扫描为准。
            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            var priorByPath = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var existing = await _store.GetIdsByPathsAsync(assets.Select(a => a.FullPath), ct).ConfigureAwait(false);
                foreach (var id in existing) existingIds.Add(id);

                var prior = await _store.QueryAsync(new AssetQuery { LibraryId = root.Id, Limit = 0 }, ct).ConfigureAwait(false);
                foreach (var p in prior) priorByPath[PathUtil.Normalize(p.FullPath)] = p;
            }
            catch (Exception ex) { Log.Warn($"[library] existing id lookup failed: {ex.Message}"); }

            int merged = 0;
            foreach (var a in assets)
            {
                if (priorByPath.TryGetValue(PathUtil.Normalize(a.FullPath), out var old) && MergeHumanState(a, old))
                    merged++;
            }
            if (merged > 0) Log.Info($"[library] preserved classification for {merged} assets");

            int added = 0, updated = 0, failed = 0;
            int total = assets.Count;
            int processed = 0;

            Report(new ScanProgress
            {
                Phase = ScanPhase.Indexing,
                Total = total,
                MediaFound = total,
                Message = "写入索引库",
            });

            for (int i = 0; i < assets.Count; i += UpsertChunk)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = assets.Skip(i).Take(UpsertChunk).ToList();
                try
                {
                    await _store.UpsertManyAsync(chunk, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed += chunk.Count;
                    Log.Warn($"[library] upsert chunk failed: {ex.Message}");
                }

                foreach (var a in chunk)
                {
                    if (existingIds.Contains(a.Id)) updated++;
                    else added++;
                    if (!string.IsNullOrEmpty(a.LastError)) failed++;
                }
                processed += chunk.Count;
                Report(new ScanProgress
                {
                    Phase = ScanPhase.Indexing,
                    Total = total,
                    MediaFound = total,
                    Processed = Math.Min(processed, total),
                    Added = added,
                    Updated = updated,
                    Failed = failed,
                    Message = "写入索引库",
                });
            }

            // ---------- 3) 清理已删除文件 ----------
            int removed = 0;
            try
            {
                var scanned = new HashSet<string>(
                    assets.Select(a => PathUtil.Normalize(a.FullPath)),
                    StringComparer.OrdinalIgnoreCase);
                var known = await _store.ListAllPathsAsync(root.Id, ct).ConfigureAwait(false);
                var staleIds = known
                    .Where(k => !scanned.Contains(PathUtil.Normalize(k.FullPath)))
                    .Select(k => k.Id)
                    .ToList();
                if (staleIds.Count > 0)
                {
                    await _store.DeleteAsync(staleIds, ct).ConfigureAwait(false);
                    removed = staleIds.Count;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"[library] purge removed files failed: {ex.Message}"); }

            // ---------- 4) 库统计写回 ----------
            root.LastScanAt = DateTimeOffset.Now;
            root.AssetCount = assets.Count;
            root.TotalBytes = assets.Sum(a => a.SizeBytes);
            await SaveRootsQuietly(root, ct).ConfigureAwait(false);

            final = new ScanProgress
            {
                Phase = ScanPhase.Completed,
                Total = total,
                MediaFound = total,
                Processed = total,
                Added = added,
                Updated = updated,
                Removed = removed,
                Failed = failed,
                Message = $"扫描完成：新增 {added}，更新 {updated}，移除 {removed}",
            };
            Report(final);
            Log.Info($"[library] scan {root.Name}: total={total} added={added} updated={updated} removed={removed} failed={failed}");
        }
        catch (OperationCanceledException)
        {
            final = new ScanProgress
            {
                Phase = ScanPhase.Cancelled,
                CurrentPath = root.Path,
                Added = final.Added,
                Updated = final.Updated,
                Removed = final.Removed,
                Failed = final.Failed,
                Message = "扫描已取消",
            };
            Report(final);
        }
        catch (Exception ex)
        {
            Log.Error($"[library] scan failed {root.Path}", ex);
            final = new ScanProgress { Phase = ScanPhase.Failed, CurrentPath = root.Path, Message = ex.Message };
            Report(final);
        }
        return final;
    }

    // ==================================================================== 分类结果合并

    /// <summary>
    /// 把旧记录里"人/AI 产生"的分类状态合并进本次扫描产出的新素材。
    /// 只搬分类字段，不搬元数据（元数据以本次扫描为准）。返回是否合并了任何内容。
    /// </summary>
    private static bool MergeHumanState(MediaAsset fresh, MediaAsset old)
    {
        bool any = false;

        if (string.IsNullOrWhiteSpace(fresh.Theme) && !string.IsNullOrWhiteSpace(old.Theme))
        { fresh.Theme = old.Theme; any = true; }
        if (string.IsNullOrWhiteSpace(fresh.Tone) && !string.IsNullOrWhiteSpace(old.Tone))
        { fresh.Tone = old.Tone; any = true; }
        if (fresh.Rating == 0 && old.Rating > 0) { fresh.Rating = old.Rating; any = true; }
        if (string.IsNullOrWhiteSpace(fresh.ColorLabel) && !string.IsNullOrWhiteSpace(old.ColorLabel))
        { fresh.ColorLabel = old.ColorLabel; any = true; }
        if (fresh.IsPick is null && old.IsPick is not null) { fresh.IsPick = old.IsPick; any = true; }
        if (string.IsNullOrWhiteSpace(fresh.AiSubject) && !string.IsNullOrWhiteSpace(old.AiSubject))
        { fresh.AiSubject = old.AiSubject; any = true; }
        if (string.IsNullOrWhiteSpace(fresh.AiMood) && !string.IsNullOrWhiteSpace(old.AiMood))
        { fresh.AiMood = old.AiMood; any = true; }
        if (fresh.QualityScore is null && old.QualityScore is not null) { fresh.QualityScore = old.QualityScore; any = true; }

        if (old.Keywords.Count > 0 && fresh.Keywords.Count == 0)
        {
            fresh.Keywords = new List<string>(old.Keywords);
            any = true;
        }

        // 后期状态：本次扫描没结论时沿用旧的（用户手勾过的值不能被清掉）
        if (fresh.PostProcess == PostProcessState.Unknown && old.PostProcess != PostProcessState.Unknown)
        {
            fresh.PostProcess = old.PostProcess;
            any = true;
        }

        if (old.Provenance.Count > 0)
        {
            foreach (var kv in old.Provenance)
            {
                if (fresh.Provenance.ContainsKey(kv.Key)) continue;
                fresh.Provenance[kv.Key] = kv.Value.Clone();
                any = true;
            }
        }

        return any;
    }

    // ==================================================================== 清理丢失文件

    public async Task<int> PurgeMissingAsync(string libraryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) return 0;
        try
        {
            var known = await _store.ListAllPathsAsync(libraryId, ct).ConfigureAwait(false);
            var missing = new List<string>();
            long lostBytes = 0;
            foreach (var k in known)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(k.FullPath))
                    {
                        missing.Add(k.Id);
                        lostBytes += k.SizeBytes;
                    }
                }
                catch { missing.Add(k.Id); }
            }

            if (missing.Count == 0) return 0;
            await _store.DeleteAsync(missing, ct).ConfigureAwait(false);

            // 更新库统计
            try
            {
                var roots = (await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false)).ToList();
                var root = roots.FirstOrDefault(r => string.Equals(r.Id, libraryId, StringComparison.OrdinalIgnoreCase));
                if (root is not null)
                {
                    root.AssetCount = Math.Max(0, root.AssetCount - missing.Count);
                    root.TotalBytes = Math.Max(0, root.TotalBytes - lostBytes);
                    await _store.SaveLibraryRootsAsync(roots, ct).ConfigureAwait(false);
                    _roots = roots;
                    SyncSettings(roots);
                }
            }
            catch (Exception ex) { Log.Warn($"[library] purge stats update failed: {ex.Message}"); }

            Log.Info($"[library] purged {missing.Count} missing files of {libraryId}");
            return missing.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[library] purge failed {libraryId}", ex);
            return 0;
        }
    }

    // ==================================================================== 内部

    private async Task SaveRootsQuietly(LibraryRoot updated, CancellationToken ct)
    {
        try
        {
            var roots = (await _store.LoadLibraryRootsAsync(ct).ConfigureAwait(false)).ToList();
            int idx = roots.FindIndex(r => string.Equals(r.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) roots[idx] = updated;
            else roots.Add(updated);
            await _store.SaveLibraryRootsAsync(roots, ct).ConfigureAwait(false);
            _roots = roots;
            SyncSettings(roots);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"[library] save roots failed: {ex.Message}"); }
    }

    private IReadOnlyList<LibraryRoot> SafeSettingsLibraries()
    {
        try
        {
            var list = _settings.Current.Libraries;
            return list is null ? Array.Empty<LibraryRoot>() : list.ToList();
        }
        catch { return Array.Empty<LibraryRoot>(); }
    }

    private AppSettings SafeSettings()
    {
        try { return _settings.Current; } catch { return new AppSettings(); }
    }

    /// <summary>把库根镜像回 settings（App 重启/其他模块读取用）。失败只记日志。</summary>
    private void SyncSettings(IReadOnlyList<LibraryRoot> roots)
    {
        try
        {
            var current = _settings.Current;
            current.Libraries = roots.ToList();
            if (string.IsNullOrWhiteSpace(current.LastLibraryId) && roots.Count > 0)
                current.LastLibraryId = roots[0].Id;
            _ = _settings.SaveAsync();
        }
        catch (Exception ex) { Log.Warn($"[library] sync settings failed: {ex.Message}"); }
    }

    private static void SafeReport(IProgress<ScanProgress>? progress, ScanProgress p)
    {
        try { progress?.Report(p.Clone()); } catch { /* UI 回调异常不能影响扫描 */ }
    }
}