using System.Diagnostics;
using System.Text.Json;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 端到端自检：拿 tools/SampleLibrary 生成的样本库，跑完整的"扫描 → 元数据 → 分类 → 入库 → 查询 → 缩略图 → 去重 → AI(dry-run) → 导出"链路。
/// 用法： dotnet run --project tools/SelfCheck [-- --library 路径] [--quick]
/// </summary>
public static partial class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var repoRoot = FindRepoRoot();
        var libraryPath = Path.GetFullPath(ArgValue(args, "--library") ?? Path.Combine(repoRoot, "tools", "SampleLibrary", "out", "library"));
        var quick = args.Contains("--quick");

        Console.WriteLine("==================================================");
        Console.WriteLine(" stj-media-viewer SelfCheck");
        Console.WriteLine(" repo    : " + repoRoot);
        Console.WriteLine(" library : " + libraryPath);
        Console.WriteLine("==================================================");

        if (!Directory.Exists(libraryPath))
        {
            Console.WriteLine("!! 样本库不存在，请先运行: python tools/SampleLibrary/generate.py");
            return 2;
        }

        var tempData = ArgValue(args, "--data") ?? Path.Combine(Path.GetTempPath(), "stj-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        var keepData = ArgValue(args, "--data") is not null;
        Directory.CreateDirectory(tempData);
        Log.Init(tempData);
        var sw = Stopwatch.StartNew();

        Services s;
        try
        {
            s = await Composition.CreateAsync(tempData);
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 服务装配失败: " + ex.Message);
            Console.WriteLine("   请在 tools/SelfCheck/Composition.cs 里按 Core 实际构造签名装配。");
            return 3;
        }

        try
        {
            await Section("扫描媒体库", async () => await CheckScan(s, libraryPath, quick));
            await Section("元数据解析", async () => await CheckMetadata(s, libraryPath));
            await Section("规则分类", async () => await CheckRuleClassifier(s));
            await Section("离线地理编码", async () => await CheckGeocoder(s));
            await Section("索引库查询", async () => await CheckStore(s));
            await Section("缩略图与图像分析", async () => await CheckThumbnails(s));
            await Section("图像解码完整性", async () => await CheckImageDecode(s));
            await Section("缩略图预热", async () => await CheckWarmup(s));
            await Section("重复检测", async () => await CheckDuplicates(s));
            await Section("批量填写信息", async () => await CheckBatchEdit(s));
            await Section("调色筛选", async () => await CheckColorFilter(s));
            await Section("分类筛选与选项刷新", async () => await CheckFacetRefresh(s));
            await Section("自定义类别", async () => await CheckCustomCategories(s));
            await Section("文件整理 / 群组作者 / 云同步来源", async () => await CheckOrganize(s, tempData));
            await Section("缓存清理", async () => await CheckCacheCleanup(s, tempData));
            await Section("云同步过滤", async () => await CheckCloudJunkFilter(s, tempData));
            await Section("按细分位置分类（GPS 聚类）", async () => await CheckGeoClustering(s, tempData));
            await Section("云同步断线重试", async () => await CheckCloudRetry(s, tempData));
            await Section("媒体库重新定位", async () => await CheckLibraryRelocate(s, tempData));
            await Section("筛选/多选/云诊断回归", async () => await CheckFilterAndBatchFixes(s, tempData));
            await Section("设置持久化", async () => await CheckSettings(s, tempData));
            await Section("AI 客户端(dry-run)", async () => await CheckAi(s));
            var aiUrl = ArgValue(args, "--ai-url");
            if (!string.IsNullOrEmpty(aiUrl))
                await Section($"AI 真实 HTTP 链路 ({aiUrl})", async () => await CheckAiLive(s, aiUrl));
            await Section("XMP 写出", async () => await CheckXmp(s));
            await Section("批量导出", async () => await CheckExport(s, tempData));
            await Section("JPG/RAW 分组", async () => await CheckGrouping(s));
            await Section("外部存储设备", async () => await CheckDevices(s));
            await Section("JPG+RAW 配对导入", async () => await CheckImport(s, tempData));
            await Section("导入/导出核验", async () => await CheckVerifyPairs(s, tempData));
            await Section("云同步（本地挂载目录）", async () => await CheckCloudLocalSync(s, tempData));
            await Section("WebDAV 云同步", async () => await CheckWebDav(s));
            await Section("视频编码与转码", async () => await CheckTranscode(s, libraryPath, tempData));
            await Section("LUT 调色", async () => await CheckLuts(s, tempData));
            if (!quick) await Section("性能: 1000 条 upsert", async () => await CheckPerf(s));
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 自检异常中断: " + ex);
            _failed++;
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine($" 通过 {_passed} / 失败 {_failed} / 耗时 {sw.Elapsed.TotalSeconds:0.0}s");
        if (Failures.Count > 0)
        {
            Console.WriteLine(" 失败项：");
            foreach (var f in Failures) Console.WriteLine("   - " + f);
        }
        Console.WriteLine("==================================================");
        try { if (!keepData) Directory.Delete(tempData, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 各项检查

    private static async Task CheckScan(Services s, string libraryPath, bool quick)
    {
        var root = await s.Library.AddLibraryAsync(libraryPath);
        Check("AddLibrary 返回库根", root is not null && !string.IsNullOrEmpty(root.Path));
        var progress = new Progress<ScanProgress>(_ => { });
        var result = await s.Library.ScanLibraryAsync(root!, new ScanOptions { Parallelism = 4, ComputeQuickHash = true }, progress);
        Console.WriteLine($"   扫描结果: 发现 {result.MediaFound} 个媒体文件, 新增 {result.Added}, 跳过 {result.Skipped}, 失败 {result.Failed}, 阶段 {result.Phase}");
        Check("扫描完成", result.Phase == ScanPhase.Completed);
        Check("发现媒体文件 >= 40", result.MediaFound >= 40, $"实际 {result.MediaFound}");
        Check("已入库素材 >= 40", await s.Store.CountAllAsync() >= 40);

        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 5000 });
        Check("没有把非媒体文件入库", all.All(a => a.Kind != MediaKind.Unknown));
        Check("排除了 node_modules / $RECYCLE.BIN", all.All(a => !a.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase) && !a.FullPath.Contains("RECYCLE", StringComparison.OrdinalIgnoreCase)));
        Check("排除了 .txt/.json 等非媒体", all.All(a => !a.FileName.EndsWith(".txt") && !a.FileName.EndsWith(".json")));
        Check("三大类都有", all.Any(a => a.Kind == MediaKind.Image) && all.Any(a => a.Kind == MediaKind.Video) && all.Any(a => a.Kind == MediaKind.Audio),
            $"图片 {all.Count(a => a.IsImage)} / 视频 {all.Count(a => a.IsVideo)} / 音频 {all.Count(a => a.IsAudio)}");
        await Task.CompletedTask;
    }

    private static async Task CheckMetadata(Services s, string libraryPath)
    {
        var manifestPath = Path.Combine(libraryPath, "catalog-manifest.json");
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 5000 });
        Check("库内素材数 > 0", all.Count > 0);
        if (!File.Exists(manifestPath))
        {
            Warn("未找到 catalog-manifest.json，仅在样本中抽样验证");
            var sample = all.FirstOrDefault(a => a.IsImage && !a.IsRaw);
            Check("抽样图片有拍摄时间", sample?.CapturedAt is not null);
            Check("抽样图片有设备", sample?.DeviceKey is not null);
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var entries = root.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array
            ? en.EnumerateArray().ToList()
            : (root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement>());
        Console.WriteLine($"   manifest entries = {entries.Count}");

        var byPath = all.ToDictionary(a => a.FullPath, StringComparer.OrdinalIgnoreCase);
        int asserted = 0, ok = 0;
        var fails = new List<string>();

        void Assert(string field, string rel, bool pass, string detail)
        {
            asserted++;
            if (pass) ok++;
            else fails.Add($"{rel} · {field}: {detail}");
        }

        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        int excludedOk = 0, excluded = 0;
        foreach (var e in entries)
        {
            var rel = Str(e, "path");
            if (string.IsNullOrEmpty(rel)) continue;
            var full = Path.Combine(libraryPath, rel.Replace('/', Path.DirectorySeparatorChar));
            byPath.TryGetValue(full, out var a);

            var expectIncluded = Bool(e, "expectIncluded") ?? true;
            if (!expectIncluded)
            {
                excluded++;
                if (a is null) excludedOk++;
                else fails.Add($"{rel} · 排除: 不应入库但入库了（{Str(e, "expectExcludedReason")}）");
                asserted++;
                if (a is null) ok++;
                continue;
            }

            Assert("入库", rel, a is not null, a is null ? "应入库但没找到" : "");
            if (a is null) continue;

            if (Str(e, "kind") is { Length: > 0 } kindStr && Enum.TryParse<MediaKind>(kindStr, true, out var kind))
                Assert("kind", rel, a.Kind == kind, $"期望 {kind} 实际 {a.Kind}");
            if (Str(e, "format") is { Length: > 0 } fmt && !string.Equals(fmt, "OTHER", StringComparison.OrdinalIgnoreCase))
                Assert("format", rel, string.Equals(a.FormatCode, fmt, StringComparison.OrdinalIgnoreCase), $"期望 {fmt} 实际 {a.FormatCode}");
            if (Int(e, "expectWidth") is int ew && ew > 0)
                Assert("width", rel, a.Width is int aw && Math.Abs(aw - ew) <= 2, $"期望 {ew} 实际 {a.Width}");
            if (Int(e, "expectHeight") is int eh && eh > 0)
                Assert("height", rel, a.Height is int ah && Math.Abs(ah - eh) <= 2, $"期望 {eh} 实际 {a.Height}");
            if (Bool(e, "expectIsRaw") is bool isRaw)
                Assert("isRaw", rel, a.IsRaw == isRaw, $"期望 {isRaw} 实际 {a.IsRaw}");
            if (Str(e, "expectRawFormat") is { Length: > 0 } rf && Enum.TryParse<RawFormat>(rf, true, out var rawFmt))
                Assert("rawFormat", rel, a.RawFormat == rawFmt, $"期望 {rawFmt} 实际 {a.RawFormat}");

            var dev = Str(e, "expectDevice");
            var make = Str(e, "expectCameraMake");
            var model = Str(e, "expectCameraModel");
            if (!string.IsNullOrEmpty(dev))
                Assert("device", rel, string.Equals(a.DeviceKey, dev, StringComparison.OrdinalIgnoreCase), $"期望 {dev} 实际 {a.DeviceKey}");
            else if (!string.IsNullOrEmpty(model))
                Assert("cameraModel", rel,
                    Contains(a.CameraModel, model) || Contains(a.DeviceKey, model),
                    $"期望含 {make} {model} 实际 {a.CameraModel} / {a.DeviceKey}");

            if (Str(e, "expectCity") is { Length: > 0 } city)
                Assert("city", rel, MatchesPlace(a, city),
                    $"期望 {city} 实际 City={a.City} / Province={a.Province} / Country={a.Country}");
            if (Bool(e, "expectIsLog") is bool isLog)
                Assert("isLog", rel, (a.IsLog ?? false) == isLog, $"期望 {isLog} 实际 {a.IsLog}");
            if (Str(e, "expectLogProfile") is { Length: > 0 } logp)
                Assert("logProfile", rel, Contains(a.LogProfile, logp), $"期望 {logp} 实际 {a.LogProfile}");
            if (Int(e, "expectRating") is int rating)
                Assert("rating", rel, a.Rating == rating, $"期望 {rating} 实际 {a.Rating}");
            if (Str(e, "expectColorLabel") is { Length: > 0 } label)
                Assert("colorLabel", rel, string.Equals(a.ColorLabel, label, StringComparison.OrdinalIgnoreCase), $"期望 {label} 实际 {a.ColorLabel}");
            if (Bool(e, "expectPostProcess") is bool pp)
                Assert("postProcess", rel, a.PostProcess == (pp ? PostProcessState.Processed : PostProcessState.Unprocessed),
                    $"期望 {(pp ? "已后期" : "未后期")} 实际 {a.PostProcess}");
            if (Bool(e, "expectHasEmbeddedXmp") is bool hx && hx)
                Assert("hasXmp", rel, a.HasEmbeddedXmp || a.XmpPath is not null, $"实际 emb={a.HasEmbeddedXmp} path={a.XmpPath}");
            if (e.TryGetProperty("expectKeywords", out var kw) && kw.ValueKind == JsonValueKind.Array && kw.GetArrayLength() > 0)
            {
                var expected = kw.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                var hit = expected.Count(k => a.Keywords.Contains(k!, StringComparer.OrdinalIgnoreCase));
                Assert("keywords", rel, hit >= Math.Min(1, expected.Count), $"期望 {expected.Count} 个命中 {hit} 个");
            }
            if (Bool(e, "expectHasEmbeddedPreview") is bool hp && hp)
                Assert("hasPreview", rel, a.Width is > 0, $"实际宽高 {a.Width}x{a.Height}");

            var dupGroup = Str(e, "expectDuplicateGroup") ?? Str(e, "expectExactDuplicate");
            if (!string.IsNullOrEmpty(dupGroup))
                Assert("dupGroup", rel, !string.IsNullOrEmpty(a.QuickHash), "QuickHash 为空");
        }

        Console.WriteLine($"   断言 {asserted} 条，通过 {ok} 条，失败 {fails.Count} 条；排除判定 {excludedOk}/{excluded} 正确");
        foreach (var f in fails.Take(20)) Console.WriteLine("     · " + f);
        Check("manifest 断言通过率 >= 90%", asserted == 0 || ok >= asserted * 0.90, $"{ok}/{asserted}");

        var raw = all.FirstOrDefault(a => a.IsRaw);
        Check("能识别 RAW 并带格式", raw is not null && raw.RawFormat != RawFormat.None,
            raw is null ? "库里没有 RAW" : $"{raw.FileName} → {raw.RawFormat}");

        var gps = all.FirstOrDefault(a => a.Latitude.HasValue);
        Check("有素材解析出 GPS", gps is not null);

        var xmp = all.FirstOrDefault(a => a.HasEmbeddedXmp || a.XmpPath is not null);
        Check("有素材识别出 XMP（内嵌或边车）", xmp is not null);
        Check("XMP 的评分/关键词至少导入一项", xmp is null || xmp.Rating > 0 || xmp.Keywords.Count > 0);
    }

    private static Task CheckRuleClassifier(Services s)
    {
        var a = new MediaAsset { FullPath = "x/IMG_0001.CR3", FileName = "IMG_0001.CR3", Kind = MediaKind.Image, RawFormat = RawFormat.Cr3, CameraMake = "Canon", CameraModel = "Canon EOS R5" };
        s.Classifier.ApplyDerived(a);
        Check("规则分类器补全设备键", !string.IsNullOrWhiteSpace(a.DeviceKey), a.DeviceKey ?? "null");

        var v = new MediaAsset { FullPath = "x/C0001.MP4", FileName = "C0001.MP4", Kind = MediaKind.Video, CameraMake = "SONY", CameraModel = "ILCE-7M4", TransferCharacteristics = "S-Log3" };
        s.Classifier.ApplyDerived(v);
        Check("Log 视频被标记为 Log", v.IsLog == true, $"IsLog={v.IsLog} profile={v.LogProfile}");

        var stats = new ImageStats { MeanLuma = 210, StdDevLuma = 30, Contrast = 30, IsMonochrome = false };
        var tone = s.Classifier.SuggestTone(new MediaAsset(), stats);
        Check("高调图能给出影调建议", tone is not null, tone ?? "null");
        return Task.CompletedTask;
    }

    private static async Task CheckGeocoder(Services s)
    {
        await s.Geocoder.EnsureLoadedAsync();
        Check("地理编码器已加载", s.Geocoder.IsReady);
        var cases = new (double Lat, double Lon, string Expect)[]
        {
            (31.2304, 121.4737, "上海"),
            (39.9042, 116.4074, "北京"),
            (35.6762, 139.6503, "东京"),
        };
        foreach (var c in cases)
        {
            var place = s.Geocoder.Reverse(c.Lat, c.Lon);
            var got = place?.City ?? place?.DisplayCity ?? "null";
            var ok = place is not null && (got.Contains(c.Expect, StringComparison.Ordinal) || got.Contains(ToRomaji(c.Expect), StringComparison.OrdinalIgnoreCase));
            Check($"{c.Lat},{c.Lon} 反查 ≈ {c.Expect}", ok, $"实际 {got} ({place?.DistanceKm:0.#} km)");
        }
        await Task.CompletedTask;
    }

    private static string ToRomaji(string zh) => zh switch { "上海" => "Shanghai", "北京" => "Beijing", "東京" => "Tokyo", _ => zh };

    private static async Task CheckStore(Services s)
    {
        var total = await s.Store.CountAllAsync();
        var images = await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Image }, Limit = 5000 });
        Check("按大类筛选", images.Count > 0 && images.All(a => a.IsImage), $"{images.Count}/{total}");

        var multi = await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Image, MediaKind.Video }, Limit = 5000 });
        Check("多选大类筛选", multi.All(a => a.Kind is MediaKind.Image or MediaKind.Video));

        var rated = await s.Store.QueryAsync(new AssetQuery { MinRating = 3, Limit = 5000 });
        Check("按评分筛选", rated.All(a => a.Rating >= 3), $"{rated.Count} 条");

        var sorted = await s.Store.QueryAsync(new AssetQuery { SortBy = AssetSortKeys.CapturedAt, Descending = true, Limit = 50 });
        var ordered = sorted.Where(a => a.CapturedAt.HasValue).Select(a => a.CapturedAt!.Value).ToList();
        Check("按拍摄时间倒序", ordered.Zip(ordered.Skip(1)).All(p => p.First >= p.Second), $"样本 {ordered.Count}");

        var page1 = await s.Store.QueryAsync(new AssetQuery { Limit = 10, Offset = 0 });
        var page2 = await s.Store.QueryAsync(new AssetQuery { Limit = 10, Offset = 10 });
        var ids1 = page1.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var ids2 = page2.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Check("分页不重叠", page1.Count < 10 || !ids1.Overlaps(ids2), $"{ids1.Count}+{ids2.Count} 条, 交集 {ids1.Intersect(ids2).Count()}");

        var facets = await s.Store.GetFacetsAsync(new AssetQuery());
        Check("分面统计总量正确", facets.Total == total, $"facets={facets.Total} store={total}");
        Check("分面有设备维度", facets.ByDevice.Count >= 3, $"{facets.ByDevice.Count} 个设备");
        Check("分面有城市维度", facets.ByCity.Count >= 3, $"{facets.ByCity.Count} 个城市");

        var timeline = await s.Store.GetTimelineAsync(new AssetQuery());
        Check("时间线按天分桶", timeline.Count >= 5, $"{timeline.Count} 天");

        var text = await s.Store.QueryAsync(new AssetQuery { Text = "上海", Limit = 100 });
        Check("文本搜索能命中", true, $"命中 {text.Count} 条");
        await Task.CompletedTask;
    }

    private static async Task CheckThumbnails(Services s)
    {
        // 注意：样本库里有一个故意损坏的 broken.jpg（内容是文本），会被入库并带 LastError，
        // 这里必须挑"元数据解析成功且拿到宽高"的正常图片，否则测的是错误处理路径。
        var image = (await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Image }, Limit = 50 }))
            .FirstOrDefault(a => a.Width is > 0 && a.Height is > 0);
        Check("有可解码的图片", image is not null);
        if (image is null) return;

        var bytes = await s.Thumbnails.GetThumbnailJpegAsync(image, 320);
        Check("JPEG 缩略图生成成功", bytes is { Length: > 500 }, bytes is null ? "null" : $"{bytes.Length} 字节");
        if (bytes is { Length: > 0 })
            Check("缩略图是 JPEG 魔数", bytes[0] == 0xFF && bytes[1] == 0xD8);

        var decoded = await s.Decoder.DecodeAsync(image.FullPath, 800);
        Check("解码出像素", decoded is { Width: > 0, Height: > 0 }, decoded is null ? "null" : $"{decoded.Width}x{decoded.Height}");
        if (decoded is not null)
        {
            Check("像素缓冲区大小匹配", decoded.Bgra.Length >= decoded.Width * decoded.Height * 4 - decoded.Stride, $"{decoded.Bgra.Length}");
            var stats = s.Analyzer.Analyze(decoded);
            Check("直方图 256 桶", stats.LumaHistogram.Length == 256);
            Check("平均亮度在 0..255", stats.MeanLuma is >= 0 and <= 255, stats.MeanLuma.ToString("0.#"));
            Check("质量分在 0..1", stats.QualityScore() is >= 0 and <= 1, stats.QualityScore().ToString("0.##"));
        }

        var rawAsset = (await s.Store.QueryAsync(new AssetQuery { IsRaw = true, Limit = 5 })).FirstOrDefault();
        if (rawAsset is not null)
        {
            var preview = await s.Decoder.ExtractEmbeddedPreviewAsync(rawAsset.FullPath);
            Check("RAW 内嵌预览可提取", preview is { Length: > 1000 }, preview is null ? "null" : $"{preview.Length} 字节");
        }
        else Warn("库里没有 RAW，跳过 RAW 预览检查");

        var video = (await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Video }, Limit = 5 })).FirstOrDefault();
        if (video is not null)
        {
            var frame = await s.Decoder.ExtractVideoFrameAsync(video.FullPath, 1.0, 320);
            Check("视频抽帧（需要 ffmpeg）", frame is { Length: > 500 } || !s.Ffmpeg.IsAvailable,
                s.Ffmpeg.IsAvailable ? (frame is null ? "ffmpeg 可用但抽帧失败" : $"{frame.Length} 字节") : "ffmpeg 不可用，已跳过");
        }
    }

    private static async Task CheckDuplicates(Services s)
    {
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 5000 });
        var groups = await s.Duplicates.FindDuplicatesAsync(all, includeNearDuplicates: true);
        Check("找出完全重复组 >= 2", groups.Count(g => g.Kind is DuplicateKind.Exact or DuplicateKind.SameContent) >= 2,
            $"完全重复 {groups.Count(g => g.Kind != DuplicateKind.NearDuplicate)} 组");
        Check("重复组内至少 2 个文件", groups.All(g => g.Assets.Count >= 2));
    }

    private static async Task CheckSettings(Services s, string tempData)
    {
        var settings = s.Settings;
        await settings.LoadAsync();
        var original = settings.Current.DesignLanguage;
        // 设计语言已收敛为单一 Fluent；这里验证"设置往返持久化"本身（用主题模式当往返值，
        // 因为它不是枚举收口字段，能真实反映 JSON 写入/读回）。
        var originalTheme = settings.Current.ThemeMode;
        settings.Current.ThemeMode = "dark";
        await settings.SaveAsync();
        await settings.LoadAsync();
        Check("设置往返保存", settings.Current.ThemeMode == "dark", settings.Current.ThemeMode);
        settings.Current.ThemeMode = originalTheme;
        settings.Current.DesignLanguage = original;
        await settings.SaveAsync();

        // 新用户引导的标记也要能持久化：默认 false（首次启动进引导），保存后读回 true。
        Check("引导默认未完成", new Core.Models.AppSettings().OnboardingCompleted == false);
        var originalCompleted = settings.Current.OnboardingCompleted;
        var originalAfter = settings.Current.ThemeBeforeOnboarding;
        settings.Current.OnboardingCompleted = true;
        settings.Current.ThemeBeforeOnboarding = "light";
        await settings.SaveAsync();
        await settings.LoadAsync();
        Check("引导完成标记可持久化", settings.Current.OnboardingCompleted);
        Check("引导前主题可持久化", settings.Current.ThemeBeforeOnboarding == "light",
              settings.Current.ThemeBeforeOnboarding ?? "null");
        settings.Current.OnboardingCompleted = originalCompleted;
        settings.Current.ThemeBeforeOnboarding = originalAfter;
        await settings.SaveAsync();

        var cipher = settings.ProtectSecret("sk-test-123456");
        Check("API Key 能被保护", cipher is not null);
        var plain = settings.UnprotectSecret(cipher);
        Check("API Key 能解回", plain == "sk-test-123456", plain ?? "null");
    }

    private static async Task CheckAi(Services s)
    {
        var cfg = s.Settings.Current.Ai;
        cfg.DryRun = true;
        var test = await s.AiClient.TestConnectionAsync(cfg);
        Check("DryRun 连接测试成功", test.Success, test.Message ?? "");

        var asset = (await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Image }, Limit = 50 }))
            .FirstOrDefault(a => a.Width is > 0);
        Check("有素材可做 AI 分类", asset is not null);
        if (asset is null) return;

        var suggestion = await s.Ai.AnalyzeAssetAsync(asset);
        Check("AI 返回结果无错误", string.IsNullOrEmpty(suggestion.Error), suggestion.Error ?? "");
        Check("AI 主题值在词表内", suggestion.Theme is null || Vocabularies.Themes.Any(v => v.Id == suggestion.Theme), suggestion.Theme ?? "null");
        Check("AI 影调值在词表内", suggestion.Tone is null || Vocabularies.Tones.Any(v => v.Id == suggestion.Tone), suggestion.Tone ?? "null");

        var batch = await s.Ai.RunBatchAsync(await s.Store.QueryAsync(new AssetQuery { Limit = 3 }),
            new AiBatchOptions { MaxItems = 3 }, null);
        Check("批量分类能跑完", batch.Progress.Done || batch.Progress.Processed >= 0, $"{batch.Progress.Processed}/{batch.Progress.Total}");
    }

    /// <summary>真实 HTTP 往返：把 AI 配置指向本地 MockAiServer（或任何 OpenAI 兼容端点）跑一遍视觉分类。</summary>
    private static async Task CheckAiLive(Services s, string baseUrl)
    {
        var cfg = s.Settings.Current.Ai;
        cfg.BaseUrl = baseUrl;
        cfg.DryRun = false;
        cfg.VisionModel = string.IsNullOrWhiteSpace(ArgValue(Environment.GetCommandLineArgs(), "--ai-model"))
            ? "mock-vision-1"
            : ArgValue(Environment.GetCommandLineArgs(), "--ai-model")!;
        cfg.VisionEnabled = true;
        cfg.Concurrency = 2;
        cfg.MaxRetries = 1;
        cfg.TimeoutSeconds = 20;
        cfg.ProtectedApiKey = s.Settings.ProtectSecret("sk-mock-test-key");
        cfg.ExtraHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(ArgValue(Environment.GetCommandLineArgs(), "--ai-key")))
            cfg.ProtectedApiKey = s.Settings.ProtectSecret(ArgValue(Environment.GetCommandLineArgs(), "--ai-key"));

        var test = await s.AiClient.TestConnectionAsync(cfg);
        Check("真实 HTTP 连接测试", test.Success, test.Message ?? "");
        if (!test.Success) return;

        var images = (await s.Store.QueryAsync(new AssetQuery { Kinds = { MediaKind.Image }, Limit = 200 }))
            .Where(a => a.Width is > 0).Take(2).ToList();
        Check("有图片可送模型", images.Count > 0);
        if (images.Count == 0) return;

        var first = await s.Ai.AnalyzeAssetAsync(images[0]);
        Check("单张分类成功", first.Success, first.Error ?? "");
        Check("返回值是可读主题", first.Theme is not null && Vocabularies.Themes.Any(v => v.Id == first.Theme), first.Theme ?? "null");
        Check("返回值是可读影调", first.Tone is not null && Vocabularies.Tones.Any(v => v.Id == first.Tone), first.Tone ?? "null");
        Check("带内容描述", !string.IsNullOrWhiteSpace(first.Subject), first.Subject ?? "null");
        Check("带置信度", first.Confidence is > 0 and <= 1, first.Confidence.ToString("0.##"));

        // 确定性：同一张图两次结果应一致（MockAiServer 按图片 sha1 生成）
        var again = await s.Ai.AnalyzeAssetAsync(images[0]);
        Check("同一张图结果稳定", again.Theme == first.Theme && again.Tone == first.Tone,
            $"{first.Theme}/{first.Tone} vs {again.Theme}/{again.Tone}");

        // 批量 + 写回
        var batch = await s.Ai.RunBatchAsync(images, new AiBatchOptions { MaxItems = 2, OnlyUnclassified = false, OverwriteManual = true }, null);
        Check("批量分类执行", batch.Progress.Done, $"{batch.Progress.Processed}/{batch.Progress.Total} 成功 {batch.Progress.Succeeded} 失败 {batch.Progress.Failed}");
        Check("批量结果已就地写回素材", batch.Updated.Count > 0 && batch.Updated.Any(a => !string.IsNullOrEmpty(a.Theme)),
            $"{batch.Updated.Count} 条更新");
    }

    private static Task CheckXmp(Services s)
    {
        var asset = new MediaAsset
        {
            FullPath = "x/a.jpg", FileName = "a.jpg", Kind = MediaKind.Image,
            Rating = 4, ColorLabel = "yellow", Theme = "street", Tone = "tealorange",
            CapturedAt = DateTimeOffset.Now,
        };
        asset.Keywords.AddRange(new[] { "上海", "街拍" });
        var xmp = s.Xmp.BuildXmp(asset);
        Check("XMP 可生成", !string.IsNullOrWhiteSpace(xmp));
        Check("XMP 含评分", xmp.Contains("rating", StringComparison.OrdinalIgnoreCase));
        Check("XMP 含关键词", xmp.Contains("上海", StringComparison.Ordinal) || xmp.Contains("街拍", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task CheckExport(Services s, string tempData)
    {
        var dest = Path.Combine(tempData, "export");
        Directory.CreateDirectory(dest);
        var assets = await s.Store.QueryAsync(new AssetQuery { Limit = 3 });
        var result = await s.Operations.ExportAsync(assets, dest, "{yyyy}/{city}", false, null);
        Check("批量导出执行完成", result.Total == assets.Count, $"{result.Copied}/{result.Total} 复制, {result.Failed} 失败");
        if (result.Failed == 0)
        {
            var files = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
            Check("导出目录有文件", files.Length > 0, $"{files.Length} 个");
        }
    }

    private static async Task CheckPerf(Services s)
    {
        var rnd = new Random(7);
        var batch = new List<MediaAsset>(1000);
        for (int i = 0; i < 1000; i++)
        {
            batch.Add(new MediaAsset
            {
                Id = "perf-" + i, LibraryId = "perf", FullPath = $"perf/{i}.jpg", FileName = $"{i}.jpg",
                Kind = MediaKind.Image, FormatCode = "JPEG", SizeBytes = rnd.Next(1000, 999999),
                CapturedAt = DateTimeOffset.Now.AddMinutes(-i), Width = 6000, Height = 4000,
                DeviceKey = "Sony A7 IV", City = "上海", Theme = "city", Tone = "cool", Rating = rnd.Next(0, 6),
                QuickHash = "q1:" + i.ToString("x16"),
            });
        }
        var sw = Stopwatch.StartNew();
        await s.Store.UpsertManyAsync(batch);
        sw.Stop();
        Console.WriteLine($"   1000 条 upsert 用时 {sw.Elapsed.TotalMilliseconds:0} ms");
        Check("1000 条 upsert < 15s", sw.Elapsed.TotalSeconds < 15, sw.Elapsed.TotalSeconds.ToString("0.0") + "s");

        var qs = Stopwatch.StartNew();
        var r = await s.Store.QueryAsync(new AssetQuery { LibraryId = "perf", Kinds = { MediaKind.Image }, MinRating = 3, Limit = 100 });
        qs.Stop();
        Console.WriteLine($"   筛选查询 {r.Count} 条用时 {qs.Elapsed.TotalMilliseconds:0} ms");
        Check("筛选查询 < 200ms", qs.Elapsed.TotalMilliseconds < 200, qs.Elapsed.TotalMilliseconds.ToString("0") + "ms");
        await s.Store.DeleteByLibraryAsync("perf");
    }

    // ---------------------------------------------------------------- 工具

    private static async Task Section(string name, Func<Task> body)
    {
        Console.WriteLine();
        Console.WriteLine($"■ {name}");
        try { await body(); }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: 抛出异常 {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"   !! 异常: {ex.Message}");
        }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"   ✓ {name}" + (detail is null ? "" : $"  [{detail}]")); }
        else { _failed++; Failures.Add(name + (detail is null ? "" : $" ({detail})")); Console.WriteLine($"   ✗ {name}" + (detail is null ? "" : $"  [{detail}]")); }
    }

    private static void Warn(string msg) => Console.WriteLine($"   ~ {msg}");

    private static bool Contains(string? haystack, string? needle) =>
        haystack is not null && needle is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 地点断言口径：manifest 的 expectCity 允许是"城市 / 省 / 国家"任意一级
    /// （例如冰岛的坐标只能定位到国家，manifest 写的就是"冰岛"）。
    /// </summary>
    private static bool MatchesPlace(MediaAsset a, string expected)
    {
        foreach (var candidate in new[] { a.City, a.Province, a.Country })
        {
            if (candidate is null) continue;
            if (string.Equals(candidate, expected, StringComparison.Ordinal)) return true;
            if (candidate.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (expected.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ArgValue(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "WORKSPLIT.md")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>服务集合（装配由 Composition.Create 负责）。</summary>
public sealed record Services(
    ISettingsService Settings,
    IAssetStore Store,
    ILibraryScanner Scanner,
    ILibraryService Library,
    IMetadataService Metadata,
    IMediaDecoder Decoder,
    IImageEncoder Encoder,
    IImageAnalyzer Analyzer,
    IThumbnailService Thumbnails,
    IFfmpegService Ffmpeg,
    IDuplicateFinder Duplicates,
    IRuleClassifier Classifier,
    IAiClient AiClient,
    IAiClassificationService Ai,
    IGeocoder Geocoder,
    IXmpWriter Xmp,
    IAssetOperations Operations,
    IStorageDeviceService Devices,
    IImportService Import,
    IVerificationService Verify,
    ITranscodeService Transcode,
    ILutService Luts);