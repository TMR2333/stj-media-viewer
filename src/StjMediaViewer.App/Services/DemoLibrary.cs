using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 演示素材库：还没接入真实扫描时，让界面有内容可看、可点、可筛选。
/// 这些记录走与真实素材完全相同的模型，所以界面代码不需要任何特判。
/// </summary>
public static class DemoLibrary
{
    private static readonly (string Make, string Model, string Device, string RawExt)[] Devices =
    {
        ("SONY", "ILCE-7M4", "Sony A7 IV", ".ARW"),
        ("Canon", "Canon EOS R5", "Canon EOS R5", ".CR3"),
        ("NIKON CORPORATION", "NIKON Z 8", "Nikon Z 8", ".NEF"),
        ("FUJIFILM", "X-T5", "Fujifilm X-T5", ".RAF"),
        ("Apple", "iPhone 15 Pro", "Apple iPhone 15 Pro", ""),
        ("DJI", "FC3582", "DJI Mavic 3 Pro", ""),
        ("Panasonic", "DC-S5M2", "Panasonic S5 II", ".RW2"),
    };

    private static readonly (double Lat, double Lon, string City, string Country, string Province)[] Places =
    {
        (31.2304, 121.4737, "上海", "中国", "上海市"),
        (39.9042, 116.4074, "北京", "中国", "北京市"),
        (30.5728, 104.0668, "成都", "中国", "四川省"),
        (30.2741, 120.1551, "杭州", "中国", "浙江省"),
        (35.6762, 139.6503, "东京", "日本", "东京都"),
        (48.8566, 2.3522, "巴黎", "法国", "法兰西岛"),
        (40.7128, -74.0060, "纽约", "美国", "纽约州"),
        (64.1466, -21.9426, "雷克雅未克", "冰岛", "首都区"),
    };

    private static readonly string[] Themes = { "nature", "humanities", "city", "street", "portrait", "candid", "art", "architecture", "night", "travel" };
    private static readonly string[] Tones = { "highkey", "lowkey", "midkey", "hard", "soft", "cool", "warm", "mono", "tealorange", "cinematic" };
    private static readonly string[] Labels = { "none", "red", "yellow", "green", "blue", "purple" };

    public static List<MediaAsset> Build()
    {
        var rnd = new Random(20261115);
        var list = new List<MediaAsset>(72);
        var root = Path.Combine(AppContext.BaseDirectory, "DemoLibrary");

        for (int i = 0; i < 72; i++)
        {
            var kind = i % 12 == 11 ? MediaKind.Audio : (i % 5 == 4 ? MediaKind.Video : MediaKind.Image);
            var dev = Devices[rnd.Next(Devices.Length)];
            var place = Places[rnd.Next(Places.Length)];
            var day = new DateTimeOffset(2023, 1, 1, 8, 0, 0, TimeSpan.FromHours(8))
                .AddDays(rnd.Next(0, 1400)).AddMinutes(rnd.Next(0, 900));

            var isRaw = kind == MediaKind.Image && rnd.NextDouble() < 0.32 && dev.RawExt.Length > 0;
            var ext = kind switch
            {
                MediaKind.Image => isRaw ? dev.RawExt : ".JPG",
                MediaKind.Video => rnd.NextDouble() < 0.3 ? ".MOV" : ".MP4",
                _ => rnd.NextDouble() < 0.5 ? ".WAV" : ".MP3",
            };
            var format = ext.Trim('.').ToUpperInvariant();
            var rawFormat = isRaw ? rawFromExt(dev.RawExt) : RawFormat.None;
            var isLog = kind == MediaKind.Video && dev.Device.Contains("Sony") && rnd.NextDouble() < 0.5;

            var theme = Themes[rnd.Next(Themes.Length)];
            var tone = Tones[rnd.Next(Tones.Length)];
            var fileName = $"{dev.Device.Replace(" ", "")}_{2000 + i:D4}{ext}";
            var rel = Path.Combine(day.Year.ToString(), place.City, fileName);

            var asset = new MediaAsset
            {
                Id = $"demo-{i:D4}",
                LibraryId = "demo",
                FullPath = Path.Combine(root, rel),
                RelativePath = rel,
                FileName = fileName,
                SizeBytes = kind switch
                {
                    MediaKind.Image => isRaw ? rnd.Next(18_000_000, 62_000_000) : rnd.Next(2_000_000, 18_000_000),
                    MediaKind.Video => rnd.NextInt64(180_000_000L, 4_800_000_000L),
                    _ => rnd.Next(3_000_000, 48_000_000),
                },
                FileModified = day,
                FileCreated = day,
                Kind = kind,
                FormatCode = format,
                MimeType = kind == MediaKind.Image ? "image/jpeg" : kind == MediaKind.Video ? "video/mp4" : "audio/wav",
                ImageFlavor = isRaw ? ImageFlavor.None : (kind == MediaKind.Image ? ImageFlavor.Jpeg : ImageFlavor.None),
                RawFormat = rawFormat,
                Width = kind == MediaKind.Image ? (isRaw ? 9504 : 6960) : kind == MediaKind.Video ? 3840 : null,
                Height = kind == MediaKind.Image ? (isRaw ? 6336 : 4640) : kind == MediaKind.Video ? 2160 : null,
                DurationSeconds = kind == MediaKind.Image ? null : rnd.Next(3, 900),
                FrameRate = kind == MediaKind.Video ? (rnd.NextDouble() < 0.5 ? 24 : 59.94) : null,
                BitrateBps = kind == MediaKind.Video ? rnd.Next(80_000_000, 400_000_000) : null,
                VideoCodec = kind == MediaKind.Video ? (rnd.NextDouble() < 0.4 ? "H.265 (HEVC)" : "H.264") : null,
                AudioCodec = kind == MediaKind.Video ? "AAC" : kind == MediaKind.Audio ? "PCM" : null,
                AudioChannels = kind == MediaKind.Audio ? 2 : null,
                AudioSampleRate = kind == MediaKind.Audio ? 48000 : null,
                HasAudio = kind != MediaKind.Image,
                CameraMake = kind == MediaKind.Audio ? null : dev.Make,
                CameraModel = kind == MediaKind.Audio ? null : dev.Model,
                DeviceKey = kind == MediaKind.Audio ? "录音设备" : dev.Device,
                LensModel = kind == MediaKind.Audio ? null : "FE 24-70mm F2.8 GM II",
                FocalLengthMm = kind == MediaKind.Audio ? null : rnd.Next(16, 200),
                ApertureF = kind == MediaKind.Audio ? null : Math.Round(1.4 + rnd.NextDouble() * 8, 1),
                ShutterSpeedSeconds = kind == MediaKind.Audio ? null : 1.0 / (30 << rnd.Next(0, 6)),
                Iso = kind == MediaKind.Audio ? null : 100 * (1 << rnd.Next(0, 6)),
                CapturedAt = kind == MediaKind.Audio ? day.AddDays(-3) : day,
                CapturedAtSource = "exif:DateTimeOriginal",
                Latitude = kind == MediaKind.Audio ? null : place.Lat,
                Longitude = kind == MediaKind.Audio ? null : place.Lon,
                Country = place.Country,
                Province = place.Province,
                City = place.City,
                CitySource = "offline",
                Title = kind == MediaKind.Audio ? $"现场收音 {i:D2}" : null,
                Creator = "STJ Studio",
                Copyright = "© 2026 STJ Studio",
                IsLog = isLog,
                LogProfile = isLog ? "S-Log3" : null,
                TransferCharacteristics = isLog ? "S-Log3" : kind == MediaKind.Video ? "BT.709" : null,
                ColorPrimaries = kind == MediaKind.Video ? "BT.709" : "sRGB",
                ColorProfileName = isRaw ? "AdobeRGB" : "sRGB",
                PostProcess = rnd.NextDouble() < 0.45 ? PostProcessState.Processed : PostProcessState.Unprocessed,
                HasDevelopEdits = rnd.NextDouble() < 0.4,
                DevelopEngine = rnd.NextDouble() < 0.4 ? "Lightroom" : null,
                Theme = rnd.NextDouble() < 0.7 ? theme : null,
                Tone = rnd.NextDouble() < 0.6 ? tone : null,
                Rating = rnd.Next(0, 6),
                ColorLabel = Labels[rnd.Next(Labels.Length)],
                IsPick = rnd.Next(0, 3) switch { 0 => true, 1 => false, _ => null },
                AiSubject = kind == MediaKind.Image ? "城市街角的黄昏光影" : null,
                AiMood = "安静 / 疏离",
                QualityScore = Math.Round(0.45 + rnd.NextDouble() * 0.5, 2),
                QuickHash = $"q1:{rnd.NextInt64():x16}",
                IndexedAt = DateTimeOffset.Now.AddDays(-rnd.Next(0, 30)),
                MetadataParsedAt = DateTimeOffset.Now.AddDays(-rnd.Next(0, 30)),
            };

            asset.Keywords.AddRange(new[] { place.City, isRaw ? "RAW" : format, isLog ? "Log" : theme });
            if (isRaw) asset.Provenance[ClassDimension.ImageKind] = FieldProvenance.Of(ClassSource.Derived);
            if (asset.Theme is not null) asset.Provenance[ClassDimension.Theme] = FieldProvenance.Of(ClassSource.Ai, 0.82, "gpt-4o-mini");
            if (asset.Tone is not null) asset.Provenance[ClassDimension.Tone] = FieldProvenance.Of(ClassSource.Manual);
            if (asset.CapturedAt is not null) asset.Provenance[ClassDimension.CapturedTime] = FieldProvenance.Of(ClassSource.Derived);
            if (asset.City is not null) asset.Provenance[ClassDimension.City] = FieldProvenance.Of(ClassSource.Derived);
            list.Add(asset);
        }

        return list;
    }

    private static RawFormat rawFromExt(string ext) => ext.ToUpperInvariant() switch
    {
        ".ARW" => RawFormat.Arw,
        ".CR3" => RawFormat.Cr3,
        ".NEF" => RawFormat.Nef,
        ".RAF" => RawFormat.Raf,
        ".RW2" => RawFormat.Rw2,
        ".DNG" => RawFormat.Dng,
        _ => RawFormat.Other,
    };
}

/// <summary>内存索引库：与 SQLite 实现同契约，供 UI 先行阶段使用。</summary>
internal sealed class FallbackAssetStore : IAssetStore
{
    private readonly List<MediaAsset> _assets;
    private readonly List<LibraryRoot> _roots = new();

    public FallbackAssetStore(List<MediaAsset> seed) => _assets = seed;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<MediaAsset?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_assets.FirstOrDefault(a => a.Id == id));

    public Task<MediaAsset?> GetByPathAsync(string fullPath, CancellationToken ct = default) =>
        Task.FromResult(_assets.FirstOrDefault(a => string.Equals(a.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<string>> GetIdsByPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct = default)
    {
        var set = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(_assets.Where(a => set.Contains(a.FullPath)).Select(a => a.Id).ToList());
    }

    public Task UpsertAsync(MediaAsset asset, CancellationToken ct = default) => UpsertManyAsync(new[] { asset }, ct);

    public Task UpsertManyAsync(IEnumerable<MediaAsset> assets, CancellationToken ct = default)
    {
        foreach (var a in assets)
        {
            var idx = _assets.FindIndex(x => x.Id == a.Id);
            if (idx >= 0) _assets[idx] = a; else _assets.Add(a);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var set = new HashSet<string>(ids);
        _assets.RemoveAll(a => set.Contains(a.Id));
        return Task.CompletedTask;
    }

    public Task DeleteByLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        _assets.RemoveAll(a => a.LibraryId == libraryId);
        return Task.CompletedTask;
    }

    private IEnumerable<MediaAsset> Apply(AssetQuery q)
    {
        IEnumerable<MediaAsset> src = _assets;
        if (!string.IsNullOrWhiteSpace(q.LibraryId)) src = src.Where(a => a.LibraryId == q.LibraryId);
        if (q.Kinds.Count > 0) src = src.Where(a => q.Kinds.Contains(a.Kind));
        if (q.Formats.Count > 0) src = src.Where(a => q.Formats.Contains(a.FormatCode, StringComparer.OrdinalIgnoreCase));
        if (q.Devices.Count > 0) src = src.Where(a => q.Devices.Contains(a.DisplayDevice, StringComparer.OrdinalIgnoreCase));
        if (q.Cities.Count > 0) src = src.Where(a => a.City is not null && q.Cities.Contains(a.City));
        if (q.Themes.Count > 0) src = src.Where(a => a.Theme is not null && q.Themes.Contains(a.Theme));
        if (q.Tones.Count > 0) src = src.Where(a => a.Tone is not null && q.Tones.Contains(a.Tone));
        if (q.Keywords.Count > 0) src = src.Where(a => q.Keywords.Any(k => a.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)));
        if (q.ColorLabels.Count > 0) src = src.Where(a => a.ColorLabel is not null && q.ColorLabels.Contains(a.ColorLabel));
        if (q.RawFormats.Count > 0) src = src.Where(a => q.RawFormats.Contains(a.RawFormat));
        if (q.IsRaw.HasValue) src = src.Where(a => a.IsRaw == q.IsRaw.Value);
        if (q.IsLog.HasValue) src = src.Where(a => (a.IsLog ?? false) == q.IsLog.Value);
        if (q.HasGps.HasValue) src = src.Where(a => (a.Latitude.HasValue && a.Longitude.HasValue) == q.HasGps.Value);
        if (q.IsPick.HasValue) src = src.Where(a => a.IsPick == q.IsPick.Value);
        if (q.PostProcess.HasValue) src = src.Where(a => a.PostProcess == q.PostProcess.Value);
        if (q.MinRating.HasValue) src = src.Where(a => a.Rating >= q.MinRating.Value);
        if (q.MaxRating.HasValue) src = src.Where(a => a.Rating <= q.MaxRating.Value);
        if (q.CapturedFrom.HasValue) src = src.Where(a => a.CapturedAt >= q.CapturedFrom.Value);
        if (q.CapturedTo.HasValue) src = src.Where(a => a.CapturedAt <= q.CapturedTo.Value);
        if (q.OnlyUnclassified) src = src.Where(a => string.IsNullOrEmpty(a.Theme) || string.IsNullOrEmpty(a.Tone));
        if (q.OnlyProblems) src = src.Where(a => !string.IsNullOrEmpty(a.LastError));
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var t = q.Text.Trim();
            src = src.Where(a =>
                a.FileName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (a.City?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.DeviceKey?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.AiSubject?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                a.Keywords.Any(k => k.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        src = q.SortBy switch
        {
            AssetSortKeys.FileName => q.Descending ? src.OrderByDescending(a => a.FileName) : src.OrderBy(a => a.FileName),
            AssetSortKeys.Size => q.Descending ? src.OrderByDescending(a => a.SizeBytes) : src.OrderBy(a => a.SizeBytes),
            AssetSortKeys.Rating => q.Descending ? src.OrderByDescending(a => a.Rating) : src.OrderBy(a => a.Rating),
            AssetSortKeys.Duration => q.Descending ? src.OrderByDescending(a => a.DurationSeconds) : src.OrderBy(a => a.DurationSeconds),
            AssetSortKeys.Quality => q.Descending ? src.OrderByDescending(a => a.QualityScore) : src.OrderBy(a => a.QualityScore),
            AssetSortKeys.FileModified => q.Descending ? src.OrderByDescending(a => a.FileModified) : src.OrderBy(a => a.FileModified),
            _ => q.Descending ? src.OrderByDescending(a => a.CapturedAt ?? a.FileModified) : src.OrderBy(a => a.CapturedAt ?? a.FileModified),
        };
        return src;
    }

    public Task<IReadOnlyList<MediaAsset>> QueryAsync(AssetQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(Apply(query).Skip(query.Offset).Take(query.Limit).ToList());

    public Task<int> CountAsync(AssetQuery query, CancellationToken ct = default) => Task.FromResult(Apply(query).Count());

    public Task<FacetCounts> GetFacetsAsync(AssetQuery query, CancellationToken ct = default)
    {
        var all = _assets.ToList();
        var f = new FacetCounts
        {
            Total = all.Count,
            RawCount = all.Count(a => a.IsRaw),
            LogCount = all.Count(a => a.IsLog == true),
            GpsCount = all.Count(a => a.Latitude.HasValue),
            UnclassifiedCount = all.Count(a => string.IsNullOrEmpty(a.Theme)),
            TotalBytes = all.Sum(a => a.SizeBytes),
            ByKind = all.GroupBy(a => a.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ByFormat = all.GroupBy(a => a.FormatCode).ToDictionary(g => g.Key, g => g.Count()),
            ByDevice = all.GroupBy(a => a.DisplayDevice).ToDictionary(g => g.Key, g => g.Count()),
            ByCity = all.Where(a => a.City is not null).GroupBy(a => a.City!).ToDictionary(g => g.Key, g => g.Count()),
            ByTheme = all.Where(a => a.Theme is not null).GroupBy(a => a.Theme!).ToDictionary(g => g.Key, g => g.Count()),
            ByTone = all.Where(a => a.Tone is not null).GroupBy(a => a.Tone!).ToDictionary(g => g.Key, g => g.Count()),
            ByYear = all.Where(a => a.CapturedAt.HasValue).GroupBy(a => a.CapturedAt!.Value.Year.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ByPostProcess = all.GroupBy(a => Vocabularies.PostProcessId(a.PostProcess)).ToDictionary(g => g.Key, g => g.Count()),
            ByPick = all.GroupBy(a => a.IsPick switch { true => "pick", false => "reject", _ => "none" }).ToDictionary(g => g.Key, g => g.Count()),
        };
        return Task.FromResult(f);
    }

    public Task<IReadOnlyList<TimelineBucket>> GetTimelineAsync(AssetQuery query, CancellationToken ct = default)
    {
        var buckets = Apply(query)
            .Where(a => a.CapturedAt.HasValue)
            .GroupBy(a => DateOnly.FromDateTime(a.CapturedAt!.Value.LocalDateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new TimelineBucket
            {
                Day = g.Key,
                Count = g.Count(),
                ImageCount = g.Count(a => a.Kind == MediaKind.Image),
                VideoCount = g.Count(a => a.Kind == MediaKind.Video),
                AudioCount = g.Count(a => a.Kind == MediaKind.Audio),
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<TimelineBucket>>(buckets);
    }

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(string field, string? libraryId, CancellationToken ct = default)
    {
        var src = string.IsNullOrWhiteSpace(libraryId) ? _assets : _assets.Where(a => a.LibraryId == libraryId).ToList();
        IEnumerable<string> values = field switch
        {
            "device" => src.Select(a => a.DisplayDevice),
            "city" => src.Select(a => a.City ?? ""),
            "theme" => src.Select(a => a.Theme ?? ""),
            "tone" => src.Select(a => a.Tone ?? ""),
            "format" => src.Select(a => a.FormatCode),
            _ => src.Select(a => a.FormatCode),
        };
        return Task.FromResult<IReadOnlyList<string>>(values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v).ToList());
    }

    public Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var set = new HashSet<string>(ids);
        return Task.FromResult<IReadOnlyList<MediaAsset>>(_assets.Where(a => set.Contains(a.Id)).ToList());
    }

    public Task<IReadOnlyList<(string Id, string FullPath, long SizeBytes, DateTimeOffset FileModified)>> ListAllPathsAsync(string? libraryId, CancellationToken ct = default)
    {
        var src = string.IsNullOrWhiteSpace(libraryId) ? _assets : _assets.Where(a => a.LibraryId == libraryId).ToList();
        return Task.FromResult<IReadOnlyList<(string, string, long, DateTimeOffset)>>(
            src.Select(a => (a.Id, a.FullPath, a.SizeBytes, a.FileModified)).ToList());
    }

    public Task<int> CountAllAsync(CancellationToken ct = default) => Task.FromResult(_assets.Count);

    public Task SaveLibraryRootsAsync(IEnumerable<LibraryRoot> roots, CancellationToken ct = default)
    {
        _roots.Clear();
        _roots.AddRange(roots);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LibraryRoot>> LoadLibraryRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryRoot>>(_roots.ToList());
}