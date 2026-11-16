using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

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

    private static readonly bool DisableThumbs =
        Environment.GetEnvironmentVariable("STJ_DISABLE_THUMBS") == "1";

    public ThumbnailLoader(IThumbnailService service) => _service = service;

    public async Task<ImageSource?> LoadAsync(MediaAsset asset, int size, CancellationToken ct = default)
    {
        if (asset is null || string.IsNullOrEmpty(asset.Id)) return null;
        // 诊断开关：STJ_DISABLE_THUMBS=1 时完全不解码缩略图（定位崩溃用）
        if (DisableThumbs) return null;
        // LUT 参与缓存键：同一素材换 LUT 是另一张图
        var lutEntry = ResolveLut(asset);
        var key = $"{asset.Id}@{size}@{lutEntry?.Id ?? "-"}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (asset.IsAudio) return null;

        if (_inflight.TryGetValue(key, out var running))
        {
            try { return await running.ConfigureAwait(true); } catch { return null; }
        }

        var work = LoadCoreAsync(asset, key, size, lutEntry);
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

    private static LutEntry? ResolveLut(MediaAsset asset)
    {
        try { return AppServices.Luts?.ResolveForAsset(asset); }
        catch { return null; }
    }

    private async Task<ImageSource?> LoadCoreAsync(MediaAsset asset, string key, int size, LutEntry? lutEntry)
    {
        // 关键：传 CancellationToken.None —— 让缩略图真正生成并落盘
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;

            // 让 Core 生成（或命中）磁盘缓存，然后按"文件"加载：
            // 不要用 InMemoryRandomAccessStream + SetSourceAsync 再立刻释放流 ——
            // BitmapImage 是延迟解码的，流被释放后渲染线程读已释放内存会直接原生崩溃。
            var lut = lutEntry is null ? null : await AppServices.Luts.LoadCubeAsync(lutEntry).ConfigureAwait(true);
            var path = await _service.GetThumbnailPathAsync(asset, size, lut, lutEntry?.Id, CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var image = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = Math.Max(64, size),
                UriSource = new Uri(path),
            };
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