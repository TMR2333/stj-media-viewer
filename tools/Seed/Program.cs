using System.Diagnostics;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Ai;
using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Classification;
using StjMediaViewer.Core.Geo;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Scanning;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Seed;

/// <summary>
/// 把测试素材库扫描进指定数据目录（默认 = App 的 %LOCALAPPDATA%\StjMediaViewer）。
/// 带逐文件计时与卡死定位：先打印候选清单，解析时每个文件打 START/DONE，超时可看出卡在哪个文件。
/// 用法：StjMediaViewer.Seed.exe [--library 路径] [--data 数据目录] [--parallel 4] [--verbose]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var repo = FindRepoRoot();
        var library = Arg(args, "--library") ?? Path.Combine(repo, "tools", "SampleLibrary", "out", "library");
        var data = Arg(args, "--data") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewer");
        var parallel = int.TryParse(Arg(args, "--parallel"), out var p) ? p : 4;
        var verbose = args.Contains("--verbose");

        Directory.CreateDirectory(data);
        Log.Init(data);
        Console.WriteLine($"library = {library}");
        Console.WriteLine($"data    = {data}   parallelism={parallel}");

        var settings = new JsonSettingsService(Path.Combine(data, "settings.json"), data);
        await settings.LoadAsync();
        settings.Current.ScanParallelism = parallel;

        var metadata = new MetadataService();
        var classifier = new RuleBasedClassifier();
        var geocoder = new OfflineGeocoder();
        var ffmpeg = new FfmpegService(settings);
        var encoder = new ImageEncoder();
        var decoder = new MediaDecoder(metadata, ffmpeg);
        var store = new SqliteAssetStore(Path.Combine(data, "catalog.db"));
        await store.InitializeAsync();
        var scanner = new LibraryScanner(metadata, classifier, geocoder, settings);
        var lib = new LibraryService(store, scanner, settings);
        await geocoder.EnsureLoadedAsync();

        var root = await lib.AddLibraryAsync(library);
        Console.WriteLine($"library id = {root.Id}");

        // 走完整的 LibraryService 路径：扫描 → 合并历史分类 → 入库 → 清理丢失文件
        var sw = Stopwatch.StartNew();
        var options = new ScanOptions { Parallelism = parallel, ComputeQuickHash = true };
        Console.WriteLine("--- 开始扫描（LibraryService.ScanLibraryAsync） ---");

        var progress = new Progress<ScanProgress>(x =>
        {
            if (x.Phase is ScanPhase.Preparing or ScanPhase.Enumerating or ScanPhase.ReadingMetadata or ScanPhase.Completed or ScanPhase.Indexing)
                Console.WriteLine($"  [{x.Phase}] processed={x.Processed}/{x.Total} failed={x.Failed} :: {Path.GetFileName(x.CurrentPath ?? "")}  ({sw.Elapsed.TotalSeconds:0.0}s)");
        });

        var scanResult = await lib.ScanLibraryAsync(root, options, progress, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"--- 扫描完成：{scanResult.Phase} 新增 {scanResult.Added} 更新 {scanResult.Updated} 移除 {scanResult.Removed}，用时 {sw.Elapsed.TotalSeconds:0.0}s ---");
        var assets = await store.QueryAsync(new AssetQuery { LibraryId = root.Id, Limit = 100000 });

        var aiUrl = Arg(args, "--ai-url");
        if (!string.IsNullOrEmpty(aiUrl))
        {
            Console.WriteLine($"--- AI 批量分类（真实 HTTP → {aiUrl}）---");
            settings.Current.Ai.BaseUrl = aiUrl;
            settings.Current.Ai.VisionModel = Arg(args, "--ai-model") ?? "mock-vision-1";
            settings.Current.Ai.DryRun = false;
            settings.Current.Ai.Concurrency = 3;
            settings.Current.Ai.UseJsonSchema = true;
            settings.Current.Ai.ProtectedApiKey = settings.ProtectSecret("sk-mock-demo-key");
            var aiClient = new OpenAiCompatibleClient(settings);
            var ai = new AiClassificationService(aiClient, decoder, encoder, settings);
            var pool = await store.QueryAsync(new AssetQuery { Limit = 100000 });
            var aiProgress = new Progress<AiBatchProgress>(p =>
            {
                if (p.Done || p.Processed % 10 == 0)
                    Console.WriteLine($"   {p.Processed}/{p.Total} 成功={p.Succeeded} 失败={p.Failed} 跳过={p.Skipped} :: {Path.GetFileName(p.CurrentFile ?? "")}");
            });
            var aiResult = await ai.RunBatchAsync(pool,
                new AiBatchOptions { MaxItems = 1000, OnlyUnclassified = false, OverwriteManual = false }, aiProgress);
            if (aiResult.Updated.Count > 0) await store.UpsertManyAsync(aiResult.Updated);
            Console.WriteLine($"   AI 分类写回 {aiResult.Updated.Count} 条");
            var after = await store.QueryAsync(new AssetQuery { Limit = 100000 });
            Console.WriteLine("   主题分布: " + string.Join(", ", after.Where(a => !string.IsNullOrEmpty(a.Theme))
                .GroupBy(a => a.Theme!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
            Console.WriteLine("   影调分布: " + string.Join(", ", after.Where(a => !string.IsNullOrEmpty(a.Tone))
                .GroupBy(a => a.Tone!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
        }

        if (args.Contains("--rescan"))
        {
            Console.WriteLine("--- 再走一遍扫描，验证重扫不会抹掉分类 ---");
            var sw2 = Stopwatch.StartNew();
            var libProgress = new Progress<ScanProgress>(x =>
                Console.WriteLine($"  [lib] {x.Phase} {x.Processed}/{x.Total} {Path.GetFileName(x.CurrentPath ?? "")} ({sw2.Elapsed.TotalSeconds:0.0}s)"));
            var r = await lib.ScanLibraryAsync(root, options, libProgress, CancellationToken.None);
            sw2.Stop();
            Console.WriteLine($"--- ScanLibraryAsync 完成：{r.Phase} 新增 {r.Added} 更新 {r.Updated} 移除 {r.Removed} 用时 {sw2.Elapsed.TotalSeconds:0.0}s ---");
        }

        var all = await store.QueryAsync(new AssetQuery { Limit = 100000 });
        Console.WriteLine($"库内总数：{all.Count}");
        foreach (var g in all.GroupBy(a => a.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key}: {g.Count()}");
        Console.WriteLine($"  有 GPS: {all.Count(a => a.Latitude.HasValue)}  有城市: {all.Count(a => !string.IsNullOrEmpty(a.City))}  RAW: {all.Count(a => a.IsRaw)}  Log: {all.Count(a => a.IsLog == true)}  已后期: {all.Count(a => a.PostProcess == PostProcessState.Processed)}  有主题: {all.Count(a => !string.IsNullOrEmpty(a.Theme))}");

        foreach (var a in assets.OrderBy(a => a.FullPath).Take(200))
        {
            if (!string.IsNullOrEmpty(a.LastError))
                Console.WriteLine($"  ! {a.FileName}: {a.LastError}");
        }
        return 0;
    }

    private static string? Arg(string[] args, string key)
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