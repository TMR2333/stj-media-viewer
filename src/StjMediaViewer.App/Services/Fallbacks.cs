using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Services;

/// <summary>ContentDialog 封装。所有对话框都从当前窗口的 XamlRoot 弹。</summary>
public sealed class DialogService : IDialogService
{
    private static XamlRoot? CurrentRoot => App.MainWindow?.Content?.XamlRoot;

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var root = CurrentRoot;
        if (root is null) return ContentDialogResult.None;
        dialog.XamlRoot = root;
        try { return await dialog.ShowAsync(); }
        catch (Exception ex) { Log.Warn("对话框显示失败", ex); return ContentDialogResult.None; }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptAsync(string title, string message, string? initial = null, string placeholder = "")
    {
        var box = new TextBox { Text = initial ?? "", PlaceholderText = placeholder, SelectionStart = initial?.Length ?? 0 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    box,
                },
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    public async Task InfoAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
        });
    }

    public async Task ErrorAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                },
            },
            CloseButtonText = "关闭",
        });
    }

    public async Task<double?> SliderAsync(string title, string message, double min, double max, double value)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = 1 };
        var label = new TextBlock { Text = value.ToString("0"), HorizontalAlignment = HorizontalAlignment.Right };
        slider.ValueChanged += (_, e) => label.Text = e.NewValue.ToString("0");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, slider, label },
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? slider.Value : null;
    }
}

// ==========================================================================================
//  UI 先行阶段的兜底实现。Core 实现到位后由 Lead 在 AppServices.InitializeAsync 里替换。
//  这些类只保证"界面不崩"，不承担业务正确性。
// ==========================================================================================

internal sealed class FallbackDesignLanguageService : IDesignLanguageService
{
    public string CurrentId { get; private set; } = DesignLanguageIds.Fluent;
    public event EventHandler? Changed;

    public Task ApplyAsync(string languageId, bool persist = true)
    {
        CurrentId = DesignLanguageIds.Normalize(languageId);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ApplySavedAsync() => ApplyAsync(DesignLanguageIds.Fluent, false);
    public ElementTheme ResolveTheme() => ElementTheme.Default;
    public Task SetThemeModeAsync(string mode, bool persist = true) => Task.CompletedTask;
}

internal sealed class FallbackWindowChromeService : IWindowChromeService
{
    public void Configure(Window window) { }
    public Task RefreshBackdropAsync() => Task.CompletedTask;
    public void SetTitleBarElement(UIElement? element) { }
}

internal sealed class FallbackSettingsService : ISettingsService
{
    private AppSettings _settings = new();

    public AppSettings Current => _settings;
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StjMediaViewer");

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            if (File.Exists(SettingsFilePath))
                _settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Warn("读取设置失败，使用默认值", ex); }
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(SettingsFilePath,
                System.Text.Json.JsonSerializer.Serialize(_settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Log.Warn("保存设置失败", ex); }
        return Task.CompletedTask;
    }

    public string? ProtectSecret(string? plainText) => plainText;
    public string? UnprotectSecret(string? cipherText) => cipherText;
}

internal sealed class FallbackThumbnailService : IThumbnailService
{
    public Task<byte[]?> GetThumbnailJpegAsync(MediaAsset asset, int maxSize, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<string?> GetThumbnailPathAsync(MediaAsset asset, int maxSize, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task InvalidateAsync(MediaAsset asset) => Task.CompletedTask;
    public Task<long> GetCacheSizeBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
    public Task ClearCacheAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FallbackScanner : ILibraryScanner
{
    public async IAsyncEnumerable<ScanProgress> ScanAsync(LibraryRoot root, ScanOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new ScanProgress { Phase = ScanPhase.Completed, Message = "演示模式：未接入真实扫描" };
    }

    public Task<IReadOnlyList<MediaAsset>> CollectAsync(LibraryRoot root, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>(Array.Empty<MediaAsset>());
}

internal sealed class FallbackLibraryService : ILibraryService
{
    private readonly IAssetStore _store;
    public FallbackLibraryService(IAssetStore store) => _store = store;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<LibraryRoot>> GetLibrariesAsync(CancellationToken ct = default) => _store.LoadLibraryRootsAsync(ct);

    public async Task<LibraryRoot> AddLibraryAsync(string folderPath, CancellationToken ct = default)
    {
        var roots = (await _store.LoadLibraryRootsAsync(ct)).ToList();
        var root = new LibraryRoot { Path = folderPath, Name = Path.GetFileName(folderPath.TrimEnd('\\', '/')) };
        roots.Add(root);
        await _store.SaveLibraryRootsAsync(roots, ct);
        return root;
    }

    public async Task RemoveLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        var roots = (await _store.LoadLibraryRootsAsync(ct)).Where(r => r.Id != libraryId).ToList();
        await _store.SaveLibraryRootsAsync(roots, ct);
    }

    public async Task<ScanProgress> ScanLibraryAsync(LibraryRoot root, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ScanProgress { Phase = ScanPhase.Completed, Message = "演示模式：未接入真实扫描" });
        await Task.CompletedTask;
        return new ScanProgress { Phase = ScanPhase.Completed, Message = "演示模式：未接入真实扫描" };
    }

    public Task<int> PurgeMissingAsync(string libraryId, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FallbackGeocoder : IGeocoder
{
    public bool IsReady => true;
    public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
    public GeoPlace? Reverse(double latitude, double longitude) => new("中国", null, "上海", null, 0, "demo");
    public Task<GeoPlace?> ReverseOnlineAsync(double latitude, double longitude, CancellationToken ct = default) => Task.FromResult<GeoPlace?>(null);
}

internal sealed class FallbackRuleClassifier : IRuleClassifier
{
    public void ApplyDerived(MediaAsset asset)
    {
        asset.DeviceKey ??= asset.DisplayDevice;
        if (asset.IsLog is null) asset.IsLog = !string.IsNullOrEmpty(asset.LogProfile);
    }

    public string? SuggestDeviceKey(string? make, string? model, string? fileName) => model ?? make;
    public string? SuggestTone(MediaAsset asset, ImageStats? stats) => stats?.SuggestTone();
}

internal sealed class NullMetadataService : IMetadataService
{
    public MediaMetadata Read(string fullPath, CancellationToken ct = default) => new() { SourcePath = fullPath };
    public MediaKind Identify(string fullPath) => MediaTypeCatalog.IsSupportedMedia(fullPath)
        ? (MediaTypeCatalog.TryGet(fullPath, out var k, out _, out _, out _, out _) ? k : MediaKind.Unknown)
        : MediaKind.Unknown;
}

internal sealed class NullMediaDecoder : IMediaDecoder
{
    public bool CanHandle(string fullPath) => true;
    public Task<DecodedImage?> DecodeAsync(string fullPath, int maxDimension, CancellationToken ct = default) => Task.FromResult<DecodedImage?>(null);
    public Task<byte[]?> ExtractEmbeddedPreviewAsync(string fullPath, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> ExtractVideoFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> ExtractAudioCoverAsync(string fullPath, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
}

internal sealed class NullImageEncoder : IImageEncoder
{
    public Task<byte[]?> EncodeJpegAsync(DecodedImage image, int quality, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> ResizeToJpegAsync(DecodedImage image, int maxDimension, int quality, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
}

internal sealed class NullImageAnalyzer : IImageAnalyzer
{
    public ImageStats Analyze(DecodedImage image) => new();
}

internal sealed class NullFfmpegService : IFfmpegService
{
    public string? FfmpegPath => null;
    public string? FfprobePath => null;
    public bool IsAvailable => false;
    public Task<string?> ProbeJsonAsync(string fullPath, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<byte[]?> ExtractFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<bool> TranscodeProxyAsync(string source, string destination, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class NullAiClient : IAiClient
{
    public Task<AiRawResponse> CompleteAsync(AiRequest request, AiSettings settings, CancellationToken ct = default) =>
        Task.FromResult(AiRawResponse.Fail("尚未接入 AI 客户端"));

    public Task<AiConnectionTestResult> TestConnectionAsync(AiSettings settings, CancellationToken ct = default) =>
        Task.FromResult(new AiConnectionTestResult { Success = false, Message = "尚未接入 AI 客户端" });
}

internal sealed class NullAiClassificationService : IAiClassificationService
{
    public Task<AiSuggestion> AnalyzeAssetAsync(MediaAsset asset, CancellationToken ct = default) =>
        Task.FromResult(new AiSuggestion { Error = "尚未接入 AI 分类服务" });

    public Task<AiBatchResult> RunBatchAsync(IReadOnlyList<MediaAsset> assets, AiBatchOptions options,
        IProgress<AiBatchProgress>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(new AiBatchResult { Progress = new AiBatchProgress { Done = true, Total = assets.Count } });
}

internal sealed class FallbackDuplicateFinder : IDuplicateFinder
{
    public Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(IReadOnlyList<MediaAsset> assets,
        bool includeNearDuplicates = true, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DuplicateGroup>>(Array.Empty<DuplicateGroup>());
}

internal sealed class FallbackXmpWriter : IXmpWriter
{
    public string BuildXmp(MediaAsset asset) => "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>";
    public Task<bool> WriteSidecarAsync(MediaAsset asset, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class FallbackAssetOperations : IAssetOperations
{
    public Task<ExportResult> ExportAsync(IReadOnlyList<MediaAsset> assets, string destinationRoot, string? subfolderTemplate,
        bool moveInsteadOfCopy, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(new ExportResult { Total = assets.Count });

    public Task<IReadOnlyList<string>> PreviewRenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(assets.Select(a => a.FileName).ToList());

    public Task<ExportResult> RenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(new ExportResult { Total = assets.Count });

    public Task<int> DeleteToRecycleBinAsync(IReadOnlyList<MediaAsset> assets, CancellationToken ct = default) => Task.FromResult(0);
}
// =====================================================================================
//  外部设备 / 导入 / 核验（演示模式兜底：不碰真实磁盘）
// =====================================================================================

internal sealed class FallbackStorageDeviceService : IStorageDeviceService
{
    private static readonly IReadOnlyList<StorageDevice> None = Array.Empty<StorageDevice>();
    public IReadOnlyList<StorageDevice> GetDevices() => None;
    public Task<IReadOnlyList<StorageDevice>> ProbeDevicesAsync(CancellationToken ct = default) => Task.FromResult(None);
    public StorageDevice? GetDeviceForPath(string path) => null;
}

internal sealed class FallbackImportService : IImportService
{
    public Task<ImportPlan> BuildPlanAsync(string sourceRoot, ImportOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ScanProgress { Phase = ScanPhase.Completed, Message = "演示模式不读取真实磁盘" });
        return Task.FromResult(new ImportPlan
        {
            SourceRoot = sourceRoot,
            Options = options,
            Warnings = { "当前处于演示模式（真实服务装配失败），导入不可用。" },
        });
    }

    public Task<ImportReport> ExecuteAsync(ImportPlan plan,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ImportProgress { Message = "演示模式不执行导入", Done = true });
        return Task.FromResult(new ImportReport { Warnings = { "演示模式不执行导入。" } });
    }
}

internal sealed class FallbackVerificationService : IVerificationService
{
    public Task<VerifyItemResult> VerifyFileAsync(string sourcePath, string destinationPath, bool strict, CancellationToken ct = default)
        => Task.FromResult(new VerifyItemResult
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Status = "Error",
            Error = "演示模式不读取真实磁盘",
        });

    public Task<VerifyReport> VerifyPairsAsync(IReadOnlyList<VerifyPair> pairs, bool strict,
        IProgress<int>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new VerifyReport { Total = pairs.Count, Errors = pairs.Count });

    public Task<VerifyReport> VerifyFolderAsync(string sourceRoot, string destinationRoot, bool strict,
        IProgress<int>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new VerifyReport { Errors = 1 });
}
