using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Devices;
using StjMediaViewer.Core.Import;
using StjMediaViewer.Core.Supply;
using StjMediaViewer.Core.Transcode;
using StjMediaViewer.Core.Ai;
using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Classification;
using StjMediaViewer.Core.Dedup;
using StjMediaViewer.Core.Geo;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Scanning;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;
using StjMediaViewer.Core.Write;
using Microsoft.UI.Xaml;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 全应用服务容器（手写 DI，够用且零依赖）。
/// 装配顺序：Settings → 基础设施 → 索引库 → 扫描/库 → 智能层 → UI 服务。
/// 真实实现装配失败时自动降级到 demo 实现，保证界面永远能起来（用户至少能看到错误提示）。
/// </summary>
public static class AppServices
{
    public static ISettingsService Settings { get; private set; } = null!;
    public static IAssetStore Store { get; private set; } = null!;
    public static ILibraryScanner Scanner { get; private set; } = null!;
    public static ILibraryService Library { get; private set; } = null!;
    public static IMetadataService Metadata { get; private set; } = null!;
    public static IMediaDecoder Decoder { get; private set; } = null!;
    public static IImageEncoder Encoder { get; private set; } = null!;
    public static IImageAnalyzer Analyzer { get; private set; } = null!;
    public static IThumbnailService Thumbnails { get; private set; } = null!;
    public static IFfmpegService Ffmpeg { get; private set; } = null!;
    public static IDuplicateFinder Duplicates { get; private set; } = null!;
    public static IRuleClassifier Classifier { get; private set; } = null!;
    public static IAiClient AiClient { get; private set; } = null!;
    public static IAiClassificationService Ai { get; private set; } = null!;
    public static IGeocoder Geocoder { get; private set; } = null!;
    public static IXmpWriter Xmp { get; private set; } = null!;
    public static IAssetOperations Operations { get; private set; } = null!;

    // ---- 外部设备 / 导入 / 核验 ----
    public static IStorageDeviceService Devices { get; private set; } = null!;
    public static IImportService Import { get; private set; } = null!;
    public static IVerificationService Verify { get; private set; } = null!;

    // ---- 云同步 ----
    public static ICloudSyncService Cloud { get; private set; } = null!;
    public static OrganizerService Organizer { get; private set; } = null!;
    public static CloudAutoSyncService CloudAuto { get; private set; } = null!;

    // ---- 转码 / 压缩 ----
    public static ITranscodeService Transcode { get; private set; } = null!;

    // ---- LUT 调色 ----
    public static ILutService Luts { get; private set; } = null!;

    // ---- 后台缩略图预热 ----
    public static ThumbnailWarmup Warmup { get; private set; } = null!;

    /// <summary>视频预览代理：系统解码器不支持 4:2:2 / 10-bit / HEVC 时用 ffmpeg 转 720p 代理再播。</summary>
    public static VideoProxyService VideoProxy { get; private set; } = null!;

    /// <summary>缩略图缓存自动清理服务。</summary>
    public static CacheMaintenanceService CacheMaintenance { get; private set; } = null!;

    /// <summary>密码加解密（DPAPI）：UI 只碰明文，落盘一律密文。</summary>
    public static class Secrets
    {
        public static string Protect(string plain) => Dpapi.Protect(plain) ?? "";
        public static bool TryUnprotect(string? protectedText, out string plain)
        {
            plain = "";
            if (string.IsNullOrEmpty(protectedText)) return false;
            var raw = Dpapi.Unprotect(protectedText);
            if (raw is null) return false;
            plain = raw;
            return true;
        }
    }

    // ---- UI 侧服务 ----
    public static IDesignLanguageService Design { get; private set; } = null!;
    public static IThumbnailLoader Thumbs { get; private set; } = null!;
    public static IWindowChromeService WindowChrome { get; private set; } = null!;
    public static IDialogService Dialogs { get; private set; } = null!;

    public static bool IsReady { get; private set; }
    public static bool IsDemoMode { get; private set; }
    public static string? DemoTakenReason { get; private set; }

    public static event EventHandler? Ready;
    public static event EventHandler? ServicesReplaced;

    public static string AppDataDirectory => Settings.AppDataDirectory;
    /// <summary>缓存根目录：自定义路径优先，否则 %LOCALAPPDATA%\StjMediaViewer\Cache。</summary>
    public static string CacheRootDirectory
    {
        get
        {
            var custom = Settings.Current.CacheDirectory;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    var full = Path.GetFullPath(custom!);
                    Directory.CreateDirectory(full);   // 旧缓存目录被删掉时自动重建
                    return full;
                }
                catch (Exception ex)
                {
                    // 自定义缓存目录不可用（比如指向已删除的软件文件夹 / 拔掉的移动硬盘）：
                    // 回退到默认目录，保证缩略图还能生成。
                    Log.Warn("自定义缓存目录不可用，回退默认缓存目录", ex);
                }
            }
            return Path.Combine(Settings.AppDataDirectory, "Cache");
        }
    }

    public static string ThumbnailCacheDirectory => Path.Combine(CacheRootDirectory, "thumbs");

    /// <summary>设置里改了缓存路径后热切换：重建缩略图服务 / 加载器 / 预热器（旧缓存留在原目录，可手动清）。</summary>
    public static void ReloadThumbnailCache()
    {
        try
        {
            Thumbnails = new ThumbnailService(Decoder, Encoder, ThumbnailCacheDirectory);
            Thumbs = new ThumbnailLoader(Thumbnails);
            Warmup = new ThumbnailWarmup(Store, Thumbnails, Luts);
        VideoProxy = new VideoProxyService(Ffmpeg, CacheRootDirectory);
            Log.Info($"缩略图缓存目录已切换：{ThumbnailCacheDirectory}");
        }
        catch (Exception ex)
        {
            Log.Warn("切换缩略图缓存目录失败", ex);
        }
    }

    /// <summary>按当前设置清理一次缩略图缓存。</summary>
    public static async Task<CacheCleanResult> CleanThumbnailCacheAsync(CancellationToken ct = default)
    {
        var settings = Settings.Current;
        var maxBytes = settings.ThumbnailCacheMb > 0 ? settings.ThumbnailCacheMb * 1024L * 1024L : 0;
        return await ThumbnailCacheCleaner.CleanAsync(
            ThumbnailCacheDirectory, maxBytes, settings.CacheMaxAgeDays, ct).ConfigureAwait(false);
    }
    public static string LibraryDatabasePath => Path.Combine(Settings.AppDataDirectory, "catalog.db");

    public static async Task InitializeAsync()
    {
        Settings = new JsonSettingsService();
        await Settings.LoadAsync();
        Log.Init(Settings.AppDataDirectory);
        Log.Info("=== STJ Media Viewer 启动 ===");

        try
        {
            await InitializeRealAsync();
            IsDemoMode = false;
        }
        catch (Exception ex)
        {
            DemoTakenReason = ex.Message;
            Log.Error("真实服务装配失败，降级到演示模式", ex);
            await InitializeDemoAsync();
            IsDemoMode = true;
        }

        Thumbs = new ThumbnailLoader(Thumbnails);
        Dialogs = new DialogService();
        Design = new DesignLanguageService();
        WindowChrome = new WindowChromeService();

        // LUT：后台生成内置 LUT + 扫描用户目录，避免首帧缩略图错过自动调色
        _ = Task.Run(async () =>
        {
            try { await Luts.ListAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("LUT 预热失败", ex); }
        });

        CacheMaintenance = new CacheMaintenanceService(Settings);
        CacheMaintenance.Start();

        IsReady = true;
        ServicesReplaced?.Invoke(null, EventArgs.Empty);
        Ready?.Invoke(null, EventArgs.Empty);
        Log.Info($"服务初始化完成（demo={IsDemoMode}, ffmpeg={Ffmpeg.IsAvailable}, geocoder={Geocoder.IsReady}）");
    }

    private static async Task InitializeRealAsync()
    {
        Metadata = new MetadataService();
        Classifier = new RuleBasedClassifier();
        Geocoder = new OfflineGeocoder();
        Ffmpeg = new FfmpegService(Settings);
        Encoder = new ImageEncoder();
        Analyzer = new ImageAnalyzer();
        Decoder = new MediaDecoder(Metadata, Ffmpeg);
        Thumbnails = new ThumbnailService(Decoder, Encoder, ThumbnailCacheDirectory);

        Store = new SqliteAssetStore(LibraryDatabasePath);
        await Store.InitializeAsync();

        Scanner = new LibraryScanner(Metadata, Classifier, Geocoder, Settings);
        Library = new LibraryService(Store, Scanner, Settings);
        Duplicates = new DuplicateFinder(Decoder);
        Xmp = new XmpWriter();
        Operations = new AssetOperations(Store);

        Devices = new StorageDeviceService();
        Import = new ImportService(Metadata, Geocoder, Library);
        Verify = new FileVerificationService();
        Transcode = new TranscodeService(Ffmpeg);
        Luts = new LutService(Settings, Decoder, Encoder);
        Warmup = new ThumbnailWarmup(Store, Thumbnails, Luts);
        VideoProxy = new VideoProxyService(Ffmpeg, CacheRootDirectory);
        Cloud = new CloudSyncService(store: Store, xmp: Xmp);
        // 下载成功 → 记住"这个文件来自哪个云同步目标"，素材库可据此筛选「来自云同步」
        CloudSyncService.FileDownloaded += (path, origin) =>
        {
            try
            {
                var settings = Settings.Current;
                lock (settings.CloudOrigins) settings.CloudOrigins[PathUtil.Normalize(path)] = origin;
                _ = Settings.SaveAsync();
            }
            catch (Exception ex) { Log.Warn($"[cloud] 记录来源失败：{ex.Message}"); }
        };
        // 别把 App 自己的缓存/数据目录当素材同步（缩略图缓存被用户放进库里时也安全）
        CloudSyncService.ExcludeDirectory(Path.Combine(Settings.AppDataDirectory, "Cache"));
        CloudSyncService.ExcludeDirectory(ThumbnailCacheDirectory);
        CloudAuto = new CloudAutoSyncService(Settings, Cloud);
        Organizer = new OrganizerService(Settings.AppDataDirectory, Cloud);

        AiClient = new OpenAiCompatibleClient(Settings);
        Ai = new AiClassificationService(AiClient, Decoder, Encoder, Settings);

        // 地理编码库（约 2 万城市）懒加载，不阻塞启动
        _ = Task.Run(async () =>
        {
            try { await Geocoder.EnsureLoadedAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("地理编码库加载失败", ex); }
        });
    }

    private static Task InitializeDemoAsync()
    {
        var demo = DemoLibrary.Build();
        Store = new FallbackAssetStore(demo);
        Thumbnails = new FallbackThumbnailService();
        Library = new FallbackLibraryService(Store);
        Duplicates = new FallbackDuplicateFinder();
        Xmp = new FallbackXmpWriter();
        Operations = new FallbackAssetOperations();
        Metadata = new NullMetadataService();
        Decoder = new NullMediaDecoder();
        Encoder = new NullImageEncoder();
        Analyzer = new NullImageAnalyzer();
        Ffmpeg = new NullFfmpegService();
        Scanner = new FallbackScanner();
        Classifier = new FallbackRuleClassifier();
        AiClient = new NullAiClient();
        Ai = new NullAiClassificationService();
        Geocoder = new FallbackGeocoder();
        Devices = new FallbackStorageDeviceService();
        Import = new FallbackImportService();
        Verify = new FallbackVerificationService();
        Transcode = new FallbackTranscodeService();
        Luts = new FallbackLutService();
        Warmup = new ThumbnailWarmup(Store, Thumbnails, Luts);
        VideoProxy = new VideoProxyService(Ffmpeg, CacheRootDirectory);
        Cloud = new FallbackCloudSyncService();
        CloudAuto = new CloudAutoSyncService(Settings, Cloud);
        Organizer = new OrganizerService(Settings.AppDataDirectory, Cloud);
        return Task.CompletedTask;
    }

    /// <summary>设备显示名（自定义名称优先，没设置就用 device_key）。</summary>
    public static string DeviceAlias(string? deviceKey)
        => Core.Models.DeviceNames.Display(deviceKey, Settings?.Current?.DeviceAliases);

    /// <summary>设置变更后重新执行一遍按设置初始化的部分（当前只有 ffmpeg 路径）。</summary>
    public static void ReinitializeTools()
    {
        try { Ffmpeg = new FfmpegService(Settings); }
        catch (Exception ex) { Log.Warn("重新初始化 ffmpeg 失败", ex); }
    }
}