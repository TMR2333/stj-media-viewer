using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 缩略图服务：磁盘缓存 + 按需生成。
/// 缓存键 = sha1(绝对路径|文件大小|修改时间) + "_" + 尺寸 + ".jpg"（任务书约定格式），
/// 命中直接读磁盘（几 ms），未命中解码→编码→原子落盘。
/// 同键并发请求合并（每键一把异步锁），避免网格快速滚动时重复解码同一张图。
/// </summary>
public sealed class ThumbnailService : IThumbnailService
{
    private const int DefaultQuality = 85;
    private const int MinSize = 32;
    private const int MaxSize = 2048;

    private readonly IMediaDecoder _decoder;
    private readonly IImageEncoder _encoder;
    private readonly string _cacheDir;

    /// <summary>解码/编码管线的版本号：改了图像解码逻辑就 +1，让旧缓存自然失效。</summary>
    private const string CacheRevision = "d2";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    /// <summary>失败负缓存：坏文件/无缩略图文件在 TTL 内不再重复解码（网格滚动时会反复请求同一张）。</summary>
    private readonly ConcurrentDictionary<string, long> _failed = new(StringComparer.Ordinal);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(5);

    /// <param name="cacheDirectory">缩略图缓存目录，由 App 传入（settings.AppDataDirectory 下）。</param>
    public ThumbnailService(IMediaDecoder decoder, IImageEncoder encoder, string cacheDirectory)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("缩略图缓存目录不能为空", nameof(cacheDirectory));
        _cacheDir = Path.GetFullPath(cacheDirectory);
    }

    public string CacheDirectory => _cacheDir;

    // ==================================================================== 生成 / 命中

    public async Task<byte[]?> GetThumbnailJpegAsync(MediaAsset asset, int maxSize, CancellationToken ct = default)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.FullPath)) return null;
        int size = ClampSize(maxSize);
        try
        {
            var path = CachePath(asset, size);
            var cached = TryReadCached(path);
            if (cached is not null) return cached;
            if (IsRecentlyFailed(path)) return null;

            var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                cached = TryReadCached(path);          // 双检：排队期间别的请求可能已生成
                if (cached is not null) return cached;

                var image = await _decoder.DecodeAsync(asset.FullPath, size, ct).ConfigureAwait(false);
                if (image is null)
                {
                    MarkFailed(path);
                    return null;
                }

                var jpeg = await _encoder.EncodeJpegAsync(image, DefaultQuality, ct).ConfigureAwait(false);
                if (jpeg is null || jpeg.Length == 0)
                {
                    MarkFailed(path);
                    return null;
                }

                _failed.TryRemove(path, out _);
                WriteAtomic(path, jpeg);
                asset.ThumbKey ??= KeyBase(asset);
                return jpeg;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[thumb] generate failed {asset.FullPath}: {ex.GetType().Name}: {ex.Message}");
            var failedPath = SafeCachePath(asset, size);
            if (failedPath is not null) MarkFailed(failedPath);
            return null;
        }
        finally
        {
            // 防止字典无限增长：请求完成且无人等待时移除
            var key = SafeCachePath(asset, size);
            if (key is not null && _gates.TryGetValue(key, out var g) && g.CurrentCount == 1)
                _gates.TryRemove(key, out _);
        }
    }

    public async Task<string?> GetThumbnailPathAsync(MediaAsset asset, int maxSize, CancellationToken ct = default)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.FullPath)) return null;
        var path = CachePath(asset, ClampSize(maxSize));
        if (File.Exists(path)) return path;
        var bytes = await GetThumbnailJpegAsync(asset, maxSize, ct).ConfigureAwait(false);
        return bytes is null ? null : path;
    }

    // ==================================================================== 失效 / 缓存管理

    public Task InvalidateAsync(MediaAsset asset)
    {
        if (asset is null) return Task.CompletedTask;
        try
        {
            var baseKey = KeyBase(asset);
            if (Directory.Exists(_cacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(_cacheDir, baseKey + "_*.jpg"))
                    TryDelete(f);
                foreach (var f in Directory.EnumerateFiles(_cacheDir, baseKey + "_*.tmp"))
                    TryDelete(f);
            }
            asset.ThumbKey = null;
            _failed.Clear();
        }
        catch (Exception ex)
        {
            Log.Warn($"[thumb] invalidate failed {asset.FullPath}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task<long> GetCacheSizeBytesAsync(CancellationToken ct = default)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(_cacheDir)) return Task.FromResult(0L);
            foreach (var f in Directory.EnumerateFiles(_cacheDir, "*.jpg"))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[thumb] cache size failed: {ex.Message}");
        }
        return Task.FromResult(total);
    }

    public Task ClearCacheAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return Task.CompletedTask;
            foreach (var f in Directory.EnumerateFiles(_cacheDir))
            {
                ct.ThrowIfCancellationRequested();
                TryDelete(f);
            }
            // 清空目录内的子目录（防手滑只删本层文件）
            foreach (var d in Directory.EnumerateDirectories(_cacheDir))
            {
                ct.ThrowIfCancellationRequested();
                try { Directory.Delete(d, true); } catch { }
            }
            _failed.Clear();
            Log.Info("[thumb] cache cleared");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[thumb] clear cache failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // ==================================================================== 键与 IO

    /// <summary>sha1(路径 + 大小 + 修改时间)：路径或内容变化即换键。</summary>
    public static string KeyBase(MediaAsset asset)
    {
        long size = asset.SizeBytes;
        long mtime = asset.FileModified.ToUnixTimeMilliseconds();
        if (size <= 0 || mtime == 0)
        {
            try
            {
                var fi = new FileInfo(asset.FullPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                }
            }
            catch { }
        }
        var text = $"{Path.GetFullPath(asset.FullPath).ToLowerInvariant()}|{size}|{mtime}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CachePath(MediaAsset asset, int size)
    {
        TryPrepareDirectory();
        return Path.Combine(_cacheDir, $"{KeyBase(asset)}_{CacheRevision}_{size}.jpg");
    }

    private string? SafeCachePath(MediaAsset asset, int size)
    {
        try { return Path.Combine(_cacheDir, $"{KeyBase(asset)}_{CacheRevision}_{size}.jpg"); }
        catch { return null; }
    }

    private void TryPrepareDirectory()
    {
        try { Directory.CreateDirectory(_cacheDir); }
        catch (Exception ex) { Log.Warn($"[thumb] cannot create cache dir {_cacheDir}: {ex.Message}"); }
    }

    /// <summary>
    /// 读缓存并做一次便宜的完整性校验：只认 SOI(FFD8) 开头且以 EOI(FFD9) 结尾的 JPEG。
    /// 进程被强杀时可能留下半截文件，WIC 拿半截 JPEG 去解码是已知的原生崩溃源，
    /// 这里直接判定为无效并删掉重生成。
    /// </summary>
    private static byte[]? TryReadCached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            if (fi.Length < 4) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 ||
                bytes[^2] != 0xFF || bytes[^1] != 0xD9)
            {
                try { File.Delete(path); } catch { }
                return null;
            }

            return bytes;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch { return null; }
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        try
        {
            TryPrepareDirectory();
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[thumb] write cache failed {path}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------ 负缓存

    private bool IsRecentlyFailed(string path)
    {
        if (!_failed.TryGetValue(path, out long stamp)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp < (long)FailureTtl.TotalMilliseconds) return true;
        _failed.TryRemove(path, out _);
        return false;
    }

    private void MarkFailed(string path)
    {
        if (_failed.Count > 20_000) _failed.Clear();      // 防御性上限，避免长会话无限增长
        _failed[path] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static int ClampSize(int maxSize) => Math.Clamp(maxSize <= 0 ? 320 : maxSize, MinSize, MaxSize);
}