namespace StjMediaViewer.Core.Models;

/// <summary>
/// 批量填写信息：字段为 null 表示"不改动"，空字符串表示"清空该字段"。
/// 用于导卡 / 扫描后一次性给选中的一批素材补分类、日期、城市等信息。
/// </summary>
public sealed class BatchEditPatch
{
    /// <summary>拍摄时间（本地时间，写入时同时更新 captured_at / captured_ms / captured_day）。</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }

    public string? CameraMake { get; set; }
    public string? DeviceKey { get; set; }

    /// <summary>已后期状态：0 未后期 / 1 已后期 / 2 未知（对应 PostProcess 枚举值）。</summary>
    public int? PostProcess { get; set; }

    public bool? IsLog { get; set; }
    public string? LogProfile { get; set; }

    /// <summary>主题（人像 / 风光 / 街头 …，自由文本，与九维分类保持一致）。</summary>
    public string? Theme { get; set; }
    /// <summary>影调（高调 / 低调 / 硬调 …）。</summary>
    public string? Tone { get; set; }

    /// <summary>评分 0–5。</summary>
    public int? Rating { get; set; }
    /// <summary>色标：red / yellow / green / blue / purple，空串清空。</summary>
    public string? ColorLabel { get; set; }
    /// <summary>选用状态：true 选用，false 废片（null 不改）。</summary>
    public bool? IsPick { get; set; }

    public string? Title { get; set; }
    public string? Caption { get; set; }

    /// <summary>自定义类别：维度名 → 值（空字符串 = 删除该维度的值）。null = 不改动。</summary>
    public Dictionary<string, string>? CustomFields { get; set; }

    /// <summary>追加关键词（去重后合并进 keywords_json / keywords_flat）。</summary>
    public List<string>? AddKeywords { get; set; }
    /// <summary>整体替换关键词（null = 不改，空列表 = 清空）。</summary>
    public List<string>? ReplaceKeywords { get; set; }

    /// <summary>是否需要写库（没有任何字段就没意义）。</summary>
    public bool HasChanges =>
        CapturedAt is not null || Country is not null || Province is not null || City is not null ||
        CameraMake is not null || DeviceKey is not null || PostProcess is not null ||
        IsLog is not null || LogProfile is not null || Theme is not null || Tone is not null ||
        Rating is not null || ColorLabel is not null || IsPick is not null ||
        Title is not null || Caption is not null ||
        AddKeywords is { Count: > 0 } || ReplaceKeywords is not null ||
        CustomFields is { Count: > 0 };

    /// <summary>人类可读的"将要写入"摘要（界面提示用）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CapturedAt is { } t) parts.Add($"拍摄时间 {t:yyyy-MM-dd HH:mm}");
        if (City is not null) parts.Add(City.Length == 0 ? "清空城市" : $"城市 {City}");
        if (Province is not null) parts.Add(Province.Length == 0 ? "清空省份" : $"省份 {Province}");
        if (Country is not null) parts.Add(Country.Length == 0 ? "清空国家" : $"国家 {Country}");
        if (DeviceKey is not null) parts.Add(DeviceKey.Length == 0 ? "清空设备" : $"设备 {DeviceKey}");
        if (CameraMake is not null) parts.Add(CameraMake.Length == 0 ? "清空品牌" : $"品牌 {CameraMake}");
        if (Theme is not null) parts.Add(Theme.Length == 0 ? "清空主题" : $"主题 {Theme}");
        if (Tone is not null) parts.Add(Tone.Length == 0 ? "清空影调" : $"影调 {Tone}");
        if (PostProcess is { } pp) parts.Add($"已后期 {PostProcessText(pp)}");
        if (IsLog is { } log) parts.Add(log ? "Log 素材" : "非 Log");
        if (LogProfile is not null) parts.Add(LogProfile.Length == 0 ? "清空 Log 曲线" : $"Log {LogProfile}");
        if (Rating is { } r) parts.Add($"评分 {r}");
        if (ColorLabel is not null) parts.Add(ColorLabel.Length == 0 ? "清空色标" : $"色标 {ColorLabel}");
        if (IsPick is { } pick) parts.Add(pick ? "选用" : "废片");
        if (Title is not null) parts.Add(Title.Length == 0 ? "清空标题" : $"标题 {Title}");
        if (Caption is not null) parts.Add(Caption.Length == 0 ? "清空说明" : "说明");
        if (AddKeywords is { Count: > 0 }) parts.Add($"加关键词 {string.Join('/', AddKeywords)}");
        if (ReplaceKeywords is not null) parts.Add($"关键词改为 {ReplaceKeywords.Count} 个");
        return parts.Count == 0 ? "（没有要写入的字段）" : string.Join(" · ", parts);
    }

    private static string PostProcessText(int value) => value switch
    {
        1 => "未后期",
        2 => "已后期",
        _ => "未知",
    };
}