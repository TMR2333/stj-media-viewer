using Microsoft.Data.Sqlite;

namespace StjMediaViewer.MediaSmoke;

/// <summary>临时诊断：打印 timeline / facets 的 SQLite 查询计划与耗时。</summary>
internal static class PerfProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var dbPath = args.Length > 0 ? args[0] : Path.Combine(Program.WorkRoot, "catalog-perf.db");
        Console.WriteLine($"[plan] db={dbPath}");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var queries = new (string Name, string Sql)[]
        {
            ("timeline", "SELECT captured_day, COUNT(*), SUM(CASE WHEN kind=1 THEN 1 ELSE 0 END), " +
                         "SUM(CASE WHEN kind=2 THEN 1 ELSE 0 END), SUM(CASE WHEN kind=3 THEN 1 ELSE 0 END) " +
                         "FROM assets WHERE captured_day IS NOT NULL GROUP BY captured_day ORDER BY captured_day DESC"),
            ("timeline-noorder", "SELECT captured_day, COUNT(*) FROM assets WHERE captured_day IS NOT NULL GROUP BY captured_day"),
            ("kind", "SELECT kind, COUNT(*) FROM assets GROUP BY kind"),
            ("captured-day-distinct", "SELECT COUNT(DISTINCT captured_day) FROM assets"),
        };

        foreach (var (name, sql) in queries)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                using var r = cmd.ExecuteReader();
                var plan = new List<string>();
                while (r.Read()) plan.Add(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
                Console.WriteLine($"[plan] {name}:");
                foreach (var p in plan) Console.WriteLine($"    {p}");
            }

            // warmup + measure
            for (int i = 0; i < 2; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var r = cmd.ExecuteReader();
                int rows = 0;
                while (r.Read()) rows++;
                sw.Stop();
                Console.WriteLine($"[plan] {name} run{i}: rows={rows} {sw.Elapsed.TotalMilliseconds:0.00}ms");
            }
        }
        conn.Close();

        // 通过 SqliteAssetStore 实测（含 Task.Run / 信号量 / 映射开销）
        using var store = new StjMediaViewer.Core.Catalog.SqliteAssetStore(dbPath);
        await store.InitializeAsync();

        // A) 显式预热一次，再测
        var warm = await store.GetTimelineAsync(new StjMediaViewer.Core.Models.AssetQuery());
        Console.WriteLine($"[store] timeline warmup buckets={warm.Count}");
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tl = await store.GetTimelineAsync(new StjMediaViewer.Core.Models.AssetQuery());
            sw.Stop();
            Console.WriteLine($"[store] timeline(A warm) run{i}: buckets={tl.Count} {sw.Elapsed.TotalMilliseconds:0.00}ms");
        }
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var page = await store.QueryAsync(new StjMediaViewer.Core.Models.AssetQuery { Limit = 200 });
            sw.Stop();
            Console.WriteLine($"[store] query200(A warm) run{i}: rows={page.Count} {sw.Elapsed.TotalMilliseconds:0.00}ms");
        }
        return 0;
    }
}