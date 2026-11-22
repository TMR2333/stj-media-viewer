using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Exchange;

public enum ExchangeMergeMode
{
    /// <summary>只填空字段（默认）：本机已有的人工成果保留。</summary>
    FillEmpty = 0,
    /// <summary>覆盖：文件里有值的字段直接覆盖，没有的字段保持不动。</summary>
    Overwrite = 1,
}

/// <summary>导入计划中的一条匹配结果。</summary>
public sealed class ExchangeMatch
{
    public required ExchangeAsset Source { get; init; }
    public required MediaAsset Target { get; init; }
    public bool Ambiguous { get; init; }
    public string FileName => Source.FileName;
}

/// <summary>导入计划（只读，不写库）。</summary>
public sealed class ExchangeImportPlan
{
    public required ExchangeDocument Document { get; init; }
    public List<ExchangeMatch> Matches { get; } = new();
    public List<string> Unmatched { get; } = new();
    public List<string> Ambiguous { get; } = new();
    public List<(string Name, List<string> AssetIds, List<string> Missing)> Collections { get; } = new();
    public int MatchedCount => Matches.Count;
    public int UnmatchedCount => Unmatched.Count;
    public bool HasSettings => Document.Settings is { } s &&
        (!string.IsNullOrWhiteSpace(s.ThemeMode) || !string.IsNullOrWhiteSpace(s.Density) || s.Sort is not null);
    public bool HasCollections => Document.Collections.Count > 0;
}

public sealed class ExchangeImportSummary
{
    public int Matched { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Unmatched { get; set; }
    public int CreatedCollections { get; set; }
    public int AddedCollectionMembers { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Messages { get; } = new();
    public bool Success => Failed == 0;

    public string Describe() =>
        $"匹配 {Matched} · 更新 {Updated} · 跳过 {Skipped} · 失败 {Failed}" +
        (Unmatched > 0 ? $" · 未匹配 {Unmatched}" : "") +
        (CreatedCollections > 0 ? $" · 新建合集 {CreatedCollections}" : "") +
        (AddedCollectionMembers > 0 ? $" · 合集新增 {AddedCollectionMembers}" : "") +
        $" · 用时 {Elapsed.TotalSeconds:0.0}s";
}

/// <summary>导入时"设置"部分的生效结果（由 App 负责保存设置与刷新界面）。</summary>
public sealed class ExchangeSettingsResult
{
    public bool ThemeApplied { get; set; }
    public string? ThemeMode { get; set; }
    public bool DensityApplied { get; set; }
    public string? Density { get; set; }
    /// <summary>排序键（AssetSortKeys 常量），null = 文件里没带。</summary>
    public string? SortKey { get; set; }
    public bool SortAscending { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// 设置与图片数据导入导出：跨平台交换文件 v1 的读写、匹配与合并。
/// 契约见 docs/PROJECT-HANDBOOK.md 与 macOS 版 SettingsExchange.swift。
/// </summary>
public sealed class SettingsExchangeService
{
    public const string FormatId = ExchangeDocument.FormatId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Windows 色标（英文）→ 交换文件（中文）。</summary>
    private static readonly Dictionary<string, string> ColorToChinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Red"] = "红", ["Orange"] = "橙", ["Yellow"] = "黄",
        ["Green"] = "绿", ["Blue"] = "蓝", ["Purple"] = "紫",
    };

    private static readonly Dictionary<string, string> ColorFromChinese = new(StringComparer.Ordinal)
    {
        ["红"] = "Red", ["橙色"] = "Orange", ["橙"] = "Orange", ["黄"] = "Yellow",
        ["绿"] = "Green", ["蓝"] = "Blue", ["紫"] = "Purple",
    };

    /// <summary>Windows 排序键（AssetSortKeys）→ 交换文件排序键。</summary>
    private static readonly Dictionary<string, string> SortToExchange = new(StringComparer.OrdinalIgnoreCase)
    {
        [AssetSortKeys.CapturedAt] = "captured",
        [AssetSortKeys.FileName] = "fileName",
        [AssetSortKeys.FileModified] = "modified",
        [AssetSortKeys.Size] = "size",
        [AssetSortKeys.Rating] = "rating",
        [AssetSortKeys.IndexedAt] = "indexed",
    };

    private static readonly Dictionary<string, string> SortFromExchange = new(StringComparer.Ordinal)
    {
        ["captured"] = AssetSortKeys.CapturedAt,
        ["fileName"] = AssetSortKeys.FileName,
        ["modified"] = AssetSortKeys.FileModified,
        ["size"] = AssetSortKeys.Size,
        ["rating"] = AssetSortKeys.Rating,
        ["indexed"] = AssetSortKeys.IndexedAt,
    };

    private readonly IAssetStore _store;

    public SettingsExchangeService(IAssetStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    // =====================================================================
    //  导出
    // =====================================================================

    /// <summary>组装交换文档（不落盘）。</summary>
    public async Task<ExchangeDocument> BuildExportAsync(
        AppSettings settings, string appVersion, string? sortKey = null, bool ascending = false,
        CancellationToken ct = default)
    {
        var doc = new ExchangeDocument
        {
            Format = FormatId,
            FormatVersion = ExchangeDocument.CurrentFormatVersion,
            App = new ExchangeAppInfo
            {
                Name = "STJ Media Viewer",
                Version = appVersion,
                Platform = "windows",
                ExportedAt = DateTimeOffset.UtcNow,
            },
            Settings = new ExchangeSettings
            {
                ThemeMode = NormalizeTheme(settings.ThemeMode),
                Density = ToExchangeDensity(settings.Grid.Density),
                Sort = sortKey is null ? null : new ExchangeSort
                {
                    Key = SortToExchange.TryGetValue(sortKey, out var k) ? k : "captured",
                    Ascending = ascending,
                },
            },
        };

        var all = await _store.QueryAsync(new AssetQuery { Limit = 0, Offset = 0 }, ct).ConfigureAwait(false);
        var idToAsset = all.Where(a => !string.IsNullOrEmpty(a.Id))
            .ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);

        // 合集成员身份（成员可能没有编辑字段，也要写进文件供另一端还原）
        var collections = await _store.GetCollectionsAsync(ct).ConfigureAwait(false);
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var exportedCollections = new List<ExchangeCollection>();
        foreach (var collection in collections.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCulture))
        {
            var members = await _store.QueryAsync(new AssetQuery
            {
                Collections = { collection.Id },
                Limit = 0,
                Offset = 0,
            }, ct).ConfigureAwait(false);

            var keys = new List<ExchangeAssetKey>();
            foreach (var m in members)
            {
                memberIds.Add(m.Id);
                keys.Add(ToKey(m));
            }
            exportedCollections.Add(new ExchangeCollection { Name = collection.Name, Assets = keys });
        }

        // 导出所有"有编辑"的素材 + 所有合集成员
        var exportedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<ExchangeAsset>();
        foreach (var asset in all)
        {
            var edited = ToExchangeAsset(asset);
            var inCollection = memberIds.Contains(asset.Id);
            if (!edited.HasAnyEdit && !inCollection) continue;
            if (!exportedKeys.Add(KeyOf(asset.FileName, asset.SizeBytes))) continue; // 同名同大小只留一份
            assets.Add(edited);
        }

        doc.Assets = assets;
        doc.Collections = exportedCollections;
        Log.Info($"[exchange] 导出准备完成：素材 {assets.Count} 条（含合集成员），合集 {exportedCollections.Count} 个");
        return doc;
    }

    /// <summary>组装并写入文件。</summary>
    public async Task<ExchangeDocument> ExportAsync(
        string filePath, AppSettings settings, string appVersion, string? sortKey = null, bool ascending = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("导出路径不能为空", nameof(filePath));
        var doc = await BuildExportAsync(settings, appVersion, sortKey, ascending, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, json, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
        Log.Info($"[exchange] 已导出 {filePath}（{new FileInfo(filePath).Length} 字节）");
        return doc;
    }

    // =====================================================================
    //  导入：解析 + 计划
    // =====================================================================

    /// <summary>读取并校验交换文件；格式/版本不符直接抛可读异常。</summary>
    public static async Task<ExchangeDocument> LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("找不到交换文件", filePath);

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException("读取交换文件失败：" + ex.Message, ex);
        }

        ExchangeDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ExchangeDocument>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("文件不是有效的 JSON，可能已损坏：" + ex.Message, ex);
        }

        Validate(doc);
        return doc;
    }

    /// <summary>校验格式标识与版本（不做"尽力而为"解析）。</summary>
    public static void Validate(ExchangeDocument? doc)
    {
        if (doc is null) throw new InvalidDataException("交换文件为空");
        if (!string.Equals(doc.Format, FormatId, StringComparison.Ordinal))
            throw new InvalidDataException($"不是本软件的交换文件（format = {doc.Format ?? "(空)"}，应为 {FormatId}）");
        if (doc.FormatVersion < 1)
            throw new InvalidDataException($"交换文件版本无效（formatVersion = {doc.FormatVersion}）");
        if (doc.FormatVersion > ExchangeDocument.CurrentFormatVersion)
            throw new InvalidDataException(
                $"交换文件来自更新的版本（formatVersion = {doc.FormatVersion}，当前支持 {ExchangeDocument.CurrentFormatVersion}），请先更新软件。");
        if (doc.App is null) doc.App = new ExchangeAppInfo();
        if (doc.Assets is null) doc.Assets = new List<ExchangeAsset>();
        if (doc.Collections is null) doc.Collections = new List<ExchangeCollection>();
        if (doc.Settings is null) doc.Settings = new ExchangeSettings();
    }

    /// <summary>在当前库里匹配文件中的素材（含合并组隐藏成员；不新建、不移动文件）。</summary>
    public async Task<ExchangeImportPlan> BuildImportPlanAsync(ExchangeDocument doc, CancellationToken ct = default)
    {
        Validate(doc);
        var plan = new ExchangeImportPlan { Document = doc };

        var all = await _store.QueryAsync(new AssetQuery { Limit = 0, Offset = 0 }, ct).ConfigureAwait(false);
        var byKey = new Dictionary<string, List<MediaAsset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in all)
        {
            var key = KeyOf(asset.FileName, asset.SizeBytes);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<MediaAsset>();
                byKey[key] = list;
            }
            list.Add(asset);
        }

        var idToAsset = all.Where(a => !string.IsNullOrEmpty(a.Id))
            .ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);

        foreach (var item in doc.Assets)
        {
            var key = KeyOf(item.FileName, item.SizeBytes);
            if (!byKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                plan.Unmatched.Add(item.FileName);
                continue;
            }

            MediaAsset? match;
            bool ambiguous = false;
            if (candidates.Count == 1)
            {
                match = candidates[0];
            }
            else
            {
                var wanted = NormalizeRelative(item.RelativePath);
                var narrowed = candidates.Where(c => NormalizeRelative(c.RelativePath) == wanted).ToList();
                if (narrowed.Count == 1)
                {
                    match = narrowed[0];
                }
                else
                {
                    match = candidates[0];
                    ambiguous = true;
                    plan.Ambiguous.Add(item.FileName);
                }
            }

            plan.Matches.Add(new ExchangeMatch { Source = item, Target = match!, Ambiguous = ambiguous });
        }

        // 合集：把成员文件名映射到本库 assetId
        foreach (var collection in doc.Collections)
        {
            var ids = new List<string>();
            var missing = new List<string>();
            foreach (var member in collection.Assets)
            {
                var key = KeyOf(member.FileName, member.SizeBytes);
                if (!byKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
                {
                    missing.Add(member.FileName);
                    continue;
                }
                var wanted = NormalizeRelative(member.RelativePath);
                var chosen = candidates.Count == 1
                    ? candidates[0]
                    : candidates.FirstOrDefault(c => NormalizeRelative(c.RelativePath) == wanted) ?? candidates[0];
                if (!string.IsNullOrEmpty(chosen.Id)) ids.Add(chosen.Id);
            }
            plan.Collections.Add((collection.Name, ids.Distinct().ToList(), missing));
        }

        _ = idToAsset;
        return plan;
    }

    // =====================================================================
    //  导入：应用
    // =====================================================================

    /// <summary>按计划写库（逐条事务）；合集按名字新建/补成员。</summary>
    public async Task<ExchangeImportSummary> ApplyAsync(
        ExchangeImportPlan plan, ExchangeMergeMode mode, CancellationToken ct = default)
    {
        var summary = new ExchangeImportSummary { Matched = plan.MatchedCount, Unmatched = plan.UnmatchedCount };
        var sw = Stopwatch.StartNew();

        foreach (var match in plan.Matches)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var patch = BuildPatch(match.Source, match.Target, mode);
                if (patch is null || !patch.HasChanges)
                {
                    summary.Skipped++;
                    continue;
                }
                var changed = await _store.ApplyBatchEditAsync(new[] { match.Target.Id }, patch, ct).ConfigureAwait(false);
                if (changed > 0) summary.Updated++;
                else summary.Skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                summary.Failed++;
                if (summary.Messages.Count < 20) summary.Messages.Add($"{match.FileName}：{ex.Message}");
                Log.Warn($"[exchange] 写入失败 {match.FileName}", ex);
            }
        }

        // 合集
        var existing = await _store.GetCollectionsAsync(ct).ConfigureAwait(false);
        var byName = existing.GroupBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);

        foreach (var (name, ids, missing) in plan.Collections)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                if (!byName.TryGetValue(name, out var collection))
                {
                    collection = await _store.CreateCollectionAsync(name, null, ct).ConfigureAwait(false);
                    byName[name] = collection;
                    summary.CreatedCollections++;
                }
                if (ids.Count > 0)
                {
                    var added = await _store.AddToCollectionAsync(collection.Id, ids, ct).ConfigureAwait(false);
                    summary.AddedCollectionMembers += added;
                }
                if (missing.Count > 0 && summary.Messages.Count < 20)
                    summary.Messages.Add($"合集「{name}」有 {missing.Count} 个文件未匹配");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                summary.Failed++;
                if (summary.Messages.Count < 20) summary.Messages.Add($"合集「{name}」：{ex.Message}");
                Log.Warn($"[exchange] 合集导入失败 {name}", ex);
            }
        }

        sw.Stop();
        summary.Elapsed = sw.Elapsed;
        Log.Info($"[exchange] 导入完成：{summary.Describe()}");
        return summary;
    }

    /// <summary>把文件里的设置子集应用到本地设置（themeMode / density）；排序由 App 负责即时生效。</summary>
    public static ExchangeSettingsResult ApplySettings(ExchangeDocument doc, AppSettings settings)
    {
        var result = new ExchangeSettingsResult();
        var s = doc.Settings;
        if (s is null) return result;

        if (!string.IsNullOrWhiteSpace(s.ThemeMode))
        {
            var mode = NormalizeTheme(s.ThemeMode);
            if (mode is not null && !string.Equals(settings.ThemeMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                settings.ThemeMode = mode;
                result.ThemeApplied = true;
                result.ThemeMode = mode;
                result.Notes.Add($"明暗模式 → {mode}");
            }
        }

        if (!string.IsNullOrWhiteSpace(s.Density))
        {
            var density = FromExchangeDensity(s.Density);
            if (density is not null && !string.Equals(settings.Grid.Density, density, StringComparison.OrdinalIgnoreCase))
            {
                settings.Grid.Density = density;
                result.DensityApplied = true;
                result.Density = density;
                result.Notes.Add($"网格密度 → {density}");
            }
        }

        if (s.Sort?.Key is { Length: > 0 } key && SortFromExchange.TryGetValue(key, out var sortKey))
        {
            result.SortKey = sortKey;
            result.SortAscending = s.Sort.Ascending;
            result.Notes.Add($"排序 → {key}{(s.Sort.Ascending ? " ↑" : " ↓")}");
        }

        return result;
    }

    // =====================================================================
    //  字段映射 / 合并
    // =====================================================================

    private static ExchangeAsset ToExchangeAsset(MediaAsset asset)
    {
        var item = new ExchangeAsset
        {
            FileName = asset.FileName,
            SizeBytes = asset.SizeBytes,
            RelativePath = NormalizeRelative(asset.RelativePath),
        };

        if (asset.Rating > 0) item.Rating = asset.Rating;
        if (!string.IsNullOrWhiteSpace(asset.ColorLabel) &&
            ColorToChinese.TryGetValue(asset.ColorLabel.Trim(), out var color))
            item.ColorLabel = color;
        if (asset.IsPick is { } pick) item.IsPick = pick ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(asset.Country)) item.Country = asset.Country;
        if (!string.IsNullOrWhiteSpace(asset.Province)) item.Province = asset.Province;
        if (!string.IsNullOrWhiteSpace(asset.City)) item.City = asset.City;

        var themes = asset.Themes.Count > 0
            ? asset.Themes
            : string.IsNullOrWhiteSpace(asset.Theme) ? new List<string>() : new List<string> { asset.Theme };
        if (themes.Count > 0) item.Themes = themes.ToList();
        if (!string.IsNullOrWhiteSpace(asset.Tone)) item.Tone = asset.Tone;
        if (asset.Keywords.Count > 0) item.Keywords = asset.Keywords.ToList();
        if (!string.IsNullOrWhiteSpace(asset.Title)) item.Title = asset.Title;
        if (asset.PostProcess == PostProcessState.Processed) item.PostProcess = 2;

        return item;
    }

    private static ExchangeAssetKey ToKey(MediaAsset asset) => new()
    {
        FileName = asset.FileName,
        SizeBytes = asset.SizeBytes,
        RelativePath = NormalizeRelative(asset.RelativePath),
    };

    /// <summary>按合并策略生成"只改需要改的字段"的 patch；null = 没有可写入内容。</summary>
    private static BatchEditPatch? BuildPatch(ExchangeAsset src, MediaAsset current, ExchangeMergeMode mode)
    {
        var fill = mode == ExchangeMergeMode.FillEmpty;
        var patch = new BatchEditPatch();

        // 评分：0 = 未设置
        if (src.Rating is { } rating && rating > 0)
        {
            if (!fill || current.Rating <= 0) patch.Rating = Math.Clamp(rating, 0, 5);
        }

        if (!string.IsNullOrWhiteSpace(src.ColorLabel))
        {
            var color = ToWindowsColor(src.ColorLabel);
            if (color is not null && (!fill || string.IsNullOrWhiteSpace(current.ColorLabel)))
                patch.ColorLabel = color;
        }

        if (src.IsPick is { } pick)
        {
            if (!fill || current.IsPick is null) patch.IsPick = pick != 0;
        }

        if (!string.IsNullOrWhiteSpace(src.Country) && (!fill || string.IsNullOrWhiteSpace(current.Country)))
            patch.Country = src.Country!.Trim();
        if (!string.IsNullOrWhiteSpace(src.Province) && (!fill || string.IsNullOrWhiteSpace(current.Province)))
            patch.Province = src.Province!.Trim();
        if (!string.IsNullOrWhiteSpace(src.City) && (!fill || string.IsNullOrWhiteSpace(current.City)))
            patch.City = src.City!.Trim();

        // 主题：并集，本机主主题优先
        var currentThemes = current.Themes.Count > 0
            ? current.Themes
            : string.IsNullOrWhiteSpace(current.Theme) ? new List<string>() : new List<string> { current.Theme };
        var fileThemes = (src.Themes ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (fileThemes.Count > 0)
        {
            var merged = new List<string>(currentThemes);
            foreach (var t in fileThemes)
                if (!merged.Contains(t, StringComparer.OrdinalIgnoreCase)) merged.Add(t);
            if (!fill || currentThemes.Count == 0) patch.Themes = merged;
            else if (merged.Count != currentThemes.Count) patch.Themes = merged;
        }

        if (!string.IsNullOrWhiteSpace(src.Tone) && (!fill || string.IsNullOrWhiteSpace(current.Tone)))
            patch.Tone = src.Tone!.Trim();

        // 关键词：并集去重保序（两种模式一致，避免丢本机新增）
        if (src.Keywords is { Count: > 0 })
            patch.AddKeywords = src.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();

        if (!string.IsNullOrWhiteSpace(src.Title) && (!fill || string.IsNullOrWhiteSpace(current.Title)))
            patch.Title = src.Title!.Trim();

        if (src.PostProcess is { } pp && pp > 1)
        {
            if (!fill || current.PostProcess is PostProcessState.Unknown or PostProcessState.Unprocessed)
                patch.PostProcess = (int)PostProcessState.Processed;
        }

        return patch.HasChanges ? patch : null;
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    private static string KeyOf(string? fileName, long size) =>
        (fileName ?? "").Trim().ToLowerInvariant() + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string? NormalizeRelative(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').TrimStart('/');

    private static string? ToWindowsColor(string chinese)
    {
        var key = chinese.Trim();
        if (ColorFromChinese.TryGetValue(key, out var color)) return color;
        // 兼容直接写英文的旧文件
        return ColorToChinese.ContainsKey(key) ? key : null;
    }

    private static string? NormalizeTheme(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        "system" => "system",
        _ => null,
    };

    private static string? ToExchangeDensity(string? density) => density?.Trim().ToLowerInvariant() switch
    {
        "compact" => "small",
        "comfortable" => "medium",
        "spacious" => "large",
        "small" => "small",
        "medium" => "medium",
        "large" => "large",
        _ => null,
    };

    private static string? FromExchangeDensity(string density) => density?.Trim().ToLowerInvariant() switch
    {
        "small" => "compact",
        "medium" => "comfortable",
        "large" => "spacious",
        "compact" => "compact",
        "comfortable" => "comfortable",
        "spacious" => "spacious",
        "list" => null, // Windows 没有列表密度
        _ => null,
    };
}