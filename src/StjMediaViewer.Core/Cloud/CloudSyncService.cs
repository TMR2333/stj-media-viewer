using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Cloud;

/// <summary>
/// 云同步引擎（协议无关：LocalFolder / WebDAV 都走同一套比对与执行逻辑）。
///
/// 设计原则：
/// 1) **先出计划，再执行**：`BuildPlanAsync` 只读两边，产出可预览的动作列表；用户可先 dry-run；
/// 2) **永不默认删除**：只做"上传 / 下载 / 冲突处理"，两端缺文件一律补齐而不是对删；
/// 3) **冲突有策略**：较新者胜（默认）/ 本地优先 / 云端优先 / 两边都保留；
/// 4) **可核验**：LocalFolder 目标按 SHA-256 逐文件核对，WebDAV 目标核对大小 + 重新列出条目；
/// 5) 逐文件失败不影响其它文件，报告里逐条列出。
/// </summary>
public sealed class CloudSyncService : ICloudSyncService
{
    /// <summary>允许同步的文件类型（媒体 + 边车）；用扩展名过滤而不是靠目录结构。</summary>
    private static readonly HashSet<string> SyncExtensions = BuildExtensions();

    /// <summary>同步时忽略的系统垃圾文件名（Windows 缩略图缓存、macOS 索引文件等）。</summary>
    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "ehthumbs.db", "desktop.ini", ".directory", "Icon", "_$folder$",
    };

    /// <summary>同步时忽略的目录名（NAS / 系统生成的索引目录）。</summary>
    private static readonly HashSet<string> JunkDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", "$RECYCLE.BIN", "__MACOSX", ".Trashes", ".Spotlight-V100",
        ".fseventsd", ".TemporaryItems", "node_modules", ".git", ".svn", ".vs",
    };

    /// <summary>额外排除的绝对目录（App 自己的缓存/数据目录，避免把缩略图缓存当素材同步）。</summary>
    private static readonly List<string> ExcludedDirectories = new();

    /// <summary>注册一个"不要同步"的目录（通常是缩略图缓存目录）。</summary>
    public static void ExcludeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            lock (ExcludedDirectories)
            {
                if (!ExcludedDirectories.Contains(full, StringComparer.OrdinalIgnoreCase))
                    ExcludedDirectories.Add(full);
            }
        }
        catch { }
    }

    /// <summary>
    /// 是否是"不该同步"的垃圾文件：
    /// - macOS AppleDouble（`._DSC01172.jpg`）、以 '.' 开头的隐藏文件/目录；
    /// - Windows 缩略图缓存（Thumbs.db / desktop.ini）、NAS 索引目录（@eaDir / #recycle …）。
    /// </summary>
    public static bool IsJunkRelativePath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("._", StringComparison.Ordinal)) return true;   // macOS AppleDouble
            if (segment.StartsWith('.')) return true;                              // 隐藏文件 / 隐藏目录
            if (JunkDirNames.Contains(segment)) return true;
        }

        return JunkFileNames.Contains(Path.GetFileName(path));
    }

    /// <summary>绝对路径是否落在被排除的目录里（App 缓存 / 数据目录）。</summary>
    public static bool IsExcludedDirectory(string absolutePath)
    {
        if (ExcludedDirectories.Count == 0) return false;
        var full = absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (ExcludedDirectories)
        {
            foreach (var excluded in ExcludedDirectories)
            {
                if (full.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private readonly Func<CloudTarget, ICloudStorage>? _storageFactory;

    /// <summary>最近一次本机扫描时被忽略的垃圾文件数。</summary>
    private int _ignoredJunk;

    /// <summary>
    /// <paramref name="storageFactory"/> 可注入自定义适配器（测试/未来加 S3、FTP 等）；
    /// 为 null 时按 <see cref="CloudTarget.Kind"/> 自己创建。
    /// </summary>
    private readonly IAssetStore? _store;
    private readonly IXmpWriter? _xmp;

    public CloudSyncService(
        Func<CloudTarget, ICloudStorage>? storageFactory = null,
        IAssetStore? store = null,
        IXmpWriter? xmp = null)
    {
        _storageFactory = storageFactory;
        _store = store;
        _xmp = xmp;
    }

    public ICloudStorage CreateStorage(CloudTarget target)
    {
        if (_storageFactory is not null) return _storageFactory(target);
        return target.Kind switch
        {
            CloudProviderKind.WebDav => CreateWebDav(target),
            _ => new LocalFolderCloudStorage(target),
        };
    }

    /// <summary>
    /// 是否是"网络抖动 / 服务器主动断连"这类可重试的错误：
    /// 坚果云等 WebDAV 在短时间大量上传时会直接关闭连接（远程主机强迫关闭了一个现有的连接），
    /// 重试 + 间隔通常就能过去。
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Net.Sockets.SocketException:
                case System.Net.Http.HttpRequestException:
                    return true;
                case IOException io when LooksTransient(io.Message):
                    return true;
            }

            if (current is TaskCanceledException || current.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return LooksTransient(ex.Message);
    }

    private static bool LooksTransient(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string[] hints =
        {
            "强迫关闭", "现有的连接", "连接被", "连接已", "重置", "已中止",
            "prematurely", "ResponseEnded", "connection", "reset by peer", "broken pipe",
            "timed out", "超时", "forcibly closed", "unexpected end", "服务器关闭",
        };
        return hints.Any(h => message.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>WebDAV 适配器由 Cloud 目录下的实现提供；找不到实现时给出明确错误。</summary>
    private static ICloudStorage CreateWebDav(CloudTarget target)
    {
        var type = typeof(CloudSyncService).Assembly.GetType("StjMediaViewer.Core.Cloud.WebDavCloudStorage")
                   ?? throw new InvalidOperationException("WebDAV 适配器未就绪");
        return (ICloudStorage)Activator.CreateInstance(type, target)!;
    }

    // ==================================================================================
    //  1) 比对
    // ==================================================================================

    public async Task<CloudSyncPlan> BuildPlanAsync(
        CloudTarget target,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var plan = new CloudSyncPlan { Target = target };
        if (string.IsNullOrWhiteSpace(target.LocalFolderPath) || !Directory.Exists(target.LocalFolderPath))
            throw new DirectoryNotFoundException($"本地同步目录不存在：{target.LocalFolderPath}");

        progress?.Report(new CloudSyncProgress { Phase = "扫描本地目录" });
        var local = await Task.Run(() => ScanLocal(target, ct), ct).ConfigureAwait(false);
        plan.LocalFileCount = local.Count;
        plan.IgnoredJunkFiles = _ignoredJunk;

        await using var storage = CreateStorage(target);
        progress?.Report(new CloudSyncProgress { Phase = $"连接云端（{target.DisplayKind}）" });
        IReadOnlyList<CloudEntry> remote;
        try
        {
            remote = await storage.ListAllAsync(
                new Progress<string>(msg => progress?.Report(new CloudSyncProgress { Phase = msg })),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new IOException($"读取云端失败：{ex.Message}", ex);
        }

        // 云端也过滤：早期版本可能已经把 ._xxx / Thumbs.db 之类传上去了，比对时直接忽略（不会主动删除）
        var junkOnRemote = 0;
        remote = remote.Where(e =>
        {
            if (e.IsDirectory) return false;
            if (IsJunkRelativePath(e.RelativePath)) { junkOnRemote++; return false; }
            return true;
        }).ToList();
        plan.IgnoredJunkFiles += junkOnRemote;

        if (target.Extensions is { Count: > 0 })
        {
            var allowed = new HashSet<string>(target.Extensions, StringComparer.OrdinalIgnoreCase);
            remote = remote.Where(e => allowed.Contains(Path.GetExtension(e.RelativePath))).ToList();
        }
        plan.RemoteFileCount = remote.Count;

        var remoteMap = remote.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var (rel, info) in local)
        {
            ct.ThrowIfCancellationRequested();
            if (!remoteMap.TryGetValue(rel, out var remoteEntry))
            {
                plan.Actions.Add(new CloudSyncAction
                {
                    Kind = target.Direction == CloudSyncDirection.DownloadOnly
                        ? CloudActionKind.LocalOnly
                        : CloudActionKind.Upload,
                    RelativePath = rel,
                    SizeBytes = info.SizeBytes,
                    LocalModified = info.ModifiedUtc,
                    Reason = "云端没有这个文件",
                });
                continue;
            }

            // 两边都有：大小一致 + 修改时间相差 2 秒内 → 视为已同步
            var sameSize = remoteEntry.SizeBytes == info.SizeBytes;
            var sameTime = Math.Abs((remoteEntry.ModifiedUtc - info.ModifiedUtc).TotalSeconds) <= 2;
            if (sameSize && sameTime)
            {
                plan.Actions.Add(new CloudSyncAction
                {
                    Kind = CloudActionKind.Skip,
                    RelativePath = rel,
                    SizeBytes = info.SizeBytes,
                    LocalModified = info.ModifiedUtc,
                    RemoteModified = remoteEntry.ModifiedUtc,
                    Reason = "大小与时间一致",
                });
                continue;
            }

            var action = new CloudSyncAction
            {
                Kind = CloudActionKind.Conflict,
                RelativePath = rel,
                SizeBytes = info.SizeBytes,
                LocalModified = info.ModifiedUtc,
                RemoteModified = remoteEntry.ModifiedUtc,
                Reason = sameSize ? "大小相同但修改时间不同" : "大小不同",
            };

            action.Resolution = ResolveConflict(target, action);
            plan.Actions.Add(action);
        }

        foreach (var remoteEntry in remote)
        {
            ct.ThrowIfCancellationRequested();
            if (local.ContainsKey(remoteEntry.RelativePath)) continue;
            plan.Actions.Add(new CloudSyncAction
            {
                Kind = target.Direction == CloudSyncDirection.UploadOnly
                    ? CloudActionKind.RemoteOnly
                    : CloudActionKind.Download,
                RelativePath = remoteEntry.RelativePath,
                SizeBytes = remoteEntry.SizeBytes,
                RemoteModified = remoteEntry.ModifiedUtc,
                Reason = "本地没有这个文件",
            });
        }

        progress?.Report(new CloudSyncProgress
        {
            Phase = "比对完成",
            FilesTotal = plan.Actions.Count,
            FilesProcessed = plan.Actions.Count,
            Done = true,
        });
        return plan;
    }

    private static CloudConflictPolicy ResolveConflict(CloudTarget target, CloudSyncAction action) =>
        target.ConflictPolicy switch
        {
            CloudConflictPolicy.KeepBoth => CloudConflictPolicy.KeepBoth,
            CloudConflictPolicy.PreferLocal => CloudConflictPolicy.PreferLocal,
            CloudConflictPolicy.PreferRemote => CloudConflictPolicy.PreferRemote,
            // 较新者胜：时间戳缺失时按大小判，仍不确定就本地优先（宁可覆盖云端也不丢本地素材）
            _ => action.RemoteModified > action.LocalModified
                ? CloudConflictPolicy.PreferRemote
                : CloudConflictPolicy.PreferLocal,
        };

    private Dictionary<string, LocalFileInfo> ScanLocal(CloudTarget target, CancellationToken ct)
    {
        _ignoredJunk = 0;
        var root = Path.GetFullPath(target.LocalFolderPath);
        var result = new Dictionary<string, LocalFileInfo>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Stack<string>();
        dirs.Push(root);

        var allowed = target.Extensions is { Count: > 0 }
            ? new HashSet<string>(target.Extensions, StringComparer.OrdinalIgnoreCase)
            : null;

        while (dirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = dirs.Pop();
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { subs = Array.Empty<string>(); }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) { _ignoredJunk++; continue; }
                if (JunkDirNames.Contains(name)) { _ignoredJunk++; continue; }
                if (IsExcludedDirectory(sub)) { _ignoredJunk++; continue; }
                dirs.Push(sub);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // 系统垃圾 / 隐藏文件（._DSC01172.jpg、Thumbs.db、桌面缓存…）一律不同步
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsJunkRelativePath(rel)) { _ignoredJunk++; continue; }

                var ext = Path.GetExtension(file);
                if (allowed is not null ? !allowed.Contains(ext) : !SyncExtensions.Contains(ext)) continue;
                try
                {
                    var fi = new FileInfo(file);
                    result[rel] = new LocalFileInfo(fi.Length, fi.LastWriteTimeUtc);
                }
                catch { }
            }
        }
        return result;
    }

    private readonly record struct LocalFileInfo(long SizeBytes, DateTimeOffset ModifiedUtc);

    // ==================================================================================
    //  2) 执行
    // ==================================================================================

    public async Task<CloudSyncReport> ExecuteAsync(
        CloudSyncPlan plan,
        bool dryRun = false,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new CloudSyncReport
        {
            TargetId = plan.Target.Id,
            TargetName = plan.Target.Name,
            DryRun = dryRun,
        };

        var target = plan.Target;
        var localRoot = Path.GetFullPath(target.LocalFolderPath);
        var actions = plan.Actions
            .Where(a => a.Kind is CloudActionKind.Upload or CloudActionKind.Download or CloudActionKind.Conflict)
            .ToList();

        long bytesTotal = actions.Sum(a => a.SizeBytes);
        int done = 0;

        await using var storage = CreateStorage(target);

        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            var item = new CloudTransferResult { RelativePath = action.RelativePath, SizeBytes = action.SizeBytes };
            var itemSw = System.Diagnostics.Stopwatch.StartNew();

            async Task TransferOnceAsync()
            {
                switch (action.Kind)
                {
                    case CloudActionKind.Upload:
                        await DoUploadAsync(storage, target, localRoot, action.RelativePath, action.RelativePath, dryRun, ct).ConfigureAwait(false);
                        item.Direction = "Upload";
                        break;

                    case CloudActionKind.Download:
                        await DoDownloadAsync(storage, target, localRoot, action.RelativePath, dryRun, ct).ConfigureAwait(false);
                        item.Direction = "Download";
                        break;

                    case CloudActionKind.Conflict:
                        await DoConflictAsync(storage, target, localRoot, action, report, dryRun, ct).ConfigureAwait(false);
                        item.Direction = "Conflict";
                        break;
                }
            }

            try
            {
                // WebDAV 服务器（尤其坚果云）短时间连续上传会主动断连：失败就退避重试。
                const int maxAttempts = 3;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await TransferOnceAsync().ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (attempt < maxAttempts &&
                                               action.Kind is CloudActionKind.Upload or CloudActionKind.Download &&
                                               IsTransient(ex))
                    {
                        Log.Warn($"[cloud] {action.RelativePath} 第 {attempt} 次失败（{ex.Message}），退避后重试");
                        await Task.Delay(attempt == 1 ? 1200 : 3500, ct).ConfigureAwait(false);
                    }
                }

                item.Ok = true;
                if (item.Direction == "Upload")
                {
                    report.Uploaded++;
                    report.BytesUp += action.SizeBytes;
                }
                else if (item.Direction == "Download")
                {
                    report.Downloaded++;
                    report.BytesDown += action.SizeBytes;
                }
                if (action.Kind == CloudActionKind.Conflict)
                {
                    report.Conflicts++;
                    item.Direction = "Conflict";
                }

                // 给服务器一点喘息时间：坚果云这类 WebDAV 对高频 PUT 很敏感
                if (action.Kind == CloudActionKind.Upload && !dryRun)
                {
                    var pacing = target.Kind == CloudProviderKind.WebDav
                        ? ((target.WebDavEndpoint ?? "").Contains("jianguoyun", StringComparison.OrdinalIgnoreCase) ? 350 : 120)
                        : 0;
                    if (pacing > 0) await Task.Delay(pacing, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Ok = false;
                item.Error = IsTransient(ex)
                    ? $"{ex.Message}（服务器/网络中断，已重试仍失败：WebDAV 短时间大量上传常被限流，稍后点「立即全部同步」继续即可）"
                    : ex.Message;
                report.Failed++;
                report.Messages.Add($"{action.RelativePath}：{item.Error}");
            }

            itemSw.Stop();
            item.Elapsed = itemSw.Elapsed;
            report.Items.Add(item);

            done++;
            progress?.Report(new CloudSyncProgress
            {
                Phase = dryRun ? "演练中" : "同步中",
                FilesProcessed = done,
                FilesTotal = actions.Count,
                BytesProcessed = (long)(bytesTotal * (done / (double)Math.Max(1, actions.Count))),
                BytesTotal = bytesTotal,
                CurrentFile = action.RelativePath,
            });
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;

        if (!dryRun)
        {
            try
            {
                await PostProcessAsync(storage, target, actions, report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("云同步后处理（作者名 / 来源标记）失败", ex);
                report.Messages.Add($"后处理失败：{ex.Message}");
            }
        }

        if (!dryRun)
        {
            target.LastSyncAt = DateTimeOffset.Now;
            target.LastSyncStatus = report.Failed == 0 ? "ok" : "partial";
            target.LastSyncMessage = report.Summary;
            target.LastUploaded = report.Uploaded;
            target.LastDownloaded = report.Downloaded;
            target.LastConflicts = report.Conflicts;
            target.LastFailed = report.Failed;
        }

        progress?.Report(new CloudSyncProgress
        {
            Phase = "完成",
            FilesProcessed = actions.Count,
            FilesTotal = actions.Count,
            BytesProcessed = bytesTotal,
            BytesTotal = bytesTotal,
            Done = true,
            CurrentFile = null,
        });
        return report;
    }

    public async Task<CloudSyncReport> SyncAsync(
        CloudTarget target,
        bool dryRun = false,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        // WebDAV（尤其坚果云）同一账号并行两个同步会话会被服务器直接断连，
        // 手动「立即全部同步」和后台自动同步撞在一起时就是这种情况 → 全局串行。
        if (target.Kind == CloudProviderKind.WebDav && !dryRun)
        {
            await WebDavSessionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var plan = await BuildPlanAsync(target, progress, ct).ConfigureAwait(false);
                return await ExecuteAsync(plan, dryRun, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                WebDavSessionGate.Release();
            }
        }

        var normalPlan = await BuildPlanAsync(target, progress, ct).ConfigureAwait(false);
        return await ExecuteAsync(normalPlan, dryRun, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 连接诊断：逐步验证 连接 → 鉴权 → 列目录 → 上传探针 → 校验 → 清理，
    /// 每一步都记进日志（[webdav] 前缀），界面直接显示哪一步失败、服务器回了什么。
    /// </summary>
    public async Task<CloudDiagnosticReport> DiagnoseAsync(
        CloudTarget target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var report = new CloudDiagnosticReport();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Add(string name, bool ok, string detail)
        {
            report.Steps.Add(new CloudDiagnosticStep(name, ok, sw.ElapsedMilliseconds, detail));
            Log.Info($"[cloud][diag] {name}: {(ok ? "OK" : "FAIL")} {sw.ElapsedMilliseconds}ms — {detail}");
        }

        ICloudStorage? storage = null;
        string? probeRelative = null;
        try
        {
            progress?.Report("创建连接…");
            sw.Restart();
            storage = CreateStorage(target);
            Add("创建连接", true, $"{target.DisplayKind} · {target.RemoteDescription} · 本地 {target.LocalFolderPath}");

            sw.Restart();
            progress?.Report("测试连通性与鉴权…");
            string? testError = null;
            bool reachable;
            try { reachable = await storage.TestAsync(out testError, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { reachable = false; testError = ex.Message; }
            if (!reachable)
            {
                Add("连通性 / 鉴权", false, testError ?? "服务器无响应（或账号 / 密码 / 路径不对）");
                return report;
            }
            Add("连通性 / 鉴权", true, "服务器有响应，账号可用");

            sw.Restart();
            progress?.Report("列出云端目录…");
            IReadOnlyList<CloudEntry> entries;
            try { entries = await storage.ListAllAsync(new Progress<string>(m => progress?.Report(m)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Add("列出云端目录", false, ex.Message); return report; }
            Add("列出云端目录", true, $"{entries.Count} 个文件");

            probeRelative = $".stj-connect-test-{DateTime.Now:HHmmss}.txt";
            var probeLocal = Path.Combine(Path.GetTempPath(), probeRelative);
            await File.WriteAllTextAsync(probeLocal, "ok", ct).ConfigureAwait(false);
            try
            {
                sw.Restart();
                progress?.Report("上传测试文件…");
                await storage.UploadAsync(probeLocal, probeRelative, ct).ConfigureAwait(false);
                Add("上传测试文件", true, $"4 B → {probeRelative}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Add("上传测试文件", false, ex.Message);
                return report;
            }

            sw.Restart();
            progress?.Report("确认文件已上传…");
            try
            {
                var after = await storage.ListAllAsync(null, ct).ConfigureAwait(false);
                var found = after.FirstOrDefault(e => string.Equals(e.RelativePath, probeRelative, StringComparison.OrdinalIgnoreCase));
                Add("确认文件已上传", found is not null,
                    found is null ? "上传接口返回成功，但云端列表里没有这个文件" : $"已确认（{found.SizeBytes} B）");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Add("确认文件已上传", false, ex.Message);
            }

            sw.Restart();
            progress?.Report("清理测试文件…");
            try
            {
                await storage.DeleteAsync(probeRelative, ct).ConfigureAwait(false);
                Add("清理测试文件", true, "已删除探针文件");
                probeRelative = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Add("清理测试文件", false, $"{ex.Message}（云端可能残留一个很小的占位文件）");
            }

            progress?.Report(report.Summary);
            return report;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Add("诊断过程异常", false, ex.Message);
            return report;
        }
        finally
        {
            if (probeRelative is not null && storage is not null)
            {
                try { await storage.DeleteAsync(probeRelative, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            if (storage is not null) await storage.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>WebDAV 同步会话全局串行锁（同一进程内）。</summary>
    private static readonly SemaphoreSlim WebDavSessionGate = new(1, 1);

    // ------------------------------------------------------------------ 群组作者 / 来源标记

    /// <summary>云端作者清单文件名（放在同步根，记录每个文件由谁上传）。</summary>
    public const string AuthorManifestName = ".stj-authors.json";

    /// <summary>
    /// 同步后处理：
    ///  - 群组目标：把作者名写进本地素材的 XMP（dc:creator）与索引库，并更新云端作者清单；
    ///  - 下载回来的文件：标记「来自 xx 云同步」，供素材库按来源分类筛选。
    /// </summary>
    private async Task PostProcessAsync(
        ICloudStorage storage,
        CloudTarget target,
        IReadOnlyList<CloudSyncAction> actions,
        CloudSyncReport report,
        CancellationToken ct)
    {
        if (_store is null) return;

        var localRoot = Path.GetFullPath(target.LocalFolderPath);
        var author = target.GroupEnabled ? target.AuthorName?.Trim() : null;
        if (!string.IsNullOrEmpty(author)) author = author!.Trim();
        var origin = target.Name;

        var uploaded = new List<string>();
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            var localPath = Path.Combine(localRoot, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            bool isUpload = action.Kind == CloudActionKind.Upload
                            || (action.Kind == CloudActionKind.Conflict
                                && action.Resolution is CloudConflictPolicy.PreferLocal or CloudConflictPolicy.KeepBoth);

            if (action.Kind == CloudActionKind.Download)
            {
                // 「来自 xx 云同步」：把目标名记到索引库，素材库可按来源筛选
                try { await _store.UpdateAssetMetaByPathAsync(localPath, origin: origin, ct: ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn($"标记来源失败 {action.RelativePath}", ex); }
                continue;
            }

            if (!isUpload || string.IsNullOrEmpty(author)) continue;

            uploaded.Add(action.RelativePath);
            try
            {
                await _store.UpdateAssetMetaByPathAsync(localPath, creator: author, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"写入作者名失败 {action.RelativePath}", ex);
            }

            // XMP 边车：把 dc:creator 写进文件旁边的 .xmp（下次同步会一起带上云端）
            if (_xmp is not null && File.Exists(localPath))
            {
                try
                {
                    var asset = new MediaAsset
                    {
                        FullPath = localPath,
                        FileName = Path.GetFileName(localPath),
                        Creator = author,
                        Kind = GuessKind(Path.GetExtension(localPath)),
                    };
                    await _xmp.WriteSidecarAsync(asset, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"写 XMP 作者失败 {action.RelativePath}", ex);
                }
            }
        }

        if (!string.IsNullOrEmpty(author) && uploaded.Count > 0)
        {
            try
            {
                await UpdateAuthorManifestAsync(storage, author!, uploaded, ct).ConfigureAwait(false);
                report.Messages.Add($"已写入作者名「{author}」（{uploaded.Count} 个文件）");
            }
            catch (Exception ex)
            {
                Log.Warn("更新云端作者清单失败", ex);
                report.Messages.Add($"作者清单更新失败：{ex.Message}");
            }
        }
    }

    private static MediaKind GuessKind(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "mov" or "mkv" or "avi" or "mxf" or "webm" or "m4v" or "r3d" or "braw" => MediaKind.Video,
            "wav" or "mp3" or "flac" or "aac" or "m4a" or "ogg" or "aiff" => MediaKind.Audio,
            _ => MediaKind.Image,
        };
    }

    /// <summary>把作者名合并进云端 `.stj-authors.json`（保留其他群组成员写入的记录）。</summary>
    private static async Task UpdateAuthorManifestAsync(
        ICloudStorage storage,
        string author,
        IReadOnlyList<string> uploadedPaths,
        CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "stj-authors-" + Guid.NewGuid().ToString("N") + ".json");
        var authors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            try
            {
                await storage.DownloadAsync(AuthorManifestName, temp, ct).ConfigureAwait(false);
                if (File.Exists(temp))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("authors", out var map) && map.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in map.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                authors[prop.Name] = prop.Value.GetString() ?? "";
                            else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                     prop.Value.TryGetProperty("author", out var authorProp))
                                authors[prop.Name] = authorProp.GetString() ?? "";
                        }
                    }
                }
            }
            catch
            {
                // 云端还没有清单（第一次同步）或格式不合法：从空清单开始
            }

            foreach (var path in uploadedPaths) authors[path] = author;

            var payload = new
            {
                version = 1,
                updatedAt = DateTimeOffset.Now.ToString("O"),
                authors,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            await storage.UploadAsync(temp, AuthorManifestName, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // ------------------------------------------------------------------ 冲突处理

    private static async Task DoConflictAsync(
        ICloudStorage storage, CloudTarget target, string localRoot, CloudSyncAction action,
        CloudSyncReport report, bool dryRun, CancellationToken ct)
    {
        var localPath = Path.Combine(localRoot, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        switch (action.Resolution)
        {
            case CloudConflictPolicy.PreferLocal:
                await DoUploadAsync(storage, target, localRoot, action.RelativePath, action.RelativePath, dryRun, ct).ConfigureAwait(false);
                return;

            case CloudConflictPolicy.PreferRemote:
                await DoDownloadAsync(storage, target, localRoot, action.RelativePath, dryRun, ct).ConfigureAwait(false);
                return;

            default: // KeepBoth
                var dir = Path.GetDirectoryName(action.RelativePath)?.Replace('\\', '/') ?? "";
                var name = Path.GetFileNameWithoutExtension(localPath);
                var ext = Path.GetExtension(localPath);
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");

                // 本地副本另存上传：name (本地 20260101-120000).ext
                var localCopyName = $"{name} (本地 {stamp}){ext}";
                var localCopyPath = Path.Combine(Path.GetDirectoryName(localPath)!, localCopyName);
                var localCopyRemote = string.IsNullOrEmpty(dir) ? localCopyName : $"{dir}/{localCopyName}";

                // 云端副本另存下载：name (云端 20260101-120000).ext
                var remoteCopyPath = Path.Combine(Path.GetDirectoryName(localPath)!, $"{name} (云端 {stamp}){ext}");

                if (!dryRun)
                {
                    File.Copy(localPath, localCopyPath, overwrite: true);
                    await storage.UploadAsync(localCopyPath, localCopyRemote, ct).ConfigureAwait(false);
                    await storage.DownloadAsync(action.RelativePath, remoteCopyPath, ct).ConfigureAwait(false);
                }
                report.Messages.Add($"冲突已保留两份：{localCopyName} / {name} (云端 {stamp}){ext}");
                return;
        }
    }

    private static async Task DoUploadAsync(
        ICloudStorage storage, CloudTarget target, string localRoot,
        string localRelative, string remoteRelative, bool dryRun, CancellationToken ct)
    {
        var localPath = Path.Combine(localRoot, localRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localPath)) throw new FileNotFoundException($"本地文件不存在：{localRelative}");
        if (dryRun) return;

        var dir = Path.GetDirectoryName(remoteRelative)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir)) await storage.EnsureFolderAsync(dir, ct).ConfigureAwait(false);
        await storage.UploadAsync(localPath, remoteRelative, ct).ConfigureAwait(false);

        if (target.VerifyAfterTransfer && storage is LocalFolderCloudStorage localCloud)
        {
            var uploaded = Path.Combine(localCloud.Root, remoteRelative.Replace('/', Path.DirectorySeparatorChar));
            var a = FileHasher.FullHash(localPath, ct);
            var b = FileHasher.FullHash(uploaded, ct);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                throw new IOException("上传后哈希不一致（文件可能损坏）");
        }
    }

    private static async Task DoDownloadAsync(
        ICloudStorage storage, CloudTarget target, string localRoot,
        string remoteRelative, bool dryRun, CancellationToken ct)
    {
        var localPath = Path.Combine(localRoot, remoteRelative.Replace('/', Path.DirectorySeparatorChar));
        if (dryRun) return;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await storage.DownloadAsync(remoteRelative, localPath, ct).ConfigureAwait(false);

        if (target.VerifyAfterTransfer && storage is LocalFolderCloudStorage localCloud)
        {
            var source = Path.Combine(localCloud.Root, remoteRelative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(source))
            {
                var a = FileHasher.FullHash(source, ct);
                var b = FileHasher.FullHash(localPath, ct);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("下载后哈希不一致（文件可能损坏）");
            }
        }
    }

    // ------------------------------------------------------------------ 扩展名

    private static HashSet<string> BuildExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp",
            ".heic", ".heif", ".avif", ".psd", ".psb", ".tga", ".exr", ".hdr",
            ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".mts", ".m2ts", ".mpg", ".mpeg",
            ".wmv", ".webm", ".flv", ".3gp", ".insv", ".braw", ".r3d", ".mxf",
            ".wav", ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".dsf",
            ".xmp", ".dop", ".cos", ".on1", ".pp3", ".arp",
        };
        foreach (var raw in Import.JpegRawPairing.RawExtensions) set.Add(raw);
        return set;
    }
}