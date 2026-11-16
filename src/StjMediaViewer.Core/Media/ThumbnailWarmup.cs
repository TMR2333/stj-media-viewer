using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 后台全库缩略图预热：空闲时以低并发（默认 2）把整库缩略图生成并落盘，
/// 之后无论怎么滚动都是"缓存命中 0ms"路径。可随时停止；再点会从断点继续
/// （已生成的直接秒命中，不会重复解码）。
/// </summary>
public sealed class ThumbnailWarmup : IDisposable
{
    private readonly IAssetStore _store;
    private readonly IThumbnailService _thumbs;
    private readonly ILutService _luts;

    private CancellationTokenSource? _cts;
    private Task? _run;
    private int _done;
    private int _failed;

    public ThumbnailWarmup(IAssetStore store, IThumbnailService thumbs, ILutService luts)
    {
        _store = store;
        _thumbs = thumbs;
        _luts = luts;
    }

    public bool IsRunning => _run is { IsCompleted: false };

    public int Total { get; private set; }
    public int Done => Volatile.Read(ref _done);
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>进度变化（后台线程触发，UI 侧切回 DispatcherQueue 再更新）。</summary>
    public event EventHandler? ProgressChanged;
    public event EventHandler? Finished;

    /// <param name="size">缩略图边长（与网格一致用 360）。</param>
    /// <param name="concurrency">并发（默认 2：不抢前台 CPU / 磁盘）。</param>
    /// <param name="collapsedOnly">true = 只预热默认视图里可见的素材（折叠后每组一张）。</param>
    public bool Start(int size = 360, int concurrency = 2, bool collapsedOnly = true)
    {
        if (IsRunning) return false;

        _done = 0;
        _failed = 0;
        Total = 0;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _run = Task.Run(async () =>
        {
            try
            {
                var assets = await _store.QueryAsync(new AssetQuery { Limit = 0, CollapseGroups = collapsedOnly }, ct)
                    .ConfigureAwait(false);
                Total = assets.Count;
                Log.Info($"[warmup] start total={Total} concurrency={concurrency} size={size}");
                ProgressChanged?.Invoke(this, EventArgs.Empty);

                var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 8), Math.Clamp(concurrency, 1, 8));
                var tasks = new List<Task>(assets.Count);

                foreach (var asset in assets)
                {
                    ct.ThrowIfCancellationRequested();
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            CubeLut? lut = null;
                            var lutEntry = _luts.ResolveForAsset(asset);
                            if (lutEntry is not null)
                                lut = await _luts.LoadCubeAsync(lutEntry, ct).ConfigureAwait(false);

                            await _thumbs.GetThumbnailPathAsync(asset, size, lut, lutEntry?.Id, ct)
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref _done);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _failed);
                            Log.Warn($"[warmup] {asset.FileName}: {ex.Message}");
                        }
                        finally
                        {
                            gate.Release();
                            if (Done % 20 == 0 || Done == Total) ProgressChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                Log.Info($"[warmup] 完成：{Done}/{Total}（失败 {Failed}）");
            }
            catch (OperationCanceledException)
            {
                Log.Info($"[warmup] 已停止（{Done}/{Total}）");
            }
            catch (Exception ex)
            {
                Log.Warn("[warmup] 预热失败", ex);
            }
            finally
            {
                ProgressChanged?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }, CancellationToken.None);

        return true;
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
    }
}