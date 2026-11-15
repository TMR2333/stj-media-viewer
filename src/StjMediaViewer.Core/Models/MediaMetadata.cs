using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Models;

/// <summary>
/// 元数据提取的原始产物。三个提取器（图片 / 视频 / 音频）都产出这个结构，
/// 由 <see cref="ApplyTo"/> 统一灌入 <see cref="MediaAsset"/>，保证字段映射一致。
/// 提取器只填自己有把握的字段，其余留空。
/// </summary>
public sealed class MediaMetadata
{
    public string SourcePath { get; set; } = "";
    public MediaKind Kind { get; set; } = MediaKind.Unknown;
    public string FormatCode { get; set; } = "";
    public string? MimeType { get; set; }
    public string? ContainerName { get; set; }
    public ImageFlavor ImageFlavor { get; set; }
    public RawFormat RawFormat { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? FrameRate { get; set; }
    public long? BitrateBps { get; set; }
    public int? BitDepth { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public bool HasAudio { get; set; }

    public string? ColorPrimaries { get; set; }
    public string? TransferCharacteristics { get; set; }
    public string? ColorMatrix { get; set; }
    public string? ColorProfileName { get; set; }
    public bool? IsLog { get; set; }
    public string? LogProfile { get; set; }
    public double? ColorTemperatureK { get; set; }

    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? DeviceKey { get; set; }
    public string? LensModel { get; set; }
    public double? FocalLengthMm { get; set; }
    public double? ApertureF { get; set; }
    public double? ShutterSpeedSeconds { get; set; }
    public int? Iso { get; set; }

    public DateTimeOffset? CapturedAt { get; set; }
    public string? CapturedAtSource { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeMeters { get; set; }

    public string? Title { get; set; }
    public string? Caption { get; set; }
    public string? Creator { get; set; }
    public string? Copyright { get; set; }
    public List<string> Keywords { get; set; } = new();

    /// <summary>检测到的边车文件绝对路径（.xmp/.dop/.cos/...）。</summary>
    public List<string> Sidecars { get; set; } = new();
    public string? XmpPath { get; set; }
    public bool HasEmbeddedXmp { get; set; }
    public bool HasDevelopEdits { get; set; }
    public string? DevelopEngine { get; set; }
    public PostProcessState PostProcess { get; set; } = PostProcessState.Unknown;

    /// <summary>是否含可提取的嵌入预览图（RAW 通常有）。</summary>
    public bool HasEmbeddedPreview { get; set; }
    public int? EmbeddedPreviewWidth { get; set; }
    public int? EmbeddedPreviewHeight { get; set; }

    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset ParsedAt { get; set; } = DateTimeOffset.Now;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>把提取结果灌入素材记录（不覆盖已有的人工/AI 分类结果）。</summary>
    public void ApplyTo(MediaAsset a)
    {
        if (Kind != MediaKind.Unknown) a.Kind = Kind;
        if (!string.IsNullOrEmpty(FormatCode)) a.FormatCode = FormatCode;
        a.MimeType ??= MimeType;
        a.ContainerName ??= ContainerName;
        if (ImageFlavor != ImageFlavor.None) a.ImageFlavor = ImageFlavor;
        if (RawFormat != RawFormat.None) a.RawFormat = RawFormat;

        a.Width ??= Width;
        a.Height ??= Height;
        a.DurationSeconds ??= DurationSeconds;
        a.FrameRate ??= FrameRate;
        a.BitrateBps ??= BitrateBps;
        a.BitDepth ??= BitDepth;
        a.VideoCodec ??= VideoCodec;
        a.AudioCodec ??= AudioCodec;
        a.AudioChannels ??= AudioChannels;
        a.AudioSampleRate ??= AudioSampleRate;
        if (HasAudio) a.HasAudio = true;

        a.ColorPrimaries ??= ColorPrimaries;
        a.TransferCharacteristics ??= TransferCharacteristics;
        a.ColorMatrix ??= ColorMatrix;
        a.ColorProfileName ??= ColorProfileName;
        a.IsLog ??= IsLog;
        a.LogProfile ??= LogProfile;
        a.ColorTemperatureK ??= ColorTemperatureK;

        a.CameraMake ??= CameraMake;
        a.CameraModel ??= CameraModel;
        a.DeviceKey ??= DeviceKey;
        a.LensModel ??= LensModel;
        a.FocalLengthMm ??= FocalLengthMm;
        a.ApertureF ??= ApertureF;
        a.ShutterSpeedSeconds ??= ShutterSpeedSeconds;
        a.Iso ??= Iso;

        a.CapturedAt ??= CapturedAt;
        a.CapturedAtSource ??= CapturedAtSource;
        a.Latitude ??= Latitude;
        a.Longitude ??= Longitude;
        a.AltitudeMeters ??= AltitudeMeters;

        a.Title ??= Title;
        a.Caption ??= Caption;
        a.Creator ??= Creator;
        a.Copyright ??= Copyright;
        if (Keywords.Count > 0)
        {
            foreach (var k in Keywords)
            {
                if (!a.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) a.Keywords.Add(k);
            }
        }

        if (Sidecars.Count > 0)
        {
            foreach (var s in Sidecars)
            {
                if (!a.Sidecars.Contains(s, StringComparer.OrdinalIgnoreCase)) a.Sidecars.Add(s);
            }
        }

        a.XmpPath ??= XmpPath;
        if (HasEmbeddedXmp) a.HasEmbeddedXmp = true;
        if (HasDevelopEdits) a.HasDevelopEdits = true;
        a.DevelopEngine ??= DevelopEngine;
        if (PostProcess != PostProcessState.Unknown) a.PostProcess = PostProcess;

        a.MetadataParsedAt = ParsedAt;
        if (HasError) a.LastError = Error;
    }
}