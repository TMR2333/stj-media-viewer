using System.Globalization;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Import;

/// <summary>一个带 GPS 的定位点（GroupKey / 素材 id 之类的外部键 + 经纬度）。</summary>
public sealed record GeoPoint(string Key, double Latitude, double Longitude);

/// <summary>一个地理簇：一个半径范围内的一组定位点。</summary>
public sealed record GeoCluster(
    double Latitude,
    double Longitude,
    int Count,
    string FolderName,
    List<string> Keys);

/// <summary>
/// GPS 聚类：把"同一个范围（默认 500 米）里拍的"照片归成一个文件夹。
///
/// 用途：导卡模板里的 `{geo}`、整理页的「细分位置」分类。
/// 簇名优先用离线城市库反查出的「城市-区县」，查不到就用坐标（保留 3 位小数，约 100 米精度）。
/// </summary>
public static class GeoClusterer
{
    /// <summary>没有 GPS 的文件放的文件夹名。</summary>
    public const string UnknownLocationFolder = "未定位";

    /// <summary>地球半径（米）。</summary>
    private const double EarthRadiusMeters = 6_371_000d;

    /// <summary>
    /// 两点球面距离（米）。
    /// </summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// 把定位点聚成簇（贪心：先按坐标排序保证稳定，再就近分配 / 新开簇）。
    /// </summary>
    /// <param name="points">所有带 GPS 的点。</param>
    /// <param name="radiusMeters">一个"范围"的半径，米（&lt;= 0 时按 500 米）。</param>
    /// <param name="placeResolver">坐标 → 地点名（通常是「城市-区县」）；返回 null 时用坐标兜底。</param>
    public static IReadOnlyList<GeoCluster> Cluster(
        IEnumerable<GeoPoint> points,
        double radiusMeters = 500,
        Func<double, double, string?>? placeResolver = null)
    {
        var radius = radiusMeters > 0 ? radiusMeters : 500;
        var ordered = points
            .Where(p => !double.IsNaN(p.Latitude) && !double.IsNaN(p.Longitude) &&
                        p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180)
            .OrderBy(p => p.Latitude)
            .ThenBy(p => p.Longitude)
            .ToList();

        var clusters = new List<MutableCluster>();
        foreach (var point in ordered)
        {
            MutableCluster? best = null;
            var bestDistance = double.MaxValue;
            foreach (var cluster in clusters)
            {
                var distance = DistanceMeters(cluster.CentroidLat, cluster.CentroidLon, point.Latitude, point.Longitude);
                if (distance <= radius && distance < bestDistance)
                {
                    best = cluster;
                    bestDistance = distance;
                }
            }

            if (best is null)
            {
                best = new MutableCluster();
                clusters.Add(best);
            }
            best.Add(point);
        }

        // 起名：同名的簇追加 -2 / -3，避免两个"上海"合成一个目录
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GeoCluster>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var baseName = ResolveName(cluster.CentroidLat, cluster.CentroidLon, placeResolver);
            var name = baseName;
            if (usedNames.TryGetValue(baseName, out var seen))
            {
                seen++;
                usedNames[baseName] = seen;
                name = $"{baseName}-{seen}";
            }
            else
            {
                usedNames[baseName] = 1;
            }

            result.Add(new GeoCluster(cluster.CentroidLat, cluster.CentroidLon, cluster.Count,
                Sanitize(name), cluster.Keys.ToList()));
        }

        if (result.Count > 0)
            Log.Info($"[geo] {ordered.Count} 个定位点 → {result.Count} 个片区（半径 {radius:0} 米）：" +
                     string.Join("、", result.Take(8).Select(c => $"{c.FolderName}({c.Count})")) +
                     (result.Count > 8 ? " …" : ""));
        return result;
    }

    /// <summary>按外部键查簇名；没有 GPS 的键不在结果里（调用方用 <see cref="UnknownLocationFolder"/>）。</summary>
    public static Dictionary<string, string> FolderNames(
        IEnumerable<GeoPoint> points,
        double radiusMeters = 500,
        Func<double, double, string?>? placeResolver = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cluster in Cluster(points, radiusMeters, placeResolver))
        {
            foreach (var key in cluster.Keys) map[key] = cluster.FolderName;
        }
        return map;
    }

    private static string ResolveName(double lat, double lon, Func<double, double, string?>? placeResolver)
    {
        try
        {
            var place = placeResolver?.Invoke(lat, lon);
            if (!string.IsNullOrWhiteSpace(place)) return place!.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn("GPS 簇命名失败，改用坐标", ex);
        }

        // 坐标兜底：保留 3 位小数（约 100 米），取绝对值避免负号影响排序观感
        var latText = Math.Abs(lat).ToString("0.000", CultureInfo.InvariantCulture);
        var lonText = Math.Abs(lon).ToString("0.000", CultureInfo.InvariantCulture);
        return $"{latText}_{lonText}";
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return UnknownLocationFolder;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(name) ? UnknownLocationFolder : name;
    }

    private sealed class MutableCluster
    {
        private double _sumLat;
        private double _sumLon;

        public int Count { get; private set; }
        public double CentroidLat => Count == 0 ? 0 : _sumLat / Count;
        public double CentroidLon => Count == 0 ? 0 : _sumLon / Count;
        public List<string> Keys { get; } = new();

        public void Add(GeoPoint point)
        {
            _sumLat += point.Latitude;
            _sumLon += point.Longitude;
            Count++;
            Keys.Add(point.Key);
        }
    }
}