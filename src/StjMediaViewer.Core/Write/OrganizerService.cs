using System.Globalization;
using System.Text.Json;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Write;

/// <summary>
/// 文件整理：按指定分类把**磁盘上的真实文件**（或云端目录里的文件）移动进分类命名的文件夹。
///
/// 设计要点：
///  - 先出计划（源路径 → 目标路径、冲突数、总大小），用户确认后再执行；
///  - 只移动，不删除；重名默认自动加序号；
///  - 边车（.xmp）跟着主文件走；
///  - 移动后同步更新索引库里的物理路径（不用重新扫描）；
///  - 每次整理写一条撤销记录，可以「撤销上次整理」。
/// </summary>
public sealed class OrganizerService
{
    private const string UndoFileName = "organize-undo.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _appDataDir;
    private readonly ICloudSyncService? _cloud;

    public OrganizerService(string? appDataDir = null, ICloudSyncService? cloud = null)
    {
        _appDataDir = appDataDir;
        _cloud = cloud;
    }

    private string UndoPath => Path.Combine(
        string.IsNullOrWhiteSpace(_appDataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewer")
            : _appDataDir,
        UndoFileName);

    // =================================================================================
    //  分类取值
    // =================================================================================

    /// <summary>按维度算出一个素材该进的文件夹名。</summary>
    public static string CategoryFor(MediaAsset asset, OrganizeDimension dimension, string? customValue = null)
    {
        var captured = asset.CapturedAt ?? asset.FileModified;
        return dimension switch
        {
            OrganizeDimension.Device => Blank(asset.DisplayDevice) ? "未标注设备" : asset.DisplayDevice,
            OrganizeDimension.CameraMake => Blank(asset.CameraMake) ? "未标注品牌" : asset.CameraMake!,
            OrganizeDimension.Year => captured.ToString("yyyy", CultureInfo.InvariantCulture),
            OrganizeDimension.YearMonth => captured.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            OrganizeDimension.Month => captured.ToString("MM", CultureInfo.InvariantCulture),
            OrganizeDimension.Day => captured.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            OrganizeDimension.City => Blank(asset.City) ? "未标注地点" : asset.City!,
            OrganizeDimension.Country => Blank(asset.Country) ? "未标注国家" : asset.Country!,
            OrganizeDimension.Theme => Blank(asset.Theme) ? "未分类" : Vocabularies.ThemeDisplay(asset.Theme!, "zh-CN"),
            OrganizeDimension.Tone => Blank(asset.Tone) ? "未分类" : Vocabularies.ToneDisplay(asset.Tone!, "zh-CN"),
            OrganizeDimension.CustomCategory => Blank(customValue) ? "未分类" : customValue!,
            OrganizeDimension.Format => Blank(asset.FormatCode) ? "UNKNOWN" : asset.FormatCode,
            OrganizeDimension.Kind => asset.Kind switch
            {
                MediaKind.Image => "图片",
                MediaKind.Video => "视频",
                MediaKind.Audio => "音频",
                _ => "其他",
            },
            OrganizeDimension.Rating => asset.Rating <= 0 ? "未评分" : $"{asset.Rating} 星",
            OrganizeDimension.ColorLabel => Blank(asset.ColorLabel) || asset.ColorLabel == "none"
                ? "无色标"
                : Vocabularies.ColorLabels.FirstOrDefault(v => v.Id == asset.ColorLabel)?.Zh ?? asset.ColorLabel!,
            OrganizeDimension.Extension => Blank(Path.GetExtension(asset.FileName).TrimStart('.'))
                ? "无扩展名"
                : Path.GetExtension(asset.FileName).TrimStart('.').ToUpperInvariant(),
            _ => "未分类",
        };
    }

    /// <summary>一级/二级组合成一个相对目录（不含根）。</summary>
    public static string CategoryPathFor(
        MediaAsset asset,
        OrganizeOptions options,
        string? customValue = null)
    {
        var parts = new List<string> { CategoryFor(asset, options.Primary, customValue) };
        if (options.Secondary is { } secondary && secondary != options.Primary)
            parts.Add(CategoryFor(asset, secondary, customValue));
        return string.Join('/', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(SanitizeFolder));
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>把任意文本变成安全的文件夹名（Windows 非法字符、首尾点号、结尾空格）。</summary>
    public static string SanitizeFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未分类";
        var chars = name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var text = new string(chars).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(text)) return "未分类";
        // Windows 保留名（CON / PRN / AUX / NUL / COM1… / LPT1…）
        var upper = text.ToUpperInvariant();
        var reserved = upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] is >= '1' and <= '9');
        return reserved ? text + "_" : text;
    }

    // =================================================================================
    //  本地：计划
    // =================================================================================

    /// <summary>
    /// 计算本地整理计划。<paramref name="libraryRoots"/> 是 libraryId → 根目录，
    /// <see cref="OrganizeOptions.RootPath"/> 为空时用它决定每个文件的目标根。
    /// </summary>
    public OrganizePlan BuildLocalPlan(
        IReadOnlyList<MediaAsset> assets,
        IReadOnlyDictionary<string, string> libraryRoots,
        OrganizeOptions options)
    {
        var plan = new OrganizePlan();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.FullPath) || !File.Exists(asset.FullPath))
            {
                plan.Warnings.Add($"文件不存在，已跳过：{asset.FileName}");
                continue;
            }

            var root = !string.IsNullOrWhiteSpace(options.RootPath)
                ? options.RootPath!
                : libraryRoots.TryGetValue(asset.LibraryId, out var libRoot) ? libRoot : "";

            if (string.IsNullOrWhiteSpace(root))
            {
                plan.Warnings.Add($"{asset.FileName}：找不到媒体库根目录，已跳过");
                continue;
            }

            string? customValue = null;
            if (NeedsCustom(options) && options.CustomValuesByAssetId is not null)
                options.CustomValuesByAssetId.TryGetValue(asset.Id, out customValue);

            string category;
            try
            {
                category = CategoryPathFor(asset, options, customValue);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"{asset.FileName}：计算分类失败（{ex.Message}）");
                continue;
            }

            plan.AssetRoots[asset.Id] = root;

            var targetDir = Path.Combine(root, category.Replace('/', Path.DirectorySeparatorChar));
            var mainTarget = PlanTarget(targetDir, asset.FileName, options.ConflictPolicy, reserved, out var mainExists, out var mainNote);
            if (mainTarget is null)
            {
                plan.Warnings.Add($"{asset.FileName}：{mainNote}");
                continue;
            }

            var info = SafeSize(asset.FullPath);
            plan.Entries.Add(new OrganizeEntry(
                asset.Id, asset.FileName, asset.FullPath, mainTarget, category, info, mainExists,
                string.Equals(mainTarget, asset.FullPath, StringComparison.OrdinalIgnoreCase) ? "已在目标位置" : mainNote));

            if (!options.IncludeSidecars) continue;

            foreach (var sidecar in asset.Sidecars)
            {
                if (!File.Exists(sidecar)) continue;
                if (string.Equals(Path.GetDirectoryName(sidecar), targetDir, StringComparison.OrdinalIgnoreCase)) continue;

                var sidecarTarget = PlanTarget(targetDir, Path.GetFileName(sidecar), options.ConflictPolicy, reserved, out var sidecarExists, out var sidecarNote);
                if (sidecarTarget is null) continue;
                if (string.Equals(sidecarTarget, sidecar, StringComparison.OrdinalIgnoreCase)) continue;

                plan.Entries.Add(new OrganizeEntry(
                    asset.Id, Path.GetFileName(sidecar), sidecar, sidecarTarget, category, SafeSize(sidecar),
                    sidecarExists, sidecarNote, IsSidecar: true));
            }
        }

        return plan;
    }

    private static bool NeedsCustom(OrganizeOptions options) =>
        options.Primary == OrganizeDimension.CustomCategory || options.Secondary == OrganizeDimension.CustomCategory;

    private static string? PlanTarget(
        string targetDir,
        string fileName,
        OrganizeConflictPolicy policy,
        HashSet<string> reserved,
        out bool exists,
        out string? note)
    {
        exists = false;
        note = null;
        var candidate = Path.Combine(targetDir, fileName);

        switch (policy)
        {
            case OrganizeConflictPolicy.Skip:
                exists = File.Exists(candidate) || reserved.Contains(candidate);
                if (exists)
                {
                    note = "目标已存在，跳过";
                    return null;
                }
                reserved.Add(candidate);
                return candidate;

            case OrganizeConflictPolicy.Overwrite:
                exists = File.Exists(candidate);
                if (exists) note = "目标已存在，将覆盖";
                reserved.Add(candidate);
                return candidate;

            default: // Rename
                var unique = UniquePath(candidate, reserved);
                reserved.Add(unique);
                return unique;
        }
    }

    private static string UniquePath(string path, HashSet<string> reserved)
    {
        if (!File.Exists(path) && !reserved.Contains(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !reserved.Contains(candidate)) return candidate;
        }
        return path;
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // =================================================================================
    //  本地：执行 / 撤销
    // =================================================================================

    public async Task<OrganizeResult> ExecuteLocalAsync(
        OrganizePlan plan,
        IAssetStore? store,
        OrganizeOptions options,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new OrganizeResult();
        var undo = new OrganizeUndoRecord
        {
            Kind = "local",
            Description = $"按「{options.Primary.Display()}」整理 {plan.FileCount} 个文件",
        };

        // 每个素材移动完成后统一回写索引（主文件 + 边车）
        var movedByAsset = new Dictionary<string, (string Path, List<string> Sidecars)>(StringComparer.Ordinal);

        int done = 0, total = plan.Entries.Count;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(total == 0 ? 100 : done * 100 / total);
            done++;

            if (entry.Note is "目标已存在，跳过")
            {
                result.Skipped++;
                continue;
            }

            try
            {
                var targetDir = Path.GetDirectoryName(entry.TargetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                if (string.Equals(entry.SourcePath, entry.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                    continue;
                }

                if (File.Exists(entry.TargetPath) && options.ConflictPolicy == OrganizeConflictPolicy.Overwrite)
                    File.Delete(entry.TargetPath);

                MoveFile(entry.SourcePath, entry.TargetPath);

                result.Moved++;
                if (!entry.IsSidecar) result.Bytes += entry.SizeBytes;

                undo.Items.Add(new OrganizeUndoItem
                {
                    From = entry.SourcePath,
                    To = entry.TargetPath,
                    AssetId = entry.IsSidecar ? null : entry.AssetId,
                });

                if (!entry.IsSidecar)
                {
                    movedByAsset[entry.AssetId] = (entry.TargetPath, new List<string>());
                }
                else if (movedByAsset.TryGetValue(entry.AssetId, out var moved))
                {
                    moved.Sidecars.Add(entry.TargetPath);
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{entry.FileName}: {ex.Message}");
                Log.Warn($"整理移动失败 {entry.SourcePath}", ex);
            }
        }

        progress?.Report(100);

        // 回写索引库（路径 + 边车）
        if (store is not null && movedByAsset.Count > 0)
        {
            foreach (var (assetId, moved) in movedByAsset)
            {
                try
                {
                    plan.AssetRoots.TryGetValue(assetId, out var assetRoot);
                    var relative = RelativeToLibrary(moved.Path, !string.IsNullOrWhiteSpace(options.RootPath) ? options.RootPath : assetRoot);
                    await store.UpdateAssetPathAsync(assetId, moved.Path, relative,
                        moved.Sidecars.Count > 0 ? moved.Sidecars : null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"整理后更新索引失败 {assetId}", ex);
                }
            }
        }

        result.UndoAvailable = undo.Items.Count > 0;
        if (result.UndoAvailable) WriteUndo(undo);
        return result;
    }

    /// <summary>把绝对路径换算成相对媒体库根的路径（'/' 分隔）；根未知就返回 null。</summary>
    private static string? RelativeToLibrary(string fullPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var relative = Path.GetRelativePath(root!, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal)) return null;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return null;
        }
    }

    public bool CanUndo => File.Exists(UndoPath);

    public OrganizeUndoRecord? ReadUndo()
    {
        try
        {
            if (!File.Exists(UndoPath)) return null;
            var json = File.ReadAllText(UndoPath);
            return JsonSerializer.Deserialize<OrganizeUndoRecord>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn("读取整理撤销记录失败", ex);
            return null;
        }
    }

    public async Task<OrganizeResult> UndoLocalAsync(
        IAssetStore? store,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var record = ReadUndo();
        var result = new OrganizeResult();
        if (record is null || record.Kind != "local" || record.IsEmpty)
        {
            result.Errors.Add("没有可撤销的整理记录");
            return result;
        }

        int done = 0, total = record.Items.Count;
        for (int i = record.Items.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(total == 0 ? 100 : done * 100 / total);
            done++;

            var item = record.Items[i];
            try
            {
                if (!File.Exists(item.To)) continue;                 // 已被用户自己动过，跳过
                if (File.Exists(item.From)) { result.Skipped++; continue; }

                var dir = Path.GetDirectoryName(item.From);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                MoveFile(item.To, item.From);
                result.Moved++;

                if (store is not null && !string.IsNullOrEmpty(item.AssetId))
                {
                    var relative = RelativeToLibrary(item.From, null);
                    await store.UpdateAssetPathAsync(item.AssetId!, item.From, relative, null, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(item.To)}: {ex.Message}");
            }
        }

        progress?.Report(100);
        try { File.Delete(UndoPath); } catch { }
        return result;
    }

    private void WriteUndo(OrganizeUndoRecord record)
    {
        try
        {
            var dir = Path.GetDirectoryName(UndoPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(UndoPath, JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn("写入整理撤销记录失败", ex);
        }
    }

    // =================================================================================
    //  云端：计划 / 执行 / 撤销
    // =================================================================================

    public ICloudStorage? CreateStorage(CloudTarget target) => _cloud?.CreateStorage(target);

    /// <summary>
    /// 云端整理计划：用本地索引库的元数据给云端文件算出目标目录。
    /// 在本地索引里找不到的云端文件会跳过（记进 Warnings），不会乱搬。
    /// </summary>
    public async Task<CloudOrganizePlan> BuildCloudPlanAsync(
        CloudTarget target,
        IReadOnlyList<MediaAsset> catalog,
        OrganizeOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = new CloudOrganizePlan { TargetId = target.Id, TargetName = target.Name };
        if (_cloud is null)
        {
            plan.Warnings.Add("云同步服务不可用");
            return plan;
        }

        await using var storage = _cloud.CreateStorage(target);
        progress?.Report("正在列出云端文件…");
        var entries = await storage.ListAllAsync(null, ct).ConfigureAwait(false);
        var files = entries.Where(e => !e.IsDirectory).ToList();
        var remotePaths = new HashSet<string>(files.Select(e => NormalizeRel(e.RelativePath)), StringComparer.OrdinalIgnoreCase);

        // 本地索引：相对路径优先匹配，文件名兜底
        var byRelative = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in catalog)
        {
            if (!string.IsNullOrWhiteSpace(asset.RelativePath))
                byRelative[NormalizeRel(asset.RelativePath!)] = asset;
        }
        var byName = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in catalog)
            byName.TryAdd(asset.FileName, asset);

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = NormalizeRel(file.RelativePath);
            if (rel.StartsWith('.')) continue;

            if (!byRelative.TryGetValue(rel, out var asset) && !byName.TryGetValue(file.FileName, out asset))
            {
                plan.Warnings.Add($"云端文件没有本地元数据，已跳过：{rel}");
                continue;
            }

            string? customValue = null;
            if (NeedsCustom(options) && options.CustomValuesByAssetId is not null)
                options.CustomValuesByAssetId.TryGetValue(asset.Id, out customValue);

            var category = CategoryPathFor(asset, options, customValue);
            var to = string.IsNullOrEmpty(category) ? file.FileName : category + "/" + file.FileName;

            if (string.Equals(NormalizeRel(to), rel, StringComparison.OrdinalIgnoreCase)) continue;

            if (!reserved.Add(NormalizeRel(to)) || remotePaths.Contains(NormalizeRel(to)))
            {
                var unique = CloudUnique(to, remotePaths, reserved);
                reserved.Add(NormalizeRel(unique));
                to = unique;
            }

            plan.Entries.Add(new CloudOrganizeEntry(
                rel, to, file.FileName, category, file.SizeBytes,
                remotePaths.Contains(NormalizeRel(to))));
        }

        return plan;
    }

    private static string CloudUnique(string relative, HashSet<string> remotePaths, HashSet<string> reserved)
    {
        var slash = relative.LastIndexOf('/');
        var dir = slash >= 0 ? relative[..slash] : "";
        var name = slash >= 0 ? relative[(slash + 1)..] : relative;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 2; i < 10_000; i++)
        {
            var candidate = (dir.Length > 0 ? dir + "/" : "") + $"{stem} ({i}){ext}";
            if (!remotePaths.Contains(NormalizeRel(candidate)) && reserved.Add(NormalizeRel(candidate)))
                return candidate;
        }
        return relative;
    }

    public async Task<OrganizeResult> ExecuteCloudAsync(
        CloudTarget target,
        CloudOrganizePlan plan,
        OrganizeOptions options,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new OrganizeResult();
        if (_cloud is null)
        {
            result.Errors.Add("云同步服务不可用");
            return result;
        }

        await using var storage = _cloud.CreateStorage(target);
        var undo = new OrganizeUndoRecord
        {
            Kind = "cloud",
            TargetId = target.Id,
            Description = $"云端按「{options.Primary.Display()}」整理 {plan.FileCount} 个文件",
        };

        int done = 0, total = plan.Entries.Count;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(total == 0 ? 100 : done * 100 / total);
            done++;

            try
            {
                var slash = entry.RelativeTo.LastIndexOf('/');
                if (slash > 0)
                    await storage.EnsureFolderAsync(entry.RelativeTo[..slash], ct).ConfigureAwait(false);

                await storage.MoveAsync(entry.RelativeFrom, entry.RelativeTo, ct).ConfigureAwait(false);
                result.Moved++;
                result.Bytes += entry.SizeBytes;
                undo.Items.Add(new OrganizeUndoItem { From = entry.RelativeFrom, To = entry.RelativeTo });
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{entry.FileName}: {ex.Message}");
                Log.Warn($"云端整理移动失败 {entry.RelativeFrom}", ex);
            }
        }

        progress?.Report(100);
        result.UndoAvailable = undo.Items.Count > 0;
        if (result.UndoAvailable) WriteUndo(undo);
        return result;
    }

    public async Task<OrganizeResult> UndoCloudAsync(
        CloudTarget target,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new OrganizeResult();
        var record = ReadUndo();
        if (record is null || record.Kind != "cloud" || record.IsEmpty || _cloud is null)
        {
            result.Errors.Add("没有可撤销的云端整理记录");
            return result;
        }

        await using var storage = _cloud.CreateStorage(target);
        for (int i = record.Items.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var item = record.Items[i];
            try
            {
                var slash = item.From.LastIndexOf('/');
                if (slash > 0) await storage.EnsureFolderAsync(item.From[..slash], ct).ConfigureAwait(false);
                await storage.MoveAsync(item.To, item.From, ct).ConfigureAwait(false);
                result.Moved++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{item.From}: {ex.Message}");
            }
        }

        try { File.Delete(UndoPath); } catch { }
        return result;
    }

    // =================================================================================
    //  工具
    // =================================================================================

    private static string NormalizeRel(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void MoveFile(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: false);
        }
        catch (IOException)
        {
            // 跨卷/被占用：先复制再删源，保证不丢文件
            File.Copy(source, target, overwrite: false);
            File.Delete(source);
        }
    }
}