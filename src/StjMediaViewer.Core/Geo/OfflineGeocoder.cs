using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Geo;

/// <summary>
/// 离线反向地理编码：嵌入 GeoNames cities15000 的紧凑数据（见 <c>Geo/Data/cities.g.cs</c>，
/// 由 <c>Geo/Data/tools/gen_cities.py</c> 生成），内存 0.25° 网格索引 + Haversine 最近邻。
/// <para>城市名统一为简体中文（東京→东京、紐約→纽约）；<see cref="GeoPlace.Country"/> 返回中文国家名
///（"中国"/"日本"/"冰岛"…，见 <c>PlaceNames.g.cs</c>），中国城市的 Province 返回中文省名。</para>
/// <para>默认完全离线，不联网；<see cref="ReverseOnlineAsync"/> 是可选的 Nominatim 精修，
/// 失败一律返回 null（不影响离线结果）。</para>
/// </summary>
public sealed class OfflineGeocoder : IGeocoder
{
    private const double CellSizeDegrees = 0.25;
    private const double EarthRadiusKm = 6371.0088;
    private const double KmPerDegreeLat = 111.32;
    private const double DegToRad = Math.PI / 180.0;
    private const int LatCellCount = (int)(180.0 / CellSizeDegrees);   // 720
    private const int LonCellCount = (int)(360.0 / CellSizeDegrees);   // 1440
    private const int MaxSearchRing = 24;                              // 最多搜 6°
    private const double CellDiagonalKm = KmPerDegreeLat * CellSizeDegrees * 1.41421356;
    private const double MaxPreferredRadiusKm = 8.0;                   // 候选可比最近点远 8km（Lead 口径）
    private const int MaxProminenceCandidates = 8;                     // 参与"显著性"比较的最近 K 个候选
    private const int MaxCandidateSearchRing = 32;
    private const string NominatimEndpoint = "https://nominatim.openstreetmap.org/reverse";

    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private volatile CityEntry[] _cities = Array.Empty<CityEntry>();
    private volatile Dictionary<long, int[]> _grid = new();
    private volatile bool _loaded;
    private volatile bool _loadFailed;
    private volatile string? _loadError;

    /// <param name="httpClient">可注入 HttpClient（代理/测试）；离线路径完全不需要它。</param>
    public OfflineGeocoder(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <summary>城市库是否已加载完成。</summary>
    public bool IsReady => _loaded;

    /// <summary>已加载的城市数量（未加载为 0）。</summary>
    public int CityCount => _cities.Length;

    /// <summary>加载失败原因（成功时为 null）。</summary>
    public string? LoadError => _loadError;

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return client;
    }

    // ============================ 加载 ============================

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded || _loadFailed) return;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded || _loadFailed) return;
            await Task.Run(LoadFromEmbedded, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _loadError = ex.Message;
            Log.Error("离线城市库加载失败，地理编码降级为不可用", ex);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void LoadFromEmbedded()
    {
        var stopwatch = Stopwatch.StartNew();
        var base64 = string.Concat(CitiesGeoData.GzipBase64Chunks);
        var compressed = Convert.FromBase64String(base64);
        var list = new List<CityEntry>(CitiesGeoData.CityCount);

        using (var stream = new MemoryStream(compressed, writable: false))
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var fields = line.Split('\t');
                if (fields.Length < 8) continue;
                if (!double.TryParse(fields[0], NumberStyles.Float, Inv, out var lat)) continue;
                if (!double.TryParse(fields[1], NumberStyles.Float, Inv, out var lon)) continue;

                var name = fields[2];
                var ascii = fields[3].Length == 0 ? name : fields[3];
                if (string.IsNullOrWhiteSpace(name)) name = ascii;
                if (string.IsNullOrWhiteSpace(name)) continue;

                int.TryParse(fields[6], NumberStyles.Integer, Inv, out var population);
                var cjk = fields[7].Length == 0 ? null : fields[7];
                var prominence = 3;
                if (fields.Length > 8 && byte.TryParse(fields[8], NumberStyles.Integer, Inv, out var rank))
                    prominence = rank;

                list.Add(new CityEntry(
                    (float)lat,
                    (float)lon,
                    name,
                    ascii,
                    fields[4],
                    fields[5],
                    population,
                    cjk,
                    (byte)Math.Clamp(prominence, 0, 9)));
            }
        }

        if (list.Count == 0) throw new InvalidDataException("嵌入城市数据解析结果为空");

        var cities = list.ToArray();
        var grid = BuildGrid(cities);

        _cities = cities;
        _grid = grid;
        _loaded = true;   // volatile：确保上面两个数组对读者可见
        Log.Info($"离线城市库已加载：{cities.Length} 个城市 / {grid.Count} 个网格单元，耗时 {stopwatch.ElapsedMilliseconds} ms（{CitiesGeoData.Source}）");
    }

    private static Dictionary<long, int[]> BuildGrid(CityEntry[] cities)
    {
        var buckets = new Dictionary<long, List<int>>(cities.Length / 2);
        for (var i = 0; i < cities.Length; i++)
        {
            var key = CellKeyOf(cities[i].Lat, cities[i].Lon);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>(4);
                buckets[key] = bucket;
            }
            bucket.Add(i);
        }

        var grid = new Dictionary<long, int[]>(buckets.Count);
        foreach (var (key, bucket) in buckets) grid[key] = bucket.ToArray();
        return grid;
    }

    private static long CellKeyOf(double lat, double lon)
    {
        var latCell = Math.Clamp((int)Math.Floor((lat + 90.0) / CellSizeDegrees), 0, LatCellCount - 1);
        var lonCell = NormalizeLonCell((int)Math.Floor((lon + 180.0) / CellSizeDegrees));
        return ((long)latCell << 32) | (uint)lonCell;
    }

    private static int NormalizeLonCell(int lonCell)
    {
        var value = lonCell % LonCellCount;
        return value < 0 ? value + LonCellCount : value;
    }

    // ============================ 离线反查 ============================

    public GeoPlace? Reverse(double latitude, double longitude)
    {
        try
        {
            if (double.IsNaN(latitude) || double.IsNaN(longitude) ||
                double.IsInfinity(latitude) || double.IsInfinity(longitude))
                return null;
            if (latitude is < -90.0 or > 90.0) return null;
            if (longitude is < -180.0 or > 180.0) return null;

            // 同步 API 首次调用时懒加载（~100ms，一般 SelfCheck/扫描流程会先 EnsureLoadedAsync）。
            if (!_loaded && !_loadFailed) EnsureLoadedAsync().GetAwaiter().GetResult();
            if (!_loaded || _cities.Length == 0) return null;

            var (index, distanceKm) = FindNearest(latitude, longitude);
            if (index < 0) return null;
            (index, distanceKm) = PreferProminent(latitude, longitude, latitude * DegToRad, index, distanceKm);
            if (index < 0) return null;

            var city = _cities[index];
            var cityName = !string.IsNullOrWhiteSpace(city.CjkName) ? city.CjkName : city.Name;
            if (string.IsNullOrWhiteSpace(cityName)) cityName = city.AsciiName;

            // 取值口径（Lead 约定）：Country 返回中文国家名（"中国"/"日本"/"冰岛"），
            // Province 对中国返回中文省名、其他国家/地区暂保留 admin1 代码。
            var countryIso = NullIfEmpty(city.Country);
            var country = PlaceNames.CountryChinese(countryIso) ?? countryIso;
            var province = PlaceNames.Admin1Chinese(countryIso, city.Admin1) ?? NullIfEmpty(city.Admin1);

            return new GeoPlace(
                country,
                province,
                NullIfEmpty(cityName),
                null,
                Math.Round(distanceKm, 2),
                "offline");
        }
        catch (Exception ex)
        {
            Log.Warn($"离线地理编码失败 ({latitude}, {longitude})", ex);
            return null;
        }
    }

    private (int Index, double DistanceKm) FindNearest(double latitude, double longitude)
    {
        var cities = _cities;
        var grid = _grid;
        if (cities.Length == 0) return (-1, double.MaxValue);

        var latRad = latitude * DegToRad;
        var centerLatCell = Math.Clamp((int)Math.Floor((latitude + 90.0) / CellSizeDegrees), 0, LatCellCount - 1);
        var centerLonCell = NormalizeLonCell((int)Math.Floor((longitude + 180.0) / CellSizeDegrees));

        var bestSq = double.MaxValue;
        var bestIndex = -1;
        var proven = false;

        void EvaluateCell(int latCell, int lonCell)
        {
            if (latCell < 0 || latCell >= LatCellCount) return;
            var wrappedLon = NormalizeLonCell(lonCell);
            var key = ((long)latCell << 32) | (uint)wrappedLon;
            if (!grid.TryGetValue(key, out var indexes)) return;
            foreach (var index in indexes)
            {
                var candidate = cities[index];
                var sq = SquaredDistanceKm(latitude, longitude, latRad, candidate.Lat, candidate.Lon);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    bestIndex = index;
                }
            }
        }

        for (var ring = 0; ring <= MaxSearchRing; ring++)
        {
            if (ring == 0)
            {
                EvaluateCell(centerLatCell, centerLonCell);
            }
            else
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    EvaluateCell(centerLatCell + dx, centerLonCell - ring);
                    EvaluateCell(centerLatCell + dx, centerLonCell + ring);
                }
                for (var dy = -ring + 1; dy <= ring - 1; dy++)
                {
                    EvaluateCell(centerLatCell - ring, centerLonCell + dy);
                    EvaluateCell(centerLatCell + ring, centerLonCell + dy);
                }
            }

            if (bestIndex < 0) continue;
            if (bestSq <= 0.25) // 50m 内直接认定
            {
                proven = true;
                break;
            }

            // 环外任意点的距离下界（保守估计，保证最近邻正确）：
            // 至少相隔 (ring-1) 个格子宽度，再减去格子对角线。
            var maxAbsLat = Math.Min(89.0, Math.Abs(latitude) + (ring + 1) * CellSizeDegrees);
            var kmPerDegreeLon = KmPerDegreeLat * Math.Cos(maxAbsLat * DegToRad);
            var kmPerGridDegree = Math.Min(KmPerDegreeLat, Math.Max(kmPerDegreeLon, 0.1));
            var lowerBoundKm = Math.Max(0.0, (ring - 1) * CellSizeDegrees * kmPerGridDegree - CellDiagonalKm);
            if (lowerBoundKm > 0 && bestSq <= lowerBoundKm * lowerBoundKm)
            {
                proven = true;
                break;
            }
        }

        if (!proven)
        {
            // 稀疏区域（远洋/极地）兜底：全球暴力最近邻，34k 城市约几毫秒。
            for (var i = 0; i < cities.Length; i++)
            {
                var candidate = cities[i];
                var sq = SquaredDistanceKm(latitude, longitude, latRad, candidate.Lat, candidate.Lon);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    bestIndex = i;
                }
            }
        }

        return bestIndex >= 0 ? (bestIndex, Math.Sqrt(bestSq)) : (-1, double.MaxValue);
    }

    /// <summary>
    /// 最近邻修正（城市名口径）：
    /// 1) 先取半径 (最近距离 + 8km) 内的候选，按距离排序取最近 K=8 个；
    /// 2) 在候选中按"行政等级（feature code rank）+ 人口"选一个：
    ///    等级高 ≥2 级直接优先（上海 vs 黄浦、东京 vs 中野），等级接近时比人口，
    ///    人口也接近时取更近的。
    /// 这样 Reverse(成都天府广场) 返回"成都"而不是同一条数据的旧别名"天府"，
    /// Reverse(上海) 返回"上海"而不是"黄浦区"。
    /// </summary>
    private (int Index, double DistanceKm) PreferProminent(
        double latitude, double longitude, double latRad, int nearestIndex, double nearestDistanceKm)
    {
        var cities = _cities;
        var grid = _grid;
        if (cities.Length == 0 || nearestIndex < 0 || nearestIndex >= cities.Length)
            return (nearestIndex, nearestDistanceKm);

        var radiusKm = Math.Max(3.0, nearestDistanceKm + MaxPreferredRadiusKm);
        var centerLatCell = Math.Clamp((int)Math.Floor((latitude + 90.0) / CellSizeDegrees), 0, LatCellCount - 1);
        var centerLonCell = NormalizeLonCell((int)Math.Floor((longitude + 180.0) / CellSizeDegrees));
        var ringLimit = Math.Min(MaxCandidateSearchRing,
            (int)Math.Ceiling(radiusKm / (KmPerDegreeLat * CellSizeDegrees)) + 2);

        var candidates = new List<(int Index, double DistanceKm)>(MaxProminenceCandidates * 2);

        void EvaluateCell(int latCell, int lonCell)
        {
            if (latCell < 0 || latCell >= LatCellCount) return;
            var wrappedLon = NormalizeLonCell(lonCell);
            if (!grid.TryGetValue(((long)latCell << 32) | (uint)wrappedLon, out var indexes)) return;
            foreach (var index in indexes)
            {
                var candidate = cities[index];
                var distanceKm = Math.Sqrt(SquaredDistanceKm(latitude, longitude, latRad, candidate.Lat, candidate.Lon));
                if (distanceKm > radiusKm) continue;
                candidates.Add((index, distanceKm));
            }
        }

        for (var ring = 0; ring <= ringLimit; ring++)
        {
            if (ring == 0)
            {
                EvaluateCell(centerLatCell, centerLonCell);
                continue;
            }
            for (var dx = -ring; dx <= ring; dx++)
            {
                EvaluateCell(centerLatCell + dx, centerLonCell - ring);
                EvaluateCell(centerLatCell + dx, centerLonCell + ring);
            }
            for (var dy = -ring + 1; dy <= ring - 1; dy++)
            {
                EvaluateCell(centerLatCell - ring, centerLonCell + dy);
                EvaluateCell(centerLatCell + ring, centerLonCell + dy);
            }
        }

        if (candidates.Count == 0) return (nearestIndex, nearestDistanceKm);

        candidates.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
        if (candidates.Count > MaxProminenceCandidates)
        {
            // 取最近 K 个，但始终保留半径内的行政中心（PPLA/PPLC）：
            // 例如埃菲尔铁塔附近最近的是 8 个"Paris xx"区，真正的"Paris"在 4km 外，
            // 只按 K 截断会把城市本体漏掉。
            var kept = new List<(int Index, double DistanceKm)>(MaxProminenceCandidates + 4);
            for (var i = 0; i < candidates.Count; i++)
            {
                var isWithinTopK = i < MaxProminenceCandidates;
                var isMajorAdministrativeCenter = cities[candidates[i].Index].Prominence >= 8;
                if (isWithinTopK || isMajorAdministrativeCenter) kept.Add(candidates[i]);
            }
            candidates = kept;
        }

        var bestIndex = nearestIndex;
        var bestEntry = cities[nearestIndex];
        var bestDistanceKm = nearestDistanceKm;

        foreach (var (index, distanceKm) in candidates)
        {
            var candidate = cities[index];
            if (!IsMoreProminent(candidate, distanceKm, bestEntry, bestDistanceKm)) continue;
            bestEntry = candidate;
            bestIndex = index;
            bestDistanceKm = distanceKm;
        }

        return (bestIndex, bestDistanceKm);
    }

    /// <summary>候选比较：等级差 ≥2 以等级优先；否则人口为主，人口接近时取更近者。</summary>
    private static bool IsMoreProminent(CityEntry candidate, double candidateDistance, CityEntry best, double bestDistance)
    {
        var rankDelta = (int)candidate.Prominence - (int)best.Prominence;
        if (rankDelta >= 2) return true;
        if (rankDelta <= -2) return false;

        var candidateScore = ProminenceScore(candidate);
        var bestScore = ProminenceScore(best);
        if (candidateScore > bestScore * 1.02) return true;
        if (bestScore > candidateScore * 1.02) return false;
        return candidateDistance < bestDistance;
    }

    /// <summary>人口为主、行政重要性为辅的城市"显著度"。</summary>
    private static double ProminenceScore(CityEntry city) =>
        Math.Max(city.Population, 1) * (1.0 + 0.35 * Math.Clamp((int)city.Prominence, 0, 9));

    private static double SquaredDistanceKm(double lat1, double lon1, double lat1Rad, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * DegToRad;
        var dLon = (lon2 - lon1) * DegToRad;
        if (dLon > Math.PI) dLon -= 2 * Math.PI;
        else if (dLon < -Math.PI) dLon += 2 * Math.PI;

        var sinLat = Math.Sin(dLat * 0.5);
        var sinLon = Math.Sin(dLon * 0.5);
        var a = sinLat * sinLat + Math.Cos(lat1Rad) * Math.Cos(lat2 * DegToRad) * sinLon * sinLon;
        a = Math.Clamp(a, 0.0, 1.0);
        var distance = EarthRadiusKm * 2.0 * Math.Asin(Math.Sqrt(a));
        return distance * distance;
    }

    // ============================ 在线精修（可选） ============================

    public async Task<GeoPlace?> ReverseOnlineAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        try
        {
            if (double.IsNaN(latitude) || double.IsNaN(longitude) ||
                double.IsInfinity(latitude) || double.IsInfinity(longitude))
                return null;
            if (latitude is < -90.0 or > 90.0 || longitude is < -180.0 or > 180.0) return null;

            var url = NominatimEndpoint +
                      "?format=jsonv2&addressdetails=1&zoom=10&accept-language=zh-CN" +
                      "&lat=" + latitude.ToString("0.######", Inv) +
                      "&lon=" + longitude.ToString("0.######", Inv);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "StjMediaViewer/1.0 (offline-first desktop media organizer; reverse geocoding fallback)");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"在线地理编码失败：HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? country = null, province = null, city = null, district = null;
            if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
            {
                country = GetString(address, "country");
                province = GetString(address, "state", "province", "region", "state_district");
                city = GetString(address, "city", "town", "village", "municipality", "county", "hamlet");
                district = GetString(address, "suburb", "city_district", "district", "neighbourhood", "quarter");
            }

            if (country is null && province is null && city is null)
            {
                var displayName = GetString(root, "display_name");
                if (string.IsNullOrWhiteSpace(displayName)) return null;
                city = displayName.Split(',')[0].Trim();
            }

            // 用离线结果兜底，保证 Country/Province/City 尽量齐全。
            var offline = Reverse(latitude, longitude);
            return new GeoPlace(
                country ?? offline?.Country,
                province ?? offline?.Province,
                city ?? offline?.City,
                district,
                offline?.DistanceKm ?? 0,
                "online");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("在线地理编码异常（已降级为 null）", ex);
            return null;
        }
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var node)) continue;
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct CityEntry(
        float Lat,
        float Lon,
        string Name,
        string AsciiName,
        string Country,
        string Admin1,
        int Population,
        string? CjkName,
        byte Prominence);
}