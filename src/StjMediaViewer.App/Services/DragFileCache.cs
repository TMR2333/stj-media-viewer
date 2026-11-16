using System.Collections.Concurrent;
using Windows.Storage;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 拖拽到外部软件需要在同步的 DragItemsStarting 里交出 StorageFile，
/// 而 <see cref="StorageFile.GetFileFromPathAsync"/> 是异步的 —— 这里提前把可见卡片的
/// StorageFile 预热好（命中即秒开），未命中时同步等待一个很短的超时作为兜底。
/// </summary>
public static class DragFileCache
{
    private static readonly ConcurrentDictionary<string, Task<StorageFile?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>预热若干路径（卡片进入视野时调用）。</summary>
    public static void Preload(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            _cache.GetOrAdd(path!, p => Task.Run(async () =>
            {
                try { return await StorageFile.GetFileFromPathAsync(p); }
                catch { return (StorageFile?)null; }
            }));
        }
    }

    /// <summary>同步取 StorageFile（最多等 timeoutMs，通常命中缓存立即返回）。</summary>
    public static StorageFile? Take(string path, int timeoutMs = 1500)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!_cache.TryGetValue(path, out var task))
        {
            task = Task.Run(async () =>
            {
                try { return await StorageFile.GetFileFromPathAsync(path); }
                catch { return (StorageFile?)null; }
            });
            _cache[path] = task;
        }

        try
        {
            if (!task.IsCompleted) task.Wait(timeoutMs);
            return task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
