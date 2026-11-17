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
    /// <summary>影像合集筛选：命中任意一个选中合集的素材（多选 = 并集）。</summary>
    public List<string> Collections { get; set; } = new();

    /// <summary>
    /// 只要"废片"（显式标记 is_pick = 0）。
    /// 注意和 <see cref="IsPick"/> 区分：IsPick=false 表示"未被选用"，包含从未标记的 NULL。
    /// </summary>
    public bool OnlyRejected { get; set; }

    /// <summary>合并组筛选：命中任意一个选中合并组的素材（多选 = 并集）。</summary>
    public List<string> MergeGroups { get; set; } = new();
    public List<string> ColorLabels { get; set; } = new();
    public List<RawFormat> RawFormats { get; set; } = new();

    public bool? IsRaw { get; set; }
    public bool? IsLog { get; set; }
    /// <summary>调色状态：true = 已调色（已后期或套了 LUT），false = 未调色，null = 不限。</summary>
    public bool? Colored { get; set; }

    /// <summary>来源云同步目标名筛选（"来自 xx 云同步"）。</summary>
    public List<string> Origins { get; set; } = new();

    /// <summary>自定义类别筛选：维度名 → 允许的值（同一维度内 OR，不同维度之间 AND）。</summary>
    public Dictionary<string, List<string>> CustomFilters { get; set; } = new(StringComparer.Ordinal);
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

    /// <summary>主题/影调为空的素材数（"未填写"伪选项用）。</summary>
    /// <summary>自定义类别分面：维度 → (值 → 计数)。</summary>
    public Dictionary<string, Dictionary<string, int>> ByCustom { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> ByOrigin { get; set; } = new(StringComparer.Ordinal);

    /// <summary>没有来源标记（本地文件）的数量。</summary>
    public int OriginUnsetCount { get; set; }

    public int ThemeUnsetCount { get; set; }

    /// <summary>影像合集分面：合集 id → 素材数。</summary>
    public Dictionary<string, int> ByCollection { get; set; } = new(StringComparer.Ordinal);

    /// <summary>没有加入任何合集的素材数（"未加入合集"伪选项用）。</summary>
    public int CollectionUnsetCount { get; set; }

    /// <summary>合并组分面：合并组 id → 素材数。</summary>
    public Dictionary<string, int> ByMergeGroup { get; set; } = new(StringComparer.Ordinal);
    public int ToneUnsetCount { get; set; }
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