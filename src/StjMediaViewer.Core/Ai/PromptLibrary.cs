using System.Text;
using System.Text.Json;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Ai;

/// <summary>
/// AI 分类提示词与输出契约（JSON Schema）。
/// 词表 ID 全部取自 <see cref="Vocabularies"/>，模型只允许输出稳定 ID，
/// 避免"中文名/英文名/自造名"污染存储层。
/// </summary>
public static class PromptLibrary
{
    public const string SchemaName = "media_classification";

    /// <summary>分类输出结构（json_schema 用）。</summary>
    public static string JsonSchema => SchemaLazy.Value;

    private static readonly Lazy<string> SchemaLazy = new(BuildSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>DryRun 模式返回的假分类 JSON（结构必须与 schema 一致，供 SelfCheck 走通链路）。</summary>
    public const string FakeClassificationJson =
        "{\"theme\":\"city\",\"tone\":\"tealorange\",\"keywords\":[\"城市\",\"黄昏\",\"建筑\",\"合成测试\"]," +
        "\"subject\":\"DryRun 模式生成的合成城市黄昏场景\",\"mood\":\"冷静克制\",\"is_log\":false," +
        "\"quality\":0.72,\"confidence\":0.8}";

    /// <summary>DryRun 模式 TestConnection 的假回声。</summary>
    public const string FakeConnectionText = "pong（DryRun 本地校验通过，未联网）";

    /// <summary>系统提示词：可选 CustomPrompt 覆盖任务描述，但输出契约与词表始终追加（否则模型必违规）。</summary>
    public static string BuildSystemPrompt(AiSettings? settings)
    {
        var task = string.IsNullOrWhiteSpace(settings?.CustomPrompt)
            ? DefaultTaskPrompt
            : settings!.CustomPrompt!.Trim() + "\n\n" + OutputContract;

        return task.TrimEnd() + "\n\n" + BuildVocabularyBlock() + "\n" + OutputFormatRules;
    }

    /// <summary>用户消息里的文字部分：文件/拍摄参数/已有元数据，给模型做辅助判断。</summary>
    public static string BuildUserText(MediaAsset asset, bool hasImage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(hasImage
            ? "请分析这张素材的画面与影调，按系统要求输出 JSON。"
            : "暂无可用图像，请仅根据以下文字信息推断最可能的分类（信息不足时降低 confidence）。");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(asset.FileName)) sb.AppendLine($"文件名：{asset.FileName}");
        sb.AppendLine($"类型：{KindText(asset)}");

        if (asset.Width is > 0 && asset.Height is > 0)
            sb.AppendLine($"尺寸：{asset.Width}×{asset.Height}（{FormatUtil.Megapixels(asset.Width, asset.Height)}）");
        if (asset.DurationSeconds is > 0)
            sb.AppendLine($"时长：{FormatUtil.Duration(asset.DurationSeconds)}，帧率 {asset.FrameRate:0.###}");
        if (!string.IsNullOrWhiteSpace(asset.VideoCodec))
            sb.AppendLine($"视频编码：{asset.VideoCodec}");

        var device = asset.DisplayDevice;
        if (!string.IsNullOrWhiteSpace(asset.CameraMake) || !string.IsNullOrWhiteSpace(asset.CameraModel) ||
            !string.IsNullOrWhiteSpace(asset.DeviceKey))
            sb.AppendLine($"拍摄设备：{device}");
        if (!string.IsNullOrWhiteSpace(asset.LensModel)) sb.AppendLine($"镜头：{asset.LensModel}");
        if (asset.FocalLengthMm is > 0 || asset.ApertureF is > 0 || asset.Iso is > 0 || asset.ShutterSpeedSeconds is > 0)
        {
            sb.AppendLine($"参数：{FormatUtil.Focal(asset.FocalLengthMm)} / {FormatUtil.Aperture(asset.ApertureF)} / " +
                          $"{FormatUtil.Shutter(asset.ShutterSpeedSeconds)} / {FormatUtil.Iso(asset.Iso)}");
        }

        if (asset.CapturedAt is { } captured) sb.AppendLine($"拍摄时间：{FormatUtil.DateTime(captured)}");
        var place = string.Join(" · ", new[] { asset.Country, asset.Province, asset.City }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(place)) sb.AppendLine($"拍摄地点：{place}");
        if (!string.IsNullOrWhiteSpace(asset.LogProfile)) sb.AppendLine($"Log 配置：{asset.LogProfile}");
        else if (asset.IsLog == true) sb.AppendLine("已知为 Log 素材");
        if (!string.IsNullOrWhiteSpace(asset.TransferCharacteristics))
            sb.AppendLine($"传输特性：{asset.TransferCharacteristics}");

        if (!string.IsNullOrWhiteSpace(asset.Title)) sb.AppendLine($"标题：{asset.Title}");
        if (!string.IsNullOrWhiteSpace(asset.Caption)) sb.AppendLine($"说明：{asset.Caption}");
        if (!string.IsNullOrWhiteSpace(asset.Creator)) sb.AppendLine($"作者：{asset.Creator}");
        if (asset.Keywords.Count > 0)
            sb.AppendLine("已有标签：" + string.Join("、", asset.Keywords.Take(12)));

        if (asset.PostProcess == PostProcessState.Processed || asset.HasDevelopEdits)
            sb.AppendLine(!string.IsNullOrWhiteSpace(asset.DevelopEngine)
                ? $"后期：已处理（{asset.DevelopEngine}）"
                : "后期：已处理");

        sb.AppendLine();
        sb.AppendLine("再次强调：只输出一个 JSON 对象，theme/tone 必须是词表里的稳定 ID。");
        return sb.ToString();
    }

    private static string KindText(MediaAsset a)
    {
        if (a.IsVideo) return $"视频 {a.FormatCode}".Trim();
        if (a.IsAudio) return $"音频 {a.FormatCode}".Trim();
        if (a.IsRaw) return $"RAW 图片 {a.FormatCode}（{MediaTypeCatalog.RawDisplay(a.RawFormat)}）".Trim();
        return (!string.IsNullOrWhiteSpace(a.FormatCode) ? "图片 " + a.FormatCode : "未知类型").Trim();
    }

    private const string DefaultTaskPrompt =
        "你是一位资深摄影编辑与影像归档助理，正在为专业创作者的素材库做批量分类。\n" +
        "请仔细观察画面的主体、场景与影调特征，判断它属于哪个主题、哪种影调，并给出简短的内容描述。";

    private const string OutputContract =
        "请分析用户提供的素材，并给出归档分类。";

    private const string OutputFormatRules =
        "【输出格式（强制）】\n" +
        "只输出一个 JSON 对象，不要 markdown 代码块、不要前缀说明、不要任何解释文字。字段如下：\n" +
        "{\n" +
        "  \"theme\": \"主题稳定ID\",\n" +
        "  \"tone\": \"影调稳定ID\",\n" +
        "  \"keywords\": [\"中文关键词\", \"3~8个\"],\n" +
        "  \"subject\": \"一句话描述画面内容（≤40字）\",\n" +
        "  \"mood\": \"2~6个字的情绪氛围\",\n" +
        "  \"is_log\": false,\n" +
        "  \"quality\": 0.7,\n" +
        "  \"confidence\": 0.8\n" +
        "}\n" +
        "规则：\n" +
        "1. theme / tone 必须严格使用下方词表里的稳定 ID，禁止自造；都不合适用 other。\n" +
        "2. keywords 为 3~8 个中文关键词或专有名词，不要重复 theme/tone 的显示名。\n" +
        "3. subject 用一句话说明主体与场景，不要空泛（避免\"一张照片\"这类描述）。\n" +
        "4. mood 用 2~6 个字表达情绪氛围（如：宁静、热烈、克制、忧郁）。\n" +
        "5. is_log：画面呈灰平、低对比、未调色的 Log 特征时为 true，否则 false；拿不准填 false。\n" +
        "6. quality 为 0~1 的技术质量（曝光/清晰度/构图可用性），不是艺术评分。\n" +
        "7. confidence 为 0~1，表示你对 theme/tone 判断的整体把握；不要虚高。\n" +
        "8. JSON 必须是合法的双引号 JSON，不能带注释或结尾多余逗号。";

    private static string BuildVocabularyBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【主题词表 theme】（只能输出左侧稳定 ID）");
        foreach (var v in Vocabularies.Themes)
            sb.AppendLine($"- {v.Id}（{v.Zh} / {v.En}）{Hint(v)}");
        sb.AppendLine();
        sb.AppendLine("【影调词表 tone】（只能输出左侧稳定 ID）");
        foreach (var v in Vocabularies.Tones)
            sb.AppendLine($"- {v.Id}（{v.Zh} / {v.En}）{Hint(v)}");
        return sb.ToString().TrimEnd();
    }

    private static string Hint(FacetValue v) => string.IsNullOrWhiteSpace(v.Hint) ? "" : "：" + v.Hint;

    private static string BuildSchema()
    {
        static string[] Ids(IReadOnlyList<FacetValue> list) => list.Select(v => v.Id).ToArray();

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "theme", "tone", "keywords", "subject", "mood", "is_log", "quality", "confidence" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["theme"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = Ids(Vocabularies.Themes),
                    ["description"] = "主题稳定 ID，必须来自词表",
                },
                ["tone"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = Ids(Vocabularies.Tones),
                    ["description"] = "影调稳定 ID，必须来自词表",
                },
                ["keywords"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["minItems"] = 1,
                    ["maxItems"] = 10,
                    ["description"] = "3~8 个中文关键词",
                },
                ["subject"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["maxLength"] = 120,
                    ["description"] = "一句话描述画面内容",
                },
                ["mood"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["maxLength"] = 40,
                    ["description"] = "情绪氛围，2~6 字",
                },
                ["is_log"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                ["quality"] = new Dictionary<string, object?>
                {
                    ["type"] = "number",
                    ["minimum"] = 0,
                    ["maximum"] = 1,
                },
                ["confidence"] = new Dictionary<string, object?>
                {
                    ["type"] = "number",
                    ["minimum"] = 0,
                    ["maximum"] = 1,
                },
            },
        };
        return JsonSerializer.Serialize(schema);
    }
}