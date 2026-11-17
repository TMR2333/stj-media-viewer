using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// ffmpeg / ffprobe 外部工具（可选增强）。探测顺序：
/// settings.FfmpegPath → 程序目录及其 ffmpeg/bin 子目录 → 从程序目录向上找 tools/ffmpeg/bin → PATH。
/// 找不到时全部优雅降级（<see cref="IsAvailable"/> = false，方法返回 null/false）。
/// </summary>
public sealed class FfmpegService : IFfmpegService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(30);

    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;

    public FfmpegService(ISettingsService? settings = null)
    {
        var configuredFfmpeg = SafeGet(() => settings?.Current?.FfmpegPath);
        var configuredFfprobe = SafeGet(() => settings?.Current?.FfprobePath);
        _ffmpeg = Locate("ffmpeg.exe", configuredFfmpeg);
        _ffprobe = Locate("ffprobe.exe", configuredFfprobe) ?? SiblingOf(_ffmpeg, "ffprobe.exe");

        if (_ffmpeg is null) Log.Info("[ffmpeg] not found — 视频抽帧/代理转码将回落到系统缩略图");
        else Log.Info($"[ffmpeg] ffmpeg={_ffmpeg} ffprobe={_ffprobe ?? "(none)"}");
    }

    public string? FfmpegPath => _ffmpeg;
    public string? FfprobePath => _ffprobe;
    public bool IsAvailable => !string.IsNullOrEmpty(_ffmpeg);

    // ==================================================================== 对外能力

    public async Task<string?> ProbeJsonAsync(string fullPath, CancellationToken ct = default)
    {
        var exe = _ffprobe;
        if (exe is null || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;
        try
        {
            var result = await RunAsync(exe, new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", fullPath,
            }, binaryStdout: false, ProbeTimeout, ct).ConfigureAwait(false);

            if (result.ExitCode != 0) return null;
            var text = Encoding.UTF8.GetString(result.StdOut);
            if (string.IsNullOrWhiteSpace(text)) return null;
            // 简单校验：必须是合法 JSON 对象
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? text : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[ffmpeg] probe failed {fullPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> ExtractFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default)
    {
        var exe = _ffmpeg;
        if (exe is null || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;
        try
        {
            double pos = Math.Max(0, positionSeconds);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-ss", pos.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", fullPath,
                "-frames:v", "1",
            };
            AppendFitFilter(args, maxSize);
            args.Add("-f"); args.Add("mjpeg"); args.Add("-");

            var result = await RunAsync(exe, args, binaryStdout: true, FrameTimeout, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.StdOut.Length < 128)
            {
                if (result.ExitCode != 0)
                    Log.Warn($"[ffmpeg] frame extract exit={result.ExitCode} {Path.GetFileName(fullPath)}: {Trim(result.StdErr)}");
                return null;
            }
            return result.StdOut;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[ffmpeg] frame extract failed {fullPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>代理转码的缩放/像素格式滤镜（不含 LUT 部分）。</summary>
    internal const string ProxyScaleFilter =
        "scale=w=min(1280\\,iw):h=min(720\\,ih):force_original_aspect_ratio=decrease,format=yuv420p";

    /// <summary>
    /// 拼 <c>-vf</c>：可选的 LUT 烧录（<c>lut3d</c>）+ 缩放。
    /// LUT 用相对文件名（工作目录=代理目录），躲开 Windows 盘符冒号与空格转义。
    /// </summary>
    public static string BuildProxyVideoFilter(string? lutFile)
        => string.IsNullOrWhiteSpace(lutFile)
            ? ProxyScaleFilter
            : $"lut3d=file={lutFile}:interp=tetrahedral,{ProxyScaleFilter}";

    public async Task<bool> TranscodeProxyAsync(
        string source, string destination, string? lutPath = null, CancellationToken ct = default)
    {
        var exe = _ffmpeg;
        if (exe is null || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) return false;
        if (!File.Exists(source)) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(destination)) File.Delete(destination);

            // LUT 复制进代理目录，用相对文件名喂 ffmpeg
            string? lutFile = null;
            string? workDir = null;
            if (!string.IsNullOrWhiteSpace(lutPath) && File.Exists(lutPath))
            {
                workDir = dir;
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(lutPath).ToLowerInvariant())))[..12].ToLowerInvariant();
                lutFile = $"lut-{hash}.cube";
                try { File.Copy(lutPath, Path.Combine(workDir!, lutFile), overwrite: true); }
                catch (Exception ex)
                {
                    Log.Warn($"[ffmpeg] LUT 复制失败，改为不套 LUT: {ex.Message}");
                    lutFile = null;
                }
            }

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", source,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p",
                "-vf", BuildProxyVideoFilter(lutFile),
                "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                "-movflags", "+faststart",
                destination,
            };
            var result = await RunAsync(exe, args, binaryStdout: false, TranscodeTimeout, ct, workDir).ConfigureAwait(false);
            bool ok = result.ExitCode == 0 && File.Exists(destination) && new FileInfo(destination).Length > 0;
            if (!ok) Log.Warn($"[ffmpeg] transcode exit={result.ExitCode}: {Trim(result.StdErr)}");
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[ffmpeg] transcode failed {source}: {ex.Message}");
            return false;
        }
    }

    // ==================================================================== 探测

    private static string? Locate(string exeName, string? explicitPath)
    {
        foreach (var candidate in Candidates(exeName, explicitPath))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var p = Path.GetFullPath(candidate);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string exeName, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath!;
            yield return Path.Combine(explicitPath!, exeName);
            yield return Path.Combine(explicitPath!, "bin", exeName);
        }

        string baseDir;
        try { baseDir = AppContext.BaseDirectory; } catch { baseDir = Environment.CurrentDirectory; }

        // 程序目录（发布形态：ffmpeg.exe 与 exe 同级或 ffmpeg/、bin/ 子目录）
        yield return Path.Combine(baseDir, exeName);
        yield return Path.Combine(baseDir, "ffmpeg", exeName);
        yield return Path.Combine(baseDir, "ffmpeg", "bin", exeName);
        yield return Path.Combine(baseDir, "bin", exeName);
        yield return Path.Combine(baseDir, "tools", "ffmpeg", "bin", exeName);

        // 开发形态：从程序目录向上找仓库根的 tools/ffmpeg/bin
        var dir = baseDir;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return Path.Combine(dir, "tools", "ffmpeg", "bin", exeName);
            yield return Path.Combine(dir, "tools", "ffmpeg", exeName);
            yield return Path.Combine(dir, "ffmpeg", "bin", exeName);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        }

        // PATH
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try { candidate = Path.Combine(part.Trim(), exeName); } catch { continue; }
                yield return candidate;
            }
        }
    }

    private static string? SiblingOf(string? exePath, string siblingName)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return null;
            var p = Path.Combine(dir, siblingName);
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    // ==================================================================== 进程调用

    private sealed record ProcResult(int ExitCode, byte[] StdOut, string StdErr);

    private static async Task<ProcResult> RunAsync(
        string exe, IEnumerable<string> args, bool binaryStdout, TimeSpan timeout, CancellationToken ct,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = binaryStdout ? null : Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return new ProcResult(-1, Array.Empty<byte>(), "process start failed");

        try { proc.StandardInput.Close(); } catch { }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                using var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token).ConfigureAwait(false);
                return ms.ToArray();
            }
            catch { return Array.Empty<byte>(); }
        }, CancellationToken.None);

        var stderrTask = Task.Run(async () =>
        {
            try { return await proc.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false); }
            catch { return ""; }
        }, CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            throw new TimeoutException($"ffmpeg 超时（{timeout.TotalSeconds:0}s）");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcResult(proc.ExitCode, stdout, stderr);
    }

    private static void AppendFitFilter(List<string> args, int maxSize)
    {
        if (maxSize <= 0) return;
        int size = Math.Clamp(maxSize, 32, 4096);
        args.Add("-vf");
        args.Add($"scale=w=min({size}\\,iw):h=min({size}\\,ih):force_original_aspect_ratio=decrease");
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length <= 300 ? s : s[..300];
    }

    private static string? SafeGet(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }
}