using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Supply;

/// <summary>
/// 完整性核验：确认"导出/导入后的文件没损坏、也没漏导"。
///
/// 两种强度：
/// - **严格**：SHA-256 全文件比对（字节级一致，能覆盖静默损坏）；
/// - **快速**：大小 + 首尾各 512KB 的 FNV 指纹（秒级验证大批量，代价是理论上漏检同位置篡改）。
/// 另外做**数量对账**：源侧每个文件都必须在目标侧找到同名且内容一致的文件，反之记为目标多余（仅提示）。
/// </summary>
public sealed class FileVerificationService : IVerificationService
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "RECYCLER", ".trash", ".Trashes",
        "lost+found", ".Spotlight-V100", ".fseventsd",
    };

    public async Task<VerifyItemResult> VerifyFileAsync(
        string sourcePath, string destinationPath, bool strict, CancellationToken ct = default)
    {
        var item = new VerifyItemResult
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
        };

        try
        {
            if (!File.Exists(sourcePath))
            {
                item.Status = "Error";
                item.Error = "源文件不存在";
                return item;
            }
            var srcLen = new FileInfo(sourcePath).Length;
            item.SizeBytes = srcLen;

            if (!File.Exists(destinationPath))
            {
                item.Status = "Missing";
                item.Error = "目标文件不存在（漏导）";
                return item;
            }

            var dstLen = new FileInfo(destinationPath).Length;
            if (dstLen != srcLen)
            {
                item.Status = "Corrupted";
                item.Error = $"大小不一致：源 {srcLen} / 目标 {dstLen}";
                return item;
            }

            var (srcHash, dstHash) = await Task.Run(() =>
            {
                var s = strict ? FileHasher.FullHash(sourcePath, ct) : FileHasher.QuickHash(sourcePath, ct);
                var d = strict ? FileHasher.FullHash(destinationPath, ct) : FileHasher.QuickHash(destinationPath, ct);
                return (s, d);
            }, ct).ConfigureAwait(false);

            item.SourceHash = srcHash;
            item.DestinationHash = dstHash;
            if (string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
            {
                item.Status = "Passed";
            }
            else
            {
                item.Status = "Corrupted";
                item.Error = strict ? "SHA-256 不一致（文件损坏）" : "快速指纹不一致（文件可能损坏）";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.Error = ex.Message;
        }
        return item;
    }

    public async Task<VerifyReport> VerifyPairsAsync(
        IReadOnlyList<VerifyPair> pairs,
        bool strict,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new VerifyReport { Total = pairs.Count, Strict = strict };

        for (int i = 0; i < pairs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = pairs[i];
            var item = await VerifyFileAsync(pair.SourcePath, pair.DestinationPath, strict, ct).ConfigureAwait(false);
            report.Items.Add(item);
            report.BytesVerified += item.SizeBytes;
            switch (item.Status)
            {
                case "Passed": report.Passed++; break;
                case "Missing": report.Missing++; break;
                case "Corrupted": report.Corrupted++; break;
                default: report.Errors++; break;
            }
            if (i % 10 == 0 || i == pairs.Count - 1) progress?.Report(i + 1);
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;
        return report;
    }

    public async Task<VerifyReport> VerifyFolderAsync(
        string sourceRoot,
        string destinationRoot,
        bool strict,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new VerifyReport { Strict = strict };

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"源目录不存在：{sourceRoot}");
        if (!Directory.Exists(destinationRoot))
        {
            report.Items.Add(new VerifyItemResult
            {
                SourcePath = sourceRoot,
                DestinationPath = destinationRoot,
                Status = "Missing",
                Error = "目标目录不存在（整个目录漏导）",
            });
            report.Total = 1;
            report.Missing = 1;
            return report;
        }

        // 1) 源侧文件清单
        var sourceFiles = new List<string>();
        await Task.Run(() =>
        {
            foreach (var f in EnumerateFiles(sourceRoot, ct)) sourceFiles.Add(f);
        }, ct).ConfigureAwait(false);

        // 2) 目标侧按文件名建索引（导出可能改子目录，所以按名字对账）
        var destIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            foreach (var f in EnumerateFiles(destinationRoot, ct))
            {
                var name = Path.GetFileName(f);
                if (!destIndex.TryGetValue(name, out var list)) destIndex[name] = list = new List<string>(1);
                list.Add(f);
            }
        }, ct).ConfigureAwait(false);

        report.Total = sourceFiles.Count;
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sourceFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = sourceFiles[i];
            var name = Path.GetFileName(src);
            var item = new VerifyItemResult { SourcePath = src };

            try
            {
                var size = new FileInfo(src).Length;
                item.SizeBytes = size;

                if (!destIndex.TryGetValue(name, out var candidates) || candidates.Count == 0)
                {
                    item.Status = "Missing";
                    item.Error = "目标侧找不到同名文件（漏导）";
                    report.Missing++;
                    report.Items.Add(item);
                    if (i % 10 == 0 || i == sourceFiles.Count - 1) progress?.Report(i + 1);
                    continue;
                }

                matchedNames.Add(name);

                // 同名多份时挑大小一致的那个
                var dest = candidates.FirstOrDefault(c => SafeLength(c) == size) ?? candidates[0];
                item.DestinationPath = dest;

                var check = await VerifyFileAsync(src, dest, strict, ct).ConfigureAwait(false);
                item.Status = check.Status;
                item.SourceHash = check.SourceHash;
                item.DestinationHash = check.DestinationHash;
                item.Error = check.Error;

                switch (item.Status)
                {
                    case "Passed": report.Passed++; report.BytesVerified += size; break;
                    case "Missing": report.Missing++; break;
                    default: report.Corrupted++; break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = "Error";
                item.Error = ex.Message;
                report.Errors++;
            }

            report.Items.Add(item);
            if (i % 10 == 0 || i == sourceFiles.Count - 1) progress?.Report(i + 1);
        }

        report.DestinationOnly = destIndex.Keys.Count(k => !matchedNames.Contains(k));
        sw.Stop();
        report.Elapsed = sw.Elapsed;
        return report;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = dirs.Pop();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { subs = Array.Empty<string>(); }
            foreach (var sub in subs)
            {
                if (!SkipDirNames.Contains(Path.GetFileName(sub))) dirs.Push(sub);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;
        }
    }
}