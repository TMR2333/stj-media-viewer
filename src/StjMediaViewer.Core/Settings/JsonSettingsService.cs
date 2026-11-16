using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Settings;

/// <summary>
/// 设置持久化：<c>%LOCALAPPDATA%\StjMediaViewer\settings.json</c>。
/// <list type="bullet">
/// <item>System.Text.Json 缩进输出，中文不转义（UnsafeRelaxedJsonEscaping）。</item>
/// <item>原子写：写同目录临时文件 → flush(true) → File.Move(overwrite)。</item>
/// <item>读取损坏/缺失时回退默认值，绝不让启动失败。</item>
/// <item>API Key 用 DPAPI（CryptProtectData，当前用户域）加密后存 <see cref="AiSettings.ProtectedApiKey"/>。</item>
/// <item>构造时初始化 <see cref="Log"/> 到 <see cref="AppDataDirectory"/>。</item>
/// </list>
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private readonly string _appDataDirectory;
    private AppSettings _current = new();

    public JsonSettingsService()
        : this(null, null)
    {
    }

    /// <summary>指定设置文件路径（自动化测试 / 便携模式）。</summary>
    public JsonSettingsService(string? settingsFilePath)
        : this(settingsFilePath, null)
    {
    }

    public JsonSettingsService(string? settingsFilePath, string? appDataDirectory)
    {
        _appDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? AppPaths.AppDataDirectory
            : PathUtil.Normalize(appDataDirectory);
        _settingsPath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? Path.Combine(_appDataDirectory, AppPaths.SettingsFileName)
            : settingsFilePath;

        try { Log.Init(_appDataDirectory); } catch { }
        try { AppPaths.EnsureCreated(); } catch { }
    }

    public AppSettings Current => _current;

    public string SettingsFilePath => _settingsPath;

    public string AppDataDirectory => _appDataDirectory;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Log.Info($"设置文件不存在，使用默认设置: {_settingsPath}");
                _current = new AppSettings();
                return;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(_settingsPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"读取设置文件失败，回退默认值: {_settingsPath}", ex);
                _current = new AppSettings();
                return;
            }

            AppSettings? loaded = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    loaded = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
                }
                catch (Exception ex)
                {
                    Log.Warn("设置文件 JSON 解析失败，回退默认值（原文件保留，下次保存会覆盖）", ex);
                }
            }

            _current = Normalize(loaded ?? new AppSettings());
            Log.Info($"设置已加载: {_settingsPath}（{_current.Libraries.Count} 个媒体库，AI Provider={_current.Ai.ProviderId}）");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var snapshot = Normalize(_current);
            _current = snapshot;

            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            // 临时文件与目标同目录，保证 File.Move 是同卷替换。
            var tmp = _settingsPath + ".tmp";
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                                 FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await fs.WriteAsync(utf8, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, _settingsPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }

            Log.Info($"设置已保存: {_settingsPath}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>重新从磁盘加载（丢弃内存修改）。</summary>
    public Task ReloadAsync(CancellationToken ct = default) => LoadAsync(ct);

    // ---------------------------- 密钥保护 ----------------------------

    public string? ProtectSecret(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return null;
        // DPAPI 失败时原样返回（宁可明文也不丢配置），调用方可用 Dpapi.LooksLikeProtectedBlob 判断。
        return Dpapi.Protect(plainText) ?? plainText;
    }

    public string? UnprotectSecret(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return null;
        return Dpapi.Unprotect(cipherText);
    }

    // ---------------------------- 便捷方法（供装配/UI 使用） ----------------------------

    /// <summary>取明文的 API Key；未配置或解密失败返回 null。</summary>
    public string? GetApiKey()
    {
        var packed = _current.Ai.ProtectedApiKey;
        if (string.IsNullOrWhiteSpace(packed)) return null;
        var plain = UnprotectSecret(packed);
        if (!string.IsNullOrWhiteSpace(plain)) return plain.Trim();
        // 兼容用户直接粘贴明文 key 的旧数据（DPAPI 解不开且不像密文）。
        return Dpapi.LooksLikeProtectedBlob(packed) ? null : packed.Trim();
    }

    /// <summary>设置 API Key（加密后写回内存，需再调用 <see cref="SaveAsync"/> 落盘）。空值清除。</summary>
    public void SetApiKey(string? plainKey)
    {
        _current.Ai.ProtectedApiKey = string.IsNullOrWhiteSpace(plainKey) ? null : ProtectSecret(plainKey.Trim());
    }

    // ---------------------------- 规范化 / 迁移 ----------------------------

    /// <summary>早期版本自带过的示例云同步目标 id；读设置时静默清掉，避免界面上一直挂着一个"自带实例"。</summary>
    private static readonly HashSet<string> DemoCloudTargetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "demo-nutstore",
        "demo-nas",
    };

    private static AppSettings Normalize(AppSettings s)
    {
        s.Ai ??= new AiSettings();
        s.Grid ??= new GridSettings();
        s.Libraries ??= new List<LibraryRoot>();

        s.SchemaVersion = s.SchemaVersion <= 0 ? 1 : s.SchemaVersion;
        s.DesignLanguage = DesignLanguageIds.Normalize(s.DesignLanguage);
        s.ThemeMode = NormalizeThemeMode(s.ThemeMode);
        if (string.IsNullOrWhiteSpace(s.Locale)) s.Locale = "zh-CN";

        foreach (var lib in s.Libraries)
        {
            if (string.IsNullOrWhiteSpace(lib.Id)) lib.Id = Guid.NewGuid().ToString("N")[..12];
            lib.Name ??= "";
            lib.Path ??= "";
        }
        s.Libraries = s.Libraries.Where(l => !string.IsNullOrWhiteSpace(l.Path)).ToList();

        var ai = s.Ai;
        ai.ProviderId = NormalizeProviderId(ai.ProviderId);
        if (string.IsNullOrWhiteSpace(ai.BaseUrl)) ai.BaseUrl = "https://api.openai.com/v1";
        if (string.IsNullOrWhiteSpace(ai.VisionModel)) ai.VisionModel = "gpt-4o-mini";
        ai.Concurrency = Math.Clamp(ai.Concurrency <= 0 ? 3 : ai.Concurrency, 1, 16);
        ai.MaxImageDimension = Math.Clamp(ai.MaxImageDimension <= 0 ? 1024 : ai.MaxImageDimension, 256, 4096);
        ai.JpegQuality = Math.Clamp(ai.JpegQuality <= 0 ? 82 : ai.JpegQuality, 40, 100);
        ai.TimeoutSeconds = Math.Clamp(ai.TimeoutSeconds <= 0 ? 90 : ai.TimeoutSeconds, 5, 600);
        ai.Temperature = Math.Clamp(ai.Temperature, 0, 2);
        ai.MaxRetries = Math.Clamp(ai.MaxRetries, 0, 8);
        ai.DailyCallLimit = Math.Max(0, ai.DailyCallLimit);
        ai.ExtraHeaders ??= new Dictionary<string, string>();

        // 清理自带示例同步目标（只删这两个固定 id，不动用户自己建的目标）
        s.CloudTargets ??= new List<CloudTarget>();
        s.CloudTargets.RemoveAll(t => t is not null && DemoCloudTargetIds.Contains(t.Id));

        s.Grid.CardSize = Math.Clamp(s.Grid.CardSize <= 0 ? 220 : s.Grid.CardSize, 80, 640);
        s.Grid.ThumbnailQuality = Math.Clamp(s.Grid.ThumbnailQuality <= 0 ? 320 : s.Grid.ThumbnailQuality, 96, 2048);
        if (s.Grid.Density is not ("compact" or "comfortable" or "spacious")) s.Grid.Density = "comfortable";

        s.ThumbnailCacheMb = Math.Clamp(s.ThumbnailCacheMb <= 0 ? 512 : s.ThumbnailCacheMb, 64, 65536);
        s.ScanParallelism = Math.Clamp(s.ScanParallelism <= 0 ? 4 : s.ScanParallelism, 1, 32);
        return s;
    }

    private static string NormalizeThemeMode(string? id) => id?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };

    private static string NormalizeProviderId(string? id)
    {
        var p = id?.Trim().ToLowerInvariant();
        return p switch
        {
            "azure" or "azure-openai" or "azureopenai" => "azure-openai",
            "ollama" => "ollama",
            "custom" or "openai-compatible" or "gateway" => "custom",
            "openai" => "openai",
            _ => string.IsNullOrWhiteSpace(p) ? "openai" : p!,
        };
    }
}