using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Scanning;

/// <summary>
/// 目录扫描器：递归枚举 → 过滤（隐藏/系统/忽略目录/忽略扩展名/最小体积）→
/// 魔数嗅探纠正 → 边车挂接 → 并行元数据解析 → 规则分类 → 反向地理编码 → 快速指纹。
/// 单个文件任何异常都只写进该素材的 LastError，不中断整体扫描。
/// </summary>
public sealed class LibraryScanner : ILibraryScanner
{
    private const int ProgressEveryFiles = 64;

    private readonly IMetadataService _metadata;
    private readonly IRuleClassifier _classifier;
    private readonly IGeocoder _geocoder;
    private readonly ISettingsService _settings;

    public LibraryScanner(
        IMetadataService metadata,
        IRuleClassifier classifier,
        IGeocoder geocoder,
        ISettingsService settings)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _geocoder = geocoder ?? throw new ArgumentNullException(nameof(geocoder));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // ==================================================================== IAsyncEnumerable

    public async IAsyncEnumerable<ScanProgress> ScanAsync(
        LibraryRoot root,
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new ScanOptions();

        var channel = Channel.CreateBounded<ScanProgress>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,   // 进度丢帧没关系，绝不阻塞扫描
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                await RunScanAsync(root, options, p => channel.Writer.TryWrite(p), null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("[scan] unexpected failure", ex);
                channel.Writer.TryWrite(new ScanProgress { Phase = ScanPhase.Failed, Message = ex.Message });
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var p in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                yield return p;
        }
        finally
        {
            // 消费者提前退出（UI 关闭/取消）时，确保生产端停下
            try { cts.Cancel(); } catch { }
            channel.Writer.TryComplete();
            _ = producer;
        }
    }

    public async Task<IReadOnlyList<MediaAsset>> CollectAsync(
        LibraryRoot root,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new ScanOptions();
        var sink = new ConcurrentBag<MediaAsset>();
        try
        {
            await RunScanAsync(root, options, p =>
            {
                try { progress?.Report(p.Clone()); } catch { /* 进度回调失败不影响扫描 */ }
            }, sink, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Info("[scan] cancelled by caller");
        }
        catch (Exception ex)
        {
            Log.Error("[scan] unexpected failure", ex);
        }

        var list = sink.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        return list;
    }

    // ==================================================================== 核心流程

    private async Task RunScanAsync(
        LibraryRoot root,
        ScanOptions options,
        Action<ScanProgress> report,
        ConcurrentBag<MediaAsset>? sink,
        CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var counter = new ScanCounters();
        int processed = 0;
        int failed = 0;

        try
        {
            var rootPath = PathUtil.Normalize(root.Path);
            report(new ScanProgress { Phase = ScanPhase.Preparing, CurrentPath = rootPath, Message = "准备扫描" });
            if (!Directory.Exists(rootPath))
            {
                report(new ScanProgress
                {
                    Phase = ScanPhase.Failed,
                    CurrentPath = rootPath,
                    Message = $"媒体库目录不存在：{rootPath}",
                });
                return;
            }

            // ---------- 1) 枚举 ----------
            var candidates = Enumerate(rootPath, options, report, counter, ct);
            report(new ScanProgress
            {
                Phase = ScanPhase.Enumerating,
                FilesSeen = counter.FilesSeen,
                MediaFound = candidates.Count,
                Skipped = counter.Skipped,
                Total = candidates.Count,
                Message = $"枚举完成：{candidates.Count} 个媒体文件",
            });

            // ---------- 2) 并行解析 ----------
            int parallelism = options.Parallelism > 0
                ? Math.Clamp(options.Parallelism, 1, 32)
                : Math.Clamp(SafeParallelism(), 1, 32);

            report(new ScanProgress
            {
                Phase = ScanPhase.ReadingMetadata,
                Total = candidates.Count,
                MediaFound = candidates.Count,
                Message = $"并行解析元数据（{parallelism} 线程）",
            });

            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            }, async (cand, token) =>
            {
                token.ThrowIfCancellationRequested();
                MediaAsset asset;
                try
                {
                    asset = BuildAsset(root, cand, options, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 单文件失败：产出一条带 LastError 的资产而不是丢弃，方便 UI「问题」筛选
                    asset = FallbackAsset(root, cand, ex);
                }

                if (!string.IsNullOrEmpty(asset.LastError)) Interlocked.Increment(ref failed);
                sink?.Add(asset);

                int done = Interlocked.Increment(ref processed);
                if (done % ProgressEveryFiles == 0 || done == candidates.Count)
                {
                    report(new ScanProgress
                    {
                        Phase = ScanPhase.ReadingMetadata,
                        FilesSeen = counter.FilesSeen,
                        MediaFound = candidates.Count,
                        Skipped = counter.Skipped,
                        Processed = done,
                        Total = candidates.Count,
                        Failed = failed,
                        CurrentPath = asset.FullPath,
                        Message = "读取元数据",
                    });
                }
                await ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            report(new ScanProgress
            {
                Phase = ScanPhase.Completed,
                FilesSeen = counter.FilesSeen,
                MediaFound = candidates.Count,
                Skipped = counter.Skipped,
                Processed = processed,
                Total = candidates.Count,
                Failed = failed,
                Message = $"扫描完成，用时 {(DateTimeOffset.Now - started).TotalSeconds:0.0}s",
            });
        }
        catch (OperationCanceledException)
        {
            report(new ScanProgress
            {
                Phase = ScanPhase.Cancelled,
                FilesSeen = counter.FilesSeen,
                Processed = processed,
                Skipped = counter.Skipped,
                Message = "扫描已取消",
            });
        }
        catch (Exception ex)
        {
            Log.Error("[scan] failed", ex);
            report(new ScanProgress { Phase = ScanPhase.Failed, Message = ex.Message });
        }
    }

    // ==================================================================== 枚举

    private sealed class Candidate
    {
        public string FullPath = "";
        public string FileName = "";
        public string RelativePath = "";
        public long SizeBytes;
        public DateTimeOffset Modified;
        public DateTimeOffset? Created;
        public MediaKind Kind;
        public string Format = "";
        public ImageFlavor Flavor;
        public RawFormat Raw;
        public string Mime = "";
        public List<string> Sidecars = new();
    }

    private sealed class ScanCounters
    {
        public int FilesSeen;
        public int Skipped;
    }

    private List<Candidate> Enumerate(
        string rootPath, ScanOptions options, Action<ScanProgress> report, ScanCounters counter, CancellationToken ct)
    {
        var result = new List<Candidate>();
        var stack = new Stack<string>();
        stack.Push(rootPath);
        int lastReported = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                counter.Skipped++;
                Log.Warn($"[scan] cannot enumerate {dir}: {ex.Message}");
                continue;
            }

            if (options.IncludeSubfolders)
            {
                foreach (var sub in subdirs)
                {
                    try
                    {
                        var di = new DirectoryInfo(sub);
                        if (MediaTypeCatalog.IsIgnoredDirectory(di.Name)) { counter.Skipped++; continue; }
                        if (options.SkipHidden && (di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        { counter.Skipped++; continue; }
                        if (IsAdditionalIgnored(di.Name, options)) { counter.Skipped++; continue; }
                        stack.Push(sub);
                    }
                    catch { counter.Skipped++; }
                }
            }

            // 边车索引：stem → 边车路径（同目录内）
            Dictionary<string, List<string>>? sidecarIndex = null;
            foreach (var f in files)
            {
                if (!MediaTypeCatalog.IsSidecar(f)) continue;
                try
                {
                    sidecarIndex ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var name = Path.GetFileName(f);
                    var ext = Path.GetExtension(f);
                    var stem = ext.Length > 0 ? name[..^ext.Length] : name;
                    AddSidecar(sidecarIndex, stem, f);
                    AddSidecar(sidecarIndex, name, f);
                }
                catch { }
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (options.MaxFiles > 0 && result.Count >= options.MaxFiles) break;
                counter.FilesSeen++;
                try
                {
                    var cand = ClassifyFile(f, rootPath, options, sidecarIndex);
                    if (cand is null) { counter.Skipped++; continue; }
                    result.Add(cand);
                }
                catch (OperationCanceledException) { throw; }
                catch { counter.Skipped++; }
            }

            if (counter.FilesSeen - lastReported >= 500)
            {
                lastReported = counter.FilesSeen;
                report(new ScanProgress
                {
                    Phase = ScanPhase.Enumerating,
                    FilesSeen = counter.FilesSeen,
                    MediaFound = result.Count,
                    Skipped = counter.Skipped,
                    CurrentPath = dir,
                    Message = "枚举目录",
                });
            }
        }

        return result;
    }

    private bool IsAdditionalIgnored(string dirName, ScanOptions options)
    {
        if (options.ExtraIgnorePatterns.Count == 0) return false;
        foreach (var pattern in options.ExtraIgnorePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                if (dirName.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    private static void AddSidecar(Dictionary<string, List<string>> index, string key, string path)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<string>(2);
            index[key] = list;
        }
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
    }

    private Candidate? ClassifyFile(
        string path, string rootPath, ScanOptions options, Dictionary<string, List<string>>? sidecarIndex)
    {
        var name = Path.GetFileName(path);
        if (MediaTypeCatalog.IsIgnoredFile(name)) return null;

        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && MediaTypeCatalog.IgnoredExtensions.Contains(ext)) return null;

        bool known = MediaTypeCatalog.TryGet(path, out var kind, out var format, out var flavor, out var raw, out var mime);

        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        if (options.SkipHidden && (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
        if (fi.Length < Math.Max(1, options.MinFileSize)) return null;

        if (!known)
        {
            if (!options.SniffMagicBytes) return null;
            var sniff = MagicSniffer.Read(path);
            if (!sniff.IsMedia) return null;
            kind = sniff.Kind; format = sniff.Format; flavor = sniff.Flavor; raw = sniff.Raw; mime = sniff.Mime;
            Log.Info($"[scan] 魔数纠正（未知扩展名）{name} → {format}");
        }
        else if (options.SniffMagicBytes)
        {
            var sniff = MagicSniffer.Read(path);
            if (sniff.IsMedia && sniff.Kind != kind)
            {
                Log.Info($"[scan] 魔数纠正 {name}: {format}/{kind} → {sniff.Format}/{sniff.Kind}");
                kind = sniff.Kind; format = sniff.Format; flavor = sniff.Flavor; raw = sniff.Raw; mime = sniff.Mime;
            }
        }

        var cand = new Candidate
        {
            FullPath = path,
            FileName = name,
            RelativePath = SafeRelative(rootPath, path),
            SizeBytes = fi.Length,
            Modified = SafeTime(fi.LastWriteTimeUtc),
            Created = SafeTimeOrNull(fi.CreationTimeUtc),
            Kind = kind,
            Format = format,
            Flavor = flavor,
            Raw = raw,
            Mime = mime,
        };

        if (sidecarIndex is not null)
        {
            var stem = ext.Length > 0 ? name[..^ext.Length] : name;
            if (sidecarIndex.TryGetValue(name, out var byFullName)) cand.Sidecars.AddRange(byFullName);
            if (sidecarIndex.TryGetValue(stem, out var byStem))
            {
                foreach (var s in byStem)
                    if (!cand.Sidecars.Contains(s, StringComparer.OrdinalIgnoreCase)) cand.Sidecars.Add(s);
            }
        }
        return cand;
    }

    // ==================================================================== 单文件解析

    private MediaAsset BuildAsset(LibraryRoot root, Candidate cand, ScanOptions options, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var asset = new MediaAsset
        {
            Id = MakeId(cand.FullPath),
            LibraryId = root.Id,
            FullPath = cand.FullPath,
            FileName = cand.FileName,
            RelativePath = cand.RelativePath,
            SizeBytes = cand.SizeBytes,
            FileModified = cand.Modified,
            FileCreated = cand.Created,
            Kind = cand.Kind,
            FormatCode = cand.Format,
            MimeType = string.IsNullOrEmpty(cand.Mime) ? null : cand.Mime,
            ImageFlavor = cand.Flavor,
            RawFormat = cand.Raw,
            IndexedAt = now,
            ThumbKey = ThumbKeyOf(cand),
        };
        asset.Sidecars.AddRange(cand.Sidecars);

        // 1) 元数据
        MediaMetadata md;
        try
        {
            md = _metadata.Read(cand.FullPath, ct) ?? new MediaMetadata { SourcePath = cand.FullPath, ParsedAt = now };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            md = new MediaMetadata { SourcePath = cand.FullPath, ParsedAt = now, Error = ex.Message };
            Log.Warn($"[scan] metadata failed {cand.FileName}: {ex.Message}");
        }
        md.ApplyTo(asset);

        if (asset.Kind == MediaKind.Unknown) asset.Kind = cand.Kind;
        if (string.IsNullOrEmpty(asset.FormatCode)) asset.FormatCode = cand.Format;
        asset.MimeType ??= string.IsNullOrEmpty(cand.Mime) ? null : cand.Mime;
        if (asset.ImageFlavor == ImageFlavor.None) asset.ImageFlavor = cand.Flavor;
        if (asset.RawFormat == RawFormat.None) asset.RawFormat = cand.Raw;
        if (asset.CapturedAt is null)
        {
            asset.CapturedAt = cand.Created ?? cand.Modified;
            asset.CapturedAtSource = "file";
        }

        // 2) 规则分类（失败不影响入库）
        try { _classifier.ApplyDerived(asset); }
        catch (Exception ex) { Log.Warn($"[scan] classifier failed {cand.FileName}: {ex.Message}"); }

        // 3) 反向地理编码（有 GPS 才做）。
        //    XMP/IPTC 已经给出 City 的素材（样本里 19 条）绝不能被反查覆盖 ——
        //    只有 City 为空时才用离线反查结果，并且只在那时标注 CitySource。
        if (asset.Latitude.HasValue && asset.Longitude.HasValue)
        {
            try
            {
                if (_geocoder.IsReady)
                {
                    bool cityWasEmpty = string.IsNullOrWhiteSpace(asset.City);
                    var place = _geocoder.Reverse(asset.Latitude.Value, asset.Longitude.Value);
                    if (place is not null)
                    {
                        asset.Country ??= place.Country;
                        asset.Province ??= place.Province;
                        if (cityWasEmpty)
                        {
                            asset.City = place.City;
                            if (!string.IsNullOrWhiteSpace(asset.City))
                                asset.CitySource ??= place.Source;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"[scan] geocode failed {cand.FileName}: {ex.Message}"); }
        }

        // 4) 指纹
        if (options.ComputeQuickHash && asset.QuickHash is null)
        {
            try { asset.QuickHash = FileHasher.QuickHash(cand.FullPath, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"[scan] quick hash failed {cand.FileName}: {ex.Message}"); }
        }
        if (options.ComputeFullHash && asset.FullHash is null)
        {
            try { asset.FullHash = FileHasher.FullHash(cand.FullPath, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"[scan] full hash failed {cand.FileName}: {ex.Message}"); }
        }

        if (string.IsNullOrEmpty(asset.LastError)) asset.LastError = null;
        return asset;
    }

    private static MediaAsset FallbackAsset(LibraryRoot root, Candidate cand, Exception ex) => new()
    {
        Id = MakeId(cand.FullPath),
        LibraryId = root.Id,
        FullPath = cand.FullPath,
        FileName = cand.FileName,
        RelativePath = cand.RelativePath,
        SizeBytes = cand.SizeBytes,
        FileModified = cand.Modified,
        FileCreated = cand.Created,
        Kind = cand.Kind,
        FormatCode = cand.Format,
        ImageFlavor = cand.Flavor,
        RawFormat = cand.Raw,
        IndexedAt = DateTimeOffset.Now,
        LastError = ex.Message,
    };

    // ==================================================================== 工具

    /// <summary>稳定 ID：同一路径永远得到同一 ID，保证重复扫描是 upsert 而不是重复插入。</summary>
    public static string MakeId(string fullPath)
    {
        var normalized = PathUtil.Normalize(fullPath).ToLowerInvariant();
        return "a" + FileHasher.StableHash(normalized);
    }

    private static string ThumbKeyOf(Candidate cand) =>
        FileHasher.StableHash(
            $"{PathUtil.Normalize(cand.FullPath).ToLowerInvariant()}|{cand.SizeBytes}|{cand.Modified.ToUnixTimeMilliseconds()}");

    private static string SafeRelative(string rootPath, string path)
    {
        try { return Path.GetRelativePath(rootPath, path); }
        catch { return Path.GetFileName(path); }
    }

    private static DateTimeOffset SafeTime(DateTime utc)
    {
        try { return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime(); }
        catch { return DateTimeOffset.Now; }
    }

    private static DateTimeOffset? SafeTimeOrNull(DateTime utc)
    {
        try
        {
            if (utc.Year < 1980) return null;
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime();
        }
        catch { return null; }
    }

    private int SafeParallelism()
    {
        try
        {
            int p = _settings.Current.ScanParallelism;
            return p > 0 ? p : Environment.ProcessorCount;
        }
        catch { return Math.Max(1, Environment.ProcessorCount / 2); }
    }
}