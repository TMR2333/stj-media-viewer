using Microsoft.Data.Sqlite;
using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Write;

// 从旧索引库 / 目录结构恢复"拍摄地点"，写回当前索引并生成 XMP 边车。
//
// 用法：
//   GeoRestore --data <应用数据目录> [--backup <旧catalog.db>] [--library <图片根目录>]
//              [--dry-run] [--overwrite] [--no-xmp]
//
// 默认：只填空值；旧库（catalog.db.bak-*）优先，没匹配到再按目录名（根目录下第一层文件夹）推断。

var opts = ParseArgs(args);
var dataDir = opts.GetValueOrDefault("data")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewer");
var dbPath = Path.Combine(dataDir, "catalog.db");
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"找不到索引库：{dbPath}");
    return 2;
}

var libraryPrefix = opts.GetValueOrDefault("library") ?? @"G:\媒体\图片";
var dryRun = opts.ContainsKey("dry-run");
var overwrite = opts.ContainsKey("overwrite");
var writeXmp = !opts.ContainsKey("no-xmp");
var backupPath = opts.GetValueOrDefault("backup") ?? FindLatestBackup(dataDir);

var cultureText = new[] { "国庆", "未标注地点", "未分类", "学校", "素材", "视频", "照片", "图片", "其他", "其它" };

static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

Console.WriteLine($"索引库：{dbPath}");
Console.WriteLine($"图片根目录：{libraryPrefix}");
Console.WriteLine($"旧库备份：{backupPath ?? "（没有，仅按目录名推断）"}");
Console.WriteLine(dryRun ? "模式：预览（不写库）" : "模式：写入索引 + " + (writeXmp ? "XMP 边车" : "不写 XMP"));

// 1) 旧库：路径 → 地点
var backup = new Dictionary<string, (string? City, string? Province, string? Country)>(StringComparer.OrdinalIgnoreCase);
if (backupPath is not null)
{
    using var conn = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "select full_path, city, province, country from assets where city is not null and city <> ''";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var path = reader.GetString(0);
        if (string.IsNullOrWhiteSpace(path)) continue;
        var city = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(city)) continue;
        backup[Norm(path)] = (city,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
    Console.WriteLine($"旧库带地点的素材：{backup.Count}");
}

// 2) 当前库
using var store = new SqliteAssetStore(dbPath);
await store.InitializeAsync();
var all = await store.QueryAsync(new AssetQuery { Limit = 200000, SortBy = AssetSortKeys.FileName });
var prefix = libraryPrefix.Replace('/', '\\').TrimEnd('\\');
var underPrefix = all.Where(a => a.FullPath.Replace('/', '\\').StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
var candidates = all.ToList(); // 旧库匹配对所有路径生效；目录推断只对图片根目录生效
Console.WriteLine($"当前索引素材：{candidates.Count}（其中位于 {libraryPrefix}：{underPrefix.Count}）");

var changed = new List<MediaAsset>();
var byCity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var fromBackup = 0;
var fromFolder = 0;

foreach (var asset in candidates)
{
    var hasCity = !string.IsNullOrWhiteSpace(asset.City);
    if (hasCity && !overwrite) continue;

    string? city = null, province = null, country = null;
    if (backup.TryGetValue(Norm(asset.FullPath), out var old) && !string.IsNullOrWhiteSpace(old.City))
    {
        city = old.City; province = old.Province; country = old.Country;
        fromBackup++;
    }
    else
    {
        var normalized = asset.FullPath.Replace('/', '\\');
        if (!normalized.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) continue;
        var rel = normalized[(prefix.Length + 1)..];
        var first = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null && !cultureText.Contains(first) && !LooksLikeDate(first))
        {
            city = first;
            fromFolder++;
        }
    }

    if (string.IsNullOrWhiteSpace(city)) continue;
    asset.City = city;
    if (!string.IsNullOrWhiteSpace(province) && string.IsNullOrWhiteSpace(asset.Province)) asset.Province = province;
    if (!string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(asset.Country)) asset.Country = country;
    asset.CitySource = "restore";
    changed.Add(asset);
    byCity[city] = byCity.GetValueOrDefault(city) + 1;
}

Console.WriteLine($"可恢复：{changed.Count} 张（旧库匹配 {fromBackup}，目录推断 {fromFolder}）");
foreach (var (city, count) in byCity.OrderByDescending(kv => kv.Value).Take(30))
    Console.WriteLine($"  {city}：{count}");

if (dryRun || changed.Count == 0)
{
    Console.WriteLine(dryRun ? "预览结束（未写入）。" : "没有需要恢复的素材。");
    return 0;
}

// 3) 写索引
await store.UpsertManyAsync(changed);
Console.WriteLine($"已写回索引：{changed.Count} 条");

// 4) 写 XMP 边车（这样下次扫描/重装也不会丢）
if (writeXmp)
{
    var writer = new XmpWriter();
    var ok = 0;
    var failed = 0;
    foreach (var asset in changed)
    {
        try
        {
            if (await writer.WriteSidecarAsync(asset)) ok++;
            else failed++;
        }
        catch { failed++; }
    }
    Console.WriteLine($"XMP 边车：成功 {ok}，失败 {failed}");
}

Console.WriteLine("完成。回到应用点「刷新」或重启即可看到地点。");
return 0;

static bool LooksLikeDate(string s) =>
    System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}([-_.]\d{1,2}){1,2}");

static string? FindLatestBackup(string dataDir)
{
    try
    {
        return Directory.EnumerateFiles(dataDir, "catalog.db.bak-*")
            .Where(f => !f.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            .Where(f => new FileInfo(f).Length > 4096)
            .Where(f =>
            {
                using var fs = File.OpenRead(f);
                Span<byte> head = stackalloc byte[16];
                var read = fs.Read(head);
                return read >= 15 && System.Text.Encoding.ASCII.GetString(head[..15]) == "SQLite format 3";
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    catch { return null; }
}

static Dictionary<string, string?> ParseArgs(string[] args)
{
    var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (!a.StartsWith("--")) continue;
        var key = a[2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) dict[key] = args[++i];
        else dict[key] = null;
    }
    return dict;
}