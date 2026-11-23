using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Cloud;

/// <summary>
/// "云端挂载成本地目录"适配器：OneDrive / 坚果云客户端同步目录 / SMB 映射盘 / 已挂载的 NAS 共享。
/// 这是覆盖面最广的接入方式（用户装好官方客户端即可），实现也最稳：普通文件系统操作。
/// </summary>
public sealed class LocalFolderCloudStorage : ICloudStorage
{
    private readonly string _root;

    public LocalFolderCloudStorage(CloudTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.RemoteFolderPath))
            throw new InvalidOperationException("没有配置云端挂载目录");

        _root = Path.GetFullPath(target.RemoteFolderPath);
        if (!string.IsNullOrWhiteSpace(target.RemoteSubPath))
            _root = Path.Combine(_root, Normalize(target.RemoteSubPath));
    }

    public CloudProviderKind Kind => CloudProviderKind.LocalFolder;

    public string Root => _root;

    public Task<bool> TestAsync(out string? error, CancellationToken ct = default)
    {
        error = null;
        try
        {
            if (!Directory.Exists(_root))
            {
                error = $"云端目录不存在：{_root}";
                return Task.FromResult(false);
            }
            // 尝试写一个探针文件，确认有写权限（读不出/写不进都算不可用）
            var probe = Path.Combine(_root, ".stj-sync-probe");
            File.WriteAllText(probe, DateTimeOffset.Now.ToString("O"));
            File.Delete(probe);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Task.FromResult(false);
        }
    }

    public Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<CloudEntry>>(() =>
        {
            var list = new List<CloudEntry>();
            if (!Directory.Exists(_root)) return list;

            var dirs = new Stack<string>();
            dirs.Push(_root);
            while (dirs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                progress?.Report(dir);

                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception ex)
                {
                    Log.Info($"[cloud] 列出目录失败 {dir}: {ex.Message}");
                    subs = Array.Empty<string>();
                }
                foreach (var sub in subs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.')) continue;
                    dirs.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { files = Array.Empty<string>(); }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".stj-", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var fi = new FileInfo(file);
                        list.Add(new CloudEntry
                        {
                            RelativePath = ToRelative(file),
                            SizeBytes = fi.Length,
                            ModifiedUtc = fi.LastWriteTimeUtc,
                        });
                    }
                    catch { /* 文件正在被写：跳过，下一轮再说 */ }
                }
            }
            return list;
        }, ct);
    }

    public Task<IReadOnlyList<CloudEntry>> ListFolderAsync(string relativeDir, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, Normalize(relativeDir));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<CloudEntry>>(Array.Empty<CloudEntry>());
        var prefix = Normalize(relativeDir);
        var result = new List<CloudEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var info = new FileInfo(path);
            var isDir = Directory.Exists(path);
            var relative = prefix.Length == 0 ? info.Name : prefix + "/" + info.Name;
            result.Add(new CloudEntry
            {
                RelativePath = relative.Replace('\\', '/'),
                IsDirectory = isDir,
                SizeBytes = isDir ? 0 : info.Length,
                ModifiedUtc = info.LastWriteTimeUtc,
            });
        }
        result.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase);
        });
        return Task.FromResult<IReadOnlyList<CloudEntry>>(result);
    }

    public Task<byte[]?> ReadRangeAsync(string relativePath, long offset, int length, CancellationToken ct = default)
    {
        var full = Path.Combine(_root, Normalize(relativePath));
        if (!File.Exists(full) || offset < 0 || length <= 0) return Task.FromResult<byte[]?>(null);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset >= fs.Length) return Task.FromResult<byte[]?>(Array.Empty<byte>());
        var count = (int)Math.Min(length, fs.Length - offset);
        var buffer = new byte[count];
        fs.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return Task.FromResult<byte[]?>(read == count ? buffer : buffer[..read]);
    }

    public Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default)
    {
        var src = ToAbsolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var tmp = localPath + ".stj-tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, localPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default)
    {
        var dst = ToAbsolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + ".stj-tmp";
        File.Copy(localPath, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromRelativePath) || string.IsNullOrWhiteSpace(toRelativePath))
            throw new IOException("移动路径不能为空");

        var from = ToAbsolute(fromRelativePath);
        var to = ToAbsolute(toRelativePath);
        var dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (!File.Exists(from)) throw new FileNotFoundException("云端（挂载目录）里找不到文件", from);
        if (File.Exists(to)) throw new IOException("目标已存在：" + toRelativePath);

        File.Move(from, to, overwrite: false);
        return Task.CompletedTask;
    }

    public Task EnsureFolderAsync(string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.CompletedTask;
        Directory.CreateDirectory(ToAbsolute(relativePath));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.CompletedTask;
        try
        {
            var full = ToAbsolute(relativePath);
            if (File.Exists(full)) File.Delete(full);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"删除失败：{ex.Message}", ex);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------ 路径

    private string ToRelative(string absolute)
    {
        var rel = Path.GetRelativePath(_root, absolute);
        return rel.Replace('\\', '/');
    }

    private string ToAbsolute(string relative)
    {
        var normalized = Normalize(relative);
        var full = Path.GetFullPath(Path.Combine(_root, normalized));
        // 防目录穿越（同步路径来自配置，但云端条目仍可能带 ../）
        if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"非法同步路径：{relative}");
        return full;
    }

    private static string Normalize(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}