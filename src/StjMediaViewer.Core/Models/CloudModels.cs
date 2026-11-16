namespace StjMediaViewer.Core.Models;

// =====================================================================================
//  云同步：目标配置 / 条目 / 计划 / 报告
// =====================================================================================

/// <summary>接入方式。主流云盘与本机 NAS 基本都能归到这两类之一。</summary>
public enum CloudProviderKind
{
    /// <summary>云端挂载成本地目录：坚果云/OneDrive/Dropbox 客户端目录、SMB 映射盘、已挂载的 NAS 共享。</summary>
    LocalFolder = 0,

    /// <summary>WebDAV：坚果云、Nextcloud/ownCloud、群晖 DSM、威联通 QTS、多数 NAS 都支持。</summary>
    WebDav = 1,
}

/// <summary>同步方向。</summary>
public enum CloudSyncDirection
{
    /// <summary>双向：本地缺的下载、云端缺的上传、冲突按策略处理。</summary>
    Bidirectional = 0,
    /// <summary>只上传：本地新增/修改推到云端（云端删除不动本地）。</summary>
    UploadOnly = 1,
    /// <summary>只下载：云端新增/修改拉到本地（云端照片自动落到本地图库）。</summary>
    DownloadOnly = 2,
}

/// <summary>两边都存在但内容不同时的处理策略。</summary>
public enum CloudConflictPolicy
{
    /// <summary>两边都保留：本地文件改名下载为 "… (云端).ext"，云端另存 "… (本地).ext"。</summary>
    KeepBoth = 0,
    /// <summary>本地优先（云端被覆盖）。</summary>
    PreferLocal = 1,
    /// <summary>云端优先（本地被覆盖）。</summary>
    PreferRemote = 2,
    /// <summary>按修改时间新的赢。</summary>
    PreferNewer = 3,
}

/// <summary>一个云同步目标（持久化在 settings.json）。</summary>
public sealed class CloudTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "新的同步";
    public CloudProviderKind Kind { get; set; } = CloudProviderKind.LocalFolder;
    public bool Enabled { get; set; } = true;

    /// <summary>本地同步目录（通常是媒体库根，或库下面的子目录）。</summary>
    public string LocalFolderPath { get; set; } = "";

    // ---- LocalFolder 适配器 ----
    /// <summary>云端挂载出来的目录，例如 "D:\Nutstore\照片" 或 "\\NAS\photo"。</summary>
    public string? RemoteFolderPath { get; set; }

    // ---- WebDAV 适配器 ----
    /// <summary>WebDAV 根地址，例如 https://dav.jianguoyun.com/dav/。</summary>
    public string? WebDavEndpoint { get; set; }
    public string? WebDavUsername { get; set; }
    /// <summary>群组协作：同步到云端时写入作者名（多人共用一个云空间时能看出是谁传的）。</summary>
    public bool GroupEnabled { get; set; }

    /// <summary>群组里的自己的用户名（群组开启时必填，写进 XMP dc:creator 与云端作者清单）。</summary>
    public string? AuthorName { get; set; }

    /// <summary>DPAPI 加密后的密码（Base64），以 "dpapi:" 开头；明文绝不落盘。</summary>
    public string? WebDavPasswordProtected { get; set; }
    /// <summary>服务器上的子目录（相对 WebDAV 根），例如 "照片/2026"。</summary>
    public string? RemoteSubPath { get; set; }

    // ---- 行为 ----
    public CloudSyncDirection Direction { get; set; } = CloudSyncDirection.Bidirectional;
    public CloudConflictPolicy ConflictPolicy { get; set; } = CloudConflictPolicy.PreferNewer;
    /// <summary>自动同步（后台定时 + 本地变更触发）。</summary>
    public bool AutoSync { get; set; }
    /// <summary>自动同步轮询间隔（秒），最小 60。</summary>
    public int PollSeconds { get; set; } = 300;
    /// <summary>只同步这些扩展名（空 = 全部媒体 + 边车）。</summary>
    public List<string> Extensions { get; set; } = new();
    /// <summary>传输后逐文件哈希核验。</summary>
    public bool VerifyAfterTransfer { get; set; } = true;

    // ---- 运行状态（持久化便于展示）----
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncMessage { get; set; }
    public int LastUploaded { get; set; }
    public int LastDownloaded { get; set; }
    public int LastConflicts { get; set; }
    public int LastFailed { get; set; }

    public string DisplayKind => Kind switch
    {
        CloudProviderKind.WebDav => "WebDAV",
        _ => "本地挂载目录",
    };

    public string RemoteDescription => Kind switch
    {
        CloudProviderKind.WebDav => string.IsNullOrWhiteSpace(RemoteSubPath)
            ? (WebDavEndpoint ?? "")
            : $"{WebDavEndpoint}{RemoteSubPath}",
        _ => RemoteFolderPath ?? "",
    };
}

/// <summary>云端（或本地同步目录）里的一个条目。</summary>
public sealed class CloudEntry
{
    /// <summary>相对同步根的路径，统一用 '/' 分隔。</summary>
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public bool IsDirectory { get; set; }
    public string? ContentHash { get; set; }

    public string FileName => RelativePath.Split('/').LastOrDefault() ?? RelativePath;
}

public enum CloudActionKind
{
    Skip = 0,
    Upload = 1,
    Download = 2,
    Conflict = 3,
    /// <summary>云端多出来的（DownloadOnly 时忽略 / 双向时下载）。</summary>
    LocalOnly = 4,
    RemoteOnly = 5,
}

public sealed class CloudSyncAction
{
    public CloudActionKind Kind { get; set; }
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset LocalModified { get; set; }
    public DateTimeOffset RemoteModified { get; set; }
    /// <summary>冲突时最终怎么处理（PreferLocal / PreferRemote / KeepBoth）。</summary>
    public CloudConflictPolicy? Resolution { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>人类可读动作描述，UI 直接展示。</summary>
    public string Description => Kind switch
    {
        CloudActionKind.Upload => "上传到云端",
        CloudActionKind.Download => "下载到本地",
        CloudActionKind.Conflict => Resolution switch
        {
            CloudConflictPolicy.PreferLocal => "冲突：覆盖云端",
            CloudConflictPolicy.PreferRemote => "冲突：覆盖本地",
            CloudConflictPolicy.KeepBoth => "冲突：两边都保留",
            _ => "冲突：较新者胜",
        },
        CloudActionKind.LocalOnly => "仅本地有",
        CloudActionKind.RemoteOnly => "仅云端有",
        _ => "已同步",
    };
}

public sealed class CloudSyncPlan
{
    public CloudTarget Target { get; set; } = new();
    public List<CloudSyncAction> Actions { get; set; } = new();
    public int LocalFileCount { get; set; }
    public int RemoteFileCount { get; set; }
    public long UploadBytes => Actions.Where(a => a.Kind == CloudActionKind.Upload ||
                                                  (a.Kind == CloudActionKind.Conflict && a.Resolution != CloudConflictPolicy.PreferRemote))
                                      .Sum(a => a.SizeBytes);
    public long DownloadBytes => Actions.Where(a => a.Kind == CloudActionKind.Download ||
                                                    (a.Kind == CloudActionKind.Conflict && a.Resolution != CloudConflictPolicy.PreferLocal))
                                        .Sum(a => a.SizeBytes);
    public int UploadCount => Actions.Count(a => a.Kind == CloudActionKind.Upload);
    public int DownloadCount => Actions.Count(a => a.Kind == CloudActionKind.Download);
    public int ConflictCount => Actions.Count(a => a.Kind == CloudActionKind.Conflict);
    public int SkippedCount => Actions.Count(a => a.Kind == CloudActionKind.Skip);
    public bool IsEmpty => UploadCount == 0 && DownloadCount == 0 && ConflictCount == 0;
    public string Summary =>
        $"上传 {UploadCount} · 下载 {DownloadCount} · 冲突 {ConflictCount} · 已同步 {SkippedCount}" +
        $"（本地 {LocalFileCount} 个 / 云端 {RemoteFileCount} 个）";
}

public sealed class CloudTransferResult
{
    public string RelativePath { get; set; } = "";
    public string Direction { get; set; } = "";      // Upload / Download
    public long SizeBytes { get; set; }
    public bool Ok { get; set; }
    public bool Verified { get; set; }
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class CloudSyncReport
{
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public TimeSpan Elapsed { get; set; }
    public bool DryRun { get; set; }
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int Conflicts { get; set; }
    public int Failed { get; set; }
    public int Verified { get; set; }
    public long BytesUp { get; set; }
    public long BytesDown { get; set; }
    public List<CloudTransferResult> Items { get; set; } = new();
    public List<string> Messages { get; set; } = new();
    public bool Success => Failed == 0;
    public string Summary =>
        $"上传 {Uploaded} · 下载 {Downloaded} · 冲突 {Conflicts} · 失败 {Failed}" +
        (Verified > 0 ? $" · 核验 {Verified}" : "") +
        $" · 用时 {Elapsed.TotalSeconds:0.0}s";
}

public sealed class CloudSyncProgress
{
    public string Phase { get; set; } = "";
    public int FilesProcessed { get; set; }
    public int FilesTotal { get; set; }
    public long BytesProcessed { get; set; }
    public long BytesTotal { get; set; }
    public string? CurrentFile { get; set; }
    public bool Done { get; set; }
    public double Percent => FilesTotal > 0
        ? Math.Round(100.0 * FilesProcessed / FilesTotal, 1)
        : (BytesTotal > 0 ? Math.Round(100.0 * BytesProcessed / BytesTotal, 1) : 0);
}