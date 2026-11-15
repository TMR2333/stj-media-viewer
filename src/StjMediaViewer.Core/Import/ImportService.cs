using System.Security.Cryptography;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Import;

/// <summary>
/// 从外部存储设备（或任意目录）导入素材。
///
/// 设计要点：
/// 1) **先出计划再执行**：`BuildPlanAsync` 只读源盘，按 JPG+RAW 分组成可预览/可勾选的计划；
/// 2) **JPG 当预览、RAW 同时导入**：分组时 JPEG 是代表（cover），执行时整组一起拷贝；
/// 3) **边拷边算哈希**：严格核验模式下，拷贝流里直接算 SHA-256（源侧只读一遍），再读回目标文件校验；
/// 4) **移动 = 拷贝 + 核验通过 + 才删源**：避免"移动"途中失败丢素材；
/// 5) 导入完成可选登记进媒体库（调用 LibraryService 扫描目标目录）。
/// </summary>
public sealed class ImportService : IImportService
{
    private readonly IMetadataService _metadata;
    private readonly IGeocoder? _geocoder;
    private readonly ILibraryService? _library;

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "RECYCLER", ".trash", ".Trashes",
        "lost+found", ".Spotlight-V100", ".fseventsd", "Windows", "Program Files",
    };

    private static readonly HashSet<string> MediaExtensions = BuildMediaExtensions();

    public ImportService(IMetadataService metadata, IGeocoder? geocoder = null, ILibraryService? library = null)
    {
        _metadata = metadata;
        _geocoder = geocoder;
        _library = library;
    }

    // ==================================================================================
    //  1) 扫描源，生成导入计划
    // ==================================================================================

    public async Task<ImportPlan> BuildPlanAsync(
        string sourceRoot,
        ImportOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"源目录不存在：{sourceRoot}");

        var plan = new ImportPlan { SourceRoot = Path.GetFullPath(sourceRoot), Options = options };
        var files = new List<string>();

        progress?.Report(new ScanProgress { Phase = ScanPhase.Enumerating, Message = plan.SourceRoot });

        await Task.Run(() =>
        {
            var dirs = new Stack<string>();
            dirs.Push(plan.SourceRoot);
            while (dirs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (SkipDirNames.Contains(name)) continue;
                        dirs.Push(sub);
                    }
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (IsCandidate(file)) files.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    plan.Warnings.Add($"跳过目录 {dir}：{ex.Message}");
                }
            }
        }, ct).ConfigureAwait(false);

        if (files.Count == 0)
        {
            progress?.Report(new ScanProgress { Phase = ScanPhase.Completed, Message = "没有找到可导入的素材" });
            return plan;
        }

        var buckets = JpegRawPairing.Group(files);

        // 读 JPEG 的拍摄时间 / 相机线索（RAW 太贵，交给库内元数据引擎）
        int done = 0;
        var groups = new List<ImportGroup>(buckets.Count);
        foreach (var bucket in buckets.Values)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            var group = new ImportGroup
            {
                GroupKey = bucket.Key,
                JpegPath = bucket.Jpegs.FirstOrDefault(),
                RawPath = bucket.Raws.FirstOrDefault(),
                CameraHint = null,
            };
            foreach (var extra in bucket.Jpegs.Skip(1)) group.OtherPaths.Add(extra);
            foreach (var extra in bucket.Raws.Skip(1)) group.OtherPaths.Add(extra);
            foreach (var other in bucket.Others) group.OtherPaths.Add(other);
            if (options.IncludeSidecars)
            {
                foreach (var side in bucket.Sidecars) group.SidecarPaths.Add(side);
            }

            var probePath = group.JpegPath ?? group.RawPath;
            if (bucket.Jpegs.Count == 0 && bucket.Raws.Count == 0 && group.OtherPaths.Count > 0)
                probePath = group.OtherPaths[0];

            group.CapturedAt = TryCaptureTime(probePath);
            group.CameraHint = TryCameraHint(probePath);
            group.Kind = (int)TryKind(probePath);

            if (options.CapturedAfter is { } after && group.CapturedAt is { } cap && cap < after) continue;
            groups.Add(group);

            if (done % 20 == 0 || done == buckets.Count)
            {
                progress?.Report(new ScanProgress
                {
                    Phase = ScanPhase.ReadingMetadata,
                    Processed = done,
                    Total = buckets.Count,
                    Message = Path.GetFileName(probePath ?? group.GroupKey),
                });
            }
        }

        // 按拍摄时间倒序（新素材在前），没有时间的排后面
        plan.Groups = groups
            .OrderByDescending(g => g.CapturedAt ?? DateTimeOffset.MinValue)
            .ToList();

        plan.TotalBytes = plan.Groups.Sum(g => SafeSize(g.AllFiles()));

        // 预估重复：目标目录下已存在同名同大小的文件
        if (!string.IsNullOrWhiteSpace(options.DestinationRoot) && options.SkipExisting)
        {
            try
            {
                plan.EstimatedDuplicates = await Task.Run(() => CountExistingDuplicates(plan.Groups, options.DestinationRoot, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                plan.Warnings.Add($"重复检查失败：{ex.Message}");
            }
        }

        progress?.Report(new ScanProgress
        {
            Phase = ScanPhase.Completed,
            Processed = plan.Groups.Count,
            Total = plan.Groups.Count,
            Message = $"{plan.Groups.Count} 组 / {plan.TotalBytes / 1024.0 / 1024:F1} MB",
        });
        return plan;
    }

    private static bool IsCandidate(string path)
    {
        if (JpegRawPairing.IsSidecar(path)) return true;
        var ext = Path.GetExtension(path);
        return MediaExtensions.Contains(ext);
    }

    private static HashSet<string> BuildMediaExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 图片
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".gif",
            ".webp", ".heic", ".heif", ".avif", ".psd", ".psb", ".tga", ".exr", ".hdr",
            // 视频
            ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".mts", ".m2ts", ".mpg", ".mpeg",
            ".wmv", ".webm", ".flv", ".3gp", ".insv", ".braw", ".r3d", ".mxf",
            // 音频
            ".wav", ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".dsf",
        };
        foreach (var raw in JpegRawPairing.RawExtensions) set.Add(raw);
        return set;
    }

    private DateTimeOffset? TryCaptureTime(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            // 只读文件头（EXIF 拍摄时间便宜），失败退回文件时间
            var md = _metadata.Read(path, CancellationToken.None);
            var t = md?.CapturedAt;
            if (t is { } captured && captured.Year > 1971) return captured;
        }
        catch (Exception ex)
        {
            Log.Info($"[import] 读取拍摄时间失败 {path}: {ex.Message}");
        }
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToLocalTime(); }
        catch { return null; }
    }

    private string? TryCameraHint(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var md = _metadata.Read(path, CancellationToken.None);
            if (md is null) return null;
            var parts = new[] { md.CameraMake, md.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            return parts.Length == 0 ? null : string.Join(" ", parts);
        }
        catch { return null; }
    }

    private static MediaKind TryKind(string? path)
    {
        if (string.IsNullOrEmpty(path)) return MediaKind.Unknown;
        return MediaTypeCatalog.TryGet(path, out var kind, out _, out _, out _, out _) ? kind : MediaKind.Unknown;
    }

    private static long SafeSize(IEnumerable<string> files)
    {
        long total = 0;
        foreach (var f in files)
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        return total;
    }

    private static int CountExistingDuplicates(List<ImportGroup> groups, string destRoot, CancellationToken ct)
    {
        if (!Directory.Exists(destRoot)) return 0;
        // 目标目录里所有文件名 + 大小（只扫两层，够用且快）
        var index = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in EnumerateDirsShallow(destRoot, 3))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (!index.TryGetValue(name, out var sizes))
                        index[name] = sizes = new HashSet<long>();
                    try { sizes.Add(new FileInfo(file).Length); } catch { }
                }
            }
        }
        catch { }

        int dup = 0;
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in g.AllFiles())
            {
                var name = Path.GetFileName(f);
                try
                {
                    var size = new FileInfo(f).Length;
                    if (index.TryGetValue(name, out var sizes) && sizes.Contains(size)) dup++;
                }
                catch { }
            }
        }
        return dup;
    }

    private static IEnumerable<string> EnumerateDirsShallow(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            yield return dir;
            if (depth >= maxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { continue; }
            foreach (var sub in subs) queue.Enqueue((sub, depth + 1));
        }
    }

    // ==================================================================================
    //  2) 执行导入
    // ==================================================================================

    public async Task<ImportReport> ExecuteAsync(
        ImportPlan plan,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new ImportReport();
        var selected = plan.Groups.Where(g => g.IsSelected).ToList();

        report.GroupsTotal = selected.Count;
        var allFiles = selected.SelectMany(g => g.AllFiles()).ToList();
        long bytesTotal = 0;
        foreach (var f in allFiles) { try { bytesTotal += new FileInfo(f).Length; } catch { } }

        var destRoot = string.IsNullOrWhiteSpace(plan.Options.DestinationRoot)
            ? throw new InvalidOperationException("没有指定目标目录")
            : Path.GetFullPath(plan.Options.DestinationRoot);
        Directory.CreateDirectory(destRoot);

        var hub = new ImportHub(progress, allFiles.Count, bytesTotal);
        hub.Report("准备导入", currentFile: null);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in selected)
        {
            ct.ThrowIfCancellationRequested();
            hub.NextGroup();

            var groupFailed = false;
            var groupSkipped = 0;
            var groupCopied = 0;
            var groupFiles = group.AllFiles().Where(p => File.Exists(p)).ToList();

            var subDir = ExpandFolder(plan.Options.SubfolderTemplate, group);
            var groupDir = Path.Combine(destRoot, subDir);
            try { Directory.CreateDirectory(groupDir); }
            catch (Exception ex)
            {
                report.GroupsFailed++;
                groupFailed = true;
                report.Items.Add(new ImportItemResult
                {
                    SourcePath = group.JpegPath ?? group.RawPath ?? group.GroupKey,
                    Status = "Failed",
                    Error = $"创建目录失败：{ex.Message}",
                });
                report.Warnings.Add($"组 {group.DisplayName} 创建目录失败：{ex.Message}");
            }

            foreach (var src in groupFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (groupFailed) break;

                var item = new ImportItemResult { SourcePath = src };
                try
                {
                    var size = new FileInfo(src).Length;
                    item.SizeBytes = size;
                    var dest = Path.Combine(groupDir, Path.GetFileName(src));

                    if (plan.Options.SkipExisting && File.Exists(dest) && new FileInfo(dest).Length == size)
                    {
                        item.DestinationPath = dest;
                        item.Status = "Skipped";
                        item.VerifyStatus = "Skipped";
                        groupSkipped++;
                        report.FilesSkipped++;
                        report.Items.Add(item);
                        hub.NextFile(size, Path.GetFileName(src), "已存在，跳过");
                        continue;
                    }

                    dest = MakeUniqueDest(dest, usedNames);
                    item.DestinationPath = dest;

                    var hashes = await CopyWithHashAsync(src, dest, plan.Options.VerifyAfterCopy, plan.Options.StrictVerify, ct)
                        .ConfigureAwait(false);

                    item.Status = "Copied";
                    item.SourceHash = hashes.SourceHash;
                    item.DestinationHash = hashes.DestinationHash;
                    item.VerifyStatus = hashes.Match switch
                    {
                        true => "Verified",
                        false => "Mismatch",
                        null => "NotVerified",
                    };
                    groupCopied++;
                    report.FilesCopied++;
                    report.BytesCopied += size;
                    if (hashes.Match == true) report.FilesVerified++;
                    if (hashes.Match == false) report.FilesCorrupted++;

                    // 移动模式：核验通过（或未开核验）才删源，避免丢素材
                    if (!plan.Options.CopyInsteadOfMove && hashes.Match != false)
                    {
                        try { File.Delete(src); }
                        catch (Exception ex) { report.Warnings.Add($"删除源文件失败 {Path.GetFileName(src)}：{ex.Message}"); }
                    }

                    hub.NextFile(size, Path.GetFileName(src), hashes.Match == false ? "内容不一致！" : "已复制");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.Error = ex.Message;
                    groupFailed = true;
                    report.FilesFailed++;
                    report.Warnings.Add($"{Path.GetFileName(src)} 导入失败：{ex.Message}");
                    report.Items.Add(item);
                    hub.NextFile(item.SizeBytes, Path.GetFileName(src), "失败");
                }

                if (item.Status != "Failed" && !report.Items.Contains(item)) report.Items.Add(item);
            }

            if (groupFailed) report.GroupsFailed++;
            else if (groupCopied == 0 && groupSkipped > 0) report.GroupsSkipped++;
            else report.GroupsImported++;
        }

        // 3) 登记进媒体库（扫描目标目录）
        if (plan.Options.IndexAfterCopy && _library is not null && report.GroupsImported > 0)
        {
            try
            {
                hub.Report("登记进媒体库…", currentFile: null);
                var target = await FindLibraryForAsync(destRoot).ConfigureAwait(false)
                    ?? await _library.AddLibraryAsync(destRoot, ct).ConfigureAwait(false);
                var scan = await _library.ScanLibraryAsync(target, new ScanOptions(), null, ct).ConfigureAwait(false);
                report.Warnings.Add($"已登记：扫描 {scan.Processed} 个文件，新增 {scan.Added} 个");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Warnings.Add($"登记媒体库失败：{ex.Message}");
            }
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;

        // 数量对账：选中的组里应有的文件数 vs 实际落盘（复制+跳过）
        try
        {
            var expected = allFiles.Count;
            var actual = report.Items.Count(i => i.Status is "Copied" or "Skipped");
            if (actual < expected)
            {
                report.FilesMissing += expected - actual;
                report.Warnings.Add($"数量对账：应有 {expected} 个文件，实际落盘/跳过 {actual} 个，缺失 {expected - actual} 个");
            }
        }
        catch { }

        hub.Done(report.Summary);
        return report;
    }

    private async Task<LibraryRoot?> FindLibraryForAsync(string destRoot)
    {
        if (_library is null) return null;
        try
        {
            var libs = await _library.GetLibrariesAsync(CancellationToken.None).ConfigureAwait(false);
            return libs
                .Where(l => destRoot.StartsWith(l.Path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Path.Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string MakeUniqueDest(string dest, HashSet<string> used)
    {
        if (!used.Contains(dest) && !File.Exists(dest)) { used.Add(dest); return dest; }
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!used.Contains(candidate) && !File.Exists(candidate)) { used.Add(candidate); return candidate; }
        }
        used.Add(dest);
        return dest;
    }

    /// <summary>拷贝并（可选）计算源/目标哈希。返回 Match=null 表示没做核验。</summary>
    private static async Task<(string? SourceHash, string? DestinationHash, bool? Match)> CopyWithHashAsync(
        string source, string destination, bool verify, bool strict, CancellationToken ct)
    {
        string? sourceHash = null;
        string? destHash = null;

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            if (verify && strict)
            {
                using var sha = SHA256.Create();
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sourceHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            else
            {
                await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            }
        }

        if (verify)
        {
            destHash = await Task.Run(() => strict
                    ? FileHasher.FullHash(destination, ct)
                    : FileHasher.QuickHash(destination, ct), ct).ConfigureAwait(false);
            if (!strict)
                sourceHash = await Task.Run(() => FileHasher.QuickHash(source, ct), ct).ConfigureAwait(false);
        }

        bool? match = verify ? string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase) : null;
        return (sourceHash, destHash, match);
    }

    // ==================================================================================
    //  3) 目标子目录模板
    // ==================================================================================

    internal static string ExpandFolder(string? template, ImportGroup group)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var when = group.CapturedAt ?? DateTimeOffset.Now;
        var name = group.DisplayName;

        var result = template
            .Replace("{yyyy}", when.ToString("yyyy"))
            .Replace("{yy}", when.ToString("yy"))
            .Replace("{MM}", when.ToString("MM"))
            .Replace("{dd}", when.ToString("dd"))
            .Replace("{HH}", when.ToString("HH"))
            .Replace("{mm}", when.ToString("mm"))
            .Replace("{ss}", when.ToString("ss"))
            .Replace("{yyyy-MM-dd}", when.ToString("yyyy-MM-dd"))
            .Replace("{yyyy-MM}", when.ToString("yyyy-MM"))
            .Replace("{date}", when.ToString("yyyy-MM-dd"))
            .Replace("{name}", name)
            .Replace("{camera}", SanitizeSegment(group.CameraHint ?? "Unknown"))
            .Replace("{kind}", group.Kind == (int)MediaKind.Video ? "视频"
                               : group.Kind == (int)MediaKind.Audio ? "音频" : "图片");

        // 未知段（如 {country}/{city} 在导入阶段还没地理编码）替换成 "_"，避免出现裸 {xxx} 目录
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\{[a-zA-Z]+\}", "_");
        return result.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar, ' ');
    }

    private static string SanitizeSegment(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().TrimEnd('.');
    }

    // ------------------------------------------------------------------ 进度聚合

    private sealed class ImportHub
    {
        private readonly IProgress<ImportProgress>? _progress;
        private readonly int _filesTotal;
        private readonly long _bytesTotal;
        private int _files;
        private long _bytes;
        private int _group;

        public ImportHub(IProgress<ImportProgress>? progress, int filesTotal, long bytesTotal)
        {
            _progress = progress;
            _filesTotal = filesTotal;
            _bytesTotal = bytesTotal;
        }

        public void NextGroup() => _group++;

        public void NextFile(long size, string? name, string? message)
        {
            _files++;
            _bytes += size;
            Report(message, name);
        }

        public void Report(string? message, string? currentFile)
        {
            _progress?.Report(new ImportProgress
            {
                GroupsProcessed = _group,
                GroupsTotal = 0,
                FilesProcessed = _files,
                FilesTotal = _filesTotal,
                BytesProcessed = _bytes,
                BytesTotal = _bytesTotal,
                CurrentFile = currentFile,
                Message = message,
            });
        }

        public void Done(string summary)
        {
            _progress?.Report(new ImportProgress
            {
                GroupsProcessed = _group,
                FilesProcessed = _files,
                FilesTotal = _filesTotal,
                BytesProcessed = _bytes,
                BytesTotal = _bytesTotal,
                Message = summary,
                Done = true,
            });
        }
    }
}