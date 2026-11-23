using System.Security.Cryptography;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Cloud;

/// <summary>
/// 云相册：在云同步之上提供"浏览 / 预览 / 导入 / 导出"。
/// 浏览只列一层目录；缩略图优先用 Range 读取内嵌预览，失败再整文件下载；
/// 导入=下载到本地素材库目录（随后由调用方触发扫描），导出=上传本地素材 + XMP 边车。
/// </summary>
public sealed class CloudAlbumService
{
    /// <summary>小于这个大小的文件直接整文件下载再生成缩略图。</summary>
    private const long FullDownloadLimit = 12L * 1024 * 1024;
    /// <summary>大文件（RAW/视频）最多整文件下载的上限，超过就不做缩略图。</summary>
    private const long HardDownloadLimit = 48L * 1024 * 1024;
    /// <summary>解析 TIFF IFD 需要读取的文件头大小。</summary>
    private const int HeadReadSize = 2 * 1024 * 1024;

    private readonly Func<CloudTarget, ICloudStorage> _storageFactory;
    private readonly IThumbnailService _thumbnails;
    private readonly string _cacheDir;
    private readonly string _tempDir;

    public CloudAlbumService(Func<CloudTarget, ICloudStorage> storageFactory, IThumbnailService thumbnails, string appDataDirectory)
    {
        _storageFactory = storageFactory ?? throw new ArgumentNullException(nameof(storageFactory));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _cacheDir = Path.Combine(appDataDirectory, "Cache", "cloud-album");
        _tempDir = Path.Combine(appDataDirectory, "Cache", "cloud-album-tmp");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_tempDir);
    }

    // ==================================================================================
    //  浏览
    // ==================================================================================

    public async Task<IReadOnlyList<CloudEntry>> ListFolderAsync(
        CloudTarget target, string relativeDir, CancellationToken ct = default)
    {
        await using var storage = _storageFactory(target);
        try
        {
            return await storage.ListFolderAsync(relativeDir, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // 适配器不支持按目录列举：退化为全部列举后过滤一层
            var all = await storage.ListAllAsync(null, ct).ConfigureAwait(false);
            var dir = (relativeDir ?? "").Replace('\\', '/').Trim('/');
            return all.Where(e =>
            {
                var parent = e.RelativePath.Contains('/')
                    ? e.RelativePath[..e.RelativePath.LastIndexOf('/')]
                    : "";
                return string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
    }

    // ==================================================================================
    //  缩略图 / 预览
    // ==================================================================================

    /// <summary>返回本地缓存的缩略图 JPEG 路径；不支持的类型返回 null（UI 显示占位）。</summary>
    public async Task<string?> GetThumbnailPathAsync(
        CloudTarget target, CloudEntry entry, int maxSize = 320, CancellationToken ct = default)
    {
        if (entry.IsDirectory) return null;
        var key = CacheKey(entry, maxSize);
        var cachePath = Path.Combine(_cacheDir, key + ".jpg");
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0) return cachePath;

        var temp = Path.Combine(_tempDir, "cloud-" + Guid.NewGuid().ToString("N")[..10] + Path.GetExtension(entry.FileName));
        bool downloaded = false;
        try
        {
            await using var storage = _storageFactory(target);

            if (entry.SizeBytes > 0 && entry.SizeBytes <= FullDownloadLimit)
            {
                await storage.DownloadAsync(entry.RelativePath, temp, ct).ConfigureAwait(false);
                downloaded = true;
            }
            else if (entry.SizeBytes > 0)
            {
                // 大文件：先尝试 Range 取内嵌预览（RAW 的 TIFF IFD 快路径）
                var head = await storage.ReadRangeAsync(entry.RelativePath, 0, HeadReadSize, ct).ConfigureAwait(false);
                if (head is { Length: > 16 } &&
                    TryGetTiffPreviewOffset(head, entry.SizeBytes, out var offset, out var length) &&
                    length is > 2 * 1024 and < 20 * 1024 * 1024)
                {
                    var preview = offset == 0 && head.Length >= length
                        ? head.AsSpan(0, (int)length).ToArray()
                        : await storage.ReadRangeAsync(entry.RelativePath, offset, (int)length, ct).ConfigureAwait(false);
                    if (preview is { Length: > 2 * 1024 } && preview[0] == 0xFF && preview[1] == 0xD8)
                    {
                        await File.WriteAllBytesAsync(temp, preview, ct).ConfigureAwait(false);
                        downloaded = true;
                    }
                }

                if (!downloaded && entry.SizeBytes <= HardDownloadLimit)
                {
                    await storage.DownloadAsync(entry.RelativePath, temp, ct).ConfigureAwait(false);
                    downloaded = true;
                }
            }

            if (!downloaded) return null;

            var (kind, format, raw) = DetectKind(entry.FileName);
            var asset = new MediaAsset
            {
                Id = key,
                FullPath = temp,
                FileName = entry.FileName,
                SizeBytes = entry.SizeBytes,
                Kind = kind,
                FormatCode = format,
                RawFormat = raw,
                FileModified = entry.ModifiedUtc.UtcDateTime,
            };
            var jpeg = await _thumbnails.GetThumbnailJpegAsync(asset, maxSize, null, "cloud", ct).ConfigureAwait(false);
            if (jpeg is not { Length: > 0 }) return null;

            var tmpOut = cachePath + ".tmp";
            await File.WriteAllBytesAsync(tmpOut, jpeg, ct).ConfigureAwait(false);
            File.Move(tmpOut, cachePath, overwrite: true);
            return cachePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[cloud-album] 缩略图失败 {entry.RelativePath}：{ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>下载整文件到临时路径（预览用）；调用方负责删除。</summary>
    public async Task<string> DownloadFullAsync(CloudTarget target, CloudEntry entry, CancellationToken ct = default)
    {
        var temp = Path.Combine(_tempDir, "preview-" + Guid.NewGuid().ToString("N")[..10] + Path.GetExtension(entry.FileName));
        await using var storage = _storageFactory(target);
        await storage.DownloadAsync(entry.RelativePath, temp, ct).ConfigureAwait(false);
        return temp;
    }

    // ==================================================================================
    //  导入（云 → 本地素材库）
    // ==================================================================================

    public sealed class ImportResult
    {
        public int Downloaded { get; set; }
        public int Failed { get; set; }
        public long Bytes { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> Messages { get; } = new();
        public string Describe() => $"下载 {Downloaded} 个文件" +
            (Bytes > 0 ? $"（{FormatBytes(Bytes)}）" : "") +
            (Failed > 0 ? $" · 失败 {Failed}" : "") + $" · 用时 {Elapsed.TotalSeconds:0.0}s";
    }

    /// <summary>把云端条目下载到本地素材库根目录（保持相对目录结构）。</summary>
    public async Task<ImportResult> ImportAsync(
        CloudTarget target, IReadOnlyList<CloudEntry> entries, LibraryRoot libraryRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = Path.GetFullPath(libraryRoot.Path);
        Directory.CreateDirectory(root);
        await using var storage = _storageFactory(target);

        int index = 0;
        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report($"下载 {index}/{entries.Count}：{entry.FileName}");
            var relative = entry.RelativePath.Replace('\\', '/');
            var local = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!local.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(local, root, StringComparison.OrdinalIgnoreCase))
            {
                result.Failed++;
                result.Messages.Add($"跳过越界路径：{relative}");
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await storage.DownloadAsync(entry.RelativePath, local, ct).ConfigureAwait(false);
                result.Downloaded++;
                result.Bytes += entry.SizeBytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Messages.Count < 10) result.Messages.Add($"{entry.FileName}：{ex.Message}");
            }
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        Log.Info($"[cloud-album] 导入 {target.Name} → {libraryRoot.Path}：{result.Describe()}");
        return result;
    }

    // ==================================================================================
    //  导出（本地素材库 → 云相册）
    // ==================================================================================

    public sealed class ExportResult
    {
        public int Uploaded { get; set; }
        public int Failed { get; set; }
        public long Bytes { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> Messages { get; } = new();
        public string Describe() => $"上传 {Uploaded} 个文件" +
            (Bytes > 0 ? $"（{FormatBytes(Bytes)}）" : "") +
            (Failed > 0 ? $" · 失败 {Failed}" : "") + $" · 用时 {Elapsed.TotalSeconds:0.0}s";
    }

    /// <summary>把本地素材（含 XMP 边车）上传到云相册的指定目录（扁平存放，同名覆盖）。</summary>
    public async Task<ExportResult> ExportAsync(
        CloudTarget target, IReadOnlyList<MediaAsset> assets, string remoteDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new ExportResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var storage = _storageFactory(target);
        var dir = (remoteDir ?? "").Replace('\\', '/').Trim('/');

        int index = 0;
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report($"上传 {index}/{assets.Count}：{asset.FileName}");
            if (string.IsNullOrWhiteSpace(asset.FullPath) || !File.Exists(asset.FullPath))
            {
                result.Failed++;
                continue;
            }

            try
            {
                if (dir.Length > 0) await storage.EnsureFolderAsync(dir, ct).ConfigureAwait(false);
                await storage.UploadAsync(asset.FullPath,
                    dir.Length == 0 ? asset.FileName : dir + "/" + asset.FileName, ct).ConfigureAwait(false);
                result.Uploaded++;
                result.Bytes += new FileInfo(asset.FullPath).Length;

                foreach (var sidecar in asset.Sidecars ?? new List<string>())
                {
                    if (!File.Exists(sidecar)) continue;
                    var sideName = Path.GetFileName(sidecar);
                    await storage.UploadAsync(sidecar, dir.Length == 0 ? sideName : dir + "/" + sideName, ct)
                        .ConfigureAwait(false);
                    result.Uploaded++;
                    result.Bytes += new FileInfo(sidecar).Length;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Messages.Count < 10) result.Messages.Add($"{asset.FileName}：{ex.Message}");
            }
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        Log.Info($"[cloud-album] 导出 → {target.Name}/{dir}：{result.Describe()}");
        return result;
    }

    // ==================================================================================
    //  工具
    // ==================================================================================

    private static (MediaKind Kind, string Format, RawFormat Raw) DetectKind(string fileName)
    {
        if (MediaTypeCatalog.TryGet(fileName, out var kind, out var format, out _, out var raw, out _))
            return (kind, format, raw);
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return (MediaKind.Unknown, ext, RawFormat.None);
    }

    private static string CacheKey(CloudEntry entry, int maxSize)
    {
        var raw = $"{entry.RelativePath}|{entry.SizeBytes}|{entry.ModifiedUtc.UtcTicks}|{maxSize}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>从 TIFF 头解析 JPEGInterchangeFormat/Length（0x0201/0x0202），取第一个带尺寸的缩略图。</summary>
    private static bool TryGetTiffPreviewOffset(byte[] head, long fileSize, out long offset, out long length)
    {
        offset = 0;
        length = 0;
        if (head.Length < 16) return false;
        bool little;
        if (head[0] == 'I' && head[1] == 'I') little = true;
        else if (head[0] == 'M' && head[1] == 'M') little = false;
        else return false;

        long ifd = ReadU32(head, 4, little);
        var visited = new HashSet<long>();
        for (int depth = 0; depth < 8 && ifd > 0 && ifd + 2 <= head.Length; depth++)
        {
            if (!visited.Add(ifd)) return false;
            int count = ReadU16(head, (int)ifd, little);
            long entriesStart = ifd + 2;
            long next = 0;
            for (int i = 0; i < count; i++)
            {
                long e = entriesStart + i * 12L;
                if (e + 12 > head.Length) return false;
                int tag = ReadU16(head, (int)e, little);
                int type = ReadU16(head, (int)e + 2, little);
                long value = ReadU32(head, (int)e + 8, little);
                if (tag == 0x0201 && (type == 4 || type == 3)) offset = value;
                if (tag == 0x0202 && (type == 4 || type == 3)) length = value;
            }
            if (offset > 8 && length > 0 && offset + length <= fileSize) return true;
            next = ReadU32(head, (int)(entriesStart + count * 12L), little);
            ifd = next;
        }
        return false;
    }

    private static int ReadU16(byte[] buffer, int offset, bool little)
    {
        if (offset + 1 >= buffer.Length) return 0;
        return little
            ? buffer[offset] | (buffer[offset + 1] << 8)
            : (buffer[offset] << 8) | buffer[offset + 1];
    }

    private static long ReadU32(byte[] buffer, int offset, bool little)
    {
        if (offset + 3 >= buffer.Length) return 0;
        return little
            ? buffer[offset] | ((long)buffer[offset + 1] << 8) | ((long)buffer[offset + 2] << 16) | ((long)buffer[offset + 3] << 24)
            : ((long)buffer[offset] << 24) | ((long)buffer[offset + 1] << 16) | ((long)buffer[offset + 2] << 8) | buffer[offset + 3];
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}