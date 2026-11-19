using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Write;

/// <summary>找回 XMP 的结果。</summary>
public sealed class XmpRepairReport
{
    public int ScannedXmp { get; set; }
    public int Orphans { get; set; }
    public int Copied { get; set; }
    public int AlreadyThere { get; set; }
    public int Unmatched { get; set; }
    public List<string> Details { get; } = new();

    public string Describe() =>
        Copied > 0
            ? $"找回 {Copied} 个 XMP" + (Unmatched > 0 ? $"，{Unmatched} 个找不到对应素材" : "")
            : (Orphans == 0 ? "没有发现遗留的 XMP" : (Unmatched > 0 ? $"遗留 {Unmatched} 个 XMP 找不到对应素材" : "遗留的 XMP 都已就位"));
}

/// <summary>
/// 找回"转移媒体时没跟着走"的 XMP 边车：
/// 在媒体库目录里扫描孤立的 *.xmp（同目录下已没有对应主文件），按文件名主干匹配索引里的素材，
/// 把 XMP 复制到素材现在所在目录（只复制，不删除原文件）。
/// </summary>
public static class XmpRepairService
{
    public static async Task<XmpRepairReport> RepairAsync(
        IAssetStore store,
        IReadOnlyList<string> searchRoots,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var report = new XmpRepairReport();
        if (store is null || searchRoots is null || searchRoots.Count == 0) return report;

        // 索引里的所有素材：文件名主干 → 素材
        var byStem = new Dictionary<string, List<MediaAsset>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<MediaAsset> assets;
        try
        {
            assets = await store.QueryAsync(new AssetQuery { Limit = 0 }, ct);
        }
        catch (Exception ex)
        {
            Log.Warn("[xmp-repair] 读取素材索引失败", ex);
            return report;
        }

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.FullPath)) continue;
            var stem = Path.GetFileNameWithoutExtension(asset.FileName);
            if (string.IsNullOrWhiteSpace(stem)) continue;
            if (!byStem.TryGetValue(stem, out var list))
            {
                list = new List<MediaAsset>();
                byStem[stem] = list;
            }
            list.Add(asset);
        }

        foreach (var root in searchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;

            foreach (var xmp in EnumerateXmp(root))
            {
                ct.ThrowIfCancellationRequested();
                report.ScannedXmp++;

                var dir = Path.GetDirectoryName(xmp)!;
                var xmpName = Path.GetFileName(xmp);          // DSC001.ARW.xmp
                var baseName = xmpName[..^4];                 // DSC001.ARW

                // 同目录下主文件还在 → 不是"转移遗留"，跳过。
                // 注意两种命名都要认：foo.jpg.xmp（全名式）和 foo.xmp（同名式，主文件是 foo.jpg/foo.ARW）。
                if (HasMediaSibling(dir, baseName)) continue;

                var stem = Path.GetFileNameWithoutExtension(baseName);   // DSC001
                if (string.IsNullOrWhiteSpace(stem)) continue;

                report.Orphans++;
                if (!byStem.TryGetValue(stem, out var candidates) || candidates.Count == 0)
                {
                    report.Unmatched++;
                    report.Details.Add($"找不到对应素材：{xmp}");
                    progress?.Report($"跳过（无对应素材）：{xmpName}");
                    continue;
                }

                var copiedHere = false;
                foreach (var candidate in candidates)
                {
                    var targetDir = Path.GetDirectoryName(candidate.FullPath);
                    if (string.IsNullOrWhiteSpace(targetDir)) continue;
                    if (string.Equals(targetDir, dir, StringComparison.OrdinalIgnoreCase)) continue;

                    // 目标名统一用「素材文件名 + .xmp」（索引/扫描器认的命名），
                    // 这样 ARW 与 JPG 都能正确关联，而不是留下一个 xxx.jpg.xmp 在 ARW 旁边。
                    var target = candidate.FullPath + ".xmp";
                    var stemTarget = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(candidate.FileName) + ".xmp");
                    try
                    {
                        // 目标目录已有同名边车（全名式或同名式）就跳过，避免重复
                        if (File.Exists(target) || File.Exists(stemTarget))
                        {
                            report.AlreadyThere++;
                            continue;
                        }
                        File.Copy(xmp, target, overwrite: false);
                        report.Copied++;
                        copiedHere = true;
                        progress?.Report($"已复制：{Path.GetFileName(xmp)} → {targetDir}");
                    }
                    catch (Exception ex)
                    {
                        report.Details.Add($"复制失败：{xmp} → {targetDir}：{ex.Message}");
                        Log.Warn($"[xmp-repair] 复制失败 {xmp} → {target}", ex);
                    }
                }

                if (!copiedHere)
                    report.Details.Add($"已存在或未复制：{xmp}");
            }
        }

        return report;
    }

    /// <summary>同目录里是否还有这个 XMP 对应的主文件（两种命名规则都认）。</summary>
    private static bool HasMediaSibling(string dir, string baseName)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, baseName))) return true;

            var stem = Path.GetFileNameWithoutExtension(baseName);
            if (string.IsNullOrWhiteSpace(stem)) return false;

            foreach (var file in Directory.EnumerateFiles(dir, stem + ".*"))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".xmp", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // 目录不可读时按"没有主文件"处理（只影响要不要尝试找回）
        }
        return false;
    }

    private static IEnumerable<string> EnumerateXmp(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.xmp", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"[xmp-repair] 扫描失败 {root}", ex);
            yield break;
        }

        foreach (var file in files) yield return file;
    }
}