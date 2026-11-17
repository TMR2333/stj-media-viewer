using StjMediaViewer.Core.Media;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 视频预览代理：相机 4:2:2 / 10-bit H.264（Sony XAVC 等）系统解码器解不了，
    /// 必须能用 ffmpeg 转出 H.264 8-bit 4:2:0 的 720p 代理再播放，并且缓存复用。
    /// </summary>
    private static async Task CheckVideoProxy(Services s, string tempData)
    {
        var ffmpeg = s.Ffmpeg?.FfmpegPath;
        var ffprobe = s.Ffmpeg?.FfprobePath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            Check("视频代理：跳过（未配置 ffmpeg）", true, "本机没找到 ffmpeg");
            return;
        }

        var dir = Path.Combine(tempData, "video-proxy");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "xavc-422-10bit.mp4");

        // 生成一段 4:2:2 10-bit H.264 测试片（正是相机原片的格式）
        var made = await RunProcessAsync(ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
            "-c:v", "libx264", "-pix_fmt", "yuv422p10le", "-preset", "ultrafast",
            source,
        }, TimeSpan.FromSeconds(60));
        Check("视频代理：生成 4:2:2 10-bit 测试片", made && File.Exists(source), source);

        var proxy = new VideoProxyService(s.Ffmpeg, Path.Combine(dir, "cache"));
        var proxyPath = await proxy.EnsureProxyAsync(source);
        Check("视频代理：转码成功", proxyPath is not null && File.Exists(proxyPath),
              proxyPath ?? "(失败)");

        if (!string.IsNullOrEmpty(proxyPath) && !string.IsNullOrEmpty(ffprobe))
        {
            var (ok, json) = await RunProcessCaptureAsync(ffprobe, new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_streams", proxyPath,
            }, TimeSpan.FromSeconds(30));
            Check("视频代理：输出是 8-bit 4:2:0（系统可播）",
                  ok && json.Contains("yuv420p", StringComparison.OrdinalIgnoreCase),
                  ok ? "yuv420p" : "(探测失败)");
            Check("视频代理：缓存在 <cache>/proxy 下",
                  proxyPath.Contains(Path.Combine("cache", "proxy"), StringComparison.OrdinalIgnoreCase),
                  proxyPath);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var again = await proxy.EnsureProxyAsync(source);
        stopwatch.Stop();
        Check("视频代理：二次请求命中缓存",
              again == proxyPath && stopwatch.ElapsedMilliseconds < 2000,
              $"{stopwatch.ElapsedMilliseconds}ms");

        var removed = await proxy.ClearAsync();
        Check("视频代理：清理缓存", removed >= 1 && !File.Exists(proxyPath), $"删除 {removed} 个");
    }

    private static async Task<bool> RunProcessAsync(string exe, string[] args, TimeSpan timeout)
    {
        var (ok, _) = await RunProcessCaptureAsync(exe, args, timeout);
        return ok;
    }

    private static async Task<(bool Ok, string Output)> RunProcessCaptureAsync(string exe, string[] args, TimeSpan timeout)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            var text = await stdout + await stderr;
            return (process.ExitCode == 0, text);
        }
        catch
        {
            return (false, "");
        }
    }
}