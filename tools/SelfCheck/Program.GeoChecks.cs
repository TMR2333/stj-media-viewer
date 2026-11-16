using StjMediaViewer.Core.Import;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Write;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 「按细分位置」：GPS 半径内聚成一个片区（一个文件夹），导卡模板 {geo} 与整理页维度共用同一套聚类。
    /// </summary>
    private static async Task CheckGeoClustering(Services s, string tempData)
    {
        // 1) 距离：上海人民广场附近两点 ~ 200 米
        var d = GeoClusterer.DistanceMeters(31.2304, 121.4737, 31.2322, 121.4737);
        Check("GPS：距离计算", d > 150 && d < 260, $"{d:0} 米");

        // 2) 500 米半径：两个近点 + 一个远点 → 2 个片区
        var points = new List<GeoPoint>
        {
            new("a", 31.2304, 121.4737),
            new("b", 31.2322, 121.4737),      // ~200 米
            new("c", 31.3000, 121.4737),      // ~7.7 公里
        };
        var clusters500 = GeoClusterer.Cluster(points, 500);
        Check("GPS：半径内合并", clusters500.Count == 2, $"{clusters500.Count} 个片区");
        Check("GPS：近点同片区",
              clusters500.Any(c => c.Keys.Contains("a") && c.Keys.Contains("b")),
              string.Join("|", clusters500.Select(c => string.Join(",", c.Keys))));
        Check("GPS：远点单独片区",
              clusters500.Any(c => c.Keys.Count == 1 && c.Keys[0] == "c"));

        // 3) 半径放大到 10 公里 → 合成一个片区
        var clusters10k = GeoClusterer.Cluster(points, 10_000);
        Check("GPS：改半径后合并", clusters10k.Count == 1, $"{clusters10k.Count} 个片区");

        // 4) 地点名解析：同一城市同区 → 同名去重带 -2
        var named = GeoClusterer.Cluster(points, 500, (_, _) => "上海-黄浦区");
        var names = named.Select(c => c.FolderName).ToList();
        Check("GPS：同名片区自动加序号",
              names.Count == 2 && names.Distinct().Count() == 2 && names.Any(n => n.Contains("-2", StringComparison.Ordinal)),
              string.Join("/", names));

        // 5) 坐标兜底命名（没有地点解析）
        var coordinateNamed = GeoClusterer.Cluster(points, 500);
        Check("GPS：坐标兜底命名",
              coordinateNamed.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.FolderName, @"^\d+\.\d{3}_\d+\.\d{3}$")),
              string.Join("/", coordinateNamed.Select(c => c.FolderName)));

        // 6) 导卡模板 {geo}
        var group = new ImportGroup { GroupKey = "DSC0001", JpegPath = "DSC0001.JPG" };
        var folder = ImportService.ExpandFolder("{geo}", group, "上海-黄浦区");
        Check("导卡：{geo} 展开为片区目录", folder == Path.Combine("上海-黄浦区"), folder);
        var combined = ImportService.ExpandFolder("{yyyy}/{geo}", group, "杭州-西湖区");
        Check("导卡：{yyyy}/{geo} 组合模板", combined.EndsWith(Path.Combine("杭州-西湖区")), combined);
        var unknown = ImportService.ExpandFolder("{geo}", group, null);
        Check("导卡：无 GPS 归「未定位」", unknown == GeoClusterer.UnknownLocationFolder, unknown);

        // 7) 整理页「细分位置」维度
        var lib = Path.Combine(tempData, "geo-organize");
        Directory.CreateDirectory(lib);
        MediaAsset Make(string id, string name, double? lat, double? lon)
        {
            var path = Path.Combine(lib, name);
            File.WriteAllBytes(path, new byte[64]);
            return new MediaAsset
            {
                Id = id,
                FileName = name,
                FullPath = path,
                LibraryId = "geo-lib",
                Latitude = lat,
                Longitude = lon,
            };
        }

        var assets = new List<MediaAsset>
        {
            Make("g1", "g1.jpg", 31.2304, 121.4737),
            Make("g2", "g2.jpg", 31.2322, 121.4737),
            Make("g3", "g3.jpg", 31.3000, 121.4737),
            Make("g4", "g4.jpg", null, null),
        };
        var roots = new Dictionary<string, string>(StringComparer.Ordinal) { ["geo-lib"] = lib };
        var organizer = new OrganizerService(tempData);
        var plan = organizer.BuildLocalPlan(assets, roots, new OrganizeOptions
        {
            Primary = OrganizeDimension.GeoArea,
            GeoRadiusMeters = 500,
        });

        var g1 = plan.Entries.First(e => e.AssetId == "g1");
        var g2 = plan.Entries.First(e => e.AssetId == "g2");
        var g3 = plan.Entries.First(e => e.AssetId == "g3");
        var g4 = plan.Entries.First(e => e.AssetId == "g4");
        Check("整理：范围内同目录", g1.CategoryPath == g2.CategoryPath, $"{g1.CategoryPath} / {g2.CategoryPath}");
        Check("整理：范围外不同目录", g1.CategoryPath != g3.CategoryPath, $"{g1.CategoryPath} / {g3.CategoryPath}");
        Check("整理：无 GPS 归「未定位」", g4.CategoryPath == GeoClusterer.UnknownLocationFolder, g4.CategoryPath);

        _ = s;
    }
}