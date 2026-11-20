using System.Threading;

namespace StjMediaViewer.Core.Cloud;

/// <summary>
/// 可暂停/继续的令牌：长任务在"每个文件之间"调用 <see cref="WaitWhilePausedAsync"/>，
/// 暂停时原地等待（不占 CPU），继续后从断点接着传。
/// 用 TaskCompletionSource 而不是轮询，恢复时立即唤醒。
/// </summary>
public sealed class PauseTokenSource
{
    private volatile bool _paused;
    private volatile TaskCompletionSource<bool>? _resume;

    public bool IsPaused => _paused;

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        Interlocked.Exchange(ref _resume, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        Interlocked.Exchange(ref _resume, null)?.TrySetResult(true);
    }

    /// <summary>暂停中就一直等；取消时抛 OperationCanceledException（取消优先于暂停）。</summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct = default)
    {
        while (_paused)
        {
            ct.ThrowIfCancellationRequested();
            var waiter = _resume;
            if (waiter is null) return;
            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}