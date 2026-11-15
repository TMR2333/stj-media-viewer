using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Ai;
using StjMediaViewer.Core.Catalog;
using StjMediaViewer.Core.Classification;
using StjMediaViewer.Core.Dedup;
using StjMediaViewer.Core.Devices;
using StjMediaViewer.Core.Import;
using StjMediaViewer.Core.Supply;
using StjMediaViewer.Core.Geo;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Scanning;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;
using StjMediaViewer.Core.Write;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 服务装配：把 Core 的具体实现按构造签名拼起来，交给自检跑。
/// 刻意用手写装配（而不是反射/DI 容器），因为它是"集成是否真的成立"的第一道证据。
/// 注意：这里的装配顺序与 App/Services/AppServices.cs 保持一致，改一处要同步另一处。
/// </summary>
public static class Composition
{
    public static async Task<Services> CreateAsync(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);

        var settings = new JsonSettingsService(
            Path.Combine(dataDirectory, "settings.json"),
            dataDirectory);
        await settings.LoadAsync();

        var metadata = new MetadataService();
        var classifier = new RuleBasedClassifier();
        var geocoder = new OfflineGeocoder();
        var ffmpeg = new FfmpegService(settings);
        var encoder = new ImageEncoder();
        var analyzer = new ImageAnalyzer();
        var decoder = new MediaDecoder(metadata, ffmpeg);

        var store = new SqliteAssetStore(Path.Combine(dataDirectory, "catalog.db"));
        await store.InitializeAsync();

        var thumbnails = new ThumbnailService(decoder, encoder, Path.Combine(dataDirectory, "thumbs"));
        var scanner = new LibraryScanner(metadata, classifier, geocoder, settings);
        var library = new LibraryService(store, scanner, settings);
        var duplicates = new DuplicateFinder(decoder);
        var xmp = new XmpWriter();
        var operations = new AssetOperations(store);
        var aiClient = new OpenAiCompatibleClient(settings);
        var ai = new AiClassificationService(aiClient, decoder, encoder, settings);
        var devices = new StorageDeviceService();
        var import = new ImportService(metadata, geocoder, library);
        var verify = new FileVerificationService();

        await geocoder.EnsureLoadedAsync();

        return new Services(
            Settings: settings,
            Store: store,
            Scanner: scanner,
            Library: library,
            Metadata: metadata,
            Decoder: decoder,
            Encoder: encoder,
            Analyzer: analyzer,
            Thumbnails: thumbnails,
            Ffmpeg: ffmpeg,
            Duplicates: duplicates,
            Classifier: classifier,
            AiClient: aiClient,
            Ai: ai,
            Geocoder: geocoder,
            Xmp: xmp,
            Operations: operations,
            Devices: devices,
            Import: import,
            Verify: verify);
    }
}