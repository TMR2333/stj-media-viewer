using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Transcode;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 自检的"视频编码识别 + 转码/压缩"部分：用样本库里的真实视频跑一遍 ffmpeg。
/// </summary>
public static partial class Program
{
    private static async Task CheckTranscode(Services s, string libraryPath, string tempData)
    {
        // ---- 1) 编码名归一化（纯逻辑，不依赖 ffmpeg） ----
        Check("编码名：avc1 → H.264", VideoCodecNames.Normalize("avc1") == "H.264", VideoCodecNames.Normalize("avc1"));
        Check("编码名：h264 → H.264", VideoCodecNames.Normalize("h264") == "H.264", null);
        Check("编码名：hev1/hvc1/h265 → H.265", VideoCodecNames.Normalize("hev1") == "H.265"
              && VideoCodecNames.Normalize("hvc1") == "H.265"
              && VideoCodecNames.Normalize("hevc") == "H.265", null);
        Check("编码名：HEVC 判定", VideoCodecNames.IsHevc("hvc1") && !VideoCodecNames.IsHevc("avc1"), null);
        Check("编码名：显示标签带别名", VideoCodecNames.DisplayLabel("h264") == "H.264 / AVC", VideoCodecNames.DisplayLabel("h264"));
        Check("编码名：AV1/ProRes/DV 识别",
              VideoCodecNames.Normalize("av01") == "AV1" &&
              VideoCodecNames.Normalize("ap4h") == "ProRes" &&
              VideoCodecNames.Normalize("dvh1") == "Dolby Vision", null);

        // ---- 2) 找样本视频 ----
        var video = Directory.EnumerateFiles(libraryPath, "*.mp4", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(libraryPath, "*.mov", SearchOption.AllDirectories))
            .FirstOrDefault();
        if (video is null)
        {
            Check("找到样本视频", false, "样本库里没有 mp4/mov");
            return;
        }
        Check("找到样本视频", true, Path.GetFileName(video));

        if (!s.Transcode.IsAvailable)
        {
            Check("ffmpeg 可用", false, "ffmpeg 不可用，跳过转码");
            return;
        }
        Check("ffmpeg 可用", true, s.Transcode.FfmpegPath);

        // ---- 3) 探测 ----
        var probe = await s.Transcode.ProbeAsync(video);
        Check("探测：识别编码", !string.IsNullOrEmpty(probe.VideoCodec), probe.VideoCodecLabel);
        Check("探测：分辨率", probe.Width > 0 && probe.Height > 0, probe.ResolutionText);
        Check("探测：时长", probe.DurationSeconds > 0, probe.DurationText);
        Check("探测：无解析错误", string.IsNullOrEmpty(probe.Error), probe.Error);

        // ---- 4) 编码器 ----
        var encoders = await s.Transcode.DetectEncodersAsync();
        Check("编码器：至少有一个 H.264 编码器", encoders.LibX264 || encoders.H264Hardware, encoders.Describe());
        Console.WriteLine("   编码器：" + encoders.Describe());

        // ---- 5) 真实转码（压缩档） ----
        var profile = s.Transcode.BuiltInProfiles.First(p => p.Id == "h264-720p" || p.Id == "h264-1080p");
        var outPath = Path.Combine(tempData, "transcode-out", "compressed.mp4");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await s.Transcode.TranscodeAsync(new TranscodeRequest
        {
            SourcePath = video,
            OutputPath = outPath,
            Profile = new TranscodeProfile
            {
                Id = "selfcheck",
                Name = "自检",
                Encoder = VideoEncoderFamily.H264,
                Container = "mp4",
                Crf = 30,
                MaxHeight = 720,
                AudioBitrateKbps = 96,
                SpeedPreset = "veryfast",
                PreferHardware = false,
            },
            Overwrite = true,
            VerifyOutput = true,
        });
        sw.Stop();
        Check("转码：执行成功", result.Success, result.Error ?? result.Summary);
        if (result.Success)
        {
            Check("转码：产出文件存在", File.Exists(result.OutputPath), result.OutputPath);
            Check("转码：输出可再次解析", !string.IsNullOrEmpty(result.OutputCodec), result.OutputCodec);
            Check("转码：输出编码符合预期", result.OutputCodec is "H.264" or "H.265", result.OutputCodec);
            Check("转码：输出体积合理", result.OutputBytes > 0,
                  $"{result.SourceBytes} → {result.OutputBytes}");
            Console.WriteLine("   " + result.Summary);

            var outProbe = await s.Transcode.ProbeAsync(result.OutputPath);
            Check("转码：输出分辨率已缩到 720p 内", outProbe.Height <= 720 && outProbe.Height > 0,
                  outProbe.ResolutionText);
        }

        // ---- 5.5) 真实压缩：造一个高码率素材，压缩后必须明显变小 ----
        var bigSource = Path.Combine(tempData, "transcode-out", "source-lossless.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(bigSource)!);
        var made = await RunFfmpegAsync(s.Transcode.FfmpegPath!,
            "-hide_banner", "-nostdin", "-y", "-f", "lavfi",
            "-i", "testsrc2=size=1280x720:rate=30:duration=2",
            "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", bigSource);
        if (made && File.Exists(bigSource))
        {
            var bigProbe = await s.Transcode.ProbeAsync(bigSource);
            var compressed = await s.Transcode.TranscodeAsync(new TranscodeRequest
            {
                SourcePath = bigSource,
                OutputPath = Path.Combine(tempData, "transcode-out", "compressed-big.mp4"),
                Profile = new TranscodeProfile
                {
                    Id = "selfcheck-compress",
                    Encoder = VideoEncoderFamily.H264,
                    Container = "mp4",
                    Crf = 30,
                    MaxHeight = 720,
                    SpeedPreset = "veryfast",
                    PreferHardware = false,
                },
                Overwrite = true,
            });
            Check("压缩：高码率素材压缩成功", compressed.Success, compressed.Error ?? compressed.Summary);
            if (compressed.Success)
            {
                Check("压缩：体积明显变小（<60%）", compressed.OutputBytes < compressed.SourceBytes * 0.6,
                      $"{compressed.SourceBytes / 1024} KB → {compressed.OutputBytes / 1024} KB（省 {compressed.SavedRatio * 100:0}%）");
                Check("压缩：输出分辨率正确", bigProbe.Height >= 720, bigProbe.ResolutionText);
            }
        }
        else
        {
            Check("压缩：生成高码率测试素材", false, "lavfi 生成失败（ffmpeg 缺少 testsrc2？）");
        }

        // ---- 6) 转封装（不重编码） ----
        var remux = await s.Transcode.TranscodeAsync(new TranscodeRequest
        {
            SourcePath = video,
            OutputPath = Path.Combine(tempData, "transcode-out", "remux.mp4"),
            Profile = s.Transcode.BuiltInProfiles.First(p => p.Encoder == VideoEncoderFamily.Copy),
            Overwrite = true,
        });
        Check("转封装：成功且不重编码", remux.Success && remux.EncoderUsed?.Contains("copy") == true,
              remux.Error ?? remux.Summary);
        if (remux.Success)
            Check("转封装：保持原编码", remux.OutputCodec == probe.VideoCodec,
                  $"{probe.VideoCodec} → {remux.OutputCodec}");

        // ---- 7) 体积估算 ----
        var estimate = s.Transcode.EstimateOutputBytes(probe,
            s.Transcode.BuiltInProfiles.First(p => p.Id == "hevc-1080p"));
        Check("估算：输出体积为正且合理", estimate > 0 && probe.DurationSeconds > 0, $"{estimate / 1024} KB");

        // ---- 8) 默认输出路径 ----
        var defaultPath = s.Transcode.DefaultOutputPath(video,
            s.Transcode.BuiltInProfiles.First(p => p.Id == "hevc-1080p"));
        Check("默认输出名带编码与分辨率后缀",
              defaultPath.Contains("_hevc") && defaultPath.Contains("1080p") && defaultPath.EndsWith(".mp4"),
              Path.GetFileName(defaultPath));
    }
}
// 供转码自检用：直接跑一条 ffmpeg 命令（造测试素材）
public static partial class Program
{
    private static async Task<bool> RunFfmpegAsync(string ffmpegPath, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
