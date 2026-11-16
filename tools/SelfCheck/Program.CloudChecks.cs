using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 自检的「WebDAV 云同步」部分（partial，复用 Program 的 Check/Section 助手）。
///
/// 用 <c>tools/MockWebDav/server.py</c> 起一个真实的本地 WebDAV 服务器（随机端口 8899–8999），
/// 然后让 <see cref="WebDavCloudStorage"/> 走真实 HTTP 完成：
/// 连通性自检 → 错误密码 → 上传（中文名 + 子目录）→ 列目录 → 下载 → SHA-256 比对。
///
/// 依赖：系统 <c>python</c>（3.x）。可用环境变量 <c>STJ_PYTHON</c> 指定解释器路径。
/// </summary>
public static partial class Program
{
    private static async Task CheckWebDav(Services s)
    {
        var repoRoot = FindRepoRoot();
        var serverScript = Path.Combine(repoRoot, "tools", "MockWebDav", "server.py");
        if (!File.Exists(serverScript))
        {
            Check("找到 Mock WebDAV 服务器脚本", false, serverScript);
            return;
        }
        Check("找到 Mock WebDAV 服务器脚本", true, serverScript);

        var python = ResolvePythonExecutable();
        if (python is null)
        {
            Check("找到可用的 Python 3", false, "PATH 里没有可用的 python；可用环境变量 STJ_PYTHON 指定解释器");
            return;
        }
        Check("找到可用的 Python 3", true, python);

        var port = PickFreeWebDavPort();
        var tempRoot = Path.Combine(Path.GetTempPath(), "stj-webdav-check-" + Guid.NewGuid().ToString("N")[..8]);
        var remoteRoot = Path.Combine(tempRoot, "remote");
        var localRoot = Path.Combine(tempRoot, "local");
        Directory.CreateDirectory(remoteRoot);
        Directory.CreateDirectory(localRoot);

        Process? serverProcess = null;
        var serverLog = new StringBuilder();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repoRoot,
            };
            foreach (var argument in new[]
                     {
                         serverScript,
                         "--port", port.ToString(CultureInfo.InvariantCulture),
                         "--root", remoteRoot,
                         "--user", "test",
                         "--password", "test123",
                         "--quiet",
                     })
                startInfo.ArgumentList.Add(argument);

            serverProcess = new Process { StartInfo = startInfo };
            serverProcess.OutputDataReceived += (_, e) => AppendLog(serverLog, e.Data);
            serverProcess.ErrorDataReceived += (_, e) => AppendLog(serverLog, e.Data);
            serverProcess.Start();
            serverProcess.BeginOutputReadLine();
            serverProcess.BeginErrorReadLine();

            var ready = await WaitForWebDavAsync(port, serverProcess, TimeSpan.FromSeconds(12));
            Check("Mock WebDAV 服务器启动并就绪", ready,
                $"http://127.0.0.1:{port}/ · pid {(ready && serverProcess is not null ? serverProcess.Id.ToString(CultureInfo.InvariantCulture) : "?")}");
            if (!ready)
            {
                var log = SnapshotLog(serverLog);
                if (log.Length > 0) Console.WriteLine("   服务器输出: " + log);
                return;
            }

            var password = ProtectWebDavPassword(s, "test123");
            Check("密码以 dpapi: 密文交给适配器",
                password is not null && password.StartsWith("dpapi:", StringComparison.Ordinal), password ?? "null");

            var target = new CloudTarget
            {
                Name = "mock-webdav",
                Kind = CloudProviderKind.WebDav,
                WebDavEndpoint = $"http://127.0.0.1:{port}/",
                WebDavUsername = "test",
                WebDavPasswordProtected = password,
            };

            await using var storage = new WebDavCloudStorage(target);

            // ---- 1) 连通性 ----
            var connected = await storage.TestAsync(out var testError);
            Check("TestAsync 连通性自检通过", connected, testError ?? "HTTP 207");

            // ---- 2) 错误密码必须 false 且给出可读原因 ----
            var badTarget = new CloudTarget
            {
                Name = "mock-webdav-bad-password",
                Kind = CloudProviderKind.WebDav,
                WebDavEndpoint = target.WebDavEndpoint,
                WebDavUsername = "test",
                WebDavPasswordProtected = ProtectWebDavPassword(s, "wrong-password"),
            };
            await using (var badStorage = new WebDavCloudStorage(badTarget))
            {
                var badOk = await badStorage.TestAsync(out var badError);
                Check("错误密码 TestAsync 返回 false", !badOk);
                Check("错误密码提示可读", !string.IsNullOrWhiteSpace(badError) &&
                                        badError!.Contains("密码", StringComparison.Ordinal), badError ?? "null");
            }

            // ---- 3) 不存在的同步子目录：404 也要翻译成用户能懂的话 ----
            var missingTarget = new CloudTarget
            {
                Name = "mock-webdav-missing",
                Kind = CloudProviderKind.WebDav,
                WebDavEndpoint = target.WebDavEndpoint,
                WebDavUsername = "test",
                WebDavPasswordProtected = password,
                RemoteSubPath = "不存在的目录",
            };
            await using (var missingStorage = new WebDavCloudStorage(missingTarget))
            {
                var missingOk = await missingStorage.TestAsync(out var missingError);
                Check("同步路径不存在 TestAsync 返回 false", !missingOk);
                Check("同步路径不存在提示可读",
                    missingError?.Contains("同步路径不存在", StringComparison.Ordinal) == true, missingError ?? "null");
            }

            // ---- 4) 上传 3 个文件：中文名 / 两层子目录 / 普通文件 ----
            var samples = new (string RelativePath, byte[] Bytes)[]
            {
                ("测试 照片.jpg", MakeWebDavTestBytes(12_345, seed: 1)),
                ("2026/01/IMG_0001.jpg", MakeWebDavTestBytes(23_456, seed: 2)),
                ("clip.mp4", MakeWebDavTestBytes(3_456, seed: 3)),
            };
            var localPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (relativePath, bytes) in samples)
            {
                var localPath = Path.Combine(localRoot, "upload", relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await File.WriteAllBytesAsync(localPath, bytes);
                localPaths[relativePath] = localPath;
                await storage.UploadAsync(localPath, relativePath);
            }
            Check("上传 3 个文件（含中文名与子目录）", true, string.Join(" / ", samples.Select(x => x.RelativePath)));

            // ---- 5) 列目录：数量 / 路径 / 大小 / 时间 / 进度 ----
            var progress = new CaptureProgress<string>();
            var entries = await storage.ListAllAsync(progress);
            Check("ListAllAsync 返回 3 个文件", entries.Count == 3,
                string.Join(", ", entries.Select(e => $"{e.RelativePath}({e.SizeBytes})")));
            Check("只返回文件（目录不下发）", entries.All(e => !e.IsDirectory));
            Check("中文文件名相对路径正确",
                entries.Any(e => e.RelativePath == "测试 照片.jpg"));
            Check("子目录相对路径统一用 '/' 且无前导斜杠",
                entries.Any(e => e.RelativePath == "2026/01/IMG_0001.jpg"),
                string.Join(" | ", entries.Select(e => e.RelativePath)));
            Check("每个条目大小正确",
                samples.All(sample => entries.Any(e =>
                    e.RelativePath == sample.RelativePath && e.SizeBytes == sample.Bytes.Length)),
                string.Join(", ", entries.Select(e => $"{e.RelativePath}={e.SizeBytes}")));
            Check("modified 是合理的 UTC 时间",
                entries.All(e => e.ModifiedUtc > DateTimeOffset.UtcNow.AddDays(-2) &&
                                 e.ModifiedUtc < DateTimeOffset.UtcNow.AddMinutes(10)),
                entries.Count > 0 ? entries[0].ModifiedUtc.ToString("O", CultureInfo.InvariantCulture) : "无条目");
            Check("进度报文包含“正在列出”",
                progress.Items.Any(message => message.Contains("正在列出", StringComparison.Ordinal)),
                progress.Items.FirstOrDefault() ?? "无进度");

            // ---- 6) 下载 + SHA-256 比对 ----
            var cnEntry = entries.First(e => e.RelativePath == "测试 照片.jpg");
            var cnDownloaded = Path.Combine(localRoot, "download", "测试 照片.jpg");
            await storage.DownloadAsync(cnEntry.RelativePath, cnDownloaded);
            Check("下载中文名文件落盘", File.Exists(cnDownloaded));
            Check("下载内容 SHA-256 与源一致",
                FileHasher.FullHash(localPaths[cnEntry.RelativePath]) == FileHasher.FullHash(cnDownloaded));
            Check("下载未残留 .tmp 文件", !File.Exists(cnDownloaded + ".tmp"));

            var nestedEntry = entries.First(e => e.RelativePath == "2026/01/IMG_0001.jpg");
            var nestedDownloaded = Path.Combine(localRoot, "download", "nested", "IMG_0001.jpg");
            await storage.DownloadAsync(nestedEntry.RelativePath, nestedDownloaded);
            Check("子目录文件下载后哈希一致",
                FileHasher.FullHash(localPaths[nestedEntry.RelativePath]) == FileHasher.FullHash(nestedDownloaded),
                $"{new FileInfo(nestedDownloaded).Length} 字节");

            // ---- 7) EnsureFolderAsync 幂等：已存在目录 MKCOL 405 必须算成功 ----
            await storage.EnsureFolderAsync("2026/01");
            await storage.EnsureFolderAsync("空目录/子目录");
            await storage.EnsureFolderAsync("空目录/子目录");
            Check("EnsureFolderAsync 幂等且能建多级目录", true, "2026/01 · 空目录/子目录");
        }
        catch (Exception ex)
        {
            Check("WebDAV 自检未抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
            var log = SnapshotLog(serverLog);
            if (log.Length > 0) Console.WriteLine("   服务器输出: " + log);
        }
        finally
        {
            KillMockServer(serverProcess);
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // ==================================================================================
    //  辅助：Python 定位
    // ==================================================================================

    private static string? ProtectWebDavPassword(Services s, string plainText)
    {
        var cipher = s.Settings.ProtectSecret(plainText);
        if (string.IsNullOrWhiteSpace(cipher)) return null;
        return cipher.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase) ? cipher : "dpapi:" + cipher;
    }

    private static string? ResolvePythonExecutable()
    {
        // 1) 显式指定优先
        var configured = Environment.GetEnvironmentVariable("STJ_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured) && ProbePython(configured))
            return configured;

        // 2) PATH 上的 python / python3（要真的能跑；Windows 上常见"未安装的 Store 别名"会返回非 0）
        foreach (var name in new[] { "python", "python3" })
        {
            var path = FindOnPath(name);
            if (path is not null && ProbePython(path)) return path;
        }

        // 3) 常见安装位置（含 DSH 运行时自带的 Python）
        foreach (var candidate in PythonFallbackCandidates())
        {
            if (File.Exists(candidate) && ProbePython(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> PythonFallbackCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var root in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Python"),
                     programFiles,
                 })
        {
            foreach (var directory in SafeEnumerateDirectories(root, "Python3*"))
                yield return Path.Combine(directory, "python.exe");
        }

        // DSH 运行时目录：<user>/.dsh/dsh-runtimes/<runtime>/dependencies/python/python.exe
        var runtimes = Path.Combine(userProfile, ".dsh", "dsh-runtimes");
        foreach (var runtime in SafeEnumerateDirectories(runtimes, "*"))
            yield return Path.Combine(runtime, "dependencies", "python", "python.exe");
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, pattern).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool ProbePython(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && output.Contains("Python 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var names = OperatingSystem.IsWindows()
            ? new[] { fileName, fileName + ".exe" }
            : new[] { fileName };

        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // PATH 里可能有非法路径片段，跳过继续找。
                }
            }
        }
        return null;
    }

    // ==================================================================================
    //  辅助：服务器进程 / 端口 / 进度
    // ==================================================================================

    private static int PickFreeWebDavPort()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var port = random.Next(8899, 9000);
            if (IsTcpPortFree(port)) return port;
        }
        // 兜底：交给系统分配（极少走到）。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsTcpPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForWebDavAsync(int port, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited) return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await client.ConnectAsync(IPAddress.Loopback, port, connectTimeout.Token);
                if (client.Connected) return true;
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            await Task.Delay(100);
        }
        return false;
    }

    private static void KillMockServer(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // 进程可能已经自己退了：忽略。
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private static void AppendLog(StringBuilder builder, string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (builder)
        {
            if (builder.Length < 4000) builder.AppendLine(line);
        }
    }

    private static string SnapshotLog(StringBuilder builder)
    {
        lock (builder) return builder.ToString().Trim();
    }

    private static byte[] MakeWebDavTestBytes(int size, int seed)
    {
        var bytes = new byte[size];
        var state = (uint)(seed * 2654435761u + 1u);
        for (var i = 0; i < size; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }

    /// <summary>同步捕获进度报文；<see cref="Progress{T}"/> 会异步投递，断言时可能还没到齐。</summary>
    private sealed class CaptureProgress<T> : IProgress<T>
    {
        private readonly List<T> _items = new();

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items) return _items.ToList();
            }
        }

        public void Report(T value)
        {
            lock (_items) _items.Add(value);
        }
    }
}