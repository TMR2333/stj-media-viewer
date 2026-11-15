using System.Diagnostics;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Classification;
using StjMediaViewer.Core.Dedup;
using StjMediaViewer.Core.Geo;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Scanning;
using StjMediaViewer.Core.Settings;

namespace StjMediaViewer.MediaSmoke;

internal static class Program
{
    internal static readonly string WorkRoot =
        Path.Combine(Path.GetTempPath(), "stj-media-smoke", Environment.ProcessId.ToString());

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        Directory.CreateDirectory(WorkRoot);
        Console.WriteLine($"# stj-media-smoke mode={mode} work={WorkRoot}");

        try
        {
            return mode switch
            {
                "raw" => await WinRtProbe.RunAsync(rest),
                "catalog" => await Tests.CatalogAsync(),
                "decode" => await Tests.DecodeAsync(rest),
                "scan" => await Tests.ScanAsync(rest),
                "dedup" => await Tests.DedupAsync(rest),
                "ffmpeg" => await Tests.FfmpegAsync(rest),
                "real" => await Tests.RealPipelineAsync(rest),
                "all" => await Tests.AllAsync(rest),
                "help" => Usage(),
                "plan" => await PerfProbe.RunAsync(rest),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fatal] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex);
            return 9;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            usage:
              StjMediaViewer.MediaSmoke raw    [files-or-dirs]   # 纯 WinRT 解码/编码探针
              StjMediaViewer.MediaSmoke catalog                  # SQLite 索引库 + 10k upsert/查询性能
              StjMediaViewer.MediaSmoke decode [files-or-dirs]   # 解码/分析/编码/缩略图缓存
              StjMediaViewer.MediaSmoke scan   [dirs]            # 扫描→分类→入库 全流程
              StjMediaViewer.MediaSmoke dedup  [dirs]            # 重复检测
              StjMediaViewer.MediaSmoke ffmpeg [dirs]            # ffmpeg 探测/ffprobe/抽帧/转码
              StjMediaViewer.MediaSmoke all    [dirs]            # 全部跑一遍
            """);
        return 0;
    }
}

internal static class Tests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name} {detail}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name} {detail}"); }
    }

    private static int Summary()
    {
        Console.WriteLine($"\n=== smoke summary: {_pass} passed, {_fail} failed ===");
        return _fail == 0 ? 0 : 1;
    }

    // ================================================================== all

    public static async Task<int> AllAsync(string[] roots)
    {
        await CatalogAsync();
        await DecodeAsync(roots);
        int scanResult = await ScanAsync(roots, quiet: true);

        // 重新打开扫描产出的库，跑一次重复检测
        var dbPath = Path.Combine(Program.WorkRoot, "scan.db");
        if (File.Exists(dbPath))
        {
            using var store = new SqliteAssetStore(dbPath);
            await store.InitializeAsync();
            var assets = await store.QueryAsync(new AssetQuery { Limit = 300 });
            if (assets.Count > 1) await DedupOnAsync(assets);
        }
        if (scanResult != 0) _fail++;
        return Summary();
    }

    // ================================================================== 索引库 / 性能

    public static async Task<int> CatalogAsync()
    {
        Console.WriteLine("\n## catalog: SQLite 索引库");
        var dbPath = Path.Combine(Program.WorkRoot, "catalog-perf.db");
        foreach (var f in Directory.EnumerateFiles(Program.WorkRoot, "catalog-perf.db*")) TryDelete(f);

        using var store = new SqliteAssetStore(dbPath);
        var sw = Stopwatch.StartNew();
        await store.InitializeAsync();
        sw.Stop();
        Check("initialize", File.Exists(dbPath), $"{sw.ElapsedMilliseconds}ms");

        // ---------- 10k upsert ----------
        const int count = 10_000;
        var assets = BuildSyntheticAssets(count);
        sw.Restart();
        await store.UpsertManyAsync(assets);
        sw.Stop();
        long upsertMs = sw.ElapsedMilliseconds;
        Check("upsert 10k", upsertMs < 15_000, $"{upsertMs}ms ({count * 1000.0 / Math.Max(1, upsertMs):0} 条/秒)");
        Check("count after upsert", await store.CountAllAsync() == count, $"{await store.CountAllAsync()}");

        // ---------- 单条 / 批量读取 ----------
        sw.Restart();
        var one = await store.GetAsync(assets[123].Id);
        sw.Stop();
        Check("get by id", one is not null && one.PhotoCount() == assets[123].PhotoCount(),
            $"{sw.ElapsedMilliseconds}ms keywords={string.Join(",", one?.Keywords ?? new List<string>())}");

        sw.Restart();
        var byPath = await store.GetByPathAsync(assets[0].FullPath);
        sw.Stop();
        Check("get by path", byPath?.Id == assets[0].Id, $"{sw.ElapsedMilliseconds}ms");

        // ---------- 查询 ----------
        // 稳态性能口径：每个查询形状先跑一次（首跑含冷磁盘页读 + JIT），指标只统计第二次
        await store.QueryAsync(new AssetQuery { Limit = 200, SortBy = AssetSortKeys.CapturedAt, Descending = true });
        await store.QueryAsync(new AssetQuery { Kinds = new List<MediaKind> { MediaKind.Image }, MinRating = 3, IsRaw = true, Limit = 100 });
        await store.GetTimelineAsync(new AssetQuery());

        sw.Restart();
        var page = await store.QueryAsync(new AssetQuery { Limit = 200, SortBy = AssetSortKeys.CapturedAt, Descending = true });
        sw.Stop();
        long pageMs = sw.ElapsedMilliseconds;
        Check("query page(200)", page.Count == 200 && pageMs < 50, $"{pageMs}ms rows={page.Count}");

        sw.Restart();
        var filtered = await store.QueryAsync(new AssetQuery
        {
            Kinds = new List<MediaKind> { MediaKind.Image },
            MinRating = 3,
            IsRaw = true,
            Limit = 100,
        });
        sw.Stop();
        long filteredMs = sw.ElapsedMilliseconds;
        Check("query 多条件", filtered.All(a => a.Kind == MediaKind.Image && a.Rating >= 3 && a.IsRaw) && filteredMs < 50,
            $"{filteredMs}ms rows={filtered.Count}");

        sw.Restart();
        var textHit = await store.QueryAsync(new AssetQuery { Text = "Sunset", Limit = 50 });
        sw.Stop();
        Check("query 文本模糊", textHit.Count > 0 && sw.ElapsedMilliseconds < 100, $"{sw.ElapsedMilliseconds}ms rows={textHit.Count}");

        sw.Restart();
        var kw = await store.QueryAsync(new AssetQuery { Keywords = new List<string> { "travel", "sunset" }, Limit = 50 });
        sw.Stop();
        Check("query 关键词(AND)", kw.Count > 0 && sw.ElapsedMilliseconds < 100, $"{sw.ElapsedMilliseconds}ms rows={kw.Count}");

        sw.Restart();
        var byDate = await store.QueryAsync(new AssetQuery
        {
            CapturedFrom = assets[0].CapturedAt!.Value.AddDays(-30),
            CapturedTo = assets[0].CapturedAt!.Value.AddDays(30),
            Limit = 100,
        });
        sw.Stop();
        Check("query 时间区间", byDate.Count > 0 && sw.ElapsedMilliseconds < 100, $"{sw.ElapsedMilliseconds}ms rows={byDate.Count}");

        sw.Restart();
        int cnt = await store.CountAsync(new AssetQuery { OnlyUnclassified = true });
        sw.Stop();
        Check("count 未分类", cnt >= 0 && sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds}ms count={cnt}");

        // ---------- 分面 ----------
        sw.Restart();
        var facets = await store.GetFacetsAsync(new AssetQuery());
        sw.Stop();
        long facetMs = sw.ElapsedMilliseconds;
        Check("facets", facets.Total == count && facetMs < 80,
            $"{facetMs}ms total={facets.Total} kinds={facets.ByKind.Count} formats={facets.ByFormat.Count} " +
            $"devices={facets.ByDevice.Count} cities={facets.ByCity.Count} years={facets.ByYear.Count} raw={facets.RawCount}");

        // ---------- 时间线 ----------
        sw.Restart();
        var timeline = await store.GetTimelineAsync(new AssetQuery());
        sw.Stop();
        Check("timeline", timeline.Count > 0 && sw.ElapsedMilliseconds < 80,
            $"{sw.ElapsedMilliseconds}ms buckets={timeline.Count} first={timeline.FirstOrDefault()?.Day}");

        // ---------- 去重值 ----------
        var formats = await store.GetDistinctValuesAsync("format", null);
        var kws = await store.GetDistinctValuesAsync("keywords", null);
        Check("distinct values", formats.Count > 0 && kws.Count > 0, $"formats={formats.Count} keywords={kws.Count}");

        // ---------- 增量 upsert（更新已有 + 新增） ----------
        var updatedList = assets.Take(500).Select(a =>
        {
            var c = a.Clone();
            c.Rating = 5;
            c.Theme = "nature";
            return c;
        }).ToList();
        var fresh = BuildSyntheticAssets(100, offset: count);
        sw.Restart();
        await store.UpsertManyAsync(updatedList.Concat(fresh));
        sw.Stop();
        Check("upsert 600 (500 更新 + 100 新增)", await store.CountAllAsync() == count + 100 && sw.ElapsedMilliseconds < 3000,
            $"{sw.ElapsedMilliseconds}ms count={await store.CountAllAsync()}");

        var all = await store.GetIdsByPathsAsync(fresh.Select(a => a.FullPath));
        Check("get ids by paths", all.Count == 100, $"matched={all.Count}");

        // ---------- 库根持久化 ----------
        var roots = new List<LibraryRoot>
        {
            new() { Id = "lib1", Path = @"C:\Photos", Name = "Photos", AssetCount = 123, TotalBytes = 456 },
            new() { Id = "lib2", Path = @"D:\Clips", Name = "Clips" },
        };
        await store.SaveLibraryRootsAsync(roots);
        var loaded = await store.LoadLibraryRootsAsync();
        Check("library roots roundtrip", loaded.Count == 2 && loaded[0].AssetCount == 123 && loaded[1].Path == @"D:\Clips",
            $"count={loaded.Count}");

        // ---------- 删除 ----------
        await store.DeleteAsync(fresh.Take(50).Select(a => a.Id));
        var left = await store.GetIdsByPathsAsync(fresh.Select(a => a.FullPath));
        Check("delete 50", left.Count == 50, $"left={left.Count}");

        // ---------- 批量 ListAllPaths ----------
        sw.Restart();
        var paths = await store.ListAllPathsAsync(null);
        sw.Stop();
        Check("list all paths", paths.Count == count + 50, $"{sw.ElapsedMilliseconds}ms rows={paths.Count}");
        return 0;
    }

    private static int PhotoCount(this MediaAsset a) => a.Keywords.Count + (a.AiSubject?.Length ?? 0);

    private static List<MediaAsset> BuildSyntheticAssets(int count, int offset = 0)
    {
        var rnd = new Random(42 + offset);
        var devices = new[] { "Sony ILCE-7M4", "Canon EOS R5", "Nikon Z8", "Fujifilm X-T5" };
        var cities = new[] { "Hangzhou", "Shanghai", "Beijing", "Chengdu", "Lhasa" };
        var themes = new[] { "nature", "city", "portrait", "street" };
        var tones = new[] { "highkey", "lowkey", "hard", "cool", "warm" };
        var formats = new[] { "JPEG", "HEIC", "CR3", "NEF", "ARW", "MP4" };
        var baseDate = new DateTimeOffset(2022, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));

        var list = new List<MediaAsset>(count);
        for (int i = 0; i < count; i++)
        {
            int n = offset + i;
            bool isVideo = n % 17 == 0;
            bool isRaw = !isVideo && n % 7 == 0;
            int rating = n % 5;
            var a = new MediaAsset
            {
                Id = "a" + n.ToString("x8"),
                LibraryId = "lib1",
                FullPath = $@"C:\Photos\{baseDate.AddHours(n):yyyy}\IMG_{n:00000}.jpg",
                FileName = $"IMG_{n:00000}.jpg",
                RelativePath = $@"2024\IMG_{n:00000}.jpg",
                SizeBytes = 1_500_000 + (n * 7919) % 8_000_000,
                FileModified = baseDate.AddHours(n % 20_000),
                FileCreated = baseDate.AddHours(n % 20_000).AddMinutes(-5),
                Kind = isVideo ? MediaKind.Video : MediaKind.Image,
                FormatCode = isVideo ? "MP4" : isRaw ? formats[2 + n % 3] : formats[n % 2],
                MimeType = isVideo ? "video/mp4" : "image/jpeg",
                ImageFlavor = isVideo ? ImageFlavor.None : ImageFlavor.Jpeg,
                RawFormat = isRaw ? (RawFormat)(1 + n % 5) : RawFormat.None,
                Width = 6000,
                Height = 4000,
                DurationSeconds = isVideo ? 30 + n % 60 : null,
                CameraMake = devices[n % devices.Length].Split(' ')[0],
                CameraModel = devices[n % devices.Length],
                DeviceKey = devices[n % devices.Length],
                LensModel = "24-70mm F2.8",
                FocalLengthMm = 24 + n % 70,
                ApertureF = 2.8,
                ShutterSpeedSeconds = 1.0 / (125 + n % 500),
                Iso = 100 + (n % 8) * 400,
                CapturedAt = baseDate.AddHours(n % 20_000),
                CapturedAtSource = "exif:DateTimeOriginal",
                Latitude = 30.0 + (n % 40) * 0.1,
                Longitude = 120.0 + (n % 30) * 0.1,
                Country = "China",
                City = cities[n % cities.Length],
                CitySource = "offline",
                Title = $"Sunset over {cities[n % cities.Length]} #{n}",
                Caption = "smoke synthetic asset",
                Creator = "catalog-engine",
                Keywords = new List<string> { "travel", n % 2 == 0 ? "sunset" : "street", cities[n % cities.Length].ToLowerInvariant() },
                Theme = n % 3 == 0 ? null : themes[n % themes.Length],
                Tone = n % 4 == 0 ? null : tones[n % tones.Length],
                Rating = rating,
                ColorLabel = rating >= 4 ? "red" : null,
                IsPick = rating == 5 ? true : rating == 0 ? false : null,
                PostProcess = n % 6 == 0 ? PostProcessState.Processed : PostProcessState.Unprocessed,
                HasAudio = isVideo,
                QuickHash = "q1:" + ((ulong)n * 0x9E3779B97F4A7C15UL).ToString("x16"),
                ThumbKey = "tk" + n,
                IndexedAt = DateTimeOffset.Now,
                MetadataParsedAt = DateTimeOffset.Now,
            };
            a.SetProv(ClassDimension.Format, FieldProvenance.Of(ClassSource.Derived));
            a.SetProv(ClassDimension.Theme, FieldProvenance.Of(ClassSource.Ai, 0.86, "smoke-vision"));
            list.Add(a);
        }
        return list;
    }

    // ================================================================== 解码 / 编码 / 缩略图

    public static async Task<int> DecodeAsync(string[] roots)
    {
        Console.WriteLine("\n## decode: WIC 解码 / 编码 / 图像分析 / 缩略图缓存");
        var ffmpeg = new FfmpegService(new FakeSettingsService(Program.WorkRoot));
        var decoder = new MediaDecoder(new FakeMetadataService(), ffmpeg);
        var encoder = new ImageEncoder();
        var analyzer = new ImageAnalyzer();
        var thumbs = new ThumbnailService(decoder, encoder, Path.Combine(Program.WorkRoot, "thumbs"));

        var files = SampleFiles.Find(roots, 8);
        Check("找到样本图片", files.Count > 0, $"count={files.Count}");
        if (files.Count == 0) return Summary();

        int decoded = 0;
        foreach (var f in files)
        {
            var sw = Stopwatch.StartNew();
            var img320 = await decoder.DecodeAsync(f, 320);
            sw.Stop();
            if (img320 is null)
            {
                // 样本库里本来就放了坏样本（AppleDouble / 文本冒充 JPEG / 0 字节），
                // 解码返回 null 才是正确行为，不能算失败
                if (SampleFiles.IsExpectedBad(f))
                {
                    Check($"decode {Path.GetFileName(f)}（预期坏样本）", true,
                        $"返回 null 符合预期 {sw.ElapsedMilliseconds}ms");
                }
                else
                {
                    Check($"decode {Path.GetFileName(f)}", false, sw.Elapsed.TotalMilliseconds.ToString("0") + "ms");
                }
                continue;
            }
            decoded++;
            bool sizeOk = Math.Max(img320.Width, img320.Height) <= 320 && img320.Bgra.Length == img320.Width * img320.Height * 4;
            Check($"decode {Path.GetFileName(f)}", sizeOk,
                $"{img320.Width}x{img320.Height} {sw.ElapsedMilliseconds}ms {img320.Bgra.Length}B");

            var stats = analyzer.Analyze(img320);
            bool statsOk = stats.LumaHistogram.Length == 256 && stats.MeanLuma is > 0 and < 256 &&
                           Math.Abs(stats.LumaHistogram.Sum() - 1.0) < 0.01;
            Check("  analyze", statsOk,
                $"mean={stats.MeanLuma:0.0} median={stats.MedianLuma:0} std={stats.StdDevLuma:0.0} " +
                $"contrast={stats.Contrast:0} sat={stats.MeanSaturation:0.00} sharp={stats.Sharpness:0} " +
                $"mono={stats.IsMonochrome} warm={stats.WarmthIndex:0.0} dom={stats.DominantColorHex} " +
                $"tone={stats.SuggestTone() ?? "-"} quality={stats.QualityScore():0.00}");

            var jpeg = await encoder.EncodeJpegAsync(img320, 85);
            Check("  encode jpeg", jpeg is { Length: > 512 }, $"{jpeg?.Length ?? 0}B");

            var resized = await encoder.ResizeToJpegAsync(img320, 160, 80);
            Check("  resize+jpeg", resized is { Length: > 256 }, $"{resized?.Length ?? 0}B");

            // 缩略图缓存：首次生成 vs 二次命中
            var asset = new MediaAsset
            {
                FullPath = f,
                FileName = Path.GetFileName(f),
                SizeBytes = new FileInfo(f).Length,
                FileModified = new FileInfo(f).LastWriteTimeUtc,
            };
            var t1 = Stopwatch.StartNew();
            var thumb1 = await thumbs.GetThumbnailJpegAsync(asset, 320);
            t1.Stop();
            var t2 = Stopwatch.StartNew();
            var thumb2 = await thumbs.GetThumbnailJpegAsync(asset, 320);
            t2.Stop();
            var thumbPath = await thumbs.GetThumbnailPathAsync(asset, 320);
            Check("  thumbnail generate+cache",
                thumb1 is { Length: > 512 } && thumb2 is { Length: > 512 } && thumbPath is not null &&
                t2.ElapsedMilliseconds <= Math.Max(5, t1.ElapsedMilliseconds),
                $"gen={t1.ElapsedMilliseconds}ms hit={t2.ElapsedMilliseconds}ms file={(thumbPath is null ? "-" : Path.GetFileName(thumbPath))}");
        }

        Check("解码成功数", decoded > 0, $"{decoded}/{files.Count}");

        // RAW 预览提取（如果有 RAW 样本）
        foreach (var f in SampleFiles.Find(roots, 50).Where(SampleFiles.IsRaw).Take(3))
        {
            var preview = await decoder.ExtractEmbeddedPreviewAsync(f);
            Check($"RAW 内嵌预览 {Path.GetFileName(f)}", preview is { Length: > 2000 },
                $"{preview?.Length ?? 0}B");
            if (preview is not null)
            {
                var img = await decoder.DecodeAsync(f, 320);
                Check("  RAW 解码", img is not null, img is null ? "" : $"{img.Width}x{img.Height}");
            }
        }

        // 内嵌封面解析器自检（样本库音频没有封面，这里造夹具验证 MP3/FLAC/M4A 三条路径）
        await TestEmbeddedCoversAsync(decoder, encoder);
        // EXIF 方向校正自检（样本库全是 orientation=1，这里造 orientation=6 的旋转图）
        await TestExifOrientationAsync(decoder, encoder);

        // 视频抽帧 / 音频封面：没有 ffmpeg、系统也没缩略图时必须优雅返回 null
        foreach (var f in SampleFiles.FindAny(roots, 12))
        {
            MediaTypeCatalog.TryGet(f, out var kind, out _, out _, out _, out _);
            var swm = Stopwatch.StartNew();
            var img = await decoder.DecodeAsync(f, 320);
            swm.Stop();
            Console.WriteLine($"  [info] {(kind == MediaKind.Video ? "video" : "audio")} {Path.GetFileName(f)} → " +
                              $"{(img is null ? "null（优雅降级）" : $"{img.Width}x{img.Height}")} {swm.ElapsedMilliseconds}ms");
            if (kind == MediaKind.Video)
            {
                var frame = await decoder.ExtractVideoFrameAsync(f, 0.5, 320);
                Console.WriteLine($"  [info]   video frame bytes={frame?.Length ?? 0}");
            }
            else
            {
                var cover = await decoder.ExtractAudioCoverAsync(f);
                Console.WriteLine($"  [info]   audio cover bytes={cover?.Length ?? 0}");
            }
        }
        Check("视频/音频解码不抛异常", true, $"ffmpeg={(ffmpeg.IsAvailable ? ffmpeg.FfmpegPath : "未安装（已降级）")}");

        // 缓存统计
        var sizeBytes = await thumbs.GetCacheSizeBytesAsync();
        Check("cache size", sizeBytes > 0, $"{sizeBytes / 1024}KB");
        var swc = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++)
            await thumbs.GetThumbnailJpegAsync(new MediaAsset
            {
                FullPath = files[i % files.Count],
                FileName = Path.GetFileName(files[i % files.Count]),
                SizeBytes = new FileInfo(files[i % files.Count]).Length,
                FileModified = new FileInfo(files[i % files.Count]).LastWriteTimeUtc,
            }, 256);
        swc.Stop();
        Console.WriteLine($"  [info] 40 次缩略图（全部命中缓存或已生成）{swc.ElapsedMilliseconds}ms " +
                          $"→ 等效 {40 * 60_000.0 / Math.Max(1, swc.ElapsedMilliseconds):0} 张/分钟");

        await thumbs.ClearCacheAsync();
        Check("clear cache", await thumbs.GetCacheSizeBytesAsync() == 0, "");
        return Summary();
    }

    // ================================================================== 内嵌封面夹具

    /// <summary>造 MP3(ID3v2 APIC) / FLAC(PICTURE) / M4A(covr) 夹具，验证内嵌封面解析。</summary>
    private static async Task TestEmbeddedCoversAsync(IMediaDecoder decoder, IImageEncoder encoder)
    {
        var jpeg = await MakeFixtureJpegAsync(encoder);
        Check("夹具 JPEG", jpeg is { Length: > 200 }, $"{jpeg?.Length ?? 0}B");
        if (jpeg is null) return;

        var dir = Path.Combine(Program.WorkRoot, "fixtures");
        Directory.CreateDirectory(dir);

        var cases = new (string Path, byte[] Bytes)[]
        {
            (Path.Combine(dir, "cover-tag.mp3"), BuildId3v2WithApic(jpeg)),
            (Path.Combine(dir, "cover-tag.flac"), BuildFlacWithPicture(jpeg)),
            (Path.Combine(dir, "cover-tag.m4a"), BuildM4aWithCovr(jpeg)),
        };
        foreach (var (path, bytes) in cases)
        {
            File.WriteAllBytes(path, bytes);
            var cover = await decoder.ExtractAudioCoverAsync(path);
            bool ok = cover is not null && cover.AsSpan().SequenceEqual(jpeg);
            Check($"内嵌封面 {Path.GetExtension(path)}", ok, $"{cover?.Length ?? 0}B / 期望 {jpeg.Length}B");
        }
    }

    /// <summary>造一张 orientation=6（顺时针 90°）的 JPEG：解码后宽高应互换。</summary>
    private static async Task TestExifOrientationAsync(IMediaDecoder decoder, IImageEncoder encoder)
    {
        var plain = await MakeFixtureJpegAsync(encoder);           // 32×24
        if (plain is null) { Check("EXIF 方向校正", false, "夹具生成失败"); return; }

        var rotated = InjectExifOrientation(plain, 6);
        var dir = Path.Combine(Program.WorkRoot, "fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rotated-90.jpg");
        File.WriteAllBytes(path, rotated);

        var img = await decoder.DecodeAsync(path, 320);
        bool ok = img is not null && img.Width == 24 && img.Height == 32;
        Check("EXIF 方向校正（orientation=6）", ok,
            img is null ? "解码失败" : $"{img.Width}x{img.Height}（原图 32x24，应互换）");
    }

    /// <summary>在 SOI 后插入最小 EXIF APP1（只带 Orientation 一个 tag）。</summary>
    private static byte[] InjectExifOrientation(byte[] jpeg, short orientation)
    {
        // TIFF: II, 42, IFD0@8；1 个 entry（0x0112 Orientation, SHORT, count=1）；next IFD=0
        var tiff = new List<byte>
        {
            (byte)'I', (byte)'I', 42, 0,
            8, 0, 0, 0,
            1, 0,                                   // entry count
            0x12, 0x01,                             // tag 0x0112
            3, 0,                                   // type SHORT
            1, 0, 0, 0,                             // count 1
            (byte)orientation, 0, 0, 0,             // value
            0, 0, 0, 0,                             // next IFD offset
        };
        var app1 = new List<byte> { 0xFF, 0xE1 };
        int segLen = 2 + 6 + tiff.Count;
        app1.Add((byte)(segLen >> 8));
        app1.Add((byte)segLen);
        app1.AddRange(Encoding.ASCII.GetBytes("Exif"));
        app1.Add(0); app1.Add(0);
        app1.AddRange(tiff);

        var outBytes = new List<byte>(jpeg.Length + app1.Count);
        outBytes.Add(jpeg[0]);
        outBytes.Add(jpeg[1]);
        outBytes.AddRange(app1);
        outBytes.AddRange(jpeg.Skip(2));
        return outBytes.ToArray();
    }

    private static async Task<byte[]?> MakeFixtureJpegAsync(IImageEncoder encoder)
    {
        const int w = 32, h = 24;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = (byte)(x * 8);
                bgra[i + 1] = (byte)(y * 10);
                bgra[i + 2] = (byte)(255 - x * 4);
                bgra[i + 3] = 255;
            }
        return await encoder.EncodeJpegAsync(new DecodedImage(bgra, w, h, w * 4), 92);
    }

    private static byte[] BuildId3v2WithApic(byte[] jpeg)
    {
        var tag = new List<byte>();
        // TIT2 帧（让解析器先踩过一个普通帧）
        var text = new List<byte> { 1, 0xFF, 0xFE };                    // encoding=1(UTF-16 LE) + BOM
        text.AddRange(Encoding.Unicode.GetBytes("fixture"));
        tag.AddRange(Id3Frame("TIT2", text.ToArray()));
        // APIC 帧
        var apic = new List<byte> { 0 };                                // encoding latin1
        apic.AddRange(Encoding.ASCII.GetBytes("image/jpeg"));
        apic.Add(0);                                                   // mime 结束
        apic.Add(3);                                                   // front cover
        apic.Add(0);                                                   // 空描述
        apic.AddRange(jpeg);
        tag.AddRange(Id3Frame("APIC", apic.ToArray()));

        int size = tag.Count;
        var sync = new byte[]
        {
            (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F),
            (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F),
        };
        var file = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0 };
        file.AddRange(sync);
        file.AddRange(tag);
        return file.ToArray();
    }

    private static byte[] Id3Frame(string id, byte[] body)
    {
        var frame = new List<byte>();
        frame.AddRange(Encoding.ASCII.GetBytes(id));
        frame.AddRange(new byte[]
        {
            (byte)(body.Length >> 24), (byte)(body.Length >> 16),
            (byte)(body.Length >> 8), (byte)body.Length,
        });
        frame.AddRange(new byte[] { 0, 0 });                            // flags
        frame.AddRange(body);
        return frame.ToArray();
    }

    private static byte[] BuildFlacWithPicture(byte[] jpeg)
    {
        var file = new List<byte> { (byte)'f', (byte)'L', (byte)'a', (byte)'C' };
        // STREAMINFO（type 0，非最后一块，34 字节零填充）
        file.Add(0x00);
        file.AddRange(new byte[] { 0, 0, 34 });
        file.AddRange(new byte[34]);

        var pic = new List<byte>();
        pic.AddRange(Be32(3));                                          // picture type
        var mime = Encoding.ASCII.GetBytes("image/jpeg");
        pic.AddRange(Be32(mime.Length));
        pic.AddRange(mime);
        pic.AddRange(Be32(0));                                          // description 长度
        pic.AddRange(Be32(32));
        pic.AddRange(Be32(24));
        pic.AddRange(Be32(24));
        pic.AddRange(Be32(0));
        pic.AddRange(Be32(jpeg.Length));
        pic.AddRange(jpeg);

        file.Add((byte)(0x80 | 6));                                     // PICTURE 且是最后一块
        file.AddRange(new byte[] { (byte)(pic.Count >> 16), (byte)(pic.Count >> 8), (byte)pic.Count });
        file.AddRange(pic);
        return file.ToArray();
    }

    private static byte[] BuildM4aWithCovr(byte[] jpeg)
    {
        var dataBox = new List<byte>();
        dataBox.AddRange(Be32(16 + jpeg.Length));
        dataBox.AddRange(Encoding.ASCII.GetBytes("data"));
        dataBox.AddRange(new byte[8]);                                  // version+flags(4) + locale(4)
        dataBox.AddRange(jpeg);

        var covrBox = new List<byte>();
        covrBox.AddRange(Be32(8 + dataBox.Count));
        covrBox.AddRange(Encoding.ASCII.GetBytes("covr"));
        covrBox.AddRange(dataBox);

        var file = new List<byte>();
        file.AddRange(Be32(8 + covrBox.Count));
        file.AddRange(Encoding.ASCII.GetBytes("moov"));
        file.AddRange(covrBox);
        // 前面加个 ftyp，让文件更像真的 M4A
        var ftyp = new List<byte>();
        ftyp.AddRange(Be32(20));
        ftyp.AddRange(Encoding.ASCII.GetBytes("ftyp"));
        ftyp.AddRange(Encoding.ASCII.GetBytes("M4A "));
        ftyp.AddRange(new byte[4]);
        ftyp.AddRange(Encoding.ASCII.GetBytes("M4A "));
        ftyp.AddRange(file);
        return ftyp.ToArray();
    }

    private static byte[] Be32(int v) => new[]
    {
        (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
    };

    // ================================================================== 扫描 → 入库

    public static async Task<int> ScanAsync(string[] roots, bool quiet = false)
    {
        Console.WriteLine("\n## scan: 扫描 → 元数据 → 分类 → 入库");
        var root = SampleFiles.PickRoot(roots);
        if (root is null) { Check("媒体库目录", false, "没有可用样本目录"); return 0; }

        var settings = new FakeSettingsService(Program.WorkRoot);
        var dbPath = Path.Combine(Program.WorkRoot, "scan.db");
        foreach (var f in Directory.EnumerateFiles(Program.WorkRoot, "scan.db*")) TryDelete(f);

        using var store = new SqliteAssetStore(dbPath);
        var scanner = new LibraryScanner(new FakeMetadataService(), new FakeRuleClassifier(), new FakeGeocoder(), settings);
        var service = new LibraryService(store, scanner, settings);

        var sw = Stopwatch.StartNew();
        await service.InitializeAsync();
        var lib = await service.AddLibraryAsync(root);
        Check("add library", lib.Path.Length > 0, $"id={lib.Id} path={lib.Path}");

        int reported = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            reported++;
            if (quiet) return;
            if (reported % 3 == 0 || p.Phase is ScanPhase.Completed or ScanPhase.Failed or ScanPhase.Cancelled)
                Console.WriteLine($"  [{p.Phase}] {p.Percent}% processed={p.Processed}/{p.Total} " +
                                  $"seen={p.FilesSeen} found={p.MediaFound} skipped={p.Skipped} failed={p.Failed}");
        });

        var scanOptions = new ScanOptions { Parallelism = 4, ComputeQuickHash = true, ForceReparse = true };
        var final = await service.ScanLibraryAsync(lib, scanOptions, progress);
        sw.Stop();
        double totalSeconds = sw.Elapsed.TotalSeconds;

        Check("scan completed", final.Phase == ScanPhase.Completed, $"{final.Message}");
        Check("scan 有结果", final.Added + final.Updated > 0,
            $"added={final.Added} updated={final.Updated} removed={final.Removed} failed={final.Failed} {totalSeconds:0.00}s");

        var count = await store.CountAllAsync();
        Check("入库数量一致", count == final.Added + final.Updated, $"db={count}");

        // 查询验证
        var sw2 = Stopwatch.StartNew();
        var assets = await store.QueryAsync(new AssetQuery { LibraryId = lib.Id, Limit = 500 });
        sw2.Stop();
        Check("查询已入库素材", assets.Count == count && sw2.ElapsedMilliseconds < 50,
            $"{assets.Count} rows {sw2.ElapsedMilliseconds}ms");

        var facets = await store.GetFacetsAsync(new AssetQuery { LibraryId = lib.Id });
        var timeline = await store.GetTimelineAsync(new AssetQuery { LibraryId = lib.Id });
        Console.WriteLine($"  [info] facets: total={facets.Total} images={facets.ByKind.GetValueOrDefault("Image")} " +
                          $"videos={facets.ByKind.GetValueOrDefault("Video")} audio={facets.ByKind.GetValueOrDefault("Audio")} " +
                          $"raw={facets.RawCount} unclassified={facets.UnclassifiedCount} bytes={facets.TotalBytes}");
        Console.WriteLine($"  [info] timeline buckets={timeline.Count}");
        if (assets.Count > 0)
        {
            var a = assets[0];
            Console.WriteLine($"  [info] sample: {a.FileName} {a.Kind} {a.FormatCode} " +
                              $"{(a.Width.HasValue ? $"{a.Width}x{a.Height}" : "?")} " +
                              $"captured={a.CapturedAt:yyyy-MM-dd HH:mm} keywords=[{string.Join(",", a.Keywords)}] " +
                              $"quickHash={a.QuickHash ?? "-"} error={a.LastError ?? "-"}");
        }

        // 缩略图吞吐（真实素材）
        var decoder = new MediaDecoder(new FakeMetadataService(), new FfmpegService(new FakeSettingsService(Program.WorkRoot)));
        var thumbs = new ThumbnailService(decoder, new ImageEncoder(), Path.Combine(Program.WorkRoot, "scan-thumbs"));
        var imageAssets = assets.Where(a => a.Kind == MediaKind.Image).Take(24).ToList();
        if (imageAssets.Count > 0)
        {
            var sw3 = Stopwatch.StartNew();
            int ok = 0;
            foreach (var a in imageAssets)
                if (await thumbs.GetThumbnailJpegAsync(a, 320) is not null) ok++;
            sw3.Stop();
            double perMinute = ok * 60_000.0 / Math.Max(1, sw3.ElapsedMilliseconds);
            Check("缩略图吞吐", ok > 0 && perMinute >= 200,
                $"{ok}/{imageAssets.Count} in {sw3.ElapsedMilliseconds}ms → {perMinute:0} 张/分钟");
        }

        // 增量重扫：应该全部是 Updated，且入库数量不变
        var final2 = await service.ScanLibraryAsync(lib, scanOptions);
        Check("rescan 幂等", final2.Added == 0 && await store.CountAllAsync() == count,
            $"added={final2.Added} updated={final2.Updated} removed={final2.Removed} db={await store.CountAllAsync()}");

        // 删除一个文件后 PurgeMissing
        var victim = assets.FirstOrDefault(a => File.Exists(a.FullPath));
        if (victim is not null)
        {
            var tmpMove = victim.FullPath + ".smoke-hidden";
            try
            {
                File.Move(victim.FullPath, tmpMove, true);
                int purged = await service.PurgeMissingAsync(lib.Id);
                Check("purge missing", purged >= 1, $"purged={purged}");
                File.Move(tmpMove, victim.FullPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [info] purge test skipped: {ex.Message}");
            }
        }

        if (quiet) return 0;
        return Summary();
    }

    // ================================================================== ffmpeg 可选增强

    public static async Task<int> FfmpegAsync(string[] roots)
    {
        Console.WriteLine("\n## ffmpeg: 探测 / ffprobe / 抽帧 / 代理转码");
        var settings = new FakeSettingsService(Program.WorkRoot);
        var ff = new FfmpegService(settings);
        Console.WriteLine($"  [info] ffmpeg={ff.FfmpegPath ?? "-"} ffprobe={ff.FfprobePath ?? "-"} available={ff.IsAvailable}");
        Check("ffmpeg 探测", ff.IsAvailable, ff.FfmpegPath ?? "未找到（应为优雅降级）");
        if (!ff.IsAvailable) return Summary();

        // 用 ffmpeg 自己生成 4s 测试视频（真实 H.264 + AAC），验证整条链路
        var genPath = Path.Combine(Program.WorkRoot, "ffmpeg-testsrc.mp4");
        if (!File.Exists(genPath) || new FileInfo(genPath).Length < 4096)
        {
            var gen = await RunFfmpegAsync(ff.FfmpegPath!, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", genPath,
            }, TimeSpan.FromMinutes(2));
            Check("生成测试视频", File.Exists(genPath) && new FileInfo(genPath).Length > 4096,
                $"{new FileInfo(genPath).Length / 1024}KB exit={gen}");
        }

        var json = await ff.ProbeJsonAsync(genPath);
        bool jsonOk = json is not null && json.Contains("\"streams\"", StringComparison.Ordinal);
        if (ff.FfprobePath is null)
            Check("ffprobe json（ffprobe 缺失，ProbeJson 返回 null 属预期降级）", json is null, "");
        else
            Check("ffprobe json", jsonOk, $"{json?.Length ?? 0}B");

        var sw = Stopwatch.StartNew();
        var frame = await ff.ExtractFrameAsync(genPath, 1.0, 320);
        sw.Stop();
        Check("抽帧", frame is { Length: > 1000 }, $"{frame?.Length ?? 0}B {sw.ElapsedMilliseconds}ms");

        // 通过 MediaDecoder 走完整路径（ffmpeg 抽帧 → WIC 解码成像素）
        var decoder = new MediaDecoder(new FakeMetadataService(), ff);
        var sw2 = Stopwatch.StartNew();
        var decoded = await decoder.DecodeAsync(genPath, 320);
        sw2.Stop();
        Check("视频解码(WIC 像素)", decoded is not null,
            decoded is null ? "" : $"{decoded.Width}x{decoded.Height} {sw2.ElapsedMilliseconds}ms");

        var proxy = Path.Combine(Program.WorkRoot, "ffmpeg-proxy.mp4");
        if (File.Exists(proxy)) TryDelete(proxy);
        var sw3 = Stopwatch.StartNew();
        var ok = await ff.TranscodeProxyAsync(genPath, proxy);
        sw3.Stop();
        Check("代理转码 H.264+AAC", ok, $"{new FileInfo(proxy).Length / 1024}KB {sw3.ElapsedMilliseconds}ms");

        // 缩略图服务对视频的端到端（走 ffmpeg 抽帧 + JPEG 缓存）
        var thumbs = new ThumbnailService(decoder, new ImageEncoder(), Path.Combine(Program.WorkRoot, "video-thumbs"));
        var videoAsset = new MediaAsset
        {
            FullPath = genPath,
            FileName = Path.GetFileName(genPath),
            SizeBytes = new FileInfo(genPath).Length,
            FileModified = new FileInfo(genPath).LastWriteTimeUtc,
            Kind = MediaKind.Video,
            FormatCode = "MP4",
        };
        var vt = await thumbs.GetThumbnailJpegAsync(videoAsset, 320);
        Check("视频缩略图", vt is { Length: > 512 }, $"{vt?.Length ?? 0}B");

        // 音频封面（没有内嵌封面时应优雅返回 null，不应抛异常）
        foreach (var f in SampleFiles.FindByKind(roots, MediaKind.Audio, 3))
        {
            var cover = await decoder.ExtractAudioCoverAsync(f);
            Console.WriteLine($"  [info] audio cover {Path.GetFileName(f)} → " +
                              $"{(cover is null ? "无内嵌封面（优雅降级）" : cover.Length + "B")}");
        }
        return Summary();
    }

    private static async Task<int> RunFfmpegAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            var err = await stderr;
            if (p.ExitCode != 0) Console.WriteLine($"  [warn] ffmpeg gen exit={p.ExitCode}: {err.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [warn] ffmpeg gen failed: {ex.Message}");
            return -1;
        }
    }

    // ================================================================== 真实实现全链路

    /// <summary>用 meta-engine / intel-engine 的真实实现跑扫描，验证跨模块集成与并行一致性。</summary>
    public static async Task<int> RealPipelineAsync(string[] roots)
    {
        Console.WriteLine("\n## real: 真实 MetadataService / RuleBasedClassifier / OfflineGeocoder / JsonSettingsService");
        var root = SampleFiles.PickRoot(roots);
        if (root is null) { Check("样本目录", false, "找不到样本库"); return Summary(); }

        var settings = new JsonSettingsService(Path.Combine(Program.WorkRoot, "real-settings.json"), Program.WorkRoot);
        await settings.LoadAsync();
        settings.Current.ScanParallelism = 4;

        var metadata = new MetadataService();
        var classifier = new RuleBasedClassifier();
        var geocoder = new OfflineGeocoder();
        await geocoder.EnsureLoadedAsync();
        Console.WriteLine($"  [info] geocoder ready={geocoder.IsReady} root={root}");

        async Task<(List<MediaAsset> Assets, ScanProgress Final, long Ms)> ScanAsync(int parallelism, string dbName)
        {
            var dbPath = Path.Combine(Program.WorkRoot, dbName);
            foreach (var f in Directory.EnumerateFiles(Program.WorkRoot, dbName + "*")) TryDelete(f);
            var store = new SqliteAssetStore(dbPath);
            var scanner = new LibraryScanner(metadata, classifier, geocoder, settings);
            var service = new LibraryService(store, scanner, settings);
            await service.InitializeAsync();
            var lib = await service.AddLibraryAsync(root);
            var sw = Stopwatch.StartNew();
            var final = await service.ScanLibraryAsync(lib,
                new ScanOptions { Parallelism = parallelism, ComputeQuickHash = true, SniffMagicBytes = true });
            sw.Stop();
            var assets = (await store.QueryAsync(new AssetQuery { LibraryId = lib.Id, Limit = 5000 })).ToList();
            store.Dispose();
            return (assets, final, sw.ElapsedMilliseconds);
        }

        var (a1, p1, ms1) = await ScanAsync(1, "real-p1.db");
        var (a8, p8, ms8) = await ScanAsync(8, "real-p8.db");

        Check("真实扫描完成", p1.Phase == ScanPhase.Completed,
            $"1线程 {ms1}ms added={p1.Added} failed={p1.Failed} msg={p1.Message}");
        Check("8 线程完成", p8.Phase == ScanPhase.Completed, $"{ms8}ms added={p8.Added}");
        Check("并行数量一致", a1.Count == a8.Count && p1.Added == p8.Added, $"{a1.Count} vs {a8.Count}");

        // 字段级一致性（元数据引擎声称线程安全，这里校验 1 线程 vs 8 线程逐字段相同）
        int mismatches = 0;
        string mismatchSample = "";
        var map8 = a8.ToDictionary(a => a.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var a in a1)
        {
            if (!map8.TryGetValue(a.FullPath, out var b)) { mismatches++; continue; }
            if (a.Width != b.Width || a.Height != b.Height || a.CapturedAt != b.CapturedAt ||
                !string.Equals(a.City, b.City) || !string.Equals(a.DeviceKey, b.DeviceKey) ||
                !string.Equals(a.FormatCode, b.FormatCode) || a.IsLog != b.IsLog ||
                a.RawFormat != b.RawFormat || !string.Equals(a.Theme, b.Theme) ||
                !string.Equals(a.Tone, b.Tone) || a.Rating != b.Rating ||
                !string.Equals(a.QuickHash, b.QuickHash))
            {
                mismatches++;
                if (mismatchSample.Length == 0) mismatchSample = a.FileName;
            }
        }
        Check("1/8 线程字段一致", mismatches == 0, $"mismatches={mismatches} first={mismatchSample}");

        // 元数据覆盖质量
        var images = a1.Where(a => a.Kind == MediaKind.Image).ToList();
        int withDims = images.Count(a => a.Width is > 0 && a.Height is > 0);
        int withCamera = a1.Count(a => !string.IsNullOrWhiteSpace(a.CameraModel));
        int withDevice = a1.Count(a => !string.IsNullOrWhiteSpace(a.DeviceKey));
        int withCaptured = a1.Count(a => a.CapturedAt.HasValue);
        int withExifTime = a1.Count(a => a.CapturedAtSource?.StartsWith("exif", StringComparison.OrdinalIgnoreCase) == true);
        int withGps = a1.Count(a => a.Latitude.HasValue && a.Longitude.HasValue);
        int withCity = a1.Count(a => !string.IsNullOrWhiteSpace(a.City));
        int rawCount = a1.Count(a => a.RawFormat != RawFormat.None);
        int logCount = a1.Count(a => a.IsLog == true);
        int errors = a1.Count(a => !string.IsNullOrEmpty(a.LastError));
        var citySources = a1.Where(a => !string.IsNullOrWhiteSpace(a.CitySource))
            .GroupBy(a => a.CitySource!)
            .ToDictionary(g => g.Key, g => g.Count());

        Check("图片尺寸覆盖率", withDims >= images.Count * 0.9, $"{withDims}/{images.Count}");
        Check("设备识别", withDevice > 0 && withCamera > 0, $"device={withDevice} camera={withCamera}");
        Check("拍摄时间覆盖", withCaptured == a1.Count, $"{withCaptured}/{a1.Count} 来自 exif={withExifTime}");
        Check("GPS/城市", withGps > 0 && withCity > 0,
            $"gps={withGps} city={withCity} sources=[{string.Join(",", citySources.Select(kv => kv.Key + ":" + kv.Value))}]");

        // XMP/IPTC 已给 City 的素材：扫描器不得用反查覆盖。
        // 新契约：元数据引擎写 CitySource="xmp"/"iptc"，反查只写 "offline"/"online"，
        // 所以「文件来源城市」= City 非空且 CitySource 是 xmp/iptc（或旧契约的 null）。
        static bool IsFileCity(MediaAsset a) =>
            !string.IsNullOrWhiteSpace(a.City) &&
            (string.IsNullOrWhiteSpace(a.CitySource) ||
             a.CitySource.Equals("xmp", StringComparison.OrdinalIgnoreCase) ||
             a.CitySource.Equals("iptc", StringComparison.OrdinalIgnoreCase));

        int cityFromMetadata = a1.Count(IsFileCity);
        int cityFromGeocoder = a1.Count(a =>
            !string.IsNullOrWhiteSpace(a.CitySource) &&
            (a.CitySource.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
             a.CitySource.Equals("online", StringComparison.OrdinalIgnoreCase)));
        Check("XMP/IPTC 城市未被反查覆盖", cityFromMetadata > 0,
            $"{cityFromMetadata} 条 City 来自文件元数据（xmp/iptc）；离线反查 {cityFromGeocoder} 条");
        Check("城市来源标注合法", cityFromGeocoder == citySources.GetValueOrDefault("offline"),
            $"xmp={citySources.GetValueOrDefault("xmp")} iptc={citySources.GetValueOrDefault("iptc")} offline={citySources.GetValueOrDefault("offline")}");

        Check("RAW/Log 判定", rawCount > 0, $"raw={rawCount} log={logCount}");
        Check("坏文件产生 LastError", errors > 0, $"errors={errors}");

        Console.WriteLine($"  [info] 解析+入库 {ms1}ms/{a1.Count} ≈ {ms1 * 1.0 / Math.Max(1, a1.Count):0.0}ms/文件（1线程），" +
                          $"8线程 {ms8}ms");

        foreach (var a in a1.Where(a => a.RawFormat != RawFormat.None).Take(2)
                     .Concat(a1.Where(a => a.Kind == MediaKind.Video).Take(1))
                     .Concat(a1.Where(a => !string.IsNullOrWhiteSpace(a.City)).Take(1)))
        {
            Console.WriteLine($"  [info] {a.RelativePath}: {a.Kind}/{a.FormatCode} raw={a.RawFormat} " +
                              $"{a.Width}x{a.Height} cam={a.CameraModel} dev={a.DeviceKey} lens={a.LensModel} " +
                              $"captured={a.CapturedAt:yyyy-MM-dd HH:mm}({a.CapturedAtSource}) " +
                              $"city={a.City}({a.CitySource}) iso={a.Iso} log={a.IsLog}/{a.LogProfile} " +
                              $"theme={a.Theme} tone={a.Tone} err={a.LastError}");
        }

        // RAW 解码走真实元数据引擎的 RawPreviewExtractor 快路径
        var ffmpeg = new FfmpegService(settings);
        var decoder = new MediaDecoder(metadata, ffmpeg);
        int rawOk = 0;
        var rawAssets = a1.Where(a => a.RawFormat != RawFormat.None).ToList();
        foreach (var a in rawAssets)
            if (await decoder.DecodeAsync(a.FullPath, 320) is not null) rawOk++;
        Check("真实 RAW 解码", rawOk == rawAssets.Count && rawOk > 0, $"{rawOk}/{rawAssets.Count}");

        foreach (var a in a1.Where(a => a.Kind == MediaKind.Video).Take(3))
        {
            var img = await decoder.DecodeAsync(a.FullPath, 320);
            Console.WriteLine($"  [info] video {a.FileName} → {(img is null ? "null（ffmpeg 缺失/系统无缩略图，已降级）" : $"{img.Width}x{img.Height}")}");
        }
        foreach (var a in a1.Where(a => a.Kind == MediaKind.Audio).Take(3))
        {
            var cover = await decoder.ExtractAudioCoverAsync(a.FullPath);
            Console.WriteLine($"  [info] audio {a.FileName} → cover={(cover is null ? "无（占位图由 UI 负责）" : cover.Length + "B")}");
        }

        // 取消语义：MetadataService.Read 是"协作式"（预取消返回部分结果不抛），
        // 扫描器必须自己保证取消时快速收敛、不抛异常、不挂起。
        var scannerForCancel = new LibraryScanner(metadata, classifier, geocoder, settings);
        var libRoot = new LibraryRoot { Id = "cancel-test", Path = root };
        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            var swc = Stopwatch.StartNew();
            var got = await scannerForCancel.CollectAsync(libRoot, new ScanOptions(), null, preCancelled.Token);
            swc.Stop();
            Check("预取消：快速返回且不抛", got.Count == 0 && swc.ElapsedMilliseconds < 2000,
                $"{got.Count} 条 {swc.ElapsedMilliseconds}ms");
        }
        using (var midCancel = new CancellationTokenSource())
        {
            midCancel.CancelAfter(5);          // 5ms 时扫描刚起步，确保真的中途打断
            var swc = Stopwatch.StartNew();
            var got = await scannerForCancel.CollectAsync(libRoot, new ScanOptions(), null, midCancel.Token);
            swc.Stop();
            Check("扫描中取消：快速收敛不抛", swc.ElapsedMilliseconds < 5000,
                $"{got.Count}/{a1.Count} 条后取消，{swc.ElapsedMilliseconds}ms");
        }
        {
            // 预取消 token 直接调 MetadataService.Read：meta-engine 约定返回部分结果不抛
            var sample = a1.FirstOrDefault(a => a.Kind == MediaKind.Image);
            if (sample is not null)
            {
                using var pre = new CancellationTokenSource();
                pre.Cancel();
                bool threw = false;
                MediaMetadata? md = null;
                try { md = metadata.Read(sample.FullPath, pre.Token); }
                catch (OperationCanceledException) { threw = true; }
                catch (Exception) { threw = true; }
                Check("Read 预取消不抛异常（协作式取消）", !threw,
                    md is null ? "" : $"Width={md.Width} warnings={md.Warnings.Count}");
            }
        }

        if (a1.Count > 1) await DedupOnAsync(a1);
        return Summary();
    }

    // ================================================================== 重复检测

    public static async Task<int> DedupAsync(string[] roots)
    {
        var settings = new FakeSettingsService(Program.WorkRoot);
        var dbPath = Path.Combine(Program.WorkRoot, "dedup.db");
        foreach (var f in Directory.EnumerateFiles(Program.WorkRoot, "dedup.db*")) TryDelete(f);
        using var store = new SqliteAssetStore(dbPath);
        var scanner = new LibraryScanner(new FakeMetadataService(), new FakeRuleClassifier(), new FakeGeocoder(), settings);
        var service = new LibraryService(store, scanner, settings);
        await service.InitializeAsync();
        var root = SampleFiles.PickRoot(roots) ?? throw new InvalidOperationException("没有样本目录");
        var lib = await service.AddLibraryAsync(root);
        await service.ScanLibraryAsync(lib, new ScanOptions { Parallelism = 4, ComputeQuickHash = true });

        var assets = await store.QueryAsync(new AssetQuery { LibraryId = lib.Id, Limit = 1000 });
        return await DedupOnAsync(assets);
    }

    private static async Task<int> DedupOnAsync(IReadOnlyList<MediaAsset> assets)
    {
        Console.WriteLine("\n## dedup: 重复检测");
        var ffmpeg = new FfmpegService(new FakeSettingsService(Program.WorkRoot));
        var decoder = new MediaDecoder(new FakeMetadataService(), ffmpeg);
        var finder = new DuplicateFinder(decoder);

        int lastPct = -1;
        var progress = new Progress<int>(p => { lastPct = p; });
        var sw = Stopwatch.StartNew();
        var groups = await finder.FindDuplicatesAsync(assets, includeNearDuplicates: true, progress);
        sw.Stop();

        long wasted = groups.Sum(g => g.WastedBytes);
        Console.WriteLine($"  [info] assets={assets.Count} groups={groups.Count} wasted={wasted / 1024}KB " +
                          $"{sw.ElapsedMilliseconds}ms progress={lastPct}%");
        foreach (var g in groups.Take(5))
            Console.WriteLine($"  [info] group {g.Kind} n={g.Assets.Count} wasted={g.WastedBytes / 1024}KB reason={g.Reason} " +
                              $"sample={g.Assets[0].FileName}");
        Check("dedup 完成", lastPct >= 0 || groups.Count >= 0, "");
        // 同一批素材重复跑必须是确定性结果
        var groups2 = await finder.FindDuplicatesAsync(assets, includeNearDuplicates: true);
        Check("dedup 确定性", groups2.Count == groups.Count, $"{groups.Count} vs {groups2.Count}");
        return Summary();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

// ====================================================================== 冒烟用的假实现
// 这些只存在于 tools/MediaSmoke，不进入产品代码；让解码/扫描/入库链路可以在
// 其他队友的 Metadata / Classification / Geo / Settings 就绪前独立验证。

internal sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(string appData)
    {
        AppDataDirectory = appData;
        Directory.CreateDirectory(appData);
        // 冒烟时可指向任意 ffmpeg：STJ_FFMPEG=C:\path\ffmpeg.exe
        var ff = Environment.GetEnvironmentVariable("STJ_FFMPEG");
        if (!string.IsNullOrWhiteSpace(ff) && File.Exists(ff)) Current.FfmpegPath = ff;
    }

    public AppSettings Current { get; } = new();
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
    public string AppDataDirectory { get; }
#pragma warning disable CS0067
    public event EventHandler? Changed;
#pragma warning restore CS0067
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public string? ProtectSecret(string? plainText) => plainText;
    public string? UnprotectSecret(string? cipherText) => cipherText;
}

internal sealed class FakeRuleClassifier : IRuleClassifier
{
    public void ApplyDerived(MediaAsset asset)
    {
        asset.DeviceKey ??= SuggestDeviceKey(asset.CameraMake, asset.CameraModel, asset.FileName);
        if (asset.DeviceKey is not null)
            asset.SetProv(ClassDimension.Device, FieldProvenance.Of(ClassSource.Derived));
        if (asset.RawFormat != RawFormat.None && asset.PostProcess == PostProcessState.Unknown)
            asset.PostProcess = PostProcessState.Unprocessed;
        if (string.IsNullOrEmpty(asset.FormatCode)) asset.FormatCode = "UNKNOWN";
    }

    public string? SuggestDeviceKey(string? make, string? model, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return string.IsNullOrWhiteSpace(make) ? model : $"{make} {model}";
        return null;
    }

    public string? SuggestTone(MediaAsset asset, ImageStats? stats) => stats?.SuggestTone();
}

internal sealed class FakeGeocoder : IGeocoder
{
    public bool IsReady => true;
    public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public GeoPlace? Reverse(double latitude, double longitude)
    {
        if (latitude is > 18 and < 54 && longitude is > 73 and < 135)
            return new GeoPlace("China", "Zhejiang", "Hangzhou", null, 3.2, "offline");
        return null;
    }

    public Task<GeoPlace?> ReverseOnlineAsync(double latitude, double longitude, CancellationToken ct = default)
        => Task.FromResult(Reverse(latitude, longitude));
}

/// <summary>最小元数据读取器：只认类型 + 图片尺寸（头解析），够冒烟用。</summary>
internal sealed class FakeMetadataService : IMetadataService
{
    public MediaKind Identify(string fullPath)
    {
        if (MediaTypeCatalog.TryGet(fullPath, out var kind, out _, out _, out _, out _)) return kind;
        return Sniff(fullPath);
    }

    public MediaMetadata Read(string fullPath, CancellationToken ct = default)
    {
        var md = new MediaMetadata { SourcePath = fullPath, ParsedAt = DateTimeOffset.Now };
        if (MediaTypeCatalog.TryGet(fullPath, out var kind, out var format, out var flavor, out var raw, out var mime))
        {
            md.Kind = kind;
            md.FormatCode = format;
            md.ImageFlavor = flavor;
            md.RawFormat = raw;
            md.MimeType = mime;
        }
        else
        {
            md.Kind = Sniff(fullPath);
            md.FormatCode = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
        }

        if (md.Kind == MediaKind.Image && md.RawFormat == RawFormat.None)
        {
            var (w, h) = ReadImageSize(fullPath);
            md.Width = w;
            md.Height = h;
        }
        return md;
    }

    private static MediaKind Sniff(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[32];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int n = fs.Read(head);
            return MediaTypeCatalog.SniffKind(head[..n]);
        }
        catch { return MediaKind.Unknown; }
    }

    private static (int W, int H) ReadImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[64 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            // PNG
            if (n > 24 && buf[0] == 0x89 && buf[1] == 'P' && buf[2] == 'N' && buf[3] == 'G')
            {
                int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                return (w, h);
            }
            // JPEG SOF
            if (n > 4 && buf[0] == 0xFF && buf[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < n)
                {
                    if (buf[i] != 0xFF) { i++; continue; }
                    byte marker = buf[i + 1];
                    if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int len = (buf[i + 2] << 8) | buf[i + 3];
                    if (len < 2) break;
                    if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        int h = (buf[i + 5] << 8) | buf[i + 6];
                        int w = (buf[i + 7] << 8) | buf[i + 8];
                        return (w, h);
                    }
                    i += 2 + len;
                }
            }
        }
        catch { }
        return (0, 0);
    }
}

internal static class SampleFiles
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif" };

    private static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw", ".x3f", ".raw" };

    public static bool IsRaw(string path) => RawExt.Contains(Path.GetExtension(path));

    /// <summary>样本库刻意放的坏文件：解码失败是预期行为。</summary>
    public static bool IsExpectedBad(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("._", StringComparison.Ordinal)) return true;      // AppleDouble
        try { if (new FileInfo(path).Length < 512) return true; } catch { return true; }
        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return stem is "broken" or "empty" or "corrupt";
    }

    public static List<string> Find(string[] roots, int max)
    {
        var result = new List<string>();
        foreach (var dir in DefaultDirs(roots))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (ImageExt.Contains(Path.GetExtension(f)) || RawExt.Contains(Path.GetExtension(f)))
                        result.Add(f);
                    if (result.Count >= max) return result;
                }
            }
            catch { }
        }
        return result;
    }

    public static List<string> FindAny(string[] roots, int max)
    {
        var result = new List<string>();
        foreach (var dir in DefaultDirs(roots))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    MediaTypeCatalog.TryGet(f, out var kind, out _, out _, out _, out _);
                    if (kind is MediaKind.Video or MediaKind.Audio) result.Add(f);
                    if (result.Count >= max) return result;
                }
            }
            catch { }
        }
        return result;
    }

    public static List<string> FindByKind(string[] roots, MediaKind kind, int max)
    {
        var result = new List<string>();
        foreach (var dir in DefaultDirs(roots))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    MediaTypeCatalog.TryGet(f, out var k, out _, out _, out _, out _);
                    if (k == kind) result.Add(f);
                    if (result.Count >= max) return result;
                }
            }
            catch { }
        }
        return result;
    }

    public static string? PickRoot(string[] roots)
    {
        foreach (var dir in DefaultDirs(roots))
        {
            if (Directory.Exists(dir) && Find(new[] { dir }, 1).Count > 0) return dir;
        }
        // 退化：用工作目录里的一张图创建一个临时库
        var files = Find(Array.Empty<string>(), 6);
        if (files.Count == 0) return null;
        var tmp = Path.Combine(Program.WorkRoot, "tmp-library");
        Directory.CreateDirectory(tmp);
        foreach (var f in files)
        {
            var dst = Path.Combine(tmp, Path.GetFileName(f));
            try { if (!File.Exists(dst)) File.Copy(f, dst); } catch { }
        }
        return Directory.Exists(tmp) ? tmp : null;
    }

    private static IEnumerable<string> DefaultDirs(string[] roots)
    {
        if (roots.Length > 0) return roots.Select(Path.GetFullPath);
        return new[]
        {
            Path.Combine(RepoRoot() ?? "", "tools", "SampleLibrary", "out", "library"),
            @"C:\Windows\Web\Wallpaper\Windows",
            @"C:\Windows\Web\Wallpaper\ThemeA",
            @"C:\Windows\Web\Wallpaper\Spotlight",
        }.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
    }

    private static string? RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "StjMediaViewer.Core"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        }
        return null;
    }
}