using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.Storage.Streams;

namespace StjMediaViewer.App.Services;

/// <summary>
/// App 侧缩略图加载器：Core 只产出 JPEG 字节，这里负责转成可绑定 ImageSource 并做 LRU 缓存。
///
/// 两个关键设计：
/// 1) **磁盘/解码工作不接受调用方的取消令牌**。列表虚拟化时控件会被快速回收，如果这里跟着取消，
///    缩略图永远生成不出来、缓存也永远不热，界面就一直是占位图。取消只用来决定"要不要更新 UI"，
///    文件与解码照常完成，下一次实现立刻命中缓存。
/// 2) **同 key 合并请求**：59 张卡片 × 回收重试会重复请求同一个缩略图，用 in-flight 表合并。
/// 必须在 UI 线程调用（BitmapImage 要求）。
/// </summary>
public sealed class ThumbnailLoader : IThumbnailLoader
{
    private readonly IThumbnailService _service;
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _gate = new(6, 6);
    private const int MaxEntries = 1200;

    public ThumbnailLoader(IThumbnailService service) => _service = service;

    public async Task<ImageSource?> LoadAsync(MediaAsset asset, int size, CancellationToken ct = default)
    {
        if (asset is null || string.IsNullOrEmpty(asset.Id)) return null;
        var key = $"{asset.Id}@{size}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (asset.IsAudio) return null;

        if (_inflight.TryGetValue(key, out var running))
        {
            try { return await running.ConfigureAwait(true); } catch { return null; }
        }

        var work = LoadCoreAsync(asset, key, size);
        _inflight[key] = work;
        try
        {
            var result = await work.ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            // UI 侧不要这张图了，但磁盘工作已经（或正在）完成，下次直接命中缓存
            return null;
        }
        finally
        {
            _inflight.Remove(key);
        }
    }

    private async Task<ImageSource?> LoadCoreAsync(MediaAsset asset, string key, int size)
    {
        // 关键：传 CancellationToken.None —— 让缩略图真正生成并落盘
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;

            var bytes = await _service.GetThumbnailJpegAsync(asset, size, CancellationToken.None).ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0) return null;

            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            writer.Dispose();
            stream.Seek(0);

            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            await image.SetSourceAsync(stream);
            Remember(key, image);
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"缩略图生成失败 {asset.FileName}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Remember(string key, ImageSource image)
    {
        _cache[key] = image;
        _order.Enqueue(key);
        while (_cache.Count > MaxEntries && _order.Count > 0)
            _cache.Remove(_order.Dequeue());
    }

    public void Invalidate(string assetId)
    {
        foreach (var k in _cache.Keys.Where(k => k.StartsWith(assetId + "@", StringComparison.Ordinal)).ToList())
            _cache.Remove(k);
    }

    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }
}