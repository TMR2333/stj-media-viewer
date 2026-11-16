using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>后台全库预热：低并发跑完整库，进度/完成事件正常，缓存确实落盘。</summary>
    private static async Task CheckWarmup(Services s)
    {
        var total = (await s.Store.QueryAsync(new AssetQuery { Limit = 0 })).Count;
        Check("预热：库里有素材", total > 0, total.ToString());

        var warmup = new ThumbnailWarmup(s.Store, s.Thumbnails, s.Luts);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Finished += (_, _) => finished.TrySetResult(true);

        var started = warmup.Start(size: 360, concurrency: 2);
        Check("预热：可以启动", started, null);
        Check("预热：重复启动会拒绝", warmup.Start() == false, null);

        var done = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(90))) == finished.Task;
        Check("预热：90 秒内跑完", done, done ? null : $"已处理 {warmup.Done}/{warmup.Total}");

        Check("预热：总数与库一致", warmup.Total > 0, $"{warmup.Total}");
        Check("预热：处理数 = 总数", warmup.Done == warmup.Total, $"{warmup.Done}/{warmup.Total}");
        Check("预热：没有失败", warmup.Failed == 0, $"失败 {warmup.Failed}");

        // 再跑一遍：此时应该全部命中缓存，速度明显更快
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var again = new ThumbnailWarmup(s.Store, s.Thumbnails, s.Luts);
        var finished2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        again.Finished += (_, _) => finished2.TrySetResult(true);
        again.Start(360, 2);
        await Task.WhenAny(finished2.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        sw.Stop();
        Check("预热：二次运行全部命中缓存", again.Done >= warmup.Done && sw.ElapsedMilliseconds < 20000,
              $"第二次 {again.Done}/{again.Total} 用时 {sw.ElapsedMilliseconds}ms");

        warmup.Dispose();
        again.Dispose();
    }
}
