import io, re

# ============ 1) 模型：GPS 字段 + 半径选项 + 片区映射 ============
p = 'src/StjMediaViewer.Core/Models/ImportModels.cs'
s = io.open(p, encoding='utf-8').read()

if 'public double? Latitude' not in s.split('class ImportGroup')[1][:1200]:
    s = s.replace('''    public DateTimeOffset? CapturedAt { get; set; }
    public string? CameraHint { get; set; }''','''    public DateTimeOffset? CapturedAt { get; set; }
    public string? CameraHint { get; set; }

    /// <summary>拍摄地 GPS（导卡时从 EXIF 读，用于「按细分位置」归档）。</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }''', 1)
    print('group: gps')

if 'GeoClusterRadiusMeters' not in s:
    s = s.replace('''    /// <summary>只导入这个时间之后拍摄的（null = 不限）。</summary>
    public DateTimeOffset? CapturedAfter { get; set; }''','''    /// <summary>只导入这个时间之后拍摄的（null = 不限）。</summary>
    public DateTimeOffset? CapturedAfter { get; set; }

    /// <summary>「按细分位置」一个范围（簇）的半径，米；默认 500。</summary>
    public double GeoClusterRadiusMeters { get; set; } = 500;''', 1)
    print('options: geo radius')

if 'GeoFolders' not in s:
    s = s.replace('''    /// <summary>源目录里发现但因为"已有同名同大小文件"等原因会跳过的组数（预估）。</summary>
    public int EstimatedDuplicates { get; set; }''','''    /// <summary>源目录里发现但因为"已有同名同大小文件"等原因会跳过的组数（预估）。</summary>
    public int EstimatedDuplicates { get; set; }

    /// <summary>「按细分位置」预计算好的组 → 片区目录名（只有模板里用了 {geo} 时才有值）。</summary>
    public Dictionary<string, string>? GeoFolders { get; set; }''', 1)
    print('plan: geo folders')

io.open(p, 'w', encoding='utf-8').write(s)

# ============ 2) ImportService：读 GPS、建簇、模板变量 ============
p = 'src/StjMediaViewer.Core/Import/ImportService.cs'
s = io.open(p, encoding='utf-8').read()

# 2.1 读取 GPS
if 'TryGeo' not in s:
    s = s.replace('''    private string? TryCameraHint(string? path)''','''    /// <summary>读 GPS（只读文件头，失败返回 null）。</summary>
    private (double? Latitude, double? Longitude) TryGeo(string? path)
    {
        if (string.IsNullOrEmpty(path)) return (null, null);
        try
        {
            var md = _metadata.Read(path, CancellationToken.None);
            if (md is null) return (null, null);
            if (md.Latitude is >= -90 and <= 90 && md.Longitude is >= -180 and <= 180)
                return (md.Latitude, md.Longitude);
        }
        catch (Exception ex)
        {
            Log.Info($"[import] 读取 GPS 失败 {path}: {ex.Message}");
        }
        return (null, null);
    }

    private string? TryCameraHint(string? path)''', 1)
    print('service: TryGeo')

# 2.2 扫描时填入 GPS
s = s.replace('''            group.CapturedAt = TryCaptureTime(probePath);
            group.CameraHint = TryCameraHint(probePath);
            group.Kind = (int)TryKind(probePath);''','''            group.CapturedAt = TryCaptureTime(probePath);
            group.CameraHint = TryCameraHint(probePath);
            group.Kind = (int)TryKind(probePath);
            if (options.SubfolderTemplate?.Contains("{geo}", StringComparison.OrdinalIgnoreCase) == true)
            {
                var (lat, lon) = TryGeo(probePath);
                group.Latitude = lat;
                group.Longitude = lon;
            }''', 1)
    print('service: read gps when {geo}')

# 2.3 计划里预计算片区
if 'GeoFolders =' not in s:
    s = s.replace('''        plan.TotalBytes = plan.Groups.Sum(g => SafeSize(g.AllFiles()));''','''        plan.TotalBytes = plan.Groups.Sum(g => SafeSize(g.AllFiles()));

        // 「按细分位置」：先按 GPS 聚成片区（半径内的算一个文件夹），模板里的 {geo} 再取名字
        if (options.SubfolderTemplate?.Contains("{geo}", StringComparison.OrdinalIgnoreCase) == true)
        {
            var points = plan.Groups
                .Where(g => g.Latitude is not null && g.Longitude is not null)
                .Select(g => new GeoPoint(g.GroupKey, g.Latitude!.Value, g.Longitude!.Value))
                .ToList();

            var resolver = BuildPlaceResolver();
            plan.GeoFolders = GeoClusterer.FolderNames(points, options.GeoClusterRadiusMeters, resolver);
            if (points.Count < plan.Groups.Count)
                plan.Warnings.Add($"{plan.Groups.Count - points.Count} 个文件没有 GPS，归到「{GeoClusterer.UnknownLocationFolder}」");
        }''', 1)
    print('service: plan geo folders')

# 2.4 地点名解析
if 'BuildPlaceResolver' not in s:
    anchor = '''    private static string SanitizeSegment(string s)'''
    assert anchor in s
    s = s.replace(anchor, '''    /// <summary>GPS → 「城市-区县」；离线城市库不可用时返回 null（聚类改用坐标命名）。</summary>
    private Func<double, double, string?>? BuildPlaceResolver()
    {
        if (_geocoder is null) return null;
        return (lat, lon) =>
        {
            try
            {
                var place = _geocoder.Reverse(lat, lon);
                if (place is null) return null;
                var city = place.City ?? place.Province ?? place.Country;
                if (string.IsNullOrWhiteSpace(city)) return null;
                var district = string.IsNullOrWhiteSpace(place.District) ? null : place.District!.Trim();
                return district is null ? city!.Trim() : $"{city!.Trim()}-{district}";
            }
            catch
            {
                return null;
            }
        };
    }

''' + anchor, 1)
    print('service: place resolver')

# 2.5 ExpandFolder 支持 {geo}
s = s.replace('''    internal static string ExpandFolder(string? template, ImportGroup group)''','''    internal static string ExpandFolder(string? template, ImportGroup group, string? geoFolder = null)''', 1)
s = s.replace('''            .Replace("{name}", name)''','''            .Replace("{geo}", SanitizeSegment(
                string.IsNullOrWhiteSpace(geoFolder) ? GeoClusterer.UnknownLocationFolder : geoFolder))
            .Replace("{name}", name)''', 1)

# 2.6 执行时传片区名
s = s.replace('''            var subDir = ExpandFolder(plan.Options.SubfolderTemplate, group);''','''            var geoFolder = plan.GeoFolders is not null && plan.GeoFolders.TryGetValue(group.GroupKey, out var geoName)
                ? geoName : null;
            var subDir = ExpandFolder(plan.Options.SubfolderTemplate, group, geoFolder);''', 1)
    print('service: expand geo')

io.open(p, 'w', encoding='utf-8').write(s)
print('import models/service done')