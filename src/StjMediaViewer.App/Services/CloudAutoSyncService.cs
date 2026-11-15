using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 云同步的后台调度器：
/// - 每个目标按 <see cref="CloudTarget.PollSeconds"/> 定时比对（最小 60s），只传输有变化的文件；
/// - 本地目录挂 <see cref="FileSystemWatcher"/>，变更后合并抖动 5s 触发一次即时同步；
/// - 全局开关 <see cref="AppSettings.CloudAutoSyncEnabled"/> + 单目标 <see cref="CloudTarget.AutoSync"/>；
/// - 同步互斥：同一目标不会并发跑两轮。
/// </summary>
public sealed class CloudAutoSyncService : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ICloudSyncService _cloud;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, TargetRuntime> _runtimes = new();
    private Task? _loop;
    private bool _disposed;

    public CloudAutoSyncService(ISettingsService settings, ICloudSyncService cloud)
    {
        _settings = settings;
        _cloud = cloud;
    }

    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "未启动";

    /// <summary>任何一轮同步（自动/手动）完成后触发。</summary>
    public event EventHandler<CloudSyncReport>? SyncCompleted;

    /// <summary>状态文本变化（给状态栏/云同步页刷新用）。</summary>
    public event EventHandler? StatusChanged;

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        SetStatus("自动同步已启动");
        _loop = Task.Run(LoopAsync);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts.Cancel();
        DisposeWatchers();
        SetStatus("自动同步已停止");
    }

    /// <summary>目标增删改后调用：重建 watcher 与运行态。</summary>
    public void RefreshTargets()
    {
        DisposeWatchers();
        _runtimes.Clear();
        foreach (var target in Targets())
        {
            var rt = new TargetRuntime(target);
            _runtimes[target.Id] = rt;
            if (target.AutoSync && target.Enabled) AttachWatcher(rt);
        }
    }

    /// <summary>手动立即同步某个目标（不等待轮询），并发去重。</summary>
    public async Task<CloudSyncReport?> RunNowAsync(string targetId, CancellationToken ct = default)
    {
        var target = Targets().FirstOrDefault(t => t.Id == targetId);
        if (target is null) return null;

        if (!_runtimes.TryGetValue(targetId, out var rt))
        {
            rt = new TargetRuntime(target);
            _runtimes[targetId] = rt;
        }

        return await RunTargetAsync(rt, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 主循环

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

                var settings = _settings.Current;
                if (!settings.CloudAutoSyncEnabled) continue;

                foreach (var target in Targets())
                {
                    if (token.IsCancellationRequested) break;
                    if (!target.Enabled || !target.AutoSync) continue;

                    if (!_runtimes.TryGetValue(target.Id, out var rt))
                    {
                        rt = new TargetRuntime(target);
                        _runtimes[target.Id] = rt;
                        AttachWatcher(rt);
                    }

                    var due = rt.LastRunAt is null ||
                              DateTimeOffset.Now - rt.LastRunAt.Value >= TimeSpan.FromSeconds(Math.Max(60, target.PollSeconds));
                    if (!due && !rt.Dirty) continue;

                    rt.Dirty = false;
                    await RunTargetAsync(rt, token).ConfigureAwait(false);
                    rt.LastRunAt = DateTimeOffset.Now;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("自动同步循环异常退出", ex);
            SetStatus("自动同步异常：" + ex.Message);
        }
    }

    private async Task<CloudSyncReport?> RunTargetAsync(TargetRuntime rt, CancellationToken ct)
    {
        if (rt.Gate.Wait(0) is false) return null;   // 上一轮还没跑完
        try
        {
            var target = Targets().FirstOrDefault(t => t.Id == rt.Target.Id);
            if (target is null) return null;

            SetStatus($"正在同步「{target.Name}」…");
            var report = await _cloud.SyncAsync(target, dryRun: false, progress: null, ct).ConfigureAwait(false);
            rt.LastReport = report;

            try { await _settings.SaveAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("保存云同步状态失败", ex); }

            SetStatus(report.Success ? $"「{target.Name}」同步完成：{report.Summary}"
                                     : $"「{target.Name}」有 {report.Failed} 个失败");
            SyncCompleted?.Invoke(this, report);
            return report;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"云同步「{rt.Target.Name}」失败", ex);
            SetStatus($"「{rt.Target.Name}」同步失败：{ex.Message}");
            return null;
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ 监听本地变更

    private void AttachWatcher(TargetRuntime rt)
    {
        try
        {
            var path = rt.Target.LocalFolderPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler handler = (_, _) => MarkDirty(rt);
            RenamedEventHandler renameHandler = (_, _) => MarkDirty(rt);
            ErrorEventHandler errorHandler = (_, e) =>
            {
                Log.Info($"[cloud] watcher 错误（{rt.Target.Name}）：{e.GetException().Message}，退化为轮询");
                try { watcher.EnableRaisingEvents = false; } catch { }
            };

            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += renameHandler;
            watcher.Error += errorHandler;
            rt.Watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Info($"[cloud] 无法监听 {rt.Target.LocalFolderPath}（退化为轮询）：{ex.Message}");
        }
    }

    /// <summary>抖动合并：变更后 5s 内没有新事件才置脏，避免导入时反复触发。</summary>
    private void MarkDirty(TargetRuntime rt)
    {
        if (!IsRunning) return;
        lock (rt.DebounceGate)
        {
            rt.DebounceToken++;
            var token = rt.DebounceToken;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                lock (rt.DebounceGate)
                {
                    if (token != rt.DebounceToken) return;   // 又变了，等下一次
                    rt.Dirty = true;
                }
            }, _cts.Token);
        }
    }

    private void DisposeWatchers()
    {
        foreach (var rt in _runtimes.Values)
        {
            try { rt.Watcher?.Dispose(); } catch { }
            rt.Watcher = null;
        }
    }

    private IReadOnlyList<CloudTarget> Targets()
    {
        try { return _settings.Current.CloudTargets; }
        catch { return Array.Empty<CloudTarget>(); }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        DisposeWatchers();
        _cts.Dispose();
    }

    // ------------------------------------------------------------------ 运行态

    private sealed class TargetRuntime
    {
        public TargetRuntime(CloudTarget target) => Target = target;
        public CloudTarget Target { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public FileSystemWatcher? Watcher { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public CloudSyncReport? LastReport { get; set; }
        /// <summary>本地有变更、等待触发。</summary>
        public bool Dirty { get; set; }
        public int DebounceToken { get; set; }
        public object DebounceGate { get; } = new();
    }
}