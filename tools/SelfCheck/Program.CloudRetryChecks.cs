using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 云同步断线重试：坚果云等 WebDAV 在连续上传时会主动断连（"远程主机强迫关闭了一个现有的连接"），
    /// 必须能自动退避重试，而不是直接算失败。
    /// </summary>
    private static async Task CheckCloudRetry(Services s, string tempData)
    {
        // 1) 瞬时错误判定
        Check("重试：连接被关闭算瞬时错误",
              CloudSyncService.IsTransient(new IOException("远程主机强迫关闭了一个现有的连接。")));
        Check("重试：响应提前结束算瞬时错误",
              CloudSyncService.IsTransient(new HttpRequestException("The response ended prematurely. (ResponseEnded)")));
        Check("重试：文件不存在不算瞬时错误",
              !CloudSyncService.IsTransient(new FileNotFoundException("本地文件不存在：x.jpg")));
        Check("重试：鉴权失败不算瞬时错误",
              !CloudSyncService.IsTransient(new IOException("没有权限上传该文件")));

        // 2) 端到端：前两次上传被"服务器断连"，第三次成功 → 报告应为成功
        var root = Path.Combine(tempData, "retry-sync", "local");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "shot.jpg"), new byte[256]);

        var fake = new FlakyStorage(failuresBeforeSuccess: 2);
        var service = new CloudSyncService(_ => fake, store: s.Store, xmp: s.Xmp);
        var target = new CloudTarget
        {
            Id = "selfcheck-retry",
            Name = "断线重试",
            Kind = CloudProviderKind.WebDav,
            LocalFolderPath = root,
            RemoteFolderPath = "/",
        };

        var plan = new CloudSyncPlan { Target = target };
        plan.Actions.Add(new CloudSyncAction
        {
            Kind = CloudActionKind.Upload,
            RelativePath = "shot.jpg",
            SizeBytes = 256,
        });

        var report = await service.ExecuteAsync(plan);
        Check("重试：失败两次后仍然上传成功",
              report.Failed == 0 && report.Uploaded == 1 && fake.UploadAttempts == 3,
              $"失败 {report.Failed} / 上传 {report.Uploaded} / 尝试 {fake.UploadAttempts} 次");

        // 3) 三次都失败 → 失败信息里带可操作提示
        var alwaysFail = new FlakyStorage(failuresBeforeSuccess: int.MaxValue);
        var service2 = new CloudSyncService(_ => alwaysFail, store: s.Store, xmp: s.Xmp);
        var report2 = await service2.ExecuteAsync(plan);
        Check("重试：一直失败时报告失败数与提示",
              report2.Failed == 1 && report2.Messages.Any(m => m.Contains("限流", StringComparison.Ordinal)),
              $"失败 {report2.Failed} / 尝试 {alwaysFail.UploadAttempts} 次");
    }

    /// <summary>前 N 次上传抛"远程主机强迫关闭"的假存储。</summary>
    private sealed class FlakyStorage : ICloudStorage
    {
        private readonly int _failuresBeforeSuccess;
        public int UploadAttempts { get; private set; }

        public FlakyStorage(int failuresBeforeSuccess) => _failuresBeforeSuccess = failuresBeforeSuccess;

        public CloudProviderKind Kind => CloudProviderKind.WebDav;

        public Task<bool> TestAsync(out string? error, CancellationToken ct = default)
        {
            error = null;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudEntry>>(Array.Empty<CloudEntry>());

        public Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default)
        {
            UploadAttempts++;
            if (UploadAttempts <= _failuresBeforeSuccess)
                throw new IOException("远程主机强迫关闭了一个现有的连接。");
            return Task.CompletedTask;
        }

        public Task EnsureFolderAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}