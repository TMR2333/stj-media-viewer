using System.Security.Cryptography;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 视频"预览代理"：系统解码器（Media Foundation）不支持 4:2:2 / 10-bit H.264、HEVC、ProRes 等格式时，
/// 用 ffmpeg 转出一份 H.264 8-bit 4:2:0 的 720p 代理再播放。
///
/// 代理文件缓存在 <c>&lt;cache&gt;/proxy</c>，键 = 源路径 | 大小 | 修改时间，源文件变了会自动重建。
/// </summary>
public sealed class VideoProxyService
{
    private readonly IFfmpegService _ffmpeg;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);

    public VideoProxyService(IFfmpegService ffmpeg, string cacheRoot)
    {
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        if (string.IsNullOrWhiteSpace(cacheRoot)) throw new ArgumentException("缓存目录不能为空", nameof(cacheRoot));
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public string ProxyDirectory => Path.Combine(_cacheRoot, "proxy");

    public bool IsAvailable => _ffmpeg.IsAvailable;

    /// <summary>
    /// 返回可直接播放的代理文件路径；失败（没有 ffmpeg / 转码失败）返回 null。
    /// 同一个源文件的并发请求会合并。
    /// </summary>
    public async Task<string?> EnsureProxyAsync(string sourcePath, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

        var destination = ProxyPathFor(sourcePath);
        if (IsUsable(destination)) return destination;

        Task<string?> task;
        lock (_inflight)
        {
            if (!_inflight.TryGetValue(destination, out task!))
            {
                task = BuildAsync(sourcePath, destination, ct);
                _inflight[destination] = task;
            }
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_inflight) _inflight.Remove(destination);
        }
    }

    /// <summary>根据源文件推断代理路径（不保证已存在）。</summary>
    public string ProxyPathFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var key = CacheKey(sourcePath, info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        return Path.Combine(ProxyDirectory, key + ".mp4");
    }

    public static string CacheKey(string fullPath, long sizeBytes, DateTime lastWriteUtc)
    {
        var raw = $"{fullPath}|{sizeBytes}|{lastWriteUtc.Ticks}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task<string?> BuildAsync(string sourcePath, string destination, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsUsable(destination)) return destination;

            Directory.CreateDirectory(ProxyDirectory);
            var tmp = destination + ".tmp.mp4";
            try { File.Delete(tmp); } catch { }

            Log.Info($"[proxy] 转码预览代理：{Path.GetFileName(sourcePath)} → {Path.GetFileName(destination)}");
            var ok = await _ffmpeg.TranscodeProxyAsync(sourcePath, tmp, ct).ConfigureAwait(false);
            if (!ok)
            {
                try { File.Delete(tmp); } catch { }
                Log.Warn($"[proxy] 转码失败：{sourcePath}");
                return null;
            }

            File.Move(tmp, destination, overwrite: true);
            Log.Info($"[proxy] 代理就绪：{destination}（{new FileInfo(destination).Length / 1024 / 1024} MB）");
            return destination;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[proxy] 转码异常 {sourcePath}: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }

    private static bool IsUsable(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 4096; }
        catch { return false; }
    }

    /// <summary>清掉代理缓存（设置里「立即清理」会连同缓一起清）。</summary>
    public Task<int> ClearAsync()
    {
        int removed = 0;
        try
        {
            if (Directory.Exists(ProxyDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(ProxyDirectory))
                {
                    try { File.Delete(file); removed++; } catch { }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"[proxy] 清理失败: {ex.Message}"); }
        return Task.FromResult(removed);
    }
}