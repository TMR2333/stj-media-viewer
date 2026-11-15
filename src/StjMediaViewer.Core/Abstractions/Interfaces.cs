using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Abstractions;

// =====================================================================================
//  契约层：所有模块只依赖这些接口与 Models，互相之间不直接引用实现。
//  并发开发时这是唯一的"共同边界"，任何实现都必须满足这里的方法签名。
// =====================================================================================

// ---------------------------- 元数据 ----------------------------

/// <summary>元数据引擎：识别文件类型、读取 EXIF/XMP/IPTC/ISOBMFF/ID3 等，产出 <see cref="MediaMetadata"/>。</summary>
public interface IMetadataService
{
    MediaMetadata Read(string fullPath, CancellationToken ct = default);
    /// <summary>只做廉价探测（大小/扩展名/魔数），不做完整解析。</summary>
    MediaKind Identify(string fullPath);
}

// ---------------------------- 索引库 ----------------------------

/// <summary>素材索引库（SQLite）。所有筛选/排序/分面统计都下沉到这里。</summary>
public interface IAssetStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<MediaAsset?> GetAsync(string id, CancellationToken ct = default);
    Task<MediaAsset?> GetByPathAsync(string fullPath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetIdsByPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct = default);

    Task UpsertAsync(MediaAsset asset, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<MediaAsset> assets, CancellationToken ct = default);
    Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task DeleteByLibraryAsync(string libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaAsset>> QueryAsync(AssetQuery query, CancellationToken ct = default);
    Task<int> CountAsync(AssetQuery query, CancellationToken ct = default);
    Task<FacetCounts> GetFacetsAsync(AssetQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TimelineBucket>> GetTimelineAsync(AssetQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctValuesAsync(string field, string? libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaAsset>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<IReadOnlyList<(string Id, string FullPath, long SizeBytes, DateTimeOffset FileModified)>> ListAllPathsAsync(string? libraryId, CancellationToken ct = default);
    Task<int> CountAllAsync(CancellationToken ct = default);
    Task SaveLibraryRootsAsync(IEnumerable<LibraryRoot> roots, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryRoot>> LoadLibraryRootsAsync(CancellationToken ct = default);
}

// ---------------------------- 扫描 ----------------------------

/// <summary>目录扫描器：递归枚举、排除非媒体、读取元数据、产出素材。</summary>
public interface ILibraryScanner
{
    /// <summary>扫描一个媒体库目录，逐个产出进度；返回扫描到的素材（未入库）。</summary>
    IAsyncEnumerable<ScanProgress> ScanAsync(LibraryRoot root, ScanOptions options, CancellationToken ct = default);

    /// <summary>同步收集扫描结果（供 SelfCheck / 批处理使用）。</summary>
    Task<IReadOnlyList<MediaAsset>> CollectAsync(LibraryRoot root, ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>库管理 + 扫描编排（扫描 → 元数据 → 分类 → 入库）。</summary>
public interface ILibraryService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LibraryRoot>> GetLibrariesAsync(CancellationToken ct = default);
    Task<LibraryRoot> AddLibraryAsync(string folderPath, CancellationToken ct = default);
    Task RemoveLibraryAsync(string libraryId, CancellationToken ct = default);
    Task<ScanProgress> ScanLibraryAsync(LibraryRoot root, ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    Task<int> PurgeMissingAsync(string libraryId, CancellationToken ct = default);
}

// ---------------------------- 解码 / 缩略图 / 分析 ----------------------------

/// <summary>媒体解码：图片（WIC）、RAW 嵌入预览、视频抽帧、音频封面。</summary>
public interface IMediaDecoder
{
    bool CanHandle(string fullPath);
    Task<DecodedImage?> DecodeAsync(string fullPath, int maxDimension, CancellationToken ct = default);
    /// <summary>抽取 RAW/JPEG 内嵌的预览图字节（通常本身就是 JPEG）。</summary>
    Task<byte[]?> ExtractEmbeddedPreviewAsync(string fullPath, CancellationToken ct = default);
    /// <summary>抽视频帧（需要 ffmpeg；不可用时回落到 WIC/系统缩略图）。</summary>
    Task<byte[]?> ExtractVideoFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default);
    Task<byte[]?> ExtractAudioCoverAsync(string fullPath, CancellationToken ct = default);
}

/// <summary>缩略图服务：磁盘缓存 + 异步生成。</summary>
public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailJpegAsync(MediaAsset asset, int maxSize, CancellationToken ct = default);
    Task<string?> GetThumbnailPathAsync(MediaAsset asset, int maxSize, CancellationToken ct = default);
    Task InvalidateAsync(MediaAsset asset);
    Task<long> GetCacheSizeBytesAsync(CancellationToken ct = default);
    Task ClearCacheAsync(CancellationToken ct = default);
}

/// <summary>JPEG 编码（把 BGRA 位图压成 JPEG，AI 上传和缩略图都用它）。</summary>
public interface IImageEncoder
{
    Task<byte[]?> EncodeJpegAsync(DecodedImage image, int quality, CancellationToken ct = default);
    Task<byte[]?> ResizeToJpegAsync(DecodedImage image, int maxDimension, int quality, CancellationToken ct = default);
}

/// <summary>图像统计（直方图/锐度/饱和度），用于自动挑片与影调辅助判断。</summary>
public interface IImageAnalyzer
{
    ImageStats Analyze(DecodedImage image);
}

/// <summary>ffmpeg / ffprobe 外部工具定位与调用（可选增强）。</summary>
public interface IFfmpegService
{
    string? FfmpegPath { get; }
    string? FfprobePath { get; }
    bool IsAvailable { get; }
    Task<string?> ProbeJsonAsync(string fullPath, CancellationToken ct = default);
    Task<byte[]?> ExtractFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default);
    Task<bool> TranscodeProxyAsync(string source, string destination, CancellationToken ct = default);
}

// ---------------------------- 重复 / 相似 ----------------------------

public interface IDuplicateFinder
{
    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyList<MediaAsset> assets,
        bool includeNearDuplicates = true,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

// ---------------------------- 分类 ----------------------------

/// <summary>规则分类器：完全基于元数据的确定性推断（设备归一化、RAW/JPG、Log、后期标记…）。</summary>
public interface IRuleClassifier
{
    /// <summary>就地补全可推断的维度，并写入 Provenance。</summary>
    void ApplyDerived(MediaAsset asset);
    string? SuggestDeviceKey(string? make, string? model, string? fileName);
    /// <summary>结合元数据 + 图像统计给出影调建议（不覆盖 AI/人工结果）。</summary>
    string? SuggestTone(MediaAsset asset, ImageStats? stats);
}

// ---------------------------- AI ----------------------------

/// <summary>OpenAI 兼容协议的底层客户端（支持 OpenAI / Azure / Ollama / 自建网关）。</summary>
public interface IAiClient
{
    Task<AiRawResponse> CompleteAsync(AiRequest request, AiSettings settings, CancellationToken ct = default);
    Task<AiConnectionTestResult> TestConnectionAsync(AiSettings settings, CancellationToken ct = default);
}

/// <summary>视觉/理解模型分类编排：取图 → 压缩 → 提问 → 解析 → 写回素材。</summary>
public interface IAiClassificationService
{
    Task<AiSuggestion> AnalyzeAssetAsync(MediaAsset asset, CancellationToken ct = default);
    Task<AiBatchResult> RunBatchAsync(
        IReadOnlyList<MediaAsset> assets,
        AiBatchOptions options,
        IProgress<AiBatchProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class AiBatchResult
{
    public List<MediaAsset> Updated { get; set; } = new();
    public AiBatchProgress Progress { get; set; } = new();
}

// ---------------------------- 地理编码 ----------------------------

/// <summary>反向地理编码：默认离线最近城市，可选联网精修。</summary>
public interface IGeocoder
{
    bool IsReady { get; }
    Task EnsureLoadedAsync(CancellationToken ct = default);
    GeoPlace? Reverse(double latitude, double longitude);
    Task<GeoPlace?> ReverseOnlineAsync(double latitude, double longitude, CancellationToken ct = default);
}

// ---------------------------- 设置 ----------------------------

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsFilePath { get; }
    string AppDataDirectory { get; }
    event EventHandler? Changed;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    string? ProtectSecret(string? plainText);
    string? UnprotectSecret(string? cipherText);
}

// ---------------------------- 写入 / 导出 ----------------------------

/// <summary>XMP 边车写入：把评分、标签、分类结果非破坏性地写回磁盘。</summary>
public interface IXmpWriter
{
    string BuildXmp(MediaAsset asset);
    Task<bool> WriteSidecarAsync(MediaAsset asset, CancellationToken ct = default);
}

/// <summary>批量文件操作：按模板重命名、按模板分目录导出、回收站删除。</summary>
public interface IAssetOperations
{
    Task<ExportResult> ExportAsync(
        IReadOnlyList<MediaAsset> assets,
        string destinationRoot,
        string? subfolderTemplate,
        bool moveInsteadOfCopy,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> PreviewRenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, CancellationToken ct = default);
    Task<ExportResult> RenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, IProgress<int>? progress = null, CancellationToken ct = default);
    Task<int> DeleteToRecycleBinAsync(IReadOnlyList<MediaAsset> assets, CancellationToken ct = default);
}

// ---------------------------- 外部存储设备 / 导入 / 核验 ----------------------------

/// <summary>外部存储设备（U 盘 / SD 卡 / 相机 / 移动硬盘）枚举。</summary>
public interface IStorageDeviceService
{
    /// <summary>
    /// 快速枚举：盘符、卷标、类型、容量（不探测总线，毫秒级）。
    /// <paramref name="includeInternal"/>=false（默认）时**只返回外部存储**：U 盘 / SD 卡 / 移动硬盘 / 网络盘，
    /// 本机内置磁盘（NVMe/SATA 固定盘）不列出；true 时返回全部（诊断 / 自检用）。
    /// </summary>
    IReadOnlyList<StorageDevice> GetDevices(bool includeInternal = false);

    /// <summary>
    /// 完整枚举：额外探测总线类型（USB/SD）与 DCIM 相机目录线索。
    /// 默认同样只返回外部存储：固定盘只有在总线是 USB / SD / MMC 时才算外部（很多读卡器/移动硬盘会报成固定盘）。
    /// </summary>
    Task<IReadOnlyList<StorageDevice>> ProbeDevicesAsync(bool includeInternal = false, CancellationToken ct = default);

    /// <summary>取某个路径所属的设备信息（用于"浏览文件夹导入"时也能显示容量）。</summary>
    StorageDevice? GetDeviceForPath(string path);
}

/// <summary>
/// 从外部设备/目录导入：先出计划（按 JPG+RAW 分组、带预览），确认后执行拷贝/移动，
/// 并逐文件核验（哈希 + 数量对账）。
/// </summary>
public interface IImportService
{
    /// <summary>扫描源目录，产出可预览、可勾选的导入计划（JPG 作为预览，RAW 自动配对）。</summary>
    Task<ImportPlan> BuildPlanAsync(
        string sourceRoot,
        ImportOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>执行导入：逐组拷贝/移动 + 可选核验 + 可选登记进媒体库。</summary>
    Task<ImportReport> ExecuteAsync(
        ImportPlan plan,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>完整性核验：保证"导出的文件没损坏、也没漏导"。</summary>
public interface IVerificationService
{
    /// <summary>单文件核验（strict=true 用 SHA-256，false 用大小 + 首尾指纹）。</summary>
    Task<VerifyItemResult> VerifyFileAsync(string sourcePath, string destinationPath, bool strict, CancellationToken ct = default);

    /// <summary>按"源 → 目标"对照表批量核验（导入/导出后用）。</summary>
    Task<VerifyReport> VerifyPairsAsync(
        IReadOnlyList<VerifyPair> pairs,
        bool strict,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 目录级对账：递归比较两棵目录树，找出"目标缺失（漏导）"与"内容不一致（损坏）"。
    /// </summary>
    Task<VerifyReport> VerifyFolderAsync(
        string sourceRoot,
        string destinationRoot,
        bool strict,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
// ---------------------------- 云同步 ----------------------------

/// <summary>
/// 云存储适配器（一个目标一个实例）：只做"列目录 / 上传 / 下载 / 建目录"四件事，
/// 把不同云服务（坚果云 WebDAV、Nextcloud、群晖 NAS、本地挂载目录…）抹平成统一形态。
/// </summary>
public interface ICloudStorage : IAsyncDisposable
{
    CloudProviderKind Kind { get; }

    /// <summary>连通性/鉴权自检。返回 false 时把原因写进 <paramref name="error"/>。</summary>
    Task<bool> TestAsync(out string? error, CancellationToken ct = default);

    /// <summary>递归列出同步根下的所有文件（相对路径统一 '/'）。</summary>
    Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default);

    Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default);

    Task EnsureFolderAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>
/// 云同步引擎：双向比对本地目录与云端条目 → 出计划（可预览）→ 执行（可选 dry-run）
/// → 报告里带上逐文件结果与哈希核验。
/// </summary>
public interface ICloudSyncService
{
    /// <summary>按目标配置创建适配器（LocalFolder / WebDAV）。</summary>
    ICloudStorage CreateStorage(CloudTarget target);

    /// <summary>只读比对，不改任何一边。</summary>
    Task<CloudSyncPlan> BuildPlanAsync(
        CloudTarget target,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>执行计划；dryRun=true 时只统计不落盘。</summary>
    Task<CloudSyncReport> ExecuteAsync(
        CloudSyncPlan plan,
        bool dryRun = false,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>一步到位：比对 + 执行。</summary>
    Task<CloudSyncReport> SyncAsync(
        CloudTarget target,
        bool dryRun = false,
        IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default);
}

// ---------------------------- 转码 / 压缩 ----------------------------

/// <summary>
/// 视频转码与压缩：探测编码信息、列内置预设、单文件/批量转码、硬件编码器自动选择。
/// 实现基于 ffmpeg CLI（不依赖 ffprobe）。
/// </summary>
public interface ITranscodeService
{
    bool IsAvailable { get; }
    string? FfmpegPath { get; }

    /// <summary>内置预设（压缩 / 高画质 / 转封装 / AV1）。</summary>
    IReadOnlyList<TranscodeProfile> BuiltInProfiles { get; }

    /// <summary>探测源视频：编码、分辨率、码率、时长、帧率、位深、HDR、音轨。</summary>
    Task<VideoProbe> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>探测本机可用编码器（含硬件 NVENC/QSV/AMF），结果内部缓存。</summary>
    Task<EncoderAvailability> DetectEncodersAsync(CancellationToken ct = default);

    /// <summary>单文件转码。</summary>
    Task<TranscodeResult> TranscodeAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>批量转码（逐个执行，进度按文件数加权）。</summary>
    Task<TranscodeBatchReport> TranscodeManyAsync(
        IReadOnlyList<TranscodeRequest> requests,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>默认输出路径（同目录 + 编码/分辨率后缀）。</summary>
    string DefaultOutputPath(string sourcePath, TranscodeProfile profile);

    /// <summary>按预设粗估输出体积（界面提示用）。</summary>
    long EstimateOutputBytes(VideoProbe probe, TranscodeProfile profile);
}
