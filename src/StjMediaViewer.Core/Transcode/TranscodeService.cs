using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Transcode;

/// <summary>
/// 基于 ffmpeg CLI 的转码/压缩服务。
///
/// 不依赖 ffprobe（本机只带了 ffmpeg）：探测信息从 `ffmpeg -i` 的 stderr 解析；
/// 编码器能力从 `ffmpeg -encoders` 探测并缓存，硬件编码器（NVENC/QSV/AMF）优先、不可用自动回落。
/// 进度用 `-progress pipe:1`（out_time_us / speed），百分比按源时长换算。
/// </summary>
public sealed class TranscodeService : ITranscodeService
{
    private readonly IFfmpegService _ffmpeg;
    private EncoderAvailability? _encoders;
    private readonly SemaphoreSlim _encoderLock = new(1, 1);

    public TranscodeService(IFfmpegService ffmpeg) => _ffmpeg = ffmpeg;

    public bool IsAvailable => _ffmpeg.IsAvailable;

    public string? FfmpegPath => _ffmpeg.FfmpegPath;

    // ==================================================================================
    //  内置预设
    // ==================================================================================

    public IReadOnlyList<TranscodeProfile> BuiltInProfiles { get; } = new List<TranscodeProfile>
    {
        new()
        {
            Id = "hevc-1080p",
            Name = "压缩 · H.265 / HEVC 1080p",
            Description = "最常见的分享档：1080p 以内、H.265 省一半体积，画质损失可控",
            Encoder = VideoEncoderFamily.H265,
            Container = "mp4",
            Crf = 28,
            MaxHeight = 1080,
            AudioBitrateKbps = 128,
        },
        new()
        {
            Id = "h264-1080p",
            Name = "压缩 · H.264 / AVC 1080p",
            Description = "兼容性最好（老设备/网页/剪辑软件都能直接播），体积比 H.265 略大",
            Encoder = VideoEncoderFamily.H264,
            Container = "mp4",
            Crf = 26,
            MaxHeight = 1080,
            AudioBitrateKbps = 128,
        },
        new()
        {
            Id = "hevc-720p-mobile",
            Name = "压缩 · H.265 720p 手机分享",
            Description = "体积最小档：720p + CRF 30，适合快速发给别人或上传社交平台",
            Encoder = VideoEncoderFamily.H265,
            Container = "mp4",
            Crf = 30,
            MaxHeight = 720,
            AudioBitrateKbps = 96,
        },
        new()
        {
            Id = "h264-high",
            Name = "高画质 H.264 原分辨率",
            Description = "不缩放，CRF 20 高画质，适合交付给客户或做剪辑代理素材",
            Encoder = VideoEncoderFamily.H264,
            Container = "mp4",
            Crf = 20,
            MaxHeight = 0,
            AudioBitrateKbps = 192,
        },
        new()
        {
            Id = "remux",
            Name = "转封装 · 不重编码",
            Description = "只换容器（例如 MOV/MP4 互通），几秒钟完成、零画质损失",
            Encoder = VideoEncoderFamily.Copy,
            Container = "mp4",
        },
        new()
        {
            Id = "av1-720p",
            Name = "压缩 · AV1 720p（最省体积）",
            Description = "AV1 同画质比 H.265 再省 20–30%，但编码慢、老设备可能播不了",
            Encoder = VideoEncoderFamily.Av1,
            Container = "webm",
            Crf = 34,
            MaxHeight = 720,
            AudioBitrateKbps = 96,
            SpeedPreset = "8",
        },
    };

    // ==================================================================================
    //  探测
    // ==================================================================================

    public async Task<VideoProbe> ProbeAsync(string path, CancellationToken ct = default)
    {
        var probe = new VideoProbe { Path = path };
        try { probe.SizeBytes = new FileInfo(path).Length; } catch { }

        if (!_ffmpeg.IsAvailable)
        {
            probe.Error = "ffmpeg 不可用，无法探测视频";
            return probe;
        }

        var (code, stdout, stderr) = await RunAsync(_ffmpeg.FfmpegPath!,
            new[] { "-hide_banner", "-i", path }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        _ = code;
        _ = stdout;
        var text = stderr;

        var inputMatch = Regex.Match(text, @"Input #0,\s*([^,]+),");
        if (inputMatch.Success) probe.ContainerFormat = inputMatch.Groups[1].Value.Trim();

        var dur = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (dur.Success)
        {
            probe.DurationSeconds = int.Parse(dur.Groups[1].Value) * 3600 +
                                    int.Parse(dur.Groups[2].Value) * 60 +
                                    double.Parse(dur.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var br = Regex.Match(text, @"Duration:.*?bitrate:\s*(\d+)\s*kb/s");
        if (br.Success) probe.BitrateBps = long.Parse(br.Groups[1].Value) * 1000;

        var video = Regex.Match(text, @"Stream #\d+:\d+.*?Video:\s*([a-zA-Z0-9_\-]+)(?:\s*\(([^)]*)\))?");
        if (video.Success)
        {
            probe.VideoCodecRaw = video.Groups[1].Value;
            probe.VideoCodec = VideoCodecNames.Normalize(video.Groups[1].Value);
            probe.VideoCodecLabel = VideoCodecNames.DisplayLabel(video.Groups[1].Value);
        }

        var res = Regex.Match(text, @"Video:.*?,\s*(\d{2,5})x(\d{2,5})");
        if (res.Success)
        {
            probe.Width = int.Parse(res.Groups[1].Value);
            probe.Height = int.Parse(res.Groups[2].Value);
        }

        var fps = Regex.Match(text, @"Video:.*?,\s*([\d.]+)\s*fps");
        if (fps.Success && double.TryParse(fps.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f))
            probe.FrameRate = f;

        var pix = Regex.Match(text, @"Video:\s*[^,]+(?:\([^)]*\))?\s*,\s*([a-z0-9]+(?:\([^)]*\))?)");
        if (pix.Success)
        {
            probe.PixelFormat = pix.Groups[1].Value;
            probe.BitDepth = probe.PixelFormat.Contains("10") ? 10
                            : probe.PixelFormat.Contains("12") ? 12
                            : probe.PixelFormat.Contains("16") ? 16
                            : 8;
        }

        probe.HasHdr = text.Contains("smpte2084", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("bt2020", StringComparison.OrdinalIgnoreCase);
        probe.ColorTransfer = text.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase) ? "HLG"
                            : text.Contains("smpte2084", StringComparison.OrdinalIgnoreCase) ? "PQ (HDR10)"
                            : text.Contains("bt709", StringComparison.OrdinalIgnoreCase) ? "BT.709"
                            : null;

        var audio = Regex.Match(text, @"Stream #\d+:\d+.*?Audio:\s*([a-zA-Z0-9_\-]+)(?:\s*\(([^)]*)\))?.*?,\s*(\d+)\s*Hz,\s*([a-z0-9.]+)");
        if (audio.Success)
        {
            probe.HasAudio = true;
            probe.AudioCodec = audio.Groups[1].Value;
            probe.AudioSampleRate = int.Parse(audio.Groups[3].Value);
            var ch = audio.Groups[4].Value;
            probe.AudioChannels = ch.StartsWith("mono") ? 1 : ch.StartsWith("stereo") ? 2 : ch.Contains("5.1") ? 6 : 2;
        }

        var rot = Regex.Match(text, @"rotate\s*:\s*(\d+)");
        if (rot.Success) probe.Rotation = rot.Groups[1].Value;

        if (probe.Width == 0 && probe.DurationSeconds == 0)
            probe.Error = text.Contains("No such file") ? "文件不存在" : "无法解析视频流信息";
        return probe;
    }

    // ==================================================================================
    //  编码器探测
    // ==================================================================================

    public async Task<EncoderAvailability> DetectEncodersAsync(CancellationToken ct = default)
    {
        if (_encoders is not null) return _encoders;
        await _encoderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_encoders is not null) return _encoders;

            var result = new EncoderAvailability();
            if (_ffmpeg.IsAvailable)
            {
                var (_, stdout, stderr) = await RunAsync(_ffmpeg.FfmpegPath!,
                    new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                var text = stdout + "\n" + stderr;
                bool Has(string name) => Regex.IsMatch(text, @"(^|\s)" + Regex.Escape(name) + @"(\s|$)", RegexOptions.Multiline);

                result.LibX264 = Has("libx264");
                result.LibX265 = Has("libx265");
                result.LibAomAv1 = Has("libaom-av1");
                result.LibSvtAv1 = Has("libsvtav1");
                result.LibVpxVp9 = Has("libvpx-vp9");

                foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" })
                {
                    if (!Has(name)) continue;
                    result.H264Hardware = true;
                    result.H264HardwareName = name;
                    break;
                }
                foreach (var name in new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf" })
                {
                    if (!Has(name)) continue;
                    result.H265Hardware = true;
                    result.H265HardwareName = name;
                    break;
                }
                foreach (var name in new[] { "av1_nvenc", "av1_qsv", "av1_amf" })
                {
                    if (!Has(name)) continue;
                    result.Av1Hardware = true;
                    result.Av1HardwareName = name;
                    break;
                }
            }
            _encoders = result;
            Log.Info($"[transcode] 编码器：{result.Describe()}");
            return result;
        }
        finally
        {
            _encoderLock.Release();
        }
    }

    // ==================================================================================
    //  转码
    // ==================================================================================

    public async Task<TranscodeResult> TranscodeAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new TranscodeResult { SourcePath = request.SourcePath };
        try
        {
            if (!_ffmpeg.IsAvailable)
                throw new InvalidOperationException("ffmpeg 不可用：请把 ffmpeg.exe 放到 tools/ffmpeg/bin 或在设置里指定路径");
            if (!File.Exists(request.SourcePath))
                throw new FileNotFoundException("源文件不存在", request.SourcePath);

            result.SourceBytes = new FileInfo(request.SourcePath).Length;
            var encoders = await DetectEncodersAsync(ct).ConfigureAwait(false);

            var output = request.OutputPath;
            if (string.IsNullOrWhiteSpace(output))
                output = DefaultOutputPath(request.SourcePath, request.Profile);
            output = EnsureExtension(output, request.Profile);
            if (!request.Overwrite) output = MakeUnique(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            result.OutputPath = output;

            var probe = await ProbeAsync(request.SourcePath, ct).ConfigureAwait(false);
            var args = BuildArguments(request.Profile, request.SourcePath, output, probe, encoders, out var encoderLabel);
            result.EncoderUsed = encoderLabel;

            progress?.Report(new TranscodeProgress { Phase = "准备转码…", CurrentFile = Path.GetFileName(request.SourcePath) });

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg.FfmpegPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            var lastReport = DateTime.UtcNow;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // -progress 输出是 key=value 行
                if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    if (!long.TryParse(e.Data.AsSpan("out_time_us=".Length), out var us)) return;
                    var processed = TimeSpan.FromMicroseconds(us);
                    var pct = probe.DurationSeconds > 0
                        ? Math.Clamp(processed.TotalSeconds / probe.DurationSeconds * 100.0, 0, 99.5)
                        : 0;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 200) return;
                    lastReport = DateTime.UtcNow;
                    progress?.Report(new TranscodeProgress
                    {
                        Phase = "转码中",
                        Percent = pct,
                        Processed = processed,
                        Speed = _lastSpeed,
                        Eta = _lastSpeed > 0.05
                            ? TimeSpan.FromSeconds(Math.Max(0, (probe.DurationSeconds - processed.TotalSeconds) / _lastSpeed))
                            : null,
                        CurrentFile = Path.GetFileName(request.SourcePath),
                    });
                }
                else if (e.Data.StartsWith("speed=", StringComparison.Ordinal))
                {
                    var v = e.Data["speed=".Length..].Trim().TrimEnd('x');
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var sp))
                        _lastSpeed = sp;
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (stderr.Length < 32_000) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                TryDelete(output);
                result.Error = "已取消";
                return result;
            }

            if (process.ExitCode != 0)
            {
                TryDelete(output);
                result.Error = ExtractFfmpegError(stderr.ToString());
                return result;
            }

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                result.Error = "转码结束但没有产出文件";
                return result;
            }

            result.OutputBytes = new FileInfo(output).Length;
            progress?.Report(new TranscodeProgress { Phase = "校验输出…", Percent = 99.5, CurrentFile = Path.GetFileName(output) });

            if (request.VerifyOutput)
            {
                var outProbe = await ProbeAsync(output, ct).ConfigureAwait(false);
                result.OutputCodec = outProbe.VideoCodec;
                result.Verified = outProbe.Width > 0 && outProbe.DurationSeconds > 0;
                if (!result.Verified)
                {
                    result.Error = $"输出校验失败：{outProbe.Error ?? "无法解析输出视频"}";
                    TryDelete(output);
                    return result;
                }
                result.OutputPath = output;
            }

            result.Success = true;
            progress?.Report(new TranscodeProgress
            {
                Phase = "完成",
                Percent = 100,
                Done = true,
                CurrentFile = Path.GetFileName(output),
                Message = result.Summary,
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消";
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"[transcode] 失败 {request.SourcePath}", ex);
            result.Error = ex.Message;
            return result;
        }
        finally
        {
            sw.Stop();
            result.Elapsed = sw.Elapsed;
        }
    }

    private double _lastSpeed;

    public async Task<TranscodeBatchReport> TranscodeManyAsync(
        IReadOnlyList<TranscodeRequest> requests,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = new TranscodeBatchReport();
        var total = requests.Count;

        for (int i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var request = requests[i];
            var index = i;

            var inner = progress is null ? null : new Progress<TranscodeProgress>(p =>
            {
                progress.Report(new TranscodeProgress
                {
                    Phase = $"[{index + 1}/{total}] {p.Phase}",
                    Percent = (index + p.Percent / 100.0) / total * 100.0,
                    Speed = p.Speed,
                    Processed = p.Processed,
                    Eta = p.Eta,
                    CurrentFile = p.CurrentFile,
                    Done = p.Done,
                    Message = p.Message,
                });
            });

            report.Items.Add(await TranscodeAsync(request, inner, ct).ConfigureAwait(false));
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;
        progress?.Report(new TranscodeProgress
        {
            Phase = "全部完成",
            Percent = 100,
            Done = true,
            Message = report.Summary,
        });
        return report;
    }

    // ==================================================================================
    //  命令行构造
    // ==================================================================================

    private static List<string> BuildArguments(
        TranscodeProfile profile, string source, string output,
        VideoProbe probe, EncoderAvailability encoders, out string encoderLabel)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source };

        // 只保留第一条视频 + 第一条音频，避免音频/字幕流炸掉 MP4
        args.AddRange(new[] { "-map", "0:v:0?", "-map", "0:a:0?" });

        if (profile.Encoder == VideoEncoderFamily.Copy)
        {
            args.AddRange(new[] { "-c", "copy" });
            encoderLabel = "不重编码（-c copy）";
        }
        else
        {
            var (encoder, label, isHardware) = SelectEncoder(profile, encoders);
            encoderLabel = label;
            args.AddRange(new[] { "-c:v", encoder });

            var filter = BuildScaleFilter(profile, probe);
            if (filter is not null) args.AddRange(new[] { "-vf", filter });

            if (isHardware)
            {
                // NVENC/QSV/AMF 用 -cq/-global_quality 近似 CRF 语义
                if (profile.UseCrf) args.AddRange(new[] { "-rc", "vbr", "-cq", profile.Crf.ToString() });
                else args.AddRange(new[] { "-b:v", $"{profile.TargetBitrateKbps}k" });
            }
            else if (profile.Encoder == VideoEncoderFamily.Av1 && encoder is "libsvtav1")
            {
                args.AddRange(new[] { "-crf", profile.Crf.ToString() });
                if (int.TryParse(profile.SpeedPreset, out var svtPreset))
                    args.AddRange(new[] { "-preset", svtPreset.ToString() });
            }
            else
            {
                if (profile.UseCrf) args.AddRange(new[] { "-crf", profile.Crf.ToString() });
                else args.AddRange(new[] { "-b:v", $"{profile.TargetBitrateKbps}k" });
                args.AddRange(new[] { "-preset", NormalizeSpeedPreset(profile) });
            }

            if (profile.Encoder == VideoEncoderFamily.H265 && !isHardware)
            {
                // 让 H.265 输出被更多播放器识别（hvc1 标签）
                args.AddRange(new[] { "-tag:v", "hvc1" });
            }

            if (profile.StripAudio)
                args.AddRange(new[] { "-an" });
            else
                args.AddRange(new[] { "-c:a", profile.AudioCodec, "-b:a", $"{profile.AudioBitrateKbps}k", "-ac", "2" });
        }

        // HDR 且不是 copy：尽量保留 10bit + 元数据，避免 HDR 片转出成灰片
        if (profile.Encoder != VideoEncoderFamily.Copy && probe.HasHdr && probe.BitDepth >= 10)
            args.AddRange(new[] { "-pix_fmt", "yuv420p10le" });

        args.AddRange(new[] { "-map_metadata", "0" });
        if (profile.Container == "mp4" && profile.FastStart)
            args.AddRange(new[] { "-movflags", "+faststart" });

        args.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
        return args;
    }

    private static string NormalizeSpeedPreset(TranscodeProfile profile)
    {
        var preset = profile.SpeedPreset;
        if (profile.Encoder == VideoEncoderFamily.H265)
        {
            // x265 不支持 ultrafast/superfast 这类 x264 命名的部分值
            if (preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast") return "fast";
            if (preset is "veryslow" or "slower" or "slow") return "slow";
            if (preset is "medium" or "slow" or "fast") return preset;
            return "medium";
        }
        return preset is "medium" or "slow" or "fast" or "faster" or "veryfast" or "veryslow" ? preset : "medium";
    }

    private static string? BuildScaleFilter(TranscodeProfile profile, VideoProbe probe)
    {
        if (profile.MaxHeight <= 0) return null;
        if (!profile.AllowUpscale && probe.Height > 0 && probe.Height <= profile.MaxHeight) return null;

        // -2 保证宽度是偶数（H.264/H.265 都要求偶宽），sar 保持像素宽高比
        return $"scale=-2:{profile.MaxHeight}:flags=lanczos";
    }

    private static (string Encoder, string Label, bool Hardware) SelectEncoder(
        TranscodeProfile profile, EncoderAvailability encoders)
    {
        var hw = profile.PreferHardware;
        switch (profile.Encoder)
        {
            case VideoEncoderFamily.H265:
                if (hw && encoders.H265Hardware && encoders.H265HardwareName is { } h265)
                    return (h265, $"{h265}（硬件）", true);
                return ("libx265", "libx265（软件）", false);

            case VideoEncoderFamily.H264:
                if (hw && encoders.H264Hardware && encoders.H264HardwareName is { } h264)
                    return (h264, $"{h264}（硬件）", true);
                return ("libx264", "libx264（软件）", false);

            case VideoEncoderFamily.Av1:
                if (hw && encoders.Av1Hardware && encoders.Av1HardwareName is { } av1hw)
                    return (av1hw, $"{av1hw}（硬件）", true);
                if (encoders.LibSvtAv1) return ("libsvtav1", "libsvtav1（软件）", false);
                return ("libaom-av1", "libaom-av1（软件）", false);

            case VideoEncoderFamily.Vp9:
                return ("libvpx-vp9", "libvpx-vp9（软件）", false);

            default:
                return ("libx264", "libx264（软件）", false);
        }
    }

    // ==================================================================================
    //  工具
    // ==================================================================================

    public string DefaultOutputPath(string sourcePath, TranscodeProfile profile)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var suffix = profile.Encoder switch
        {
            VideoEncoderFamily.H265 => "_hevc",
            VideoEncoderFamily.H264 => "_h264",
            VideoEncoderFamily.Av1 => "_av1",
            VideoEncoderFamily.Vp9 => "_vp9",
            _ => "_remux",
        };
        if (profile.MaxHeight > 0) suffix += $"{profile.MaxHeight}p";
        return Path.Combine(dir, name + suffix + "." + profile.Container);
    }

    /// <summary>按预设粗估输出体积（用于界面提示，误差 ±30%）。</summary>
    public long EstimateOutputBytes(VideoProbe probe, TranscodeProfile profile)
    {
        if (probe.DurationSeconds <= 0) return 0;
        if (profile.Encoder == VideoEncoderFamily.Copy) return probe.SizeBytes;

        double kbps;
        if (!profile.UseCrf)
        {
            kbps = profile.TargetBitrateKbps;
        }
        else
        {
            // CRF 粗估：按 1080p 基准码率 × 分辨率因子 × 编码器效率
            double baseKbps = profile.Encoder switch
            {
                VideoEncoderFamily.H265 => 2500,
                VideoEncoderFamily.Av1 => 1800,
                VideoEncoderFamily.Vp9 => 2600,
                _ => 4000,
            };
            var height = profile.MaxHeight > 0 && probe.Height > profile.MaxHeight ? profile.MaxHeight : probe.Height;
            var factor = height > 0 ? Math.Pow(height / 1080.0, 2) : 1.0;
            var crfDelta = profile.Crf - 28;     // 每 +6 CRF 大约减半
            var crfFactor = Math.Pow(2, crfDelta / 6.0);
            var speedFactor = profile.SpeedPreset is "slow" or "slower" or "veryslow" ? 0.85 : 1.0;
            kbps = baseKbps * factor * crfFactor * speedFactor;
        }
        kbps += profile.StripAudio ? 0 : profile.AudioBitrateKbps;
        return (long)(kbps * 1000 / 8 * probe.DurationSeconds);
    }

    private static string EnsureExtension(string path, TranscodeProfile profile)
    {
        var expected = "." + profile.Container;
        if (Path.GetExtension(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) return path;
        return Path.ChangeExtension(path, expected);
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ExtractFfmpegError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "ffmpeg 执行失败（无输出）";
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var important = lines
            .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        if (important.Count > 0) return string.Join(" | ", important);
        return lines.Length > 0 ? lines[^1] : stderr[..Math.Min(stderr.Length, 400)];
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
        }

        string stdout = "", stderr = "";
        try { stdout = await stdoutTask.ConfigureAwait(false); } catch { }
        try { stderr = await stderrTask.ConfigureAwait(false); } catch { }
        return (process.HasExited ? process.ExitCode : -1, stdout, stderr);
    }
}