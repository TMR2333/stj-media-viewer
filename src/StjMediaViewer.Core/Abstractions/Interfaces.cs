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