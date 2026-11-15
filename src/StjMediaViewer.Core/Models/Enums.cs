namespace StjMediaViewer.Core.Models;

/// <summary>素材大类。</summary>
public enum MediaKind
{
    Unknown = 0,
    Image = 1,
    Video = 2,
    Audio = 3,
}

/// <summary>图片文件族（与 RAW 互斥，RAW 单独用 <see cref="RawFormat"/> 表达）。</summary>
public enum ImageFlavor
{
    None = 0,
    Jpeg = 1,
    Png = 2,
    Tiff = 3,
    Heif = 4,
    WebP = 5,
    Bmp = 6,
    Gif = 7,
    Psd = 8,
    Other = 99,
}

/// <summary>RAW 格式。</summary>
public enum RawFormat
{
    None = 0,
    Dng = 1,
    Cr2 = 2,
    Cr3 = 3,
    Nef = 4,
    Arw = 5,
    Raf = 6,
    Orf = 7,
    Rw2 = 8,
    Pef = 9,
    Srw = 10,
    X3f = 11,
    Mos = 12,
    Iiq = 13,
    Other = 99,
}

/// <summary>是否已经过后期处理（用户勾选 + 自动推断）。</summary>
public enum PostProcessState
{
    Unknown = 0,
    Unprocessed = 1,
    Processed = 2,
}

/// <summary>分类取值的来源，用于冲突裁决与 UI 标注。</summary>
public enum ClassSource
{
    None = 0,
    Derived = 1,   // 从元数据自动推断
    Sidecar = 2,   // 来自 XMP / 边车文件
    Manual = 3,    // 用户手工
    Ai = 4,        // AI 模型
}

/// <summary>可被标注来源的分类维度。</summary>
public enum ClassDimension
{
    ImageKind = 1,
    Format = 2,
    Device = 3,
    CapturedTime = 4,
    City = 5,
    PostProcess = 6,
    VideoLog = 7,
    Theme = 8,
    Tone = 9,
    Keywords = 10,
    Rating = 11,
    Quality = 12,
}

/// <summary>某个分类维度上取值的来源信息。</summary>
public sealed class FieldProvenance
{
    public ClassSource Source { get; set; } = ClassSource.None;
    public double Confidence { get; set; } = 1.0;
    public string? ModelId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string? Note { get; set; }

    public static FieldProvenance Of(ClassSource source, double confidence = 1.0, string? modelId = null) => new()
    {
        Source = source,
        Confidence = confidence,
        ModelId = modelId,
        At = DateTimeOffset.Now,
    };

    public FieldProvenance Clone() => (FieldProvenance)MemberwiseClone();
}