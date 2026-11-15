using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;

// 元数据引擎冒烟测试：解析 tools/SampleLibrary/out/library 里的真实样本，
// 与 catalog-manifest.json 的期望值逐项对比，并统计单文件解析耗时。
var repoRoot = FindRepoRoot();
var library = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "tools", "SampleLibrary", "out", "library");
Console.OutputEncoding = Encoding.UTF8;

var report = new StringBuilder();
void W(string line = "")
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

// 单文件调试模式：META_DEBUG=<path> MetaSmoke.exe
var debugPath = Environment.GetEnvironmentVariable("META_DEBUG");
if (!string.IsNullOrEmpty(debugPath))
{
    var mm = new MetadataService().Read(debugPath);
    W($"[meta] kind={mm.Kind} format={mm.FormatCode} container={mm.ContainerName} size={mm.Width}x{mm.Height} dur={mm.DurationSeconds} fps={mm.FrameRate}");
    W($"[meta] make='{mm.CameraMake}' model='{mm.CameraModel}' device='{mm.DeviceKey}' lens='{mm.LensModel}'");
    W($"[meta] captured={mm.CapturedAt:O} src={mm.CapturedAtSource} gps={mm.Latitude},{mm.Longitude} city={mm.City}({mm.CitySource})");
    W($"[meta] raw={mm.RawFormat} preview={mm.HasEmbeddedPreview} {mm.EmbeddedPreviewWidth}x{mm.EmbeddedPreviewHeight}");
    W($"[meta] rating={mm.Rating} label={mm.ColorLabel} title='{mm.Title}' kw=[{string.Join(",", mm.Keywords)}]");
    W($"[meta] xmpEmbedded={mm.HasEmbeddedXmp} xmpPath={mm.XmpPath} sidecars=[{string.Join(",", mm.Sidecars.Select(Path.GetFileName))}]");
    W($"[meta] develop={mm.HasDevelopEdits} engine='{mm.DevelopEngine}' post={mm.PostProcess}");
    W($"[meta] color: prim={mm.ColorPrimaries} trc={mm.TransferCharacteristics} matrix={mm.ColorMatrix} profile='{mm.ColorProfileName}' isLog={mm.IsLog} logProfile='{mm.LogProfile}' temp={mm.ColorTemperatureK}");
    W($"[meta] audio: codec={mm.AudioCodec} ch={mm.AudioChannels} rate={mm.AudioSampleRate} bits={mm.BitDepth} hasAudio={mm.HasAudio} vcodec={mm.VideoCodec} bitrate={mm.BitrateBps}");
    W($"[meta] error='{mm.Error}' warnings=[{string.Join(" | ", mm.Warnings)}]");
    {
        var swp = Stopwatch.StartNew();
        var pre = RawPreviewExtractor.Find(debugPath, mm.RawFormat);
        swp.Stop();
        W($"[preview] found={pre.Found} {pre.Width}x{pre.Height} len={pre.Length} method={pre.Method} {swp.Elapsed.TotalMilliseconds:0.0}ms");
    }
    if (mm.Kind == MediaKind.Video)
    {
        var iso = IsoBmffParser.Parse(debugPath);
        W($"[iso] brand={iso.MajorBrand} compat={iso.CompatibleBrands} format={iso.FormatCode} w={iso.Width} h={iso.Height} dur={iso.DurationSeconds} fps={iso.FrameRate}");
        W($"[iso] cicp {iso.CicpPrimaries}/{iso.CicpTransfer}/{iso.CicpMatrix} icc='{iso.IccProfileName}' make='{iso.Make}' model='{iso.Model}' date='{iso.CreationDateString}' gps='{iso.LocationIso6709}'");
        W($"[iso] hints=[{string.Join(",", iso.TextHints)}] strings=[{string.Join(",", iso.MetadataStrings)}] warn=[{string.Join(" | ", iso.Warnings)}] err='{iso.Error}'");
    }
    if (mm.Kind == MediaKind.Image && mm.FormatCode is "JPEG")
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(debugPath); }
        catch (Exception ex) { dirs = Array.Empty<MetadataExtractor.Directory>(); W("[exif] reader failed " + ex.Message); }
        foreach (var dir in dirs)
        {
            W($"[exif] {dir.GetType().Name} / {dir.Name}");
            if (dir.GetType().Name == "ExifSubIfdDirectory")
            {
                W($"    [const] TagTimeZone={MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagTimeZone} TagTimeZoneOriginal={MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagTimeZoneOriginal}");
                foreach (var t in new[] { 0x9010, 0x9011, 0x9012 })
                {
                    string? raw = null;
                    try { raw = MetadataExtractor.DirectoryExtensions.GetString(dir, t); } catch (Exception ex) { raw = "ERR:" + ex.Message; }
                    W($"    [raw] tag 0x{t:X4} = '{raw}' contains={dir.ContainsTag(t)}");
                }
            }
            foreach (var tag in dir.Tags)
            {
                string desc;
                try { desc = tag.Description ?? ""; } catch { desc = "?"; }
                if (tag.Name is "Make" or "Model" or "Lens Model" or "Date/Time Original" or "Time Zone Original" or "Time Zone" or "Offset Time Original" or "Offset Time" or "Date/Time Digitized" or "ISO Speed Ratings" or "F-Number" or "Exposure Time" or "Focal Length" or "GPS Latitude" or "GPS Longitude" or "GPS Altitude" or "Rating")
                    W($"    tag {tag.Type} '{tag.Name}' = {desc}");
            }
        }
    }
    return 0;
}

W($"library = {library}");
if (!Directory.Exists(library))
{
    W("样本库不存在，退出");
    return 2;
}

var service = new MetadataService();
var manifestPath = Path.Combine(library, "catalog-manifest.json");
var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(manifestPath))
{
    // 注意：JsonDocument 必须活着，因为下面的 JsonElement 引用了它
    var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var root = doc.RootElement;
    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entriesEl))
        root = entriesEl;
    if (root.ValueKind == JsonValueKind.Array)
        foreach (var item in root.EnumerateArray())
            if (item.TryGetProperty("path", out var p) && p.GetString() is { } path)
                entries[path.Replace('/', Path.DirectorySeparatorChar)] = item;
}

var files = Directory.GetFiles(library, "*", SearchOption.AllDirectories);
W($"files = {files.Length}");

var failures = new List<string>();
var cityPending = 0;
var cityChecked = 0;
var citySources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var checkedFiles = 0;
var totalMs = 0.0;
var maxMs = 0.0;
string? maxFile = null;
var perKind = new Dictionary<MediaKind, (int Count, double Ms)>();

foreach (var file in files)
{
    var rel = Path.GetRelativePath(library, file);
    var ext = Path.GetExtension(file).ToLowerInvariant();
    // 跳过 manifest/非媒体/边车（扫描器会先过滤，这里只测媒体）
    if (ext is ".json" or ".txt" or ".tmp" or ".csv" or ".db" or ".ini" or ".xmp" or ".dop" or ".cos" or ".on1" or ".pp3" or ".arp")
        continue;
    if (Path.GetFileName(file).StartsWith(".")) continue;

    var sw = Stopwatch.StartNew();
    MediaMetadata m;
    try
    {
        if (Environment.GetEnvironmentVariable("META_VERBOSE") == "1")
        {
            Console.Error.WriteLine($"[read] {rel}");
            Console.Error.Flush();
        }
        m = service.Read(file);
        sw.Stop();
    }
    catch (Exception ex)
    {
        failures.Add($"{rel}: 抛出异常 {ex.GetType().Name}: {ex.Message}");
        continue;
    }
    var ms = sw.Elapsed.TotalMilliseconds;
    totalMs += ms;
    if (ms > maxMs) { maxMs = ms; maxFile = rel; }
    checkedFiles++;
    perKind[m.Kind] = (perKind.GetValueOrDefault(m.Kind).Count + 1, perKind.GetValueOrDefault(m.Kind).Ms + ms);

    if (!entries.TryGetValue(rel, out var e))
    {
        W($"[无 manifest] {rel} → kind={m.Kind} format={m.FormatCode} device={m.DeviceKey} size={m.Width}x{m.Height}");
        continue;
    }
    if (e.TryGetProperty("expectIncluded", out var inc) && !inc.GetBoolean()) continue;

    void Fail(string msg) => failures.Add($"{rel}: {msg}");

    // kind
    if (e.TryGetProperty("kind", out var kindEl) && Enum.TryParse<MediaKind>(kindEl.GetString(), true, out var expectKind) && m.Kind != expectKind)
        Fail($"kind 期望 {expectKind} 实际 {m.Kind}");

    // device
    if (e.TryGetProperty("expectDevice", out var devEl) && devEl.GetString() is { Length: > 0 } dev &&
        !string.Equals(m.DeviceKey, dev, StringComparison.OrdinalIgnoreCase))
        Fail($"DeviceKey 期望 '{dev}' 实际 '{m.DeviceKey}' (make='{m.CameraMake}' model='{m.CameraModel}')");

    // camera make/model/lens
    CheckStr(e, "expectCameraMake", m.CameraMake, Fail, strict: true);
    CheckStr(e, "expectCameraModel", m.CameraModel, Fail, strict: true);
    CheckStr(e, "expectLensModel", m.LensModel, Fail, strict: true);

    // dims
    if (e.TryGetProperty("expectWidth", out var wEl) && wEl.ValueKind == JsonValueKind.Number && m.Width != wEl.GetInt32())
        Fail($"Width 期望 {wEl.GetInt32()} 实际 {m.Width}");
    if (e.TryGetProperty("expectHeight", out var hEl) && hEl.ValueKind == JsonValueKind.Number && m.Height != hEl.GetInt32())
        Fail($"Height 期望 {hEl.GetInt32()} 实际 {m.Height}");

    // captured at
    if (e.TryGetProperty("expectCapturedAt", out var capEl) && capEl.GetString() is { Length: > 0 } capStr &&
        DateTimeOffset.TryParse(capStr, out var expectCap))
    {
        if (m.CapturedAt is null)
            Fail($"CapturedAt 期望 {expectCap:O} 实际 null");
        else if (Math.Abs((m.CapturedAt.Value - expectCap).TotalSeconds) > 1.0)
            Fail($"CapturedAt 期望 {expectCap:O} 实际 {m.CapturedAt:O} (src={m.CapturedAtSource})");
        else if (m.CapturedAt.Value.Offset != expectCap.Offset)
            Fail($"CapturedAt Offset 期望 {expectCap.Offset} 实际 {m.CapturedAt.Value.Offset} (src={m.CapturedAtSource})");
    }

    // gps
    if (e.TryGetProperty("expectGps", out var gpsEl) && gpsEl.ValueKind == JsonValueKind.Object)
    {
        var lat = gpsEl.GetProperty("lat").GetDouble();
        var lon = gpsEl.GetProperty("lon").GetDouble();
        if (m.Latitude is null || m.Longitude is null)
            Fail($"GPS 期望 {lat},{lon} 实际 null");
        else if (Math.Abs(m.Latitude.Value - lat) > 0.001 || Math.Abs(m.Longitude.Value - lon) > 0.001)
            Fail($"GPS 期望 {lat},{lon} 实际 {m.Latitude},{m.Longitude}");
    }

    // raw
    if (e.TryGetProperty("expectIsRaw", out var rawEl) && m.RawFormat != RawFormat.None != rawEl.GetBoolean())
        Fail($"IsRaw 期望 {rawEl.GetBoolean()} 实际 {m.RawFormat}");
    if (e.TryGetProperty("expectRawFormat", out var rfEl) && rfEl.GetString() is { Length: > 0 } rf &&
        !string.Equals(m.RawFormat.ToString(), rf, StringComparison.OrdinalIgnoreCase))
        Fail($"RawFormat 期望 {rf} 实际 {m.RawFormat}");

    // xmp text
    CheckStr(e, "expectTitle", m.Title, Fail, strict: true);
    CheckStr(e, "expectCaption", m.Caption, Fail, strict: true);
    CheckStr(e, "expectColorLabel", m.ColorLabel, Fail, strict: false);
    if (e.TryGetProperty("expectRating", out var ratEl) && ratEl.ValueKind == JsonValueKind.Number && m.Rating != ratEl.GetInt32())
        Fail($"Rating 期望 {ratEl.GetInt32()} 实际 {m.Rating}");
    if (e.TryGetProperty("expectKeywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var kw in kwEl.EnumerateArray())
        {
            var s = kw.GetString();
            if (s is not null && !m.Keywords.Contains(s, StringComparer.OrdinalIgnoreCase))
                Fail($"缺少关键词 {s} (实际 {string.Join(",", m.Keywords)})");
        }
    }

    // develop / postprocess
    if (e.TryGetProperty("expectHasDevelopEdits", out var hdeEl) && hdeEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        m.HasDevelopEdits != hdeEl.GetBoolean())
        Fail($"HasDevelopEdits 期望 {hdeEl.GetBoolean()} 实际 {m.HasDevelopEdits}");
    CheckStr(e, "expectDevelopEngine", m.DevelopEngine, Fail, strict: true);
    if (e.TryGetProperty("expectHasEmbeddedXmp", out var hexEl) && hexEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        m.HasEmbeddedXmp != hexEl.GetBoolean())
        Fail($"HasEmbeddedXmp 期望 {hexEl.GetBoolean()} 实际 {m.HasEmbeddedXmp}");
    if (e.TryGetProperty("expectXmpPath", out var xpEl) && xpEl.GetString() is { Length: > 0 } xp)
    {
        var expectXmp = Path.GetFullPath(Path.Combine(library, xp.Replace('/', Path.DirectorySeparatorChar)));
        if (m.XmpPath is null || !string.Equals(Path.GetFullPath(m.XmpPath), expectXmp, StringComparison.OrdinalIgnoreCase))
            Fail($"XmpPath 期望 {xp} 实际 {m.XmpPath}");
    }
    if (e.TryGetProperty("expectSidecars", out var scEl) && scEl.ValueKind == JsonValueKind.Array)
    {
        var expectSc = scEl.EnumerateArray()
            .Select(x => Path.GetFullPath(Path.Combine(library, x!.GetString()!.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSc = m.Sidecars.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectSc.SetEquals(actualSc))
            Fail($"Sidecars 期望 [{string.Join(",", expectSc.Select(Path.GetFileName))}] 实际 [{string.Join(",", actualSc.Select(Path.GetFileName))}]");
    }
    if (e.TryGetProperty("expectPostProcess", out var ppEl) && ppEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
    {
        var expectProcessed = ppEl.GetBoolean();
        if (expectProcessed && m.PostProcess != PostProcessState.Processed)
            Fail($"PostProcess 期望 Processed 实际 {m.PostProcess}");
        if (!expectProcessed && m.PostProcess == PostProcessState.Processed)
            Fail($"PostProcess 期望非 Processed 实际 {m.PostProcess}");
    }

    // raw preview
    if (e.TryGetProperty("expectHasEmbeddedPreview", out var hpEl) && hpEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        m.HasEmbeddedPreview != hpEl.GetBoolean())
        Fail($"HasEmbeddedPreview 期望 {hpEl.GetBoolean()} 实际 {m.HasEmbeddedPreview}");
    if (e.TryGetProperty("expectEmbeddedPreviewWidth", out var epwEl) && epwEl.ValueKind == JsonValueKind.Number &&
        m.EmbeddedPreviewWidth != epwEl.GetInt32())
        Fail($"PreviewWidth 期望 {epwEl.GetInt32()} 实际 {m.EmbeddedPreviewWidth}");
    if (e.TryGetProperty("expectEmbeddedPreviewHeight", out var ephEl) && ephEl.ValueKind == JsonValueKind.Number &&
        m.EmbeddedPreviewHeight != ephEl.GetInt32())
        Fail($"PreviewHeight 期望 {ephEl.GetInt32()} 实际 {m.EmbeddedPreviewHeight}");

    // duration
    if (e.TryGetProperty("expectDurationSeconds", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
    {
        var expectDur = durEl.GetDouble();
        if (m.DurationSeconds is null || Math.Abs(m.DurationSeconds.Value - expectDur) > 0.35)
            Fail($"Duration 期望 {expectDur} 实际 {m.DurationSeconds}（warnings: {string.Join(" | ", m.Warnings.Take(2))}）");
    }

    // city（XMP 里有城市时必须精确；没有则由 IGeocoder 后续补齐，这里只统计）
    if (e.TryGetProperty("expectCity", out var cityEl) && cityEl.GetString() is { Length: > 0 } expectCity)
    {
        if (m.City is null) cityPending++;
        else
        {
            citySources[m.CitySource ?? "(null)"] = citySources.GetValueOrDefault(m.CitySource ?? "(null)") + 1;
            cityChecked++;
            if (!string.Equals(m.City, expectCity, StringComparison.Ordinal))
                Fail($"City 期望 '{expectCity}' 实际 '{m.City}'");
        }
    }

    // log
    if (e.TryGetProperty("expectIsLog", out var logEl) && logEl.ValueKind == JsonValueKind.True && m.IsLog != true)
        Fail($"IsLog 期望 true 实际 {m.IsLog} (profile={m.LogProfile}, transfer={m.TransferCharacteristics})");

    // 颜色（宽松比较：包含即可）
    LooseColorCheck(e, "expectColorPrimaries", m.ColorPrimaries, Fail);
    LooseColorCheck(e, "expectTransferCharacteristics", m.TransferCharacteristics, Fail);

    // 音频
    if (e.TryGetProperty("expectAudioCodec", out var acEl) && acEl.GetString() is { } ac && !LooseEqual(m.AudioCodec, ac))
        Fail($"AudioCodec 期望 {ac} 实际 {m.AudioCodec}");
    if (e.TryGetProperty("expectAudioChannels", out var achEl) && achEl.ValueKind == JsonValueKind.Number &&
        m.AudioChannels != achEl.GetInt32())
        Fail($"AudioChannels 期望 {achEl.GetInt32()} 实际 {m.AudioChannels}");
    if (e.TryGetProperty("expectAudioSampleRate", out var asrEl) && asrEl.ValueKind == JsonValueKind.Number &&
        m.AudioSampleRate != asrEl.GetInt32())
        Fail($"AudioSampleRate 期望 {asrEl.GetInt32()} 实际 {m.AudioSampleRate}");

    // 错误预期（仅报告，不作为失败）
    if (e.TryGetProperty("expectParseError", out var peEl) && peEl.ValueKind == JsonValueKind.True &&
        string.IsNullOrEmpty(m.Error) && m.Warnings.Count == 0)
        W($"[提示] {rel}: manifest 期望解析错误，但我们没有 Error/Warning");
}

W();
W("================ 统计 ================");
W($"检查媒体文件: {checkedFiles}   总耗时: {totalMs:0.0} ms   平均: {totalMs / Math.Max(1, checkedFiles):0.00} ms   最慢: {maxFile} {maxMs:0.0} ms");
foreach (var kv in perKind)
    W($"  {kv.Key}: {kv.Value.Count} 个, 平均 {kv.Value.Ms / Math.Max(1, kv.Value.Count):0.00} ms");

W($"City: 有文件元数据城市并确认 {cityChecked} 条，等待 IGeocoder {cityPending} 条；来源分布=[{string.Join(", ", citySources.Select(kv => kv.Key + ":" + kv.Value))}]");
W($"失败项: {failures.Count}");
foreach (var g in failures.Select(f => f.Split(':')[1].Trim().Split(' ')[0]).GroupBy(x => x).OrderByDescending(g => g.Count()))
    W($"  {g.Key}: {g.Count()}");
foreach (var f in failures.Take(90)) W("  - " + f);

// ============ 并发 / 取消 / 重复性 ============
W();
W("================ 并发与取消 ================");
var mediaFiles = files.Where(f =>
{
    var ext = Path.GetExtension(f).ToLowerInvariant();
    if (ext is ".json" or ".txt" or ".tmp" or ".csv" or ".db" or ".ini" or ".xmp" or ".dop" or ".cos" or ".on1" or ".pp3" or ".arp") return false;
    return !Path.GetFileName(f).StartsWith(".");
}).ToList();
var concurrentErrors = 0;
var sequential = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var f in mediaFiles)
{
    var r = service.Read(f);
    sequential[f] = $"{r.Kind}|{r.DeviceKey}|{r.Width}x{r.Height}|{r.Rating}|{r.CapturedAt:O}";
}
var swc = Stopwatch.StartNew();
Parallel.ForEach(mediaFiles, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
{
    try
    {
        var r = service.Read(f);
        var sig = $"{r.Kind}|{r.DeviceKey}|{r.Width}x{r.Height}|{r.Rating}|{r.CapturedAt:O}";
        if (sequential[f] != sig) Interlocked.Increment(ref concurrentErrors);
    }
    catch { Interlocked.Increment(ref concurrentErrors); }
});
swc.Stop();
W($"并发解析 {mediaFiles.Count} 个文件（8 线程）: {swc.Elapsed.TotalMilliseconds:0} ms, 结果不一致/异常 {concurrentErrors}");
var cancelled = service.Read(mediaFiles.First(), new CancellationToken(canceled: true));
W($"预取消 token: kind={cancelled.Kind}, warnings={cancelled.Warnings.Count}, error='{cancelled.Error}'（不应抛异常）");
var absent = service.Read(Path.Combine(library, "不存在的文件.jpg"));
W($"不存在文件: error='{absent.Error}'，未抛异常");

// ============ XmpReader 独立可用性 ============
W();
W("================ XmpReader 独立解析 ================");
var xmpFiles = Directory.GetFiles(library, "*.xmp", SearchOption.AllDirectories);
foreach (var xf in xmpFiles.Take(3))
{
    var d = XmpReader.ParseFile(xf);
    W($"{Path.GetFileName(xf)}: props={d.Properties.Count} rating={d.Rating} label={d.ColorLabel} city={d.City} country={d.Country} " +
      $"title={d.Title} keywords={string.Join("/", d.Keywords)} hasSettings={d.HasDevelopSettings} pv={d.ProcessVersion} engine={SidecarScanner.GuessEngineFromXmp(d)}");
}

// ============ RAW 预览提取 ============
W();
W("================ RAW 内嵌预览 ================");
foreach (var rawFile in Directory.GetFiles(library, "*", SearchOption.AllDirectories)
             .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".arw" or ".cr2" or ".dng" or ".nef").Take(8))
{
    var sw = Stopwatch.StartNew();
    var preview = RawPreviewExtractor.Find(rawFile, RawFormat.Other);
    sw.Stop();
    var bytes = preview.Found ? RawPreviewExtractor.Extract(rawFile, preview) : null;
    W($"{Path.GetFileName(rawFile)}: found={preview.Found} pos={preview.Offset} len={preview.Length} {preview.Width}x{preview.Height} method={preview.Method} bytes={bytes?.Length} {sw.Elapsed.TotalMilliseconds:0.0}ms");
}

Directory.CreateDirectory(Path.Combine(repoRoot, "tools", "MetaSmoke", "out"));
var reportPath = Path.Combine(repoRoot, "tools", "MetaSmoke", "out", "meta-report.txt");
File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
W();
W($"报告写入: {reportPath}");
return failures.Count == 0 ? 0 : 1;

static void CheckStr(JsonElement e, string prop, string? actual, Action<string> fail, bool strict)
{
    if (!e.TryGetProperty(prop, out var el) || el.GetString() is not { Length: > 0 } expected) return;
    if (strict)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            fail($"{prop} 期望 '{expected}' 实际 '{actual}'");
    }
    else if (!LooseEqual(actual, expected))
        fail($"{prop} 期望 '{expected}' 实际 '{actual}'");
}

static void LooseColorCheck(JsonElement e, string prop, string? actual, Action<string> fail)
{
    if (!e.TryGetProperty(prop, out var el) || el.GetString() is not { Length: > 0 } expected) return;
    if (!LooseEqual(actual, expected))
        fail($"{prop} 期望 '{expected}' 实际 '{actual}'");
}

static bool LooseEqual(string? actual, string expected)
{
    if (actual is null) return false;
    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
    var a = Normalize(actual);
    var b = Normalize(expected);
    return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
}

static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "WORKSPLIT.md")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}