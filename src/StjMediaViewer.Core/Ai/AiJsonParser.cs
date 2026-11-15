using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Ai;

/// <summary>
/// 容错 JSON 解析：视觉模型经常用 markdown 代码块包裹 JSON、加前后说明文字、
/// 返回数组或网关包装（<c>{"result": {...}}</c>）、甚至带结尾逗号。
/// 这里把这些情况都归一成一个 <see cref="AiSuggestion"/>。
/// </summary>
public static class AiJsonParser
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    private static readonly Regex TrailingCommaRegex = new(@",(\s*[}\]])", RegexOptions.Compiled);

    /// <summary>从模型输出中提取第一个可解析的 JSON 片段（对象或数组）。找不到返回 null。</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim().Trim('\uFEFF', '\u200B', '\u00A0', '\0');
        if (s.Length == 0) return null;

        // 1) markdown 代码块
        var fenced = ExtractFromCodeFence(s);
        if (fenced != null && TryNormalize(fenced) is { } fencedJson) return fencedJson;

        // 2) 本身就是 JSON
        if (TryNormalize(s) is { } direct) return direct;

        // 3) 从任意位置扫描平衡的 {...} / [...]
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is not ('{' or '[')) continue;
            var candidate = ScanBalanced(s, i);
            if (candidate is null) continue;
            if (TryNormalize(candidate) is { } normalized) return normalized;
        }
        return null;
    }

    /// <summary>把模型文本解析成分类建议；解析失败时返回带 Error 的对象（不抛异常）。</summary>
    public static AiSuggestion ParseSuggestion(string? text, string? modelId = null)
    {
        var suggestion = new AiSuggestion { ModelId = modelId, RawJson = text };
        try
        {
            var json = ExtractJson(text);
            if (json is null)
            {
                suggestion.Error = "未能在模型返回中找到合法 JSON";
                return suggestion;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json, documentOptions: DocOptions);
            }
            catch (Exception ex)
            {
                suggestion.Error = "JSON 解析失败: " + ex.Message;
                return suggestion;
            }

            var obj = FindPayloadObject(node, depth: 0);
            if (obj is null)
            {
                suggestion.Error = "模型返回的 JSON 不是对象";
                return suggestion;
            }

            suggestion.Theme = GetString(obj, "theme", "themeId", "theme_id", "category", "topic", "主题");
            suggestion.Tone = GetString(obj, "tone", "toneId", "tone_id", "colorTone", "影调", "色调");
            suggestion.Keywords = GetKeywords(obj);
            suggestion.Subject = GetString(obj, "subject", "description", "caption", "content", "mainSubject", "主体", "描述");
            suggestion.Mood = GetString(obj, "mood", "feeling", "atmosphere", "氛围", "情绪");
            suggestion.IsLog = GetBool(obj, "is_log", "isLog", "log", "logFootage", "is_log_footage");
            suggestion.QualityScore = GetDouble(obj, "quality", "qualityScore", "quality_score", "score", "质量");
            var conf = GetDouble(obj, "confidence", "conf", "sure", "置信度");
            suggestion.Confidence = conf ?? 0.7;

            suggestion.Keywords = suggestion.Keywords
                .Select(CleanKeyword)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            suggestion.Subject = Truncate(suggestion.Subject, 120);
            suggestion.Mood = Truncate(suggestion.Mood, 40);

            var hasAnything = suggestion.Theme != null || suggestion.Tone != null ||
                              suggestion.Subject != null || suggestion.Mood != null ||
                              suggestion.Keywords.Count > 0 || suggestion.IsLog != null ||
                              suggestion.QualityScore != null;
            if (!hasAnything)
            {
                suggestion.Error = "模型返回的 JSON 缺少有效分类字段";
                return suggestion;
            }

            suggestion.Error = null;
            return suggestion;
        }
        catch (Exception ex)
        {
            suggestion.Error = "解析异常: " + ex.Message;
            return suggestion;
        }
    }

    // ---------------------------- 提取 ----------------------------

    private static string? ExtractFromCodeFence(string s)
    {
        var fenceStart = s.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0) return null;
        var afterFence = s.IndexOf('\n', fenceStart);
        var contentStart = afterFence >= 0 ? afterFence + 1 : fenceStart + 3;
        if (contentStart >= s.Length) return null;

        var fenceEnd = s.IndexOf("```", contentStart, StringComparison.Ordinal);
        var content = fenceEnd >= 0 ? s[contentStart..fenceEnd] : s[contentStart..];
        return content.Trim();
    }

    private static string? ScanBalanced(string s, int start)
    {
        var open = s[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return s[start..(i + 1)];
            }
        }
        return null;
    }

    private static string? TryNormalize(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        foreach (var attempt in new[] { candidate, Repair(candidate) })
        {
            if (string.IsNullOrWhiteSpace(attempt)) continue;
            if (!LooksLikeJson(attempt)) continue;
            try
            {
                using var _ = JsonDocument.Parse(attempt, DocOptions);
                return attempt;
            }
            catch
            {
                // 继续尝试修复版本
            }
        }
        return null;
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>常见模型小毛病：中文引号、结尾多余逗号。</summary>
    private static string Repair(string candidate)
    {
        var s = candidate;
        try
        {
            s = s.Replace('\u201c', '"').Replace('\u201d', '"');
            s = TrailingCommaRegex.Replace(s, "$1");
        }
        catch
        {
            // 修复失败就返回原文
        }
        return s;
    }

    /// <summary>网关包装/数组/字符串嵌套层层剥开，找到真正的分类对象。</summary>
    private static JsonObject? FindPayloadObject(JsonNode? node, int depth)
    {
        if (node is null || depth > 4) return null;

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (FindPayloadObject(item, depth + 1) is { } found) return found;
            }
            return null;
        }

        if (node is not JsonObject obj) return null;

        // 若对象自身带分类字段，直接用
        if (HasAnyField(obj, "theme", "tone", "subject", "keywords", "quality")) return obj;

        foreach (var wrapper in new[] { "result", "data", "classification", "output", "response", "message", "content", "choices" })
        {
            if (!TryFind(obj, wrapper, out var inner) || inner is null) continue;

            if (inner is JsonValue v && v.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text) && LooksLikeJson(text.TrimStart()))
            {
                try
                {
                    var parsed = JsonNode.Parse(text, documentOptions: DocOptions);
                    if (FindPayloadObject(parsed, depth + 1) is { } nested) return nested;
                }
                catch { }
                continue;
            }

            if (FindPayloadObject(inner, depth + 1) is { } found) return found;
        }

        return null;
    }

    // ---------------------------- 字段读取 ----------------------------

    private static bool HasAnyField(JsonObject obj, params string[] names) =>
        names.Any(n => TryFind(obj, n, out _));

    private static bool TryFind(JsonObject obj, string name, out JsonNode? node)
    {
        if (obj.TryGetPropertyValue(name, out node)) return true;
        foreach (var kv in obj)
        {
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                node = kv.Value;
                return true;
            }
        }
        node = null;
        return false;
    }

    private static JsonNode? Find(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryFind(obj, name, out var node) && node is not null) return node;
        }
        return null;
    }

    private static string? GetString(JsonObject obj, params string[] names)
    {
        var node = Find(obj, names);
        return NodeToString(node);
    }

    private static string? NodeToString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                if (value.TryGetValue<string>(out var s)) return s.Trim();
                if (value.TryGetValue<double>(out var d)) return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                return value.ToJsonString().Trim().Trim('"');
            case JsonArray arr:
            {
                var parts = arr.Select(NodeToString).Where(x => !string.IsNullOrWhiteSpace(x));
                var joined = string.Join(" ", parts);
                return string.IsNullOrWhiteSpace(joined) ? null : joined;
            }
            case JsonObject o:
                return GetString(o, "text", "value", "name");
            default:
                return null;
        }
    }

    private static List<string> GetKeywords(JsonObject obj)
    {
        var node = Find(obj, "keywords", "keyword", "tags", "labels", "标签", "关键词");
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var s = NodeToString(item);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        else if (node is not null)
        {
            var s = NodeToString(node);
            if (!string.IsNullOrWhiteSpace(s)) list.AddRange(SplitKeywords(s));
        }
        return list;
    }

    private static IEnumerable<string> SplitKeywords(string s) =>
        s.Split(new[] { ',', '，', '、', ';', '；', '|', '/', ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CleanKeyword(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Trim('#', '"', '\'', '。', '.', '，', ',');
        s = Regex.Replace(s, @"\s+", " ");
        return s.Length > 40 ? s[..40] : s;
    }

    private static bool? GetBool(JsonObject obj, params string[] names)
    {
        var node = Find(obj, names);
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<double>(out var d)) return d != 0;
            if (value.TryGetValue<string>(out var s))
            {
                var t = s.Trim().ToLowerInvariant();
                if (t is "true" or "yes" or "y" or "1" or "是" or "log") return true;
                if (t is "false" or "no" or "n" or "0" or "否" or "none" or "not log") return false;
            }
        }
        return null;
    }

    private static double? GetDouble(JsonObject obj, params string[] names)
    {
        var node = Find(obj, names);
        if (node is null) return null;
        double d;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out d)) { }
            else if (value.TryGetValue<string>(out var s))
            {
                s = s.Trim().TrimEnd('%');
                if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d))
                    return null;
            }
            else return null;
        }
        else return null;

        // 模型偶尔输出 0~100：>1 且 <=100 时按百分制折算
        if (d > 1 && d <= 100) d /= 100.0;
        return Math.Clamp(d, 0, 1);
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.Length <= max) return t;
        return t[..max];
    }
}