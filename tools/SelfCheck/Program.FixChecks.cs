using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 回归：①「只看 RAW」被 JPG+RAW 折叠吃掉的问题；② 多选分类只写一张的问题；③ 云同步连接诊断。
    /// </summary>
    private static async Task CheckFilterAndBatchFixes(Services s, string tempData)
    {
        // ---------------- ① 只看 RAW ----------------
        var root = Path.Combine(tempData, "raw-filter");
        Directory.CreateDirectory(root);
        var jpg = Path.Combine(root, "shot.jpg");
        var raw = Path.Combine(root, "shot.arw");
        File.WriteAllBytes(jpg, new byte[64]);
        File.WriteAllBytes(raw, new byte[64]);

        var jpgAsset = NewAsset("raw-filter-jpg", "raw-lib", jpg, "shot.jpg");
        jpgAsset.GroupId = "g-raw-1";
        jpgAsset.IsGroupCover = true;
        var rawAsset = NewAsset("raw-filter-arw", "raw-lib", raw, "shot.arw");
        rawAsset.GroupId = "g-raw-1";
        rawAsset.IsGroupCover = false;
        rawAsset.RawFormat = RawFormat.Arw;
        rawAsset.FormatCode = "ARW";
        await s.Store.UpsertManyAsync(new[] { jpgAsset, rawAsset });

        var collapsed = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "raw-lib",
            CollapseGroups = true,
            Limit = 0,
        });
        Check("只看RAW：默认折叠只显示 1 张", collapsed.Count == 1, $"{collapsed.Count} 条");

        var rawOnly = await s.Store.QueryAsync(new AssetQuery
        {
            LibraryId = "raw-lib",
            CollapseGroups = true,
            IsRaw = true,
            Limit = 0,
        });
        Check("只看RAW：能查到 RAW 文件", rawOnly.Count == 1 && rawOnly[0].FileName == "shot.arw",
              string.Join("/", rawOnly.Select(a => a.FileName)));

        // ---------------- ② 多选分类写入 ----------------
        var lib = "batch-lib";
        var batchDir = Path.Combine(tempData, "batch");
        Directory.CreateDirectory(batchDir);
        var batchIds = new List<string>();
        var batchAssets = new List<MediaAsset>();
        for (int i = 0; i < 3; i++)
        {
            var path = Path.Combine(batchDir, $"b{i}.jpg");
            File.WriteAllBytes(path, new byte[32]);
            var asset = NewAsset($"batch-{i}", lib, path, $"b{i}.jpg");
            batchAssets.Add(asset);
            batchIds.Add(asset.Id);
        }
        await s.Store.UpsertManyAsync(batchAssets);

        var affected = await s.Store.ApplyBatchEditAsync(batchIds, new BatchEditPatch { Theme = "city", Tone = "warm", Rating = 4 });
        var reloadedBatch = await s.Store.GetByIdsAsync(batchIds);
        Check("多选分类：三张都写入（不是只写一张）",
              affected == 3 && reloadedBatch.Count == 3 &&
              reloadedBatch.All(a => a.Theme == "city" && a.Tone == "warm" && a.Rating == 4),
              $"affected={affected}，theme={string.Join(",", reloadedBatch.Select(a => a.Theme))}");

        // ---------------- ③ 连接诊断 ----------------
        var target = new CloudTarget
        {
            Id = "diag-ok",
            Name = "诊断自检",
            Kind = CloudProviderKind.WebDav,
            LocalFolderPath = batchDir,
            WebDavEndpoint = "https://example.invalid/dav/",
        };

        var okService = new CloudSyncService(_ => new DiagnosticStorage(failTest: false), store: s.Store, xmp: s.Xmp);
        var okReport = await okService.DiagnoseAsync(target);
        Check("诊断：全部步骤通过",
              okReport.Ok && okReport.Steps.Count >= 5 &&
              okReport.Steps.Any(st => st.Name.Contains("上传", StringComparison.Ordinal)) &&
              okReport.Steps.Any(st => st.Name.Contains("清理", StringComparison.Ordinal)),
              $"{okReport.Steps.Count} 步 / {okReport.Summary}");

        var badService = new CloudSyncService(_ => new DiagnosticStorage(failTest: true), store: s.Store, xmp: s.Xmp);
        var badReport = await badService.DiagnoseAsync(target);
        Check("诊断：鉴权失败能定位到步骤",
              !badReport.Ok && badReport.Summary.Contains("鉴权", StringComparison.Ordinal),
              badReport.Summary);

        // 诊断日志必须写出 [webdav]/[cloud] 前缀，方便用户在日志文件里搜
        Check("诊断：结论可读", okReport.Summary.Contains("连接正常", StringComparison.Ordinal), okReport.Summary);
    }

    /// <summary>可配置成败的假 WebDAV 存储（诊断自检用）。</summary>
    private sealed class DiagnosticStorage : ICloudStorage
    {
        private readonly bool _failTest;
        private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);

        public DiagnosticStorage(bool failTest) => _failTest = failTest;

        public CloudProviderKind Kind => CloudProviderKind.WebDav;

        public Task<bool> TestAsync(out string? error, CancellationToken ct = default)
        {
            error = _failTest ? "401 Unauthorized（账号或密码不对）" : null;
            return Task.FromResult(!_failTest);
        }

        public Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var list = _files.Select(kv => new CloudEntry
            {
                RelativePath = kv.Key,
                SizeBytes = kv.Value,
                IsDirectory = false,
            }).ToList();
            return Task.FromResult<IReadOnlyList<CloudEntry>>(list);
        }

        public Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default)
        {
            _files[relativePath] = new FileInfo(localPath).Length;
            return Task.CompletedTask;
        }

        public Task EnsureFolderAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string relativePath, CancellationToken ct = default)
        {
            _files.Remove(relativePath);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}