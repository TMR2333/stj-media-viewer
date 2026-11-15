namespace StjMediaViewer.Core.Models;

/// <summary>离线反向地理编码结果。</summary>
public sealed record GeoPlace(
    string? Country,
    string? Province,
    string? City,
    string? District,
    double DistanceKm,
    string Source)
{
    public string DisplayCity =>
        !string.IsNullOrWhiteSpace(City) ? City! :
        (!string.IsNullOrWhiteSpace(Province) ? Province! : Country ?? "未知地点");
}

/// <summary>扫描/导出的阶段。</summary>
public enum ScanPhase
{
    Preparing = 0,
    Enumerating = 1,
    ReadingMetadata = 2,
    Hashing = 3,
    Indexing = 4,
    Thumbnails = 5,
    Completed = 6,
    Cancelled = 7,
    Failed = 8,
}

public sealed class ScanProgress
{
    public ScanPhase Phase { get; set; }
    public int FilesSeen { get; set; }
    public int MediaFound { get; set; }
    public int Skipped { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? CurrentPath { get; set; }
    public string? Message { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Failed { get; set; }
    public double Percent => Total > 0 ? Math.Round(100.0 * Processed / Total, 1) : 0;

    public ScanProgress Clone() => (ScanProgress)MemberwiseClone();
}

public sealed class ScanOptions
{
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>重新读取所有文件的元数据（忽略缓存）。</summary>
    public bool ForceReparse { get; set; }
    public bool ComputeQuickHash { get; set; } = true;
    public bool ComputeFullHash { get; set; }
    public bool SkipHidden { get; set; } = true;
    public bool SniffMagicBytes { get; set; } = true;
    public long MinFileSize { get; set; } = 1;
    public int Parallelism { get; set; } = 4;
    /// <summary>超过它就不要了（0 = 不限）。</summary>
    public int MaxFiles { get; set; }
    public List<string> ExtraIgnorePatterns { get; set; } = new();
}

/// <summary>解码后的位图（BGRA8 紧密排列）。</summary>
public sealed record DecodedImage(byte[] Bgra, int Width, int Height, int Stride)
{
    public int PixelCount => Width * Height;
}

/// <summary>图像统计，用于自动挑片与影调分类辅助。</summary>
public sealed class ImageStats
{
    public double MeanLuma { get; set; }
    public double MedianLuma { get; set; }
    public double StdDevLuma { get; set; }
    public double Contrast { get; set; }
    /// <summary>拉普拉斯方差，越大越锐。</summary>
    public double Sharpness { get; set; }
    public double MeanSaturation { get; set; }
    public bool IsMonochrome { get; set; }
    public double ShadowClipRatio { get; set; }
    public double HighlightClipRatio { get; set; }
    public double[] LumaHistogram { get; set; } = Array.Empty<double>();      // 256 bins
    public double[] RedHistogram { get; set; } = Array.Empty<double>();
    public double[] GreenHistogram { get; set; } = Array.Empty<double>();
    public double[] BlueHistogram { get; set; } = Array.Empty<double>();
    public string DominantColorHex { get; set; } = "#808080";
    public double WarmthIndex { get; set; }   // >0 偏暖, <0 偏冷

    /// <summary>粗略影调推断，AI 之外的第二道参考。</summary>
    public string? SuggestTone()
    {
        if (IsMonochrome) return "mono";
        if (MeanLuma > 165 && Contrast < 55) return "highkey";
        if (MeanLuma < 78 && StdDevLuma > 45) return "lowkey";
        if (StdDevLuma > 78) return "hard";
        if (StdDevLuma < 34) return "soft";
        if (MeanSaturation < 0.16) return "lowsat";
        if (WarmthIndex > 12) return "warm";
        if (WarmthIndex < -12) return "cool";
        return null;
    }

    /// <summary>0..1 综合技术质量（曝光/锐度/对比）。</summary>
    public double QualityScore()
    {
        double exp = 1.0 - Math.Min(1.0, Math.Abs(MeanLuma - 128) / 128.0);
        double sharp = Math.Min(1.0, Sharpness / 600.0);
        double con = Math.Min(1.0, Contrast / 70.0);
        double clip = 1.0 - Math.Min(1.0, (ShadowClipRatio + HighlightClipRatio) * 4.0);
        return Math.Round(0.35 * exp + 0.3 * sharp + 0.2 * con + 0.15 * clip, 3);
    }
}

public enum DuplicateKind
{
    Exact = 0,        // 完全相同内容
    SameContent = 1,  // 哈希相同（改名/移动）
    NearDuplicate = 2 // 视觉近似（连拍/微小改动）
}

public sealed class DuplicateGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DuplicateKind Kind { get; set; }
    public List<MediaAsset> Assets { get; set; } = new();
    public long WastedBytes { get; set; }
    public string? Reason { get; set; }
}

public sealed class ExportResult
{
    public int Total { get; set; }
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
}