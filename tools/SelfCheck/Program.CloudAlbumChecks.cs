using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>云相册：按目录列举 / Range 读取 / 缩略图 / 导入素材库 / 导出到云端（用本地挂载目录模拟云端）。</summary>
    private static async Task CheckCloudAlbum(Services s, string tempData)
    {
        var cloudRoot = Path.Combine(tempData, "album-cloud");
        var localLibrary = Path.Combine(tempData, "album-local-library");
        Directory.CreateDirectory(cloudRoot);
        Directory.CreateDirectory(localLibrary);

        // 用样本库里的真实 JPG/RAW 充当云端文件
        var sampleLib = Path.Combine(FindRepoRoot(), "tools", "SampleLibrary", "out", "library");
        var jpg = Directory.Exists(sampleLib)
            ? Directory.EnumerateFiles(sampleLib, "*.jpg", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        var raw = Directory.Exists(sampleLib)
            ? Directory.EnumerateFiles(sampleLib, "*.arw", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (jpg is null || raw is null)
        {
            Check("云相册：找到样本 JPG/RAW", false, "样本库缺失，先运行 tools/SampleLibrary/generate.py");
            return;
        }
        Check("云相册：找到样本 JPG/RAW", true, $"{Path.GetFileName(jpg)} / {Path.GetFileName(raw)}");

        File.Copy(jpg, Path.Combine(cloudRoot, "sample.jpg"), overwrite: true);
        File.Copy(raw, Path.Combine(cloudRoot, "sample.arw"), overwrite: true);
        Directory.CreateDirectory(Path.Combine(cloudRoot, "sub"));
        File.Copy(jpg, Path.Combine(cloudRoot, "sub", "nested.jpg"), overwrite: true);

        var target = new CloudTarget
        {
            Kind = CloudProviderKind.LocalFolder,
            Name = "云相册测试",
            LocalFolderPath = Path.Combine(tempData, "album-sync-local"),
            RemoteFolderPath = cloudRoot,
            Direction = CloudSyncDirection.Bidirectional,
        };
        Directory.CreateDirectory(target.LocalFolderPath);

        var svc = new CloudAlbumService(_ => new LocalFolderCloudStorage(target), s.Thumbnails, Path.Combine(tempData, "album-appdata"));

        var rootEntries = await svc.ListFolderAsync(target, "");
        Check("云相册：按目录列举只返回一层",
              rootEntries.Any(e => e.FileName == "sample.jpg") &&
              rootEntries.Any(e => e.IsDirectory && e.FileName == "sub") &&
              rootEntries.All(e => !e.RelativePath.Contains("nested")),
              $"条目 {rootEntries.Count}（{string.Join(",", rootEntries.Select(e => e.FileName).Take(4))}）");

        var subEntries = await svc.ListFolderAsync(target, "sub");
        Check("云相册：进入子目录",
              subEntries.Count == 1 && subEntries[0].FileName == "nested.jpg",
              $"sub 条目 {subEntries.Count}");

        await using (var storage = new LocalFolderCloudStorage(target))
        {
            var head = await storage.ReadRangeAsync("sample.jpg", 0, 64);
            var fileHead = await File.ReadAllBytesAsync(Path.Combine(cloudRoot, "sample.jpg"));
            Check("云相册：Range 读取与本地文件一致",
                  head is { Length: 64 } && head.SequenceEqual(fileHead.Take(64)),
                  $"读回 {head?.Length ?? 0} 字节");
        }

        var jpgEntry = rootEntries.First(e => e.FileName == "sample.jpg");
        var rawEntry = rootEntries.First(e => e.FileName == "sample.arw");
        var jpgThumb = await svc.GetThumbnailPathAsync(target, jpgEntry, 320);
        var rawThumb = await svc.GetThumbnailPathAsync(target, rawEntry, 320);
        Check("云相册：JPG 缩略图",
              jpgThumb is not null && File.Exists(jpgThumb) && new FileInfo(jpgThumb).Length > 1024,
              jpgThumb is null ? "未生成" : $"{new FileInfo(jpgThumb).Length} 字节");
        Check("云相册：RAW 缩略图（内嵌预览）",
              rawThumb is not null && File.Exists(rawThumb) && new FileInfo(rawThumb).Length > 1024,
              rawThumb is null ? "未生成（该 RAW 可能没有内嵌预览）" : $"{new FileInfo(rawThumb).Length} 字节");

        var libraryRoot = new LibraryRoot { Id = "album-lib", Name = "云相册导入", Path = localLibrary };
        var import = await svc.ImportAsync(target, new[] { jpgEntry, rawEntry }, libraryRoot);
        var importedJpg = Path.Combine(localLibrary, "sample.jpg");
        var importedRaw = Path.Combine(localLibrary, "sample.arw");
        Check("云相册：导入到本地素材库",
              import.Downloaded == 2 && import.Failed == 0 &&
              File.Exists(importedJpg) && File.Exists(importedRaw) &&
              new FileInfo(importedJpg).Length == new FileInfo(Path.Combine(cloudRoot, "sample.jpg")).Length,
              import.Describe());

        // 导出：把一个本地素材上传到子目录，并验证文件与边车
        var exportJpg = Path.Combine(localLibrary, "upload.jpg");
        File.Copy(jpg, exportJpg, overwrite: true);
        var sidecar = exportJpg + ".xmp";
        await File.WriteAllTextAsync(sidecar, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        var asset = new MediaAsset
        {
            Id = "album-export",
            FullPath = exportJpg,
            FileName = "upload.jpg",
            SizeBytes = new FileInfo(exportJpg).Length,
            Kind = MediaKind.Image,
            FormatCode = "JPG",
            Sidecars = new List<string> { sidecar },
        };
        var export = await svc.ExportAsync(target, new[] { asset }, "exported");
        Check("云相册：导出素材 + XMP 边车",
              export.Uploaded == 2 && export.Failed == 0 &&
              File.Exists(Path.Combine(cloudRoot, "exported", "upload.jpg")) &&
              File.Exists(Path.Combine(cloudRoot, "exported", "upload.jpg.xmp")),
              export.Describe());

        // 不支持 Range 的适配器：默认实现返回 null（调用方退化整文件下载）
        ICloudStorage unsupportedStorage = new NoRangeStorage(target);
        var unsupported = await unsupportedStorage.ReadRangeAsync("sample.jpg", 0, 16);
        Check("云相册：不支持 Range 的适配器安全降级", unsupported is null, "返回 null");
    }

    private sealed class NoRangeStorage : ICloudStorage
    {
        private readonly CloudTarget _target;
        public NoRangeStorage(CloudTarget target) => _target = target;
        public CloudProviderKind Kind => _target.Kind;
        public Task<bool> TestAsync(out string? error, CancellationToken ct = default) { error = null; return Task.FromResult(true); }
        public Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudEntry>>(Array.Empty<CloudEntry>());
        public Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureFolderAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}