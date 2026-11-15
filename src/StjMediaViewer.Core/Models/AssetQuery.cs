namespace StjMediaViewer.Core.Models;

/// <summary>素材查询条件。UI 的面板筛选、搜索、排序最终都编译成它。</summary>
public sealed class AssetQuery
{
    public string? LibraryId { get; set; }
    /// <summary>文件名 / 关键词 / 标题 / 描述 / 地点 的模糊搜索。</summary>
    public string? Text { get; set; }

    public List<MediaKind> Kinds { get; set; } = new();
    public List<string> Formats { get; set; } = new();
    public List<string> Devices { get; set; } = new();
    public List<string> Cities { get; set; } = new();
    public List<string> Themes { get; set; } = new();
    public List<string> Tones { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public List<string> ColorLabels { get; set; } = new();
    public List<RawFormat> RawFormats { get; set; } = new();

    public bool? IsRaw { get; set; }
    public bool? IsLog { get; set; }
    public bool? HasGps { get; set; }
    public bool? IsPick { get; set; }
    public PostProcessState? PostProcess { get; set; }
    public int? MinRating { get; set; }
    public int? MaxRating { get; set; }

    public DateTimeOffset? CapturedFrom { get; set; }
    public DateTimeOffset? CapturedTo { get; set; }

    /// <summary>只要还没有主题/影调分类的结果。</summary>
    public bool OnlyUnclassified { get; set; }
    /// <summary>只要解析失败或尚未解析元数据的。</summary>
    public bool OnlyProblems { get; set; }

    /// <summary>
    /// 折叠 JPG+RAW 分组：开启时只返回每组代表（JPG 优先），网格里一张照片只出现一次。
    /// </summary>
    public bool CollapseGroups { get; set; }

    public string SortBy { get; set; } = AssetSortKeys.CapturedAt;
    public bool Descending { get; set; } = true;
    public int Offset { get; set; }
    public int Limit { get; set; } = 500;

    public bool HasAnyFacetFilter =>
        Kinds.Count > 0 || Formats.Count > 0 || Devices.Count > 0 || Cities.Count > 0 ||
        Themes.Count > 0 || Tones.Count > 0 || Keywords.Count > 0 || ColorLabels.Count > 0 ||
        RawFormats.Count > 0 || IsRaw.HasValue || IsLog.HasValue || HasGps.HasValue ||
        IsPick.HasValue || PostProcess.HasValue || MinRating.HasValue || MaxRating.HasValue ||
        CapturedFrom.HasValue || CapturedTo.HasValue || OnlyUnclassified || OnlyProblems;
}

public static class AssetSortKeys
{
    public const string CapturedAt = "capturedAt";
    public const string FileName = "fileName";
    public const string FileModified = "fileModified";
    public const string Size = "size";
    public const string Rating = "rating";
    public const string IndexedAt = "indexedAt";
    public const string Duration = "duration";
    public const string Quality = "quality";
}

/// <summary>侧面筛选面板用的分面计数。</summary>
public sealed class FacetCounts
{
    public Dictionary<string, int> ByKind { get; set; } = new();
    public Dictionary<string, int> ByFormat { get; set; } = new();
    public Dictionary<string, int> ByDevice { get; set; } = new();
    public Dictionary<string, int> ByCity { get; set; } = new();
    public Dictionary<string, int> ByTheme { get; set; } = new();
    public Dictionary<string, int> ByTone { get; set; } = new();
    public Dictionary<string, int> ByYear { get; set; } = new();
    public Dictionary<string, int> ByPostProcess { get; set; } = new();
    public Dictionary<string, int> ByPick { get; set; } = new();
    public int Total { get; set; }
    public int RawCount { get; set; }
    public int LogCount { get; set; }
    public int GpsCount { get; set; }
    public int UnclassifiedCount { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>按天分组的时间线桶。</summary>
public sealed class TimelineBucket
{
    public DateOnly Day { get; set; }
    public int Count { get; set; }
    public int ImageCount { get; set; }
    public int VideoCount { get; set; }
    public int AudioCount { get; set; }
}