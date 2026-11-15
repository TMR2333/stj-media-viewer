using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Write;

/// <summary>
/// 文件级批量操作：按模板分目录导出 / 批量重命名 / 回收站删除。
/// 模板变量：{yyyy} {MM} {dd} {HH} {mm} {ss} {date} {city} {device} {format} {kind} {theme} {tone} {name} {ext} {seq}
/// </summary>
public sealed class AssetOperations : IAssetOperations
{
    private readonly IAssetStore? _store;

    public AssetOperations(IAssetStore? store = null) => _store = store;

    // ------------------------------------------------------------------ 导出

    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<MediaAsset> assets,
        string destinationRoot,
        string? subfolderTemplate,
        bool moveInsteadOfCopy,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ExportResult { Total = assets.Count };
        Directory.CreateDirectory(destinationRoot);
        var done = 0;

        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(asset.FullPath)) { result.Skipped++; result.Errors.Add($"不存在: {asset.FullPath}"); continue; }

                var relative = string.IsNullOrWhiteSpace(subfolderTemplate)
                    ? ""
                    : ExpandTemplate(subfolderTemplate!, asset, done + 1);
                var targetDir = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetDir);

                var target = PathUtil.MakeUnique(targetDir, asset.FileName);
                if (moveInsteadOfCopy)
                {
                    File.Move(asset.FullPath, target, overwrite: false);
                    if (_store is not null)
                    {
                        asset.FullPath = target;
                        await _store.UpsertAsync(asset, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    File.Copy(asset.FullPath, target, overwrite: false);
                }

                // 边车一起带走
                foreach (var sidecar in asset.Sidecars.Where(File.Exists))
                {
                    var sidecarTarget = Path.Combine(targetDir, Path.GetFileName(sidecar));
                    if (!File.Exists(sidecarTarget) && !string.Equals(sidecarTarget, sidecar, StringComparison.OrdinalIgnoreCase))
                    {
                        if (moveInsteadOfCopy) File.Move(sidecar, sidecarTarget, overwrite: true);
                        else File.Copy(sidecar, sidecarTarget, overwrite: true);
                    }
                }

                result.Copied++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{asset.FileName}: {ex.Message}");
                Log.Warn($"导出失败 {asset.FullPath}", ex);
            }
            finally
            {
                done++;
                progress?.Report(done);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ 重命名

    public Task<IReadOnlyList<string>> PreviewRenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, CancellationToken ct = default)
    {
        var names = new List<string>(assets.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var asset in assets)
        {
            index++;
            var name = BuildNewFileName(asset, pattern, index);
            var candidate = name;
            var n = 1;
            while (!used.Add(candidate)) candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n++}){Path.GetExtension(name)}";
            names.Add(candidate);
        }
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task<ExportResult> RenameAsync(IReadOnlyList<MediaAsset> assets, string pattern, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new ExportResult { Total = assets.Count };
        var planned = await PreviewRenameAsync(assets, pattern, ct).ConfigureAwait(false);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        for (int i = 0; i < assets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var asset = assets[i];
            try
            {
                if (!File.Exists(asset.FullPath)) { result.Skipped++; continue; }
                var dir = Path.GetDirectoryName(asset.FullPath)!;
                var target = Path.Combine(dir, planned[i]);
                if (!usedTargets.Add(target)) { result.Skipped++; continue; }
                if (string.Equals(target, asset.FullPath, StringComparison.OrdinalIgnoreCase)) { result.Skipped++; continue; }
                if (File.Exists(target)) target = PathUtil.MakeUnique(dir, planned[i]);

                var oldXmp = asset.XmpPath;
                File.Move(asset.FullPath, target);
                asset.FullPath = target;
                asset.FileName = Path.GetFileName(target);

                if (!string.IsNullOrWhiteSpace(oldXmp) && File.Exists(oldXmp))
                {
                    var newXmp = XmpWriter.ResolveSidecarPath(asset);
                    File.Move(oldXmp, newXmp, overwrite: true);
                    asset.XmpPath = newXmp;
                }
                if (_store is not null) await _store.UpsertAsync(asset, ct).ConfigureAwait(false);
                result.Copied++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{asset.FileName}: {ex.Message}");
                Log.Warn($"重命名失败 {asset.FullPath}", ex);
            }
            finally
            {
                done++;
                progress?.Report(done);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ 删除

    public Task<int> DeleteToRecycleBinAsync(IReadOnlyList<MediaAsset> assets, CancellationToken ct = default)
    {
        var paths = assets.Select(a => a.FullPath).Where(File.Exists).ToArray();
        if (paths.Length == 0) return Task.FromResult(0);

        var ok = 0;
        foreach (var path in paths)
        {
            if (SendToRecycleBin(path)) ok++;
        }
        if (ok > 0 && _store is not null)
        {
            var ids = assets.Where(a => File.Exists(a.FullPath) is false).Select(a => a.Id).ToList();
            if (ids.Count > 0) _ = _store.DeleteAsync(ids, ct);
        }
        return Task.FromResult(ok);
    }

    private static bool SendToRecycleBin(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            var rc = SHFileOperationW(ref op);
            if (rc != 0) Log.Warn($"回收站删除失败 rc={rc}: {path}");
            return rc == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Warn("回收站 P/Invoke 失败，降级为普通删除", ex);
            try { File.Delete(path); return true; } catch { return false; }
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    // ------------------------------------------------------------------ 模板

    private static string BuildNewFileName(MediaAsset asset, string pattern, int sequence)
    {
        var expanded = ExpandTemplate(pattern, asset, sequence);
        var ext = Path.GetExtension(asset.FileName);
        if (!expanded.Contains('.') || !expanded.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            expanded += ext;
        return PathUtil.SafeFileName(expanded);
    }

    private static string ExpandTemplate(string pattern, MediaAsset asset, int sequence)
    {
        var dt = asset.CapturedAt ?? asset.FileModified;
        var sb = new StringBuilder(pattern);
        sb.Replace("{yyyy}", dt.ToString("yyyy", CultureInfo.InvariantCulture));
        sb.Replace("{MM}", dt.ToString("MM", CultureInfo.InvariantCulture));
        sb.Replace("{dd}", dt.ToString("dd", CultureInfo.InvariantCulture));
        sb.Replace("{HH}", dt.ToString("HH", CultureInfo.InvariantCulture));
        sb.Replace("{mm}", dt.ToString("mm", CultureInfo.InvariantCulture));
        sb.Replace("{ss}", dt.ToString("ss", CultureInfo.InvariantCulture));
        sb.Replace("{date}", dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Replace("{datetime}", dt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
        sb.Replace("{city}", string.IsNullOrWhiteSpace(asset.City) ? "未标注" : asset.City!);
        sb.Replace("{country}", string.IsNullOrWhiteSpace(asset.Country) ? "未标注" : asset.Country!);
        sb.Replace("{device}", Safe(asset.DisplayDevice));
        sb.Replace("{format}", string.IsNullOrWhiteSpace(asset.FormatCode) ? "UNKNOWN" : asset.FormatCode);
        sb.Replace("{kind}", asset.Kind switch { MediaKind.Image => "图片", MediaKind.Video => "视频", MediaKind.Audio => "音频", _ => "其他" });
        sb.Replace("{theme}", string.IsNullOrWhiteSpace(asset.Theme) ? "未分类" : asset.Theme!);
        sb.Replace("{tone}", string.IsNullOrWhiteSpace(asset.Tone) ? "未分类" : asset.Tone!);
        sb.Replace("{name}", Path.GetFileNameWithoutExtension(asset.FileName));
        sb.Replace("{ext}", Path.GetExtension(asset.FileName).TrimStart('.'));
        sb.Replace("{seq}", sequence.ToString("D4", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string Safe(string s) =>
        string.Join("_", s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(' ', '_');
}