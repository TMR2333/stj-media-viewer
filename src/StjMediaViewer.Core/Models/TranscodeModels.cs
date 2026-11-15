namespace StjMediaViewer.Core.Models;

// =====================================================================================
//  转码 / 压缩
// =====================================================================================

public enum VideoEncoderFamily
{
    H264 = 0,
    H265 = 1,
    Av1 = 2,
    Vp9 = 3,
    /// <summary>不重编码，只换容器（-c copy）。</summary>
    Copy = 9,
}

/// <summary>一个转码预设。内置若干常用档，用户也可以改任意参数（自定义）。</summary>
public sealed class TranscodeProfile
{
    public string Id { get; set; } = "custom";
    public string Name { get; set; } = "自定义";
    public string Description { get; set; } = "";

    public VideoEncoderFamily Encoder { get; set; } = VideoEncoderFamily.H265;

    /// <summary>输出容器扩展名：mp4 / mov / mkv / webm。</summary>
    public string Container { get; set; } = "mp4";

    /// <summary>质量模式：CRF（恒定质量，推荐）或目标码率。</summary>
    public bool UseCrf { get; set; } = true;
    /// <summary>CRF 值：H.265 常用 24–32，H.264 常用 18–28，数值越大越糊越小。</summary>
    public int Crf { get; set; } = 28;
    /// <summary>目标码率（kbps），UseCrf=false 时生效。</summary>
    public int TargetBitrateKbps { get; set; } = 8000;

    /// <summary>缩放目标高度（1080 = 1080p，0 = 保持原分辨率）。</summary>
    public int MaxHeight { get; set; }
    /// <summary>是否在缩小分辨率（MaxHeight>0 且源更高）时按 -2 保持偶数宽。</summary>
    public bool AllowUpscale { get; set; }

    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrateKbps { get; set; } = 128;
    /// <summary>去掉音轨。</summary>
    public bool StripAudio { get; set; }

    /// <summary>x264/x265 的 preset（ultrafast…veryslow），影响速度与压缩率。</summary>
    public string SpeedPreset { get; set; } = "medium";
    /// <summary>是否优先用硬件编码器（NVENC / QSV / AMF），不可用时自动回落软件编码。</summary>
    public bool PreferHardware { get; set; } = true;
    /// <summary>MP4 输出时把 moov 放前面（网页/快速预览更友好）。</summary>
    public bool FastStart { get; set; } = true;

    public string VideoEncoderName => Encoder switch
    {
        VideoEncoderFamily.H264 => "H.264 / AVC",
        VideoEncoderFamily.H265 => "H.265 / HEVC",
        VideoEncoderFamily.Av1 => "AV1",
        VideoEncoderFamily.Vp9 => "VP9",
        _ => "不重编码（仅换容器）",
    };

    public string Summary => Encoder == VideoEncoderFamily.Copy
        ? $"仅换容器 → .{Container}"
        : $"{VideoEncoderName} · " +
          (UseCrf ? $"CRF {Crf}" : $"{TargetBitrateKbps / 1000.0:0.#} Mbps") +
          (MaxHeight > 0 ? $" · {MaxHeight}p" : " · 原分辨率") +
          $" · .{Container}";
}

/// <summary>转码前对源视频的探测结果（不依赖 ffprobe，直接解析 ffmpeg 的流信息）。</summary>
public sealed class VideoProbe
{
    public string Path { get; set; } = "";
    public string? ContainerFormat { get; set; }
    public string? VideoCodecRaw { get; set; }
    public string VideoCodec { get; set; } = "";
    public string VideoCodecLabel { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
    public long BitrateBps { get; set; }
    public double FrameRate { get; set; }
    public int BitDepth { get; set; }
    public string? PixelFormat { get; set; }
    public string? ColorTransfer { get; set; }
    public string? AudioCodec { get; set; }
    public int AudioChannels { get; set; }
    public int AudioSampleRate { get; set; }
    public bool HasAudio { get; set; }
    public bool HasHdr { get; set; }
    public string? Rotation { get; set; }
    public long SizeBytes { get; set; }
    public string? Error { get; set; }

    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "未知";
    public string BitrateText => BitrateBps > 0 ? $"{BitrateBps / 1000.0 / 1000.0:0.##} Mbps" : "未知";
    public string DurationText => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "未知";
}

public sealed class TranscodeRequest
{
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public TranscodeProfile Profile { get; set; } = new();
    /// <summary>输出已存在时的处理：true=覆盖，false=自动加 (1)(2) 后缀。</summary>
    public bool Overwrite { get; set; }
    public bool VerifyOutput { get; set; } = true;
}

public sealed class TranscodeProgress
{
    public string Phase { get; set; } = "";
    public double Percent { get; set; }
    /// <summary>处理速度（例如 2.4x 实时）。</summary>
    public double Speed { get; set; }
    public TimeSpan Processed { get; set; }
    public TimeSpan? Eta { get; set; }
    public string? CurrentFile { get; set; }
    public bool Done { get; set; }
    public string? Message { get; set; }
}

public sealed class TranscodeResult
{
    public string SourcePath { get; set; } = "";
    public string? OutputPath { get; set; }
    public bool Success { get; set; }
    public long SourceBytes { get; set; }
    public long OutputBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string? EncoderUsed { get; set; }
    public string? OutputCodec { get; set; }
    public bool Verified { get; set; }
    public string? Error { get; set; }

    public double SavedRatio => SourceBytes > 0 && OutputBytes > 0
        ? 1.0 - (double)OutputBytes / SourceBytes
        : 0;

    public string SavedText => OutputBytes <= 0
        ? ""
        : SavedRatio >= 0 ? $"体积减少 {SavedRatio * 100:0.#}%" : $"体积增加 {-SavedRatio * 100:0.#}%";

    public string Summary => Success
        ? $"{HumanSize(SourceBytes)} → {HumanSize(OutputBytes)}（{SavedText}）· {EncoderUsed} · 用时 {Elapsed.TotalSeconds:0.0}s"
        : $"失败：{Error}";

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

public sealed class TranscodeBatchReport
{
    public List<TranscodeResult> Items { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
    public int Succeeded => Items.Count(i => i.Success);
    public int Failed => Items.Count(i => !i.Success);
    public long BytesSaved => Items.Where(i => i.Success && i.OutputBytes > 0).Sum(i => Math.Max(0, i.SourceBytes - i.OutputBytes));
    public string Summary =>
        $"成功 {Succeeded} / 失败 {Failed} · 节省 {HumanSize(BytesSaved)} · 用时 {Elapsed.TotalSeconds:0.#}s";

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>本机可用的视频编码器（ffmpeg -encoders 探测结果，用于自动选硬件/软件编码）。</summary>
public sealed class EncoderAvailability
{
    public bool H264Hardware { get; set; }
    public bool H265Hardware { get; set; }
    public bool Av1Hardware { get; set; }
    public string? H264HardwareName { get; set; }
    public string? H265HardwareName { get; set; }
    public string? Av1HardwareName { get; set; }
    public bool LibX264 { get; set; }
    public bool LibX265 { get; set; }
    public bool LibAomAv1 { get; set; }
    public bool LibSvtAv1 { get; set; }
    public bool LibVpxVp9 { get; set; }
    public string Describe()
    {
        var parts = new List<string>();
        if (H264Hardware) parts.Add($"H.264 硬编({H264HardwareName})");
        if (H265Hardware) parts.Add($"H.265 硬编({H265HardwareName})");
        if (Av1Hardware) parts.Add($"AV1 硬编({Av1HardwareName})");
        if (LibX264) parts.Add("libx264");
        if (LibX265) parts.Add("libx265");
        if (LibSvtAv1) parts.Add("libsvtav1");
        else if (LibAomAv1) parts.Add("libaom-av1");
        if (LibVpxVp9) parts.Add("libvpx-vp9");
        return parts.Count == 0 ? "未探测到可用编码器" : string.Join(" · ", parts);
    }
}