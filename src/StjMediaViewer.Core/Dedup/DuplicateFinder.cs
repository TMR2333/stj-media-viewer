using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Dedup;

/// <summary>
/// 重复 / 近重复检测。
/// <list type="number">
/// <item>按 Size 粗分桶（只有大小相同的文件才可能完全相同）</item>
/// <item>桶内算 QuickHash（头尾 512KB），(Size,QuickHash) 再分桶 → 候选集</item>
/// <item>候选算 SHA-256 FullHash → 精确重复组（Exact）</item>
/// <item>可选近重复：按画面比例分桶，图片解码到 8×8 算 aHash，汉明距离 ≤ 6 归为同组</item>
/// </list>
/// 全程不抛异常（取消除外），单个文件 IO 失败只是跳过它。
/// </summary>
public sealed class DuplicateFinder : IDuplicateFinder
{
    /// <summary>近重复比较的汉明距离阈值。</summary>
    private const int NearDuplicateThreshold = 6;

    /// <summary>单次调用最多解码多少张图算 aHash（防止 10 万张库把磁盘打满）。</summary>
    private const int MaxNearHashDecodes = 8000;

    /// <summary>一个比例桶最多两两比较多少张（O(n²) 保护）。</summary>
    private const int MaxBucketForNearCompare = 1500;

    private readonly IMediaDecoder _decoder;

    public DuplicateFinder(IMediaDecoder decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
    }

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyList<MediaAsset> assets,
        bool includeNearDuplicates = true,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var groups = new List<DuplicateGroup>();
        try
        {
            var list = (assets ?? Array.Empty<MediaAsset>())
                .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.FullPath))
                .GroupBy(a => a.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            Report(progress, 1);
            if (list.Count < 2) { Report(progress, 100); return groups; }

            // ---------- 1) Size 粗桶 ----------
            var sizeCandidates = list
                .GroupBy(a => a.SizeBytes)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            if (sizeCandidates.Count < 2) { Report(progress, 100); return groups; }
            Report(progress, 5);

            // ---------- 2) QuickHash ----------
            await FillHashesAsync(sizeCandidates, quick: true, ct, progress, 8, 40).ConfigureAwait(false);

            var quickCandidates = sizeCandidates
                .Where(a => !string.IsNullOrEmpty(a.QuickHash))
                .GroupBy(a => a.QuickHash!)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            if (quickCandidates.Count < 2) { Report(progress, 100); return groups; }

            // ---------- 3) FullHash 精确去重 ----------
            await FillHashesAsync(quickCandidates, quick: false, ct, progress, 42, 78).ConfigureAwait(false);

            var hashedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in quickCandidates.Where(a => !string.IsNullOrEmpty(a.FullHash))
                         .GroupBy(a => a.FullHash!)
                         .Where(g => g.Count() > 1))
            {
                ct.ThrowIfCancellationRequested();
                var items = g.ToList();
                groups.Add(CreateGroup(DuplicateKind.Exact, items, "SHA-256 完全相同"));
                foreach (var a in items) hashedPaths.Add(a.FullPath);
            }

            // 文件读不了时兜底：大小 + 快指纹相同也判定为同内容
            foreach (var g in quickCandidates.Where(a => string.IsNullOrEmpty(a.FullHash))
                         .GroupBy(a => a.QuickHash!)
                         .Where(g => g.Count() > 1))
            {
                ct.ThrowIfCancellationRequested();
                var items = g.ToList();
                groups.Add(CreateGroup(DuplicateKind.SameContent, items, "大小+快速指纹相同"));
                foreach (var a in items) hashedPaths.Add(a.FullPath);
            }

            Report(progress, 80);

            // ---------- 4) 近重复 ----------
            if (includeNearDuplicates)
            {
                var near = await FindNearDuplicatesAsync(list, hashedPaths, progress, ct).ConfigureAwait(false);
                groups.AddRange(near);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("[dedup] find duplicates failed", ex);
        }

        Report(progress, 100);
        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    // ==================================================================== 精确哈希

    private static async Task FillHashesAsync(
        List<MediaAsset> items, bool quick, CancellationToken ct, IProgress<int>? progress, int fromPct, int toPct)
    {
        int total = items.Count;
        if (total == 0) return;
        int done = 0;

        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct,
        }, async (a, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (quick)
                {
                    if (string.IsNullOrEmpty(a.QuickHash)) a.QuickHash = FileHasher.QuickHash(a.FullPath, token);
                }
                else
                {
                    if (string.IsNullOrEmpty(a.FullHash)) a.FullHash = FileHasher.FullHash(a.FullPath, token);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[dedup] hash failed {a.FullPath}: {ex.Message}");
            }

            int d = Interlocked.Increment(ref done);
            if (d % 16 == 0 || d == total)
                Report(progress, fromPct + (int)((long)(toPct - fromPct) * d / total));
            await ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    // ==================================================================== 近重复

    private async Task<List<DuplicateGroup>> FindNearDuplicatesAsync(
        List<MediaAsset> list,
        HashSet<string> alreadyGrouped,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var result = new List<DuplicateGroup>();
        var images = list
            .Where(a => !alreadyGrouped.Contains(a.FullPath))
            .Where(a => a.Kind == MediaKind.Image || a.RawFormat != RawFormat.None)
            .ToList();
        if (images.Count < 2) return result;

        // 画面比例分桶（同一构图的重压缩/缩放版本比例一致）
        var buckets = images
            .GroupBy(AspectBucketKey)
            .Where(g => g.Count() > 1)
            .ToList();

        int decodeBudget = MaxNearHashDecodes;
        int processedBuckets = 0;

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();
            if (decodeBudget <= 0) break;

            var items = bucket.Take(MaxBucketForNearCompare).ToList();
            if (items.Count < 2) continue;
            if (items.Count > decodeBudget) items = items.Take(decodeBudget).ToList();
            decodeBudget -= items.Count;

            // 计算 aHash
            var hashes = new ulong[items.Count];
            var failed = new bool[items.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = ct,
            }, async (i, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var img = await _decoder.DecodeAsync(items[i].FullPath, 8, token).ConfigureAwait(false);
                    if (img is null) { failed[i] = true; return; }
                    hashes[i] = PerceptualHash.AverageHash(img);
                }
                catch (OperationCanceledException) { throw; }
                catch { failed[i] = true; }
            }).ConfigureAwait(false);

            // 并查集归组
            var parent = new int[items.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (failed[i]) continue;
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (failed[j]) continue;
                    if (PerceptualHash.Hamming(hashes[i], hashes[j]) <= NearDuplicateThreshold)
                        Union(i, j);
                }
            }

            var components = new Dictionary<int, List<MediaAsset>>();
            for (int i = 0; i < items.Count; i++)
            {
                if (failed[i]) continue;
                int root = Find(i);
                if (!components.TryGetValue(root, out var comp)) { comp = new List<MediaAsset>(); components[root] = comp; }
                comp.Add(items[i]);
            }

            foreach (var comp in components.Values.Where(c => c.Count > 1))
                result.Add(CreateGroup(DuplicateKind.NearDuplicate, comp, $"感知哈希相近（汉明距离≤{NearDuplicateThreshold}）"));

            processedBuckets++;
            if (processedBuckets % 8 == 0)
                Report(progress, 80 + Math.Min(18, processedBuckets * 2));
        }
        return result;
    }

    private static string AspectBucketKey(MediaAsset a)
    {
        double? ar = a.AspectRatio;
        if (ar is > 0) return "ar:" + Math.Round(ar.Value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        // 没有尺寸信息时至少把同类格式放一起，避免跨桶彻底失联
        return "fmt:" + (a.FormatCode ?? "");
    }

    // ==================================================================== 组构造

    private static DuplicateGroup CreateGroup(DuplicateKind kind, List<MediaAsset> items, string reason)
    {
        long total = 0, max = 0;
        foreach (var a in items)
        {
            total += Math.Max(0, a.SizeBytes);
            if (a.SizeBytes > max) max = a.SizeBytes;
        }
        return new DuplicateGroup
        {
            Kind = kind,
            Assets = items.OrderByDescending(a => a.SizeBytes).ToList(),
            WastedBytes = Math.Max(0, total - max),
            Reason = reason,
        };
    }

    private static void Report(IProgress<int>? progress, int pct)
    {
        try { progress?.Report(Math.Clamp(pct, 0, 100)); } catch { }
    }
}