using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Exchange;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.ExchangeCli;

/// <summary>
/// 跨平台交换文件的命令行入口（CI / 两端联调用，与 macOS 版 stjcheck 的 --export-exchange / --import-exchange 对应）。
///
/// 用法：
///   StjMediaViewer.ExchangeCli --data &lt;数据目录&gt; --export &lt;file.json&gt; [--sort captured] [--ascending]
///   StjMediaViewer.ExchangeCli --data &lt;数据目录&gt; --import &lt;file.json&gt; [--merge fill|overwrite]
/// 数据目录里应有 catalog.db（和可选的 settings.json）。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var dataDir = Arg(args, "--data") ?? DefaultDataDir();
        var exportPath = Arg(args, "--export");
        var importPath = Arg(args, "--import");
        var mergeText = (Arg(args, "--merge") ?? "fill").Trim().ToLowerInvariant();
        var merge = mergeText is "overwrite" or "overwrite-existing" ? ExchangeMergeMode.Overwrite : ExchangeMergeMode.FillEmpty;

        if (exportPath is null && importPath is null)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        var dbPath = Path.Combine(Path.GetFullPath(dataDir), "catalog.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"找不到 catalog.db：{dbPath}");
            return 3;
        }

        Log.Init(dataDir);
        using var store = new SqliteAssetStore(dbPath);
        await store.InitializeAsync();

        var dataRoot = Path.GetFullPath(dataDir);
        var settings = new JsonSettingsService(Path.Combine(dataRoot, "settings.json"), dataRoot);
        await settings.LoadAsync();
        var service = new SettingsExchangeService(store);

        try
        {
            if (exportPath is not null)
            {
                var sortKey = Arg(args, "--sort") ?? AssetSortKeysCaptured;
                var doc = await service.ExportAsync(
                    Path.GetFullPath(exportPath), settings.Current, Version,
                    sortKey, args.Contains("--ascending"));
                Console.WriteLine($"已导出 {doc.Assets.Count} 条素材编辑、{doc.Collections.Count} 个合集 → {Path.GetFullPath(exportPath)}");
                Console.WriteLine($"文件大小：{new FileInfo(exportPath).Length} 字节（不含任何密钥）");
                return 0;
            }

            var docIn = await SettingsExchangeService.LoadAsync(Path.GetFullPath(importPath!));
            var plan = await service.BuildImportPlanAsync(docIn);
            Console.WriteLine($"来源：{docIn.App.Version} · {docIn.App.Platform} · {docIn.App.ExportedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            Console.WriteLine($"匹配 {plan.MatchedCount} 条 / 未匹配 {plan.UnmatchedCount} 条" +
                              (plan.Ambiguous.Count > 0 ? $" / 歧义 {plan.Ambiguous.Count} 条" : "") +
                              (plan.HasCollections ? $" / 合集 {plan.Collections.Count} 个" : "") +
                              (plan.HasSettings ? " / 含设置" : ""));
            if (plan.MatchedCount == 0)
            {
                Console.Error.WriteLine("没有匹配的素材，未写入任何内容。");
                return 4;
            }

            var summary = await service.ApplyAsync(plan, merge);
            var settingsResult = SettingsExchangeService.ApplySettings(docIn, settings.Current);
            try { await settings.SaveAsync(); }
            catch (Exception ex) { Console.Error.WriteLine("提示：设置已应用，但写回 settings.json 失败（" + ex.Message + "）"); }
            Console.WriteLine(summary.Describe());
            if (settingsResult.Notes.Count > 0) Console.WriteLine("设置：" + string.Join("、", settingsResult.Notes));
            foreach (var message in summary.Messages.Take(5)) Console.WriteLine("  · " + message);
            return summary.Success ? 0 : 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine("交换文件无效：" + ex.Message);
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            Log.Error("[exchange-cli] 失败", ex);
            return 1;
        }
    }

    private const string AssetSortKeysCaptured = "capturedAt";
    private const string Version = "1.28.1";

    private static string DefaultDataDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewer");

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private const string Usage = """
        STJ Media Viewer · 设置与图片数据交换（stj-media-viewer/exchange v1）

          --data <目录>          数据目录（含 catalog.db，默认 %LOCALAPPDATA%\StjMediaViewer）
          --export <文件.json>   导出设置 + 图片编辑成果 + 合集
          --import <文件.json>   导入（按「文件名 + 大小」匹配当前库，不新建素材）
          --merge fill|overwrite 合并策略（默认 fill = 只填空字段）
          --sort <键>            导出时的排序键（capturedAt/fileName/fileModified/size/rating/indexedAt）
          --ascending            导出时的排序方向

        示例：
          StjMediaViewer.ExchangeCli --data D:\AppData --export out.json
          StjMediaViewer.ExchangeCli --data D:\AppData --import mac.json --merge overwrite
        """;
}