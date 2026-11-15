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
    public static CloudAutoSyncService CloudAuto { get; private set; } = null!;

    // ---- 转码 / 压缩 ----
    public static ITranscodeService Transcode { get; private set; } = null!;

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
    public static string ThumbnailCacheDirectory => Path.Combine(Settings.AppDataDirectory, "Cache", "thumbs");
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
        Cloud = new CloudSyncService();
        CloudAuto = new CloudAutoSyncService(Settings, Cloud);

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
        Cloud = new FallbackCloudSyncService();
        CloudAuto = new CloudAutoSyncService(Settings, Cloud);
        return Task.CompletedTask;
    }

    /// <summary>设置变更后重新执行一遍按设置初始化的部分（当前只有 ffmpeg 路径）。</summary>
    public static void ReinitializeTools()
    {
        try { Ffmpeg = new FfmpegService(Settings); }
        catch (Exception ex) { Log.Warn("重新初始化 ffmpeg 失败", ex); }
    }
}