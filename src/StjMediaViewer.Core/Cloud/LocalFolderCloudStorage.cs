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

    public Task EnsureFolderAsync(string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.CompletedTask;
        Directory.CreateDirectory(ToAbsolute(relativePath));
        return Task.CompletedTask;
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