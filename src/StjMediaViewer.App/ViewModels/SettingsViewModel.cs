using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>媒体库列表里的一行。</summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    public LibraryItemViewModel(LibraryRoot root)
    {
        Root = root;
    }

    public LibraryRoot Root { get; }

    public string Id => Root.Id;
    public string Name => string.IsNullOrWhiteSpace(Root.Name) ? Root.Path : Root.Name;
    public string Path => Root.Path;

    [ObservableProperty]
    private int _assetCount;

    [ObservableProperty]
    private string _sizeText = "—";

    [ObservableProperty]
    private string _lastScanText = "从未扫描";

    [ObservableProperty]
    private string _countText = "";
}

/// <summary>
/// 设置页状态：AI 接入 / 设计语言 / 媒体库 / 工具链 / 关于。
/// 所有改动即时写回 <see cref="AppServices.Settings"/> 并防抖 SaveAsync（400ms），
/// API Key 只在内存里保留明文，落盘前一律 <see cref="ISettingsService.ProtectSecret"/>。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const int SaveDebounceMs = 400;

    public static SettingsViewModel Shared { get; } = new();

    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _hintTimer;
    private bool _loading;

    private SettingsViewModel()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is not null)
        {
            _saveTimer = queue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(SaveDebounceMs);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += async (_, _) => await SaveAsync();

            _hintTimer = queue.CreateTimer();
            _hintTimer.Interval = TimeSpan.FromSeconds(2.5);
            _hintTimer.IsRepeating = false;
            _hintTimer.Tick += (_, _) => SavedHintVisibility = Visibility.Collapsed;
        }
    }

    // ==================================================================================
    //  AI 接入
    // ==================================================================================
    [ObservableProperty]
    private string _providerId = "openai";

    [ObservableProperty]
    private string _providerName = "OpenAI";

    [ObservableProperty]
    private string _baseUrl = "";

    /// <summary>明文 API Key，只在内存；落盘走 ProtectSecret。由码后 PasswordBox 写入。</summary>
    public string ApiKey { get; private set; } = "";

    [ObservableProperty]
    private string _keyStatusText = "尚未配置 API Key";

    [ObservableProperty]
    private Visibility _keyConfiguredVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _visionModel = "gpt-4o-mini";

    [ObservableProperty]
    private string _textModel = "";

    [ObservableProperty]
    private bool _useJsonSchema = true;

    [ObservableProperty]
    private bool _dryRun;

    [ObservableProperty]
    private int _concurrency = 3;

    [ObservableProperty]
    private int _timeoutSeconds = 90;

    [ObservableProperty]
    private int _maxImageDimension = 1024;

    [ObservableProperty]
    private int _jpegQuality = 82;

    [ObservableProperty]
    private int _dailyCallLimit = 2000;

    [ObservableProperty]
    private string _customPrompt = "";

    [ObservableProperty]
    private string _testStatus = "";

    [ObservableProperty]
    private Visibility _testSuccessVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _testErrorVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _testErrorText = "";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testButtonText = "测试连接";

    [ObservableProperty]
    private bool _hasApiKey;

    public Visibility OpenAiSelectedVisibility => ProviderId == "openai" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AzureSelectedVisibility => ProviderId == "azure-openai" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OllamaSelectedVisibility => ProviderId == "ollama" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CustomSelectedVisibility => ProviderId == "custom" ? Visibility.Visible : Visibility.Collapsed;

    partial void OnProviderIdChanged(string value)
    {
        ProviderName = ProviderId switch
        {
            "azure-openai" => "Azure OpenAI",
            "ollama" => "Ollama（本地）",
            "custom" => "自定义网关",
            _ => "OpenAI",
        };
        OnPropertyChanged(nameof(OpenAiSelectedVisibility));
        OnPropertyChanged(nameof(AzureSelectedVisibility));
        OnPropertyChanged(nameof(OllamaSelectedVisibility));
        OnPropertyChanged(nameof(CustomSelectedVisibility));
    }

    /// <summary>切换 Provider 预设：自动填 BaseUrl / 模型（Azure 与自定义保留用户已填内容）。</summary>
    public void ApplyProviderPreset(string id)
    {
        ProviderId = id;
        switch (id)
        {
            case "openai":
                if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl.Contains("127.0.0.1") || BaseUrl.Contains("azure") || BaseUrl.Contains("localhost"))
                    BaseUrl = "https://api.openai.com/v1";
                if (string.IsNullOrWhiteSpace(VisionModel) || VisionModel.StartsWith("llava") || VisionModel.StartsWith("mock"))
                    VisionModel = "gpt-4o-mini";
                break;
            case "azure-openai":
                if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl.Contains("api.openai.com") || BaseUrl.Contains("127.0.0.1"))
                    BaseUrl = "https://YOUR-RESOURCE.openai.azure.com/openai/deployments/YOUR-DEPLOYMENT";
                break;
            case "ollama":
                BaseUrl = "http://127.0.0.1:11434/v1";
                if (string.IsNullOrWhiteSpace(VisionModel) || VisionModel.StartsWith("gpt") || VisionModel.StartsWith("mock"))
                    VisionModel = "llava";
                break;
            case "custom":
                if (BaseUrl.Contains("api.openai.com") || BaseUrl.Contains("YOUR-RESOURCE"))
                    BaseUrl = "";
                break;
        }
        ScheduleSave();
    }

    public void SetApiKey(string? key)
    {
        ApiKey = key ?? "";
        HasApiKey = !string.IsNullOrWhiteSpace(ApiKey);
        KeyStatusText = HasApiKey ? "已填写（保存后 DPAPI 加密落盘）" : "尚未配置 API Key";
        KeyConfiguredVisibility = HasApiKey ? Visibility.Visible : Visibility.Collapsed;
        ScheduleSave();
    }

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(ApiReadyVisibility));

    public Visibility ApiReadyVisibility => HasApiKey || DryRun ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>未配置 Key 时的引导卡片可见性。</summary>
    public Visibility ApiMissingVisibility => HasApiKey ? Visibility.Collapsed : Visibility.Visible;

    partial void OnDryRunChanged(bool value)
    {
        OnPropertyChanged(nameof(ApiReadyVisibility));
        OnPropertyChanged(nameof(ApiMissingVisibility));
        ScheduleSave();
    }

    // ==================================================================================
    //  外观 / 明暗（设计语言已收敛为单一 Fluent，不再提供切换）
    // ==================================================================================
    [ObservableProperty]
    private string _designId = DesignLanguageIds.Fluent;

    [ObservableProperty]
    private string _themeMode = "system";

    public async Task ApplyThemeModeAsync(string mode)
    {
        ThemeMode = mode;
        try
        {
            await AppServices.Design.SetThemeModeAsync(mode);
            ShellPageThemeHook();
            AppEvents.SetStatus(mode switch
            {
                "light" => "已切换到浅色主题",
                "dark" => "已切换到深色主题",
                _ => "已切换为跟随系统主题",
            });
        }
        catch (Exception ex)
        {
            Log.Warn("切换明暗模式失败", ex);
        }
    }

    /// <summary>设置页改主题后同步 Shell 的根元素（ShellPage 自己会从设置解析）。</summary>
    private static void ShellPageThemeHook()
    {
        try { Views.ShellPage.Current?.ApplyThemeFromSettings(); }
        catch (Exception ex) { Log.Warn("同步 Shell 主题失败", ex); }
    }

    public static string DesignDisplayName(string id) => "Fluent Design";

    // ==================================================================================
    //  媒体库
    // ==================================================================================
    public ObservableCollection<LibraryItemViewModel> Libraries { get; } = new();

    [ObservableProperty]
    private string _librarySummary = "";

    [ObservableProperty]
    private Visibility _libraryEmptyVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private bool _isLibraryBusy;

    [ObservableProperty]
    private string _libraryBusyText = "";

    // ==================================================================================
    //  工具链
    // ==================================================================================
    [ObservableProperty]
    private string _ffmpegPath = "";

    [ObservableProperty]
    private string _ffmpegStatus = "";

    [ObservableProperty]
    private Visibility _ffmpegOkVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _ffmpegMissingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _geocoderStatus = "";

    [ObservableProperty]
    private Visibility _geocoderOkVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _geocoderMissingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _cacheText = "";

    // ---------------- 缩略图预热 ----------------
    [ObservableProperty] private string _warmupText = "缩略图预热：未开始";
    [ObservableProperty] private double _warmupProgress;
    [ObservableProperty] private Visibility _warmupProgressVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _warmupButtonText = "后台预热整个库";
    [ObservableProperty] private bool _warmupRunning;

    [ObservableProperty]
    private bool _isClearingCache;

    // ==================================================================================
    //  关于
    // ==================================================================================
    [ObservableProperty]
    private string _versionText = "";

    [ObservableProperty]
    private string _settingsPath = "";

    [ObservableProperty]
    private string _appDataPath = "";

    [ObservableProperty]
    private string _runtimeSummary = "";

    [ObservableProperty]
    private string _demoReason = "";

    [ObservableProperty]
    private Visibility _demoVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _savedHint = "已保存";

    [ObservableProperty]
    private Visibility _savedHintVisibility = Visibility.Collapsed;

    // ==================================================================================
    //  加载
    // ==================================================================================
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var settings = AppServices.Settings.Current;
            var ai = settings.Ai;

            ProviderId = NormalizeProvider(ai.ProviderId);
            BaseUrl = ai.BaseUrl;
            VisionModel = ai.VisionModel;
            TextModel = ai.TextModel ?? "";
            UseJsonSchema = ai.UseJsonSchema;
            DryRun = ai.DryRun;
            Concurrency = ai.Concurrency <= 0 ? 3 : ai.Concurrency;
            TimeoutSeconds = ai.TimeoutSeconds <= 0 ? 90 : ai.TimeoutSeconds;
            MaxImageDimension = ai.MaxImageDimension <= 0 ? 1024 : ai.MaxImageDimension;
            JpegQuality = ai.JpegQuality is <= 0 or > 100 ? 82 : ai.JpegQuality;
            DailyCallLimit = ai.DailyCallLimit <= 0 ? 2000 : ai.DailyCallLimit;
            CustomPrompt = ai.CustomPrompt ?? "";

            ApiKey = DecryptKey(ai.ProtectedApiKey);
            HasApiKey = !string.IsNullOrWhiteSpace(ApiKey);
            KeyStatusText = HasApiKey ? "已配置（DPAPI 加密保存）" : "尚未配置 API Key";
            KeyConfiguredVisibility = HasApiKey ? Visibility.Visible : Visibility.Collapsed;
            OnPropertyChanged(nameof(ApiReadyVisibility));
            OnPropertyChanged(nameof(ApiMissingVisibility));

            DesignId = AppServices.Design.CurrentId;
            ThemeMode = (settings.ThemeMode ?? "system").ToLowerInvariant();

            FfmpegPath = settings.FfmpegPath ?? "";
            SettingsPath = AppServices.Settings.SettingsFilePath;
            AppDataPath = AppServices.Settings.AppDataDirectory;
            VersionText = ResolveVersion();
            DemoVisibility = AppServices.IsDemoMode ? Visibility.Visible : Visibility.Collapsed;
            DemoReason = AppServices.DemoTakenReason ?? "";
            RuntimeSummary = BuildRuntimeSummary();

            await RefreshLibrariesAsync();
            await RefreshToolStatusAsync();
            await RefreshCacheSizeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("加载设置失败", ex);
            AppEvents.SetError($"加载设置失败：{ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private static string NormalizeProvider(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "azure-openai" or "azure" => "azure-openai",
        "ollama" => "ollama",
        "custom" => "custom",
        _ => "openai",
    };

    private static string DecryptKey(string? protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey)) return "";
        try { return AppServices.Settings.UnprotectSecret(protectedKey) ?? ""; }
        catch (Exception ex)
        {
            Log.Warn("解密 API Key 失败", ex);
            return "";
        }
    }

    private static string ResolveVersion()
    {
        try
        {
            var asm = typeof(App).Assembly;
            var info = asm.GetName().Version;
            var attr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();
            var v = attr?.InformationalVersion ?? info?.ToString() ?? "1.0.0";
            var plus = v.IndexOf('+');
            if (plus > 0) v = v[..plus];
            return $"STJ Media Viewer {v}";
        }
        catch
        {
            return "STJ Media Viewer";
        }
    }

    private static string BuildRuntimeSummary()
    {
        try
        {
            var parts = new List<string>
            {
                $".NET {Environment.Version}",
                Environment.Is64BitProcess ? "x64" : "x86",
                $"OS {Environment.OSVersion.Version}",
            };
            if (AppServices.IsDemoMode)
                parts.Add("演示模式");
            else
                parts.Add("已接入真实索引库");
            return string.Join(" · ", parts);
        }
        catch
        {
            return "";
        }
    }

    // ==================================================================================
    //  保存
    // ==================================================================================
    public void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    public async Task SaveAsync()
    {
        try
        {
            var ai = AppServices.Settings.Current.Ai;
            ai.ProviderId = ProviderId;
            ai.BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "https://api.openai.com/v1" : BaseUrl.Trim();
            ai.VisionModel = string.IsNullOrWhiteSpace(VisionModel) ? "gpt-4o-mini" : VisionModel.Trim();
            ai.TextModel = string.IsNullOrWhiteSpace(TextModel) ? null : TextModel.Trim();
            ai.UseJsonSchema = UseJsonSchema;
            ai.DryRun = DryRun;
            ai.Concurrency = Math.Clamp(Concurrency, 1, 16);
            ai.TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 600);
            ai.MaxImageDimension = Math.Clamp(MaxImageDimension, 256, 4096);
            ai.JpegQuality = Math.Clamp(JpegQuality, 30, 100);
            ai.DailyCallLimit = Math.Max(0, DailyCallLimit);
            ai.CustomPrompt = string.IsNullOrWhiteSpace(CustomPrompt) ? null : CustomPrompt;
            ai.ProtectedApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : AppServices.Settings.ProtectSecret(ApiKey);

            AppServices.Settings.Current.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();

            await AppServices.Settings.SaveAsync();
            ShowSavedHint();
        }
        catch (Exception ex)
        {
            Log.Warn("保存设置失败", ex);
            AppEvents.SetError($"保存设置失败：{ex.Message}");
        }
    }

    private void ShowSavedHint()
    {
        SavedHintVisibility = Visibility.Visible;
        _hintTimer?.Stop();
        _hintTimer?.Start();
    }

    // 数值/开关类属性改动即时落库
    partial void OnBaseUrlChanged(string value) => ScheduleSave();
    partial void OnVisionModelChanged(string value) => ScheduleSave();
    partial void OnTextModelChanged(string value) => ScheduleSave();
    partial void OnUseJsonSchemaChanged(bool value) => ScheduleSave();
    partial void OnConcurrencyChanged(int value) => ScheduleSave();
    partial void OnTimeoutSecondsChanged(int value) => ScheduleSave();
    partial void OnMaxImageDimensionChanged(int value) => ScheduleSave();
    partial void OnJpegQualityChanged(int value) => ScheduleSave();
    partial void OnDailyCallLimitChanged(int value) => ScheduleSave();
    partial void OnCustomPromptChanged(string value) => ScheduleSave();
    partial void OnFfmpegPathChanged(string value) => ScheduleSave();

    // ==================================================================================
    //  测试连接
    // ==================================================================================
    public async Task TestConnectionAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        TestButtonText = "连接中…";
        TestStatus = "";
        TestSuccessVisibility = Visibility.Collapsed;
        TestErrorVisibility = Visibility.Collapsed;
        try
        {
            await SaveAsync();
            var settings = AppServices.Settings.Current.Ai;
            if (!settings.HasApiKey)
            {
                ShowTestError("请先填写 API Key（Ollama 本地服务可以随便填一个非空值）。");
                return;
            }

            var result = await AppServices.AiClient.TestConnectionAsync(settings);
            if (result.Success)
            {
                var model = string.IsNullOrWhiteSpace(result.ModelEcho) ? settings.VisionModel : result.ModelEcho!;
                TestStatus = $"连接成功 · 模型 {model} · 延迟 {result.LatencyMs} ms";
                TestSuccessVisibility = Visibility.Visible;
                AppEvents.SetStatus("AI 连接测试成功");
            }
            else
            {
                ShowTestError(DescribeError(result.Message));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("测试 AI 连接失败", ex);
            ShowTestError(DescribeError(ex.Message));
        }
        finally
        {
            IsTesting = false;
            TestButtonText = "测试连接";
        }
    }

    private void ShowTestError(string message)
    {
        TestErrorText = message;
        TestErrorVisibility = Visibility.Visible;
        TestSuccessVisibility = Visibility.Collapsed;
        AppEvents.SetError("AI 连接测试失败");
    }

    /// <summary>把常见的 HTTP / 网络错误翻译成用户能动手修的人话，同时保留原文。</summary>
    private static string DescribeError(string? raw)
    {
        var message = (raw ?? "").Trim();
        var lower = message.ToLowerInvariant();
        string hint;
        if (lower.Contains("401") || lower.Contains("unauthorized") || lower.Contains("invalid api key") || lower.Contains("api-key"))
            hint = "API Key 无效或没有权限（401）。检查 Key 是否完整、是否属于当前 Provider。";
        else if (lower.Contains("403") || lower.Contains("forbidden"))
            hint = "访问被拒绝（403）。检查 Azure 的 deployment 名、网关白名单或账号权限。";
        else if (lower.Contains("404") || lower.Contains("not found") || lower.Contains("model_not_found"))
            hint = "地址或模型不存在（404）。检查 BaseUrl 是否带 /v1、模型名是否拼写正确。";
        else if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("quota"))
            hint = "被限流或额度用尽（429）。稍后重试，或检查账号额度 / 每日上限。";
        else if (lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("taskcanceled") || lower.Contains("取消"))
            hint = "连接超时。检查网络、代理，或把超时时间调长。";
        else if (lower.Contains("connect") || lower.Contains("refused") || lower.Contains("dns") || lower.Contains("socket") || lower.Contains("no such host"))
            hint = "无法连接到服务器。检查 BaseUrl 主机/端口是否可达（本地服务请确认已启动）。";
        else if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503"))
            hint = "服务端错误（5xx）。稍后重试或检查网关日志。";
        else
            hint = "连接失败。请核对 Provider、BaseUrl、API Key 与模型名。";

        return string.IsNullOrWhiteSpace(message) ? hint : $"{hint}\n\n原始错误：{message}";
    }

    // ==================================================================================
    //  媒体库
    // ==================================================================================
    public async Task RefreshLibrariesAsync()
    {
        try
        {
            var roots = await AppServices.Library.GetLibrariesAsync();
            Libraries.Clear();
            long totalBytes = 0;
            int totalCount = 0;
            foreach (var root in roots)
            {
                var item = new LibraryItemViewModel(root)
                {
                    LastScanText = root.LastScanAt is null ? "从未扫描" : $"上次扫描 {Formatting.DateTime(root.LastScanAt)}",
                };

                try
                {
                    var paths = await AppServices.Store.ListAllPathsAsync(root.Id);
                    item.AssetCount = paths.Count;
                    item.SizeText = Formatting.FileSize(paths.Sum(p => p.SizeBytes));
                    totalBytes += paths.Sum(p => p.SizeBytes);
                    totalCount += paths.Count;
                }
                catch (Exception ex)
                {
                    Log.Warn($"统计媒体库 {root.Path} 失败", ex);
                }

                item.CountText = $"{item.AssetCount} 个素材 · {item.SizeText}";
                Libraries.Add(item);
            }

            LibrarySummary = Libraries.Count == 0
                ? "还没有添加媒体库"
                : $"{Libraries.Count} 个媒体库 · {totalCount} 个素材 · {Formatting.FileSize(totalBytes)}";
            LibraryEmptyVisibility = Libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn("读取媒体库列表失败", ex);
            AppEvents.SetError($"读取媒体库失败：{ex.Message}");
        }
    }

    public async Task AddLibraryAsync(string folderPath)
    {
        IsLibraryBusy = true;
        LibraryBusyText = $"正在添加并扫描 {folderPath} …";
        try
        {
            AppEvents.ReportScan(new ScanStatus { IsActive = true, IsIndeterminate = true, Text = $"正在扫描 {folderPath} …" });
            var root = await AppServices.Library.AddLibraryAsync(folderPath);
            var progress = new Progress<ScanProgress>(p => AppEvents.ReportScan(new ScanStatus
            {
                IsActive = true,
                IsIndeterminate = p.Total <= 0,
                Percent = p.Percent,
                Text = $"{p.Phase} · {p.Processed}/{p.Total} · {Path.GetFileName(p.CurrentPath ?? "")}",
            }));

            var result = await AppServices.Library.ScanLibraryAsync(root, new ScanOptions(), progress);
            AppEvents.ReportScan(new ScanStatus
            {
                IsActive = false,
                Text = $"扫描完成：发现 {result.MediaFound} 个媒体文件（新增 {result.Added}）",
            });
            AppEvents.RaiseLibraryContentChanged();
            await RefreshLibrariesAsync();
            await RefreshToolStatusAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("添加媒体库失败", ex);
            AppEvents.ReportScan(new ScanStatus { IsActive = false });
            AppEvents.SetError($"添加媒体库失败：{ex.Message}");
        }
        finally
        {
            IsLibraryBusy = false;
            LibraryBusyText = "";
        }
    }

    public async Task RemoveLibraryAsync(LibraryItemViewModel item)
    {
        var ok = await AppServices.Dialogs.ConfirmAsync(
            "移除媒体库",
            $"从列表中移除「{item.Name}」？\n\n只移除索引条目，磁盘上的文件不会被删除。",
            "移除", "取消");
        if (!ok) return;

        try
        {
            await AppServices.Library.RemoveLibraryAsync(item.Id);
            AppEvents.RaiseLibraryContentChanged();
            await RefreshLibrariesAsync();
            AppEvents.SetStatus($"已移除媒体库 {item.Name}");
        }
        catch (Exception ex)
        {
            Log.Warn("移除媒体库失败", ex);
            AppEvents.SetError($"移除媒体库失败：{ex.Message}");
        }
    }

    public async Task RescanAsync(LibraryItemViewModel item)
    {
        IsLibraryBusy = true;
        LibraryBusyText = $"正在重新扫描 {item.Name} …";
        try
        {
            AppEvents.ReportScan(new ScanStatus { IsActive = true, IsIndeterminate = true, Text = $"正在重新扫描 {item.Name} …" });
            var progress = new Progress<ScanProgress>(p => AppEvents.ReportScan(new ScanStatus
            {
                IsActive = true,
                IsIndeterminate = p.Total <= 0,
                Percent = p.Percent,
                Text = $"{p.Phase} · {p.Processed}/{p.Total}",
            }));
            var result = await AppServices.Library.ScanLibraryAsync(item.Root, new ScanOptions(), progress);
            AppEvents.ReportScan(new ScanStatus { IsActive = false, Text = $"重新扫描完成：{result.MediaFound} 个媒体文件" });
            AppEvents.RaiseLibraryContentChanged();
            await RefreshLibrariesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("重新扫描失败", ex);
            AppEvents.ReportScan(new ScanStatus { IsActive = false });
            AppEvents.SetError($"重新扫描失败：{ex.Message}");
        }
        finally
        {
            IsLibraryBusy = false;
            LibraryBusyText = "";
        }
    }

    public async Task PurgeMissingAsync(LibraryItemViewModel item)
    {
        try
        {
            var removed = await AppServices.Library.PurgeMissingAsync(item.Id);
            await RefreshLibrariesAsync();
            AppEvents.SetStatus($"已清理 {removed} 条失效记录");
        }
        catch (Exception ex)
        {
            Log.Warn("清理失效记录失败", ex);
            AppEvents.SetError($"清理失败：{ex.Message}");
        }
    }

    // ==================================================================================
    //  工具链 / 缓存
    // ==================================================================================
    public async Task RefreshToolStatusAsync()
    {
        try
        {
            var ffmpeg = AppServices.Ffmpeg;
            var available = ffmpeg?.IsAvailable == true;
            FfmpegStatus = available
                ? $"已就绪：{ffmpeg!.FfmpegPath}"
                : "未找到 ffmpeg（视频抽帧会回落到系统缩略图）。把 ffmpeg.exe 放到 tools/ffmpeg/bin/ 或在上方填写路径。";
            FfmpegOkVisibility = available ? Visibility.Visible : Visibility.Collapsed;
            FfmpegMissingVisibility = available ? Visibility.Collapsed : Visibility.Visible;
            if (available && string.IsNullOrWhiteSpace(FfmpegPath)) FfmpegPath = ffmpeg!.FfmpegPath ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn("读取 ffmpeg 状态失败", ex);
        }

        try
        {
            var geocoder = AppServices.Geocoder;
            var ready = geocoder?.IsReady == true;
            var cityCount = -1;
            if (geocoder is StjMediaViewer.Core.Geo.OfflineGeocoder offline) cityCount = offline.CityCount;
            GeocoderStatus = cityCount > 0
                ? $"已加载 {cityCount:N0} 个城市（离线反向地理编码）"
                : ready
                    ? "城市库已就绪"
                    : "城市库尚未加载（首次反查 GPS 时会自动加载）";
            GeocoderOkVisibility = ready ? Visibility.Visible : Visibility.Collapsed;
            GeocoderMissingVisibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn("读取城市库状态失败", ex);
        }

        await RefreshCacheSizeAsync();
    }

    public async Task RefreshCacheSizeAsync()
    {
        try
        {
            var bytes = await AppServices.Thumbnails.GetCacheSizeBytesAsync();
            CacheText = bytes > 0
                ? $"缩略图缓存 {Formatting.FileSize(bytes)}（{AppServices.ThumbnailCacheDirectory}）"
                : "缩略图缓存为空";
        }
        catch (Exception ex)
        {
            Log.Warn("读取缓存大小失败", ex);
            CacheText = "缓存大小未知";
        }
    }

    // ---------------------------------------------------------------- 缩略图预热

    /// <summary>开始/停止后台全库预热。</summary>
    public void ToggleWarmup()
    {
        var warmup = AppServices.Warmup;
        if (warmup.IsRunning)
        {
            warmup.Cancel();
            WarmupRunning = false;
            WarmupText = $"缩略图预热：已停止（{warmup.Done}/{Math.Max(warmup.Total, warmup.Done)}）";
            WarmupButtonText = "继续预热";
            WarmupProgressVisibility = Visibility.Collapsed;
            return;
        }

        var dispatcher = App.MainWindow?.DispatcherQueue;
        warmup.ProgressChanged -= OnWarmupProgress;
        warmup.ProgressChanged += OnWarmupProgress;
        warmup.Finished -= OnWarmupFinished;
        warmup.Finished += OnWarmupFinished;

        if (!warmup.Start()) return;
        WarmupRunning = true;
        WarmupButtonText = "停止预热";
        WarmupProgressVisibility = Visibility.Visible;
        WarmupProgress = 0;
        WarmupText = "缩略图预热：准备中…";
        AppEvents.SetStatus("已开始后台预热缩略图（低优先级，不抢前台）");
    }

    private void OnWarmupProgress(object? sender, EventArgs e)
    {
        var warmup = AppServices.Warmup;
        var total = Math.Max(warmup.Total, 1);
        var done = warmup.Done;
        var failed = warmup.Failed;

        var dispatcher = App.MainWindow?.DispatcherQueue;
        void Apply()
        {
            WarmupProgress = Math.Clamp(done * 100.0 / total, 0, 100);
            WarmupText = $"缩略图预热：{done}/{warmup.Total}" + (failed > 0 ? $"（失败 {failed}）" : "");
        }

        if (dispatcher is null) Apply();
        else dispatcher.TryEnqueue(Apply);
    }

    private void OnWarmupFinished(object? sender, EventArgs e)
    {
        var warmup = AppServices.Warmup;
        var dispatcher = App.MainWindow?.DispatcherQueue;
        void Apply()
        {
            WarmupRunning = false;
            WarmupButtonText = "后台预热整个库";
            WarmupProgressVisibility = Visibility.Collapsed;
            WarmupText = $"缩略图预热：完成 {warmup.Done}/{warmup.Total}" + (warmup.Failed > 0 ? $"（失败 {warmup.Failed}）" : "");
            AppEvents.SetStatus($"缩略图预热完成：{warmup.Done} 张已就绪");
        }
        if (dispatcher is null) Apply();
        else dispatcher.TryEnqueue(Apply);
    }

    public async Task ClearCacheAsync()
    {
        if (IsClearingCache) return;
        IsClearingCache = true;
        try
        {
            await AppServices.Thumbnails.ClearCacheAsync();
            AppServices.Thumbs.Clear();
            await RefreshCacheSizeAsync();
            AppEvents.SetStatus("缩略图缓存已清空");
        }
        catch (Exception ex)
        {
            Log.Warn("清空缓存失败", ex);
            AppEvents.SetError($"清空缓存失败：{ex.Message}");
        }
        finally
        {
            IsClearingCache = false;
        }
    }

    public async Task ReloadFfmpegAsync()
    {
        try
        {
            await SaveAsync();
            AppServices.ReinitializeTools();
            await RefreshToolStatusAsync();
            AppEvents.SetStatus(AppServices.Ffmpeg.IsAvailable ? "已找到 ffmpeg" : "仍未找到 ffmpeg");
        }
        catch (Exception ex)
        {
            Log.Warn("重新探测 ffmpeg 失败", ex);
        }
    }
}