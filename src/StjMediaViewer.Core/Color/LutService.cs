using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// LUT 服务：内置 LUT 按公开公式生成到磁盘；用户 LUT 目录扫描；Sony + Log 素材自动匹配；
/// 预渲染（解码静帧 → 应用 LUT → JPEG，落缓存供界面按路径加载）。
/// </summary>
public sealed class LutService : ILutService
{
    private static readonly TimeSpan ScanTtl = TimeSpan.FromSeconds(10);

    private readonly ISettingsService _settings;
    private readonly IMediaDecoder _decoder;
    private readonly IImageEncoder _encoder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<LutEntry> _entries = new();
    private readonly Dictionary<string, CubeLut> _cubes = new(StringComparer.Ordinal);
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private Task<int>? _ensureTask;

    public LutService(ISettingsService settings, IMediaDecoder decoder, IImageEncoder encoder)
    {
        _settings = settings;
        _decoder = decoder;
        _encoder = encoder;

        var root = Path.Combine(settings.AppDataDirectory, "luts");
        BuiltInFolder = Path.Combine(root, "builtin");

        var configured = settings.Current.LutFolderPath;
        UserFolder = string.IsNullOrWhiteSpace(configured) ? Path.Combine(root, "user") : configured!;
    }

    public string BuiltInFolder { get; }

    public string UserFolder { get; }

    // ==================================================================== 列表 / 扫描

    public async Task<IReadOnlyList<LutEntry>> ListAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await EnsureBuiltInsAsync(ct).ConfigureAwait(false);

        if (!forceRefresh && _entries.Count > 0 && DateTimeOffset.UtcNow - _lastScan < ScanTtl)
            return _entries;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _entries.Count > 0 && DateTimeOffset.UtcNow - _lastScan < ScanTtl)
                return _entries;

            Directory.CreateDirectory(UserFolder);
            var list = new List<LutEntry>();

            // 内置 LUT：id 用固定的 "builtin:<key>"，方便设置里保存默认值
            foreach (var def in BuiltInLuts.All)
            {
                var path = BuiltInLuts.PathOf(BuiltInFolder, def);
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                list.Add(new LutEntry
                {
                    Id = "builtin:" + def.Key,
                    Name = def.Name,
                    FilePath = path,
                    Size = BuiltInLuts.CubeSize,
                    Is1D = false,
                    Category = LutCategory.BuiltIn,
                    Brand = def.Brand,
                    LogProfile = def.LogProfile,
                    Description = def.Description,
                    FileSizeBytes = fi.Length,
                    ModifiedAt = fi.LastWriteTime,
                });
            }

            list.AddRange(LutCatalog.Scan(new[] { UserFolder }));
            _entries = list;
            _lastScan = DateTimeOffset.UtcNow;
            Log.Info($"[lut] 扫描完成：{list.Count} 条（内置 {list.Count(e => e.Category == LutCategory.BuiltIn)}，用户目录 {UserFolder}）");
            return _entries;
        }
        finally { _gate.Release(); }
    }

    private Task<int> EnsureBuiltInsAsync(CancellationToken ct)
    {
        // 并发调用合并成一次生成
        _ensureTask ??= BuiltInLuts.EnsureAsync(BuiltInFolder, ct);
        return _ensureTask;
    }

    public async Task<LutEntry?> GetAsync(string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var list = await ListAsync(ct: ct).ConfigureAwait(false);
        return list.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    }

    // ==================================================================== 导入 / 删除

    public async Task<IReadOnlyList<LutEntry>> ImportAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UserFolder);
        var imported = new List<string>();

        foreach (var source in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
            if (!source.EndsWith(".cube", StringComparison.OrdinalIgnoreCase)) continue;

            var target = UniquePath(UserFolder, Path.GetFileName(source));
            try
            {
                File.Copy(source, target, overwrite: false);
                imported.Add(target);
                Log.Info($"[lut] 导入 {source} → {target}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[lut] 导入失败 {source}: {ex.Message}");
            }
        }

        await ListAsync(forceRefresh: true, ct).ConfigureAwait(false);
        return _entries.Where(e => imported.Contains(e.FilePath, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public async Task<bool> DeleteAsync(LutEntry entry, CancellationToken ct = default)
    {
        if (entry is null) return false;
        if (entry.Category == LutCategory.BuiltIn) return false;   // 内置不允许删

        try
        {
            File.Delete(entry.FilePath);
            _cubes.Remove(entry.Id);
            await ListAsync(forceRefresh: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[lut] 删除失败 {entry.FilePath}: {ex.Message}");
            return false;
        }
    }

    // ==================================================================== 加载 / 匹配

    public async Task<CubeLut?> LoadCubeAsync(LutEntry entry, CancellationToken ct = default)
    {
        if (entry is null) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cubes.TryGetValue(entry.Id, out var cached)) return cached;
            var cube = await Task.Run(() => CubeLut.ParseFile(entry.FilePath), ct).ConfigureAwait(false);
            if (_cubes.Count > 64) _cubes.Clear();   // 33³ 大约 1.3MB/条，限制内存
            _cubes[entry.Id] = cube;
            return cube;
        }
        catch (Exception ex)
        {
            Log.Warn($"[lut] 解析失败 {entry.FilePath}: {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }

    public LutEntry? ResolveForAsset(MediaAsset asset)
    {
        if (asset is null) return null;

        // 1) 素材显式指定
        if (!string.IsNullOrWhiteSpace(asset.LutId))
        {
            var explicitEntry = _entries.FirstOrDefault(e => string.Equals(e.Id, asset.LutId, StringComparison.Ordinal));
            if (explicitEntry is not null) return explicitEntry;
        }

        // 2) 自动匹配：Sony 相机 + Log 素材 → 内置基准转换
        if (!_settings.Current.AutoAssignLutForSony) return null;
        var key = LutCatalog.AutoMatchBuiltInKey(asset);
        if (key is null) return null;
        return _entries.FirstOrDefault(e =>
            e.Category == LutCategory.BuiltIn &&
            string.Equals(e.Id, "builtin:" + key, StringComparison.Ordinal));
    }

    /// <summary>后台预热：让 ResolveForAsset 在首帧就有列表可用（启动时调用一次即可）。</summary>
    public Task WarmUpAsync() => ListAsync();

    // ==================================================================== 预渲染

    public async Task<string?> RenderPreviewAsync(MediaAsset asset, LutEntry lut, int maxSize,
        CancellationToken ct = default)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.FullPath)) return null;
        try
        {
            var cube = await LoadCubeAsync(lut, ct).ConfigureAwait(false);
            var image = await _decoder.DecodeAsync(asset.FullPath, maxSize, ct).ConfigureAwait(false);
            if (image is null) return null;

            if (cube is not null) image = LutApplier.Apply(image, cube);

            var jpeg = await _encoder.EncodeJpegAsync(image, 88, ct).ConfigureAwait(false);
            if (jpeg is null || jpeg.Length == 0) return null;

            var dir = Path.Combine(_settings.AppDataDirectory, "Cache", "LutPreview");
            Directory.CreateDirectory(dir);
            var hash = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(asset.Id + "|" + lut.Id + "|" + maxSize));
            var path = Path.Combine(dir, Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".jpg");

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, jpeg, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[lut] 预渲染失败 {asset.FileName} × {lut.Name}: {ex.Message}");
            return null;
        }
    }
}