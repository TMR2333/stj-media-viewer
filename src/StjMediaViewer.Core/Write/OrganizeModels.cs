using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Write;

// =====================================================================================
//  文件整理：按分类把磁盘上的真实文件（以及云端目录）移动到对应文件夹
// =====================================================================================

/// <summary>整理时写进文件夹名的维度。</summary>
public enum OrganizeDimension
{
    Device = 0,
    CameraMake = 1,
    Year = 2,
    YearMonth = 3,
    Month = 4,
    Day = 5,
    City = 6,
    Country = 7,
    Theme = 8,
    Tone = 9,
    CustomCategory = 10,
    Format = 11,
    Kind = 12,
    Rating = 13,
    ColorLabel = 14,
    Extension = 15,
    /// <summary>细分位置：GPS 半径范围内的归一个文件夹（城市-区县）。</summary>
    GeoArea = 16,
}

public static class OrganizeDimensions
{
    public static IReadOnlyList<OrganizeDimension> All { get; } = Enum.GetValues<OrganizeDimension>();

    public static string Display(this OrganizeDimension dimension) => dimension switch
    {
        OrganizeDimension.Device => "拍摄设备",
        OrganizeDimension.CameraMake => "相机品牌",
        OrganizeDimension.Year => "拍摄年份",
        OrganizeDimension.YearMonth => "拍摄年月",
        OrganizeDimension.Month => "拍摄月份",
        OrganizeDimension.Day => "拍摄日期",
        OrganizeDimension.City => "拍摄地点",
        OrganizeDimension.Country => "国家/地区",
        OrganizeDimension.Theme => "拍摄主题",
        OrganizeDimension.Tone => "拍摄影调",
        OrganizeDimension.CustomCategory => "自定义类别",
        OrganizeDimension.Format => "文件格式",
        OrganizeDimension.Kind => "媒体类型",
        OrganizeDimension.Rating => "评分",
        OrganizeDimension.ColorLabel => "颜色标签",
        OrganizeDimension.Extension => "扩展名",
        OrganizeDimension.GeoArea => "细分位置",
        _ => dimension.ToString(),
    };

    public static string Example(this OrganizeDimension dimension) => dimension switch
    {
        OrganizeDimension.Device => "Sony A7 IV / Canon EOS R5",
        OrganizeDimension.CameraMake => "Sony / Canon / DJI",
        OrganizeDimension.Year => "2024 / 2025",
        OrganizeDimension.YearMonth => "2024-05",
        OrganizeDimension.Month => "01 月 … 12 月",
        OrganizeDimension.Day => "2024-05-18",
        OrganizeDimension.City => "上海 / 巴黎",
        OrganizeDimension.Country => "中国 / 日本",
        OrganizeDimension.Theme => "人像 / 风光 / 街拍",
        OrganizeDimension.Tone => "高调 / 低调 / 电影感",
        OrganizeDimension.CustomCategory => "项目 / 客户 / 场地（自己输入的维度）",
        OrganizeDimension.Format => "JPG / ARW / MOV",
        OrganizeDimension.Kind => "图片 / 视频 / 音频",
        OrganizeDimension.Rating => "5 星 … 未评分",
        OrganizeDimension.ColorLabel => "红 / 黄 / 绿 …",
        OrganizeDimension.Extension => "jpg / arw / mov",
        OrganizeDimension.GeoArea => "上海-黄浦区 / 杭州-西湖区（范围内的算一个）",
        _ => "",
    };
}

public enum OrganizeConflictPolicy
{
    /// <summary>重名时自动加序号（默认，最安全）。</summary>
    Rename = 0,
    /// <summary>目标已存在就跳过这个文件。</summary>
    Skip = 1,
    /// <summary>覆盖目标（谨慎使用）。</summary>
    Overwrite = 2,
}

public sealed class OrganizeOptions
{
    /// <summary>一级分类维度。</summary>
    public OrganizeDimension Primary { get; set; } = OrganizeDimension.Device;

    /// <summary>二级分类维度（可选，会变成 "一级/二级/文件名"）。</summary>
    public OrganizeDimension? Secondary { get; set; }

    /// <summary>三级分类维度（可选，会变成 "一级/二级/三级/文件名"）。</summary>
    public OrganizeDimension? Tertiary { get; set; }

    /// <summary>当维度是「自定义类别」时用哪个维度名（项目 / 客户 / 场地……）。</summary>
    public string? CustomDimensionName { get; set; }

    /// <summary>目标根目录；为空 = 每个文件仍留在它自己媒体库的根目录下。</summary>
    public string? RootPath { get; set; }

    public OrganizeConflictPolicy ConflictPolicy { get; set; } = OrganizeConflictPolicy.Rename;

    /// <summary>把 XMP 边车一起搬走（默认开）。</summary>
    public bool IncludeSidecars { get; set; } = true;

    /// <summary>本地整理时：素材 id → 自定义类别值（只在一级/二级用到自定义类别时需要）。</summary>
    public IReadOnlyDictionary<string, string>? CustomValuesByAssetId { get; set; }

    /// <summary>自定义设备名（"设备"维度分文件夹时用别名）。</summary>
    public IReadOnlyDictionary<string, string>? DeviceAliases { get; set; }

    /// <summary>「细分位置」的聚类半径（米）：半径内的照片归一个文件夹。</summary>
    public double GeoRadiusMeters { get; set; } = 500;

    /// <summary>GPS → 「城市-区县」地点名；为 null 时用坐标命名。</summary>
    public Func<double, double, string?>? GeoPlaceResolver { get; set; }
}

/// <summary>计划里的一个文件（主文件或边车）。</summary>
public sealed record OrganizeEntry(
    string AssetId,
    string FileName,
    string SourcePath,
    string TargetPath,
    string CategoryPath,
    long SizeBytes,
    bool TargetExists,
    string? Note = null,
    bool IsSidecar = false);

public sealed class OrganizePlan
{
    public List<OrganizeEntry> Entries { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>素材 id → 它所属媒体库根目录（执行时换算 relative_path 用）。</summary>
    public Dictionary<string, string> AssetRoots { get; } = new(StringComparer.Ordinal);

    public int FileCount => Entries.Count(e => !e.IsSidecar);
    public long TotalBytes => Entries.Where(e => !e.IsSidecar).Sum(e => e.SizeBytes);
    public int ConflictCount => Entries.Count(e => e.TargetExists);
    public int SidecarCount => Entries.Count(e => e.IsSidecar);
    public bool IsEmpty => FileCount == 0;
}

public sealed class OrganizeResult
{
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public List<string> Errors { get; } = new();
    public bool UndoAvailable { get; set; }

    public string Summary => Moved == 0 && Failed == 0 && Skipped == 0
        ? "没有需要移动的文件"
        : $"移动 {Moved} 个文件" + (Skipped > 0 ? $" · 跳过 {Skipped}" : "") +
          (Failed > 0 ? $" · 失败 {Failed}" : "") + $" · {FormatSize(Bytes)}";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => bytes + " B",
    };
}

// ------------------------------------------------------------------ 云端

public sealed record CloudOrganizeEntry(
    string RelativeFrom,
    string RelativeTo,
    string FileName,
    string CategoryPath,
    long SizeBytes,
    bool TargetExists,
    string? Note = null);

public sealed class CloudOrganizePlan
{
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public List<CloudOrganizeEntry> Entries { get; } = new();
    public List<string> Warnings { get; } = new();

    public long TotalBytes => Entries.Sum(e => e.SizeBytes);
    public int FileCount => Entries.Count;
    public int ConflictCount => Entries.Count(e => e.TargetExists);
    public bool IsEmpty => Entries.Count == 0;
}

// ------------------------------------------------------------------ 撤销

public sealed class OrganizeUndoRecord
{
    /// <summary>"local" 或 "cloud"。</summary>
    public string Kind { get; set; } = "local";
    public string? TargetId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<OrganizeUndoItem> Items { get; set; } = new();

    public bool IsEmpty => Items.Count == 0;
}

public sealed class OrganizeUndoItem
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? AssetId { get; set; }
    public string? OldRelativePath { get; set; }
    public string? NewRelativePath { get; set; }
}