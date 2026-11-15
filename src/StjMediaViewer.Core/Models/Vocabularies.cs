namespace StjMediaViewer.Core.Models;

/// <summary>一个可枚举的分类取值：稳定 ID（AI/存储/查询用）+ 中英显示名。</summary>
public sealed record FacetValue(string Id, string Zh, string En, string? Hint = null)
{
    public string Display(string locale) => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? En : Zh;
}

/// <summary>
/// 全部分类词表。存储与 AI 交互一律使用稳定 ID，界面按语言显示；
/// 这样换语言、换模型、换皮肤都不会破坏数据。
/// </summary>
public static class Vocabularies
{
    // ---- 拍摄主题 ----
    public static readonly IReadOnlyList<FacetValue> Themes = new FacetValue[]
    {
        new("nature", "自然", "Nature", "山川湖海、植被、天气、自然景观"),
        new("humanities", "人文", "Humanities", "民俗、纪实、人文场景与人物活动"),
        new("city", "城市", "City", "城市建筑、天际线、城市空间"),
        new("street", "街拍", "Street", "街头构图与瞬间"),
        new("portrait", "人像", "Portrait", "以人物为主体"),
        new("candid", "扫街", "Candid", "抓拍、随走随拍"),
        new("art", "艺术创作", "Artistic", "观念、抽象、实验性创作"),
        new("architecture", "建筑", "Architecture"),
        new("stilllife", "静物", "Still Life"),
        new("landscape", "风光", "Landscape"),
        new("wildlife", "野生动物", "Wildlife"),
        new("night", "夜景", "Night"),
        new("travel", "旅行", "Travel"),
        new("sport", "运动", "Sport"),
        new("macro", "微距", "Macro"),
        new("documentary", "纪实", "Documentary"),
        new("product", "产品", "Product"),
        new("food", "美食", "Food"),
        new("other", "其他", "Other"),
    };

    // ---- 影调 ----
    public static readonly IReadOnlyList<FacetValue> Tones = new FacetValue[]
    {
        new("highkey", "高调", "High Key", "整体明亮、低反差"),
        new("lowkey", "低调", "Low Key", "整体暗、强氛围"),
        new("midkey", "中间调", "Mid Key"),
        new("hard", "硬调", "Hard", "强反差、硬光"),
        new("soft", "柔调", "Soft", "低反差、柔光"),
        new("cool", "冷调", "Cool"),
        new("warm", "暖调", "Warm"),
        new("mono", "黑白", "Monochrome"),
        new("lowsat", "低饱和", "Low Saturation", "莫兰迪、灰调"),
        new("hisat", "高饱和", "High Saturation"),
        new("tealorange", "青橙", "Teal & Orange", "电影感经典配色"),
        new("vintage", "复古", "Vintage"),
        new("cinematic", "电影感", "Cinematic"),
        new("neon", "霓虹", "Neon"),
        new("natural", "自然色彩", "Natural Color"),
        new("other", "其他", "Other"),
    };

    // ---- 颜色标签（Lightroom 风格）----
    public static readonly IReadOnlyList<FacetValue> ColorLabels = new FacetValue[]
    {
        new("none", "无", "None"),
        new("red", "红", "Red"),
        new("yellow", "黄", "Yellow"),
        new("green", "绿", "Green"),
        new("blue", "蓝", "Blue"),
        new("purple", "紫", "Purple"),
    };

    // ---- 后期状态 ----
    public static readonly IReadOnlyList<FacetValue> PostProcessStates = new FacetValue[]
    {
        new("unknown", "未标记", "Unknown"),
        new("unprocessed", "未调色", "Unprocessed"),
        new("processed", "已调色", "Processed"),
    };

    public static string ThemeDisplay(string? id, string locale) =>
        DisplayOf(Themes, id) is { } v ? v.Display(locale) : (string.IsNullOrWhiteSpace(id) ? "未分类" : id!);

    public static string ToneDisplay(string? id, string locale) =>
        DisplayOf(Tones, id) is { } v ? v.Display(locale) : (string.IsNullOrWhiteSpace(id) ? "未分类" : id!);

    public static FacetValue? DisplayOf(IReadOnlyList<FacetValue> list, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : list.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>把模型返回的中文/英文/杂项文本映射回稳定 ID；失败返回 null。</summary>
    public static string? NormalizeTheme(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        foreach (var v in Themes)
        {
            if (t.Equals(v.Id, StringComparison.OrdinalIgnoreCase) ||
                t.Equals(v.Zh, StringComparison.Ordinal) ||
                t.Equals(v.En, StringComparison.OrdinalIgnoreCase))
                return v.Id;
        }
        foreach (var v in Themes)
        {
            if (t.Contains(v.Zh, StringComparison.Ordinal) || t.Contains(v.En, StringComparison.OrdinalIgnoreCase))
                return v.Id;
        }
        return null;
    }

    public static string? NormalizeTone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        foreach (var v in Tones)
        {
            if (t.Equals(v.Id, StringComparison.OrdinalIgnoreCase) ||
                t.Equals(v.Zh, StringComparison.Ordinal) ||
                t.Equals(v.En, StringComparison.OrdinalIgnoreCase))
                return v.Id;
        }
        foreach (var v in Tones)
        {
            if (t.Contains(v.Zh, StringComparison.Ordinal) || t.Contains(v.En, StringComparison.OrdinalIgnoreCase))
                return v.Id;
        }
        return null;
    }

    public static string PostProcessId(PostProcessState s) => s switch
    {
        PostProcessState.Processed => "processed",
        PostProcessState.Unprocessed => "unprocessed",
        _ => "unknown",
    };

    public static PostProcessState ParsePostProcess(string? id) => id?.ToLowerInvariant() switch
    {
        "processed" => PostProcessState.Processed,
        "unprocessed" => PostProcessState.Unprocessed,
        _ => PostProcessState.Unknown,
    };
}