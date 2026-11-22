using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Exchange;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>设置与图片数据导入导出：跨平台交换文件 v1 的导出/匹配/合并/合集/设置映射。</summary>
    private static async Task CheckExchange(Services s, string tempData)
    {
        var dir = Path.Combine(tempData, "exchange");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "catalog.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);

        using var store = new SqliteAssetStore(dbPath);
        await store.InitializeAsync();

        // ---------- 准备素材：A 有完整编辑、B 只有评分、C 只在合集里 ----------
        var root = Path.Combine(dir, "library");
        Directory.CreateDirectory(root);
        MediaAsset Make(string id, string name, long size) => new()
        {
            Id = id,
            LibraryId = "lib-exchange",
            FullPath = Path.Combine(root, name),
            RelativePath = name,
            FileName = name,
            SizeBytes = size,
            Kind = MediaKind.Image,
            FormatCode = "JPG",
            FileModified = DateTimeOffset.Now,
            FileCreated = DateTimeOffset.Now,
        };

        var a = Make("ex-a", "SH_0001.jpg", 69421);
        a.Rating = 5;
        a.ColorLabel = "Blue";
        a.IsPick = true;
        a.Country = "中国";
        a.Province = "上海市";
        a.City = "上海";
        a.SetThemes(new[] { "城市夜景", "延时" });
        a.Tone = "冷调";
        a.Keywords = new List<string> { "外滩", "夜景" };
        a.Title = "外滩夜色";
        a.PostProcess = PostProcessState.Processed;

        var b = Make("ex-b", "SH_0002.jpg", 12345);
        b.Rating = 3;

        var c = Make("ex-c", "SH_0003.jpg", 7777);

        await store.UpsertManyAsync(new[] { a, b, c });
        var collection = await store.CreateCollectionAsync("跨平台合集");
        await store.AddToCollectionAsync(collection.Id, new[] { "ex-c" });

        var settings = new AppSettings { ThemeMode = "dark" };
        settings.Grid.Density = "comfortable";

        var svc = new SettingsExchangeService(store);
        var doc = await svc.BuildExportAsync(settings, "1.25.0", AssetSortKeys.CapturedAt, ascending: false);
        Check("交换：导出包含编辑素材与合集",
              doc.Assets.Any(x => x.FileName == "SH_0001.jpg") &&
              doc.Collections.Count == 1 && doc.Collections[0].Assets.Count == 1,
              $"assets={doc.Assets.Count} collections={doc.Collections.Count}");

        var exportedA = doc.Assets.First(x => x.FileName == "SH_0001.jpg");
        Check("交换：字段映射（色标中文/三态/主题/后处理）",
              exportedA.Rating == 5 && exportedA.ColorLabel == "蓝" && exportedA.IsPick == 1 &&
              exportedA.Themes is { Count: 2 } && exportedA.Themes[0] == "城市夜景" &&
              exportedA.PostProcess == 2 && exportedA.Keywords is { Count: 2 },
              $"rating={exportedA.Rating} color={exportedA.ColorLabel} pick={exportedA.IsPick} themes={exportedA.Themes?.Count}");

        Check("交换：设置映射到跨平台枚举",
              doc.Settings.ThemeMode == "dark" && doc.Settings.Density == "medium" &&
              doc.Settings.Sort?.Key == "captured" && doc.App.Platform == "windows",
              $"theme={doc.Settings.ThemeMode} density={doc.Settings.Density} sort={doc.Settings.Sort?.Key}");

        // ---------- 写文件：不含密钥 ----------
        var file = Path.Combine(dir, "exchange.json");
        await svc.ExportAsync(file, settings, "1.25.0");
        var raw = await File.ReadAllTextAsync(file);
        Check("交换：文件不含任何密钥",
              !raw.Contains("apiKey", StringComparison.OrdinalIgnoreCase) &&
              !raw.Contains("password", StringComparison.OrdinalIgnoreCase) &&
              !raw.Contains("DPAPI", StringComparison.OrdinalIgnoreCase) &&
              raw.Contains("stj-media-viewer/exchange"),
              $"文件 {raw.Length} 字节");

        var loaded = await SettingsExchangeService.LoadAsync(file);
        Check("交换：读回文件并通过格式校验",
              loaded.Format == SettingsExchangeService.FormatId && loaded.Assets.Count == doc.Assets.Count,
              $"assets={loaded.Assets.Count}");

        // ---------- 往返（fillEmpty）：全部跳过 ----------
        var plan = await svc.BuildImportPlanAsync(loaded);
        var roundtrip = await svc.ApplyAsync(plan, ExchangeMergeMode.FillEmpty);
        Check("交换：往返导入（fillEmpty）不改动已有值",
              roundtrip.Updated == 0 && roundtrip.Skipped >= 2 && roundtrip.Matched == 3,
              roundtrip.Describe());

        // ---------- 只填空：清空 A 的评分/城市/关键词后再导入 ----------
        await store.ApplyBatchEditAsync(new[] { "ex-a" }, new BatchEditPatch
        {
            Rating = 0,
            City = "",
            ReplaceKeywords = new List<string>(),
            Themes = new List<string>(),
            Title = "",
        });
        var cleared = await store.GetAsync("ex-a");
        var clearOk = cleared is { Rating: 0 } && string.IsNullOrEmpty(cleared.City) &&
                      cleared.Keywords.Count == 0 && cleared.Themes.Count == 0;
        var plan2 = await svc.BuildImportPlanAsync(await SettingsExchangeService.LoadAsync(file));
        var filled = await svc.ApplyAsync(plan2, ExchangeMergeMode.FillEmpty);
        var restored = await store.GetAsync("ex-a");
        Check("交换：fillEmpty 只补空字段",
              clearOk && filled.Updated >= 1 && restored is { Rating: 5 } &&
              restored.City == "上海" && restored.Keywords.Count == 2 && restored.Themes.Count == 2,
              $"更新 {filled.Updated} · rating={restored?.Rating} city={restored?.City} kw={restored?.Keywords.Count}");

        var again = await svc.ApplyAsync(await svc.BuildImportPlanAsync(loaded), ExchangeMergeMode.FillEmpty);
        Check("交换：再次导入不重复写", again.Updated == 0, again.Describe());

        // ---------- fillEmpty vs overwrite ----------
        await store.ApplyBatchEditAsync(new[] { "ex-a" }, new BatchEditPatch { Rating = 1 });
        await svc.ApplyAsync(await svc.BuildImportPlanAsync(loaded), ExchangeMergeMode.FillEmpty);
        var keepLocal = (await store.GetAsync("ex-a"))!.Rating == 1;
        await svc.ApplyAsync(await svc.BuildImportPlanAsync(loaded), ExchangeMergeMode.Overwrite);
        var overwritten = (await store.GetAsync("ex-a"))!.Rating == 5;
        Check("交换：覆盖模式才写文件里的值", keepLocal && overwritten,
              $"fillEmpty 保留={keepLocal} overwrite 写入={overwritten}");

        // ---------- 未匹配：不新建素材 ----------
        var before = await store.CountAllAsync();
        var extra = await SettingsExchangeService.LoadAsync(file);
        extra.Assets.Add(new ExchangeAsset { FileName = "NOT_IN_LIBRARY.jpg", SizeBytes = 1 });
        var plan3 = await svc.BuildImportPlanAsync(extra);
        var missed = await svc.ApplyAsync(plan3, ExchangeMergeMode.FillEmpty);
        var after = await store.CountAllAsync();
        Check("交换：未匹配条目只统计、不新建素材",
              plan3.UnmatchedCount == 1 && missed.Unmatched == 1 && before == after,
              $"未匹配 {plan3.UnmatchedCount} · 素材数 {before}→{after}");

        // ---------- 合集：删除后重建，重复导入不重复 ----------
        var collections = await store.GetCollectionsAsync();
        foreach (var col in collections) await store.DeleteCollectionAsync(col.Id);
        var planCol = await svc.BuildImportPlanAsync(loaded);
        var colSummary = await svc.ApplyAsync(planCol, ExchangeMergeMode.FillEmpty);
        var rebuilt = await store.GetCollectionsAsync();
        var members = rebuilt.Count == 1
            ? await store.QueryAsync(new AssetQuery { Collections = { rebuilt[0].Id }, Limit = 0 })
            : new List<MediaAsset>();
        var colSummary2 = await svc.ApplyAsync(await svc.BuildImportPlanAsync(loaded), ExchangeMergeMode.FillEmpty);
        var members2 = await store.QueryAsync(new AssetQuery { Collections = { rebuilt[0].Id }, Limit = 0 });
        Check("交换：合集按名字重建、成员不重复",
              rebuilt.Count == 1 && members.Count == 1 && colSummary.CreatedCollections == 1 &&
              colSummary.AddedCollectionMembers == 1 && colSummary2.AddedCollectionMembers == 0 &&
              members2.Count == 1,
              $"合集 {rebuilt.Count} · 成员 {members.Count}→{members2.Count}");

        // ---------- 格式 / 版本校验 ----------
        var badFormat = Path.Combine(dir, "bad-format.json");
        await File.WriteAllTextAsync(badFormat, "{\"format\":\"other/app\",\"formatVersion\":1}");
        var badFormatRejected = false;
        try { await SettingsExchangeService.LoadAsync(badFormat); }
        catch (InvalidDataException) { badFormatRejected = true; }

        var badVersion = Path.Combine(dir, "bad-version.json");
        await File.WriteAllTextAsync(badVersion, "{\"format\":\"stj-media-viewer/exchange\",\"formatVersion\":99}");
        var badVersionRejected = false;
        try { await SettingsExchangeService.LoadAsync(badVersion); }
        catch (InvalidDataException ex) when (ex.Message.Contains("更新")) { badVersionRejected = true; }

        var broken = Path.Combine(dir, "broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");
        var brokenRejected = false;
        try { await SettingsExchangeService.LoadAsync(broken); }
        catch (InvalidDataException) { brokenRejected = true; }

        Check("交换：错误格式 / 版本过高 / 文件损坏都明确报错",
              badFormatRejected && badVersionRejected && brokenRejected,
              $"format={badFormatRejected} version={badVersionRejected} broken={brokenRejected}");

        // ---------- 设置导入映射 ----------
        var localSettings = new AppSettings { ThemeMode = "system" };
        localSettings.Grid.Density = "comfortable";
        var macDoc = new ExchangeDocument
        {
            Settings = new ExchangeSettings { ThemeMode = "dark", Density = "large" },
        };
        var applied = SettingsExchangeService.ApplySettings(macDoc, localSettings);
        Check("交换：设置导入（dark + large→spacious）",
              applied.ThemeApplied && localSettings.ThemeMode == "dark" &&
              applied.DensityApplied && localSettings.Grid.Density == "spacious",
              string.Join("；", applied.Notes));

        // ---------- 大库导出/导入性能 ----------
        var bulk = new List<MediaAsset>();
        for (int i = 0; i < 3000; i++)
        {
            var item = Make($"bulk-{i}", $"bulk_{i:D5}.jpg", 1000 + i);
            if (i % 2 == 0) item.Rating = 4;
            if (i % 3 == 0) item.Keywords = new List<string> { "kw" + (i % 10) };
            bulk.Add(item);
        }
        await store.UpsertManyAsync(bulk);
        var perfSw = System.Diagnostics.Stopwatch.StartNew();
        var bigDoc = await svc.BuildExportAsync(settings, "1.25.0");
        var exportMs = perfSw.ElapsedMilliseconds;
        perfSw.Restart();
        var bigPlan = await svc.BuildImportPlanAsync(bigDoc);
        var importMs = perfSw.ElapsedMilliseconds;
        // 只有"有编辑"的条目会进文件：偶数条有评分、3 的倍数有关键词 → 并集约 2000 条
        Check("交换：3000 条规模导出/匹配性能",
              bigDoc.Assets.Count >= 2000 && exportMs < 5000 && importMs < 5000,
              $"导出 {exportMs}ms / 匹配 {importMs}ms / 条目 {bigDoc.Assets.Count}");

        _ = s; // 本组检查用独立临时库，避免污染主库
    }
}