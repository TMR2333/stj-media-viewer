using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 缩略图缓存自动清理：
///  - 启动后约 20 秒跑第一次（错开首屏解码高峰）；
///  - 之后每 30 分钟检查一次，只有距上次清理超过「清理间隔小时」才真正执行；
///  - 是否启用、保留天数、体积上限全部来自设置（设置页可改）。
/// </summary>
public sealed class CacheMaintenanceService : IDisposable
{
    private readonly ISettingsService _settings;
    private Timer? _timer;
    private int _running;
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public CacheMaintenanceService(ISettingsService settings) => _settings = settings;

    /// <summary>清理完成（删除过文件时触发，便于界面刷新）。</summary>
    public event EventHandler<CacheCleanResult>? Cleaned;

    public DateTimeOffset LastRun => _lastRun;

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = RunIfDueAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(30));
        Log.Info($"[cache] 自动清理已启用（每 {_settings.Current.CacheCleanIntervalHours} 小时检查，保留 {_settings.Current.CacheMaxAgeDays} 天 / 上限 {_settings.Current.ThumbnailCacheMb} MB）");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>到点就清（启动/定时器都走这里）。</summary>
    public async Task RunIfDueAsync(bool force = false)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var settings = _settings.Current;
            if (!settings.CacheAutoCleanEnabled && !force) return;

            var interval = TimeSpan.FromHours(Math.Max(1, settings.CacheCleanIntervalHours));
            if (!force && DateTimeOffset.Now - _lastRun < interval) return;

            _lastRun = DateTimeOffset.Now;
            var result = await AppServices.CleanThumbnailCacheAsync().ConfigureAwait(false);
            if (result.FilesDeleted > 0)
            {
                Log.Info($"[cache] 自动清理：{result.Summary}");
                try { Cleaned?.Invoke(this, result); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("自动清理缓存失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>设置页「立即清理」用：无视间隔与开关，按当前规则清一次。</summary>
    public async Task<CacheCleanResult> RunNowAsync()
    {
        _lastRun = DateTimeOffset.Now;
        var result = await AppServices.CleanThumbnailCacheAsync().ConfigureAwait(false);
        if (result.FilesDeleted > 0)
        {
            try { Cleaned?.Invoke(this, result); } catch { }
        }
        return result;
    }

    public void Dispose() => Stop();
}