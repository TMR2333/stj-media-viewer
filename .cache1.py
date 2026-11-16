import io

# ================= 1) Settings 字段 =================
p = 'src/StjMediaViewer.Core/Models/Settings.cs'
s = io.open(p, encoding='utf-8').read()
if 'CacheDirectory' not in s:
    s = s.replace('''    public int ThumbnailCacheMb { get; set; } = 512;''','''    public int ThumbnailCacheMb { get; set; } = 512;

    /// <summary>缩略图缓存根目录；null/空 = 默认 %LOCALAPPDATA%\\StjMediaViewer\\Cache。</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>自动清理缓存（启动时 + 按间隔）。</summary>
    public bool CacheAutoCleanEnabled { get; set; } = true;

    /// <summary>缓存文件最多保留多少天（0 = 不限）。</summary>
    public int CacheMaxAgeDays { get; set; } = 90;

    /// <summary>自动清理间隔（小时）。</summary>
    public int CacheCleanIntervalHours { get; set; } = 6;''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('settings: cache fields')

# ================= 2) Normalize =================
p = 'src/StjMediaViewer.Core/Settings/JsonSettingsService.cs'
s = io.open(p, encoding='utf-8').read()
if 's.CacheMaxAgeDays' not in s:
    anchor = '''        s.ThumbnailCacheMb = Math.Clamp(s.ThumbnailCacheMb <= 0 ? 512 : s.ThumbnailCacheMb, 64, 65536);'''
    assert anchor in s
    s = s.replace(anchor, anchor + '''

        s.CacheDirectory = string.IsNullOrWhiteSpace(s.CacheDirectory) ? null : s.CacheDirectory.Trim();
        s.CacheMaxAgeDays = Math.Clamp(s.CacheMaxAgeDays, 0, 3650);
        s.CacheCleanIntervalHours = Math.Clamp(s.CacheCleanIntervalHours <= 0 ? 6 : s.CacheCleanIntervalHours, 1, 168);''', 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('settings: cache normalize')

# ================= 3) AppServices：缓存路径 + 热切换 + 清理 =================
p = 'src/StjMediaViewer.App/Services/AppServices.cs'
s = io.open(p, encoding='utf-8').read()
if 'ReloadThumbnailCache' not in s:
    s = s.replace('''    public static string ThumbnailCacheDirectory => Path.Combine(Settings.AppDataDirectory, "Cache", "thumbs");''','''    /// <summary>缓存根目录：自定义路径优先，否则 %LOCALAPPDATA%\\StjMediaViewer\\Cache。</summary>
    public static string CacheRootDirectory
    {
        get
        {
            var custom = Settings.Current.CacheDirectory;
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Settings.AppDataDirectory, "Cache")
                : Path.GetFullPath(custom!);
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
    }''')
    if 'using StjMediaViewer.Core.Media;' not in s:
        s = s.replace('using StjMediaViewer.Core.Models;', 'using StjMediaViewer.Core.Media;\nusing StjMediaViewer.Core.Models;')
    io.open(p, 'w', encoding='utf-8').write(s)
    print('appservices: cache path + reload + clean')
PYEOF_MARK