using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>按筛选上传：只把符合条件（评分 / RAW）的素材传到云端目标。</summary>
    private static async Task CheckFilteredUpload(Services s, string tempData)
    {
        var local = Path.Combine(tempData, "filtered-local");
        var remote = Path.Combine(tempData, "filtered-remote");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(remote);

        // 4 个素材：高分 JPG、低分 JPG、高分 RAW、低分 RAW
        var specs = new[]
        {
            ("fu-jpg-hi", "hi.jpg", 5, false),
            ("fu-jpg-lo", "lo.jpg", 1, false),
            ("fu-raw-hi", "hi.raw", 4, true),
            ("fu-raw-lo", "lo.raw", 2, true),
        };
        var lib = await s.Library.AddLibraryAsync(local);
        var assets = new List<MediaAsset>();
        foreach (var (id, name, rating, isRaw) in specs)
        {
            var path = Path.Combine(local, name);
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
            var asset = new MediaAsset
            {
                Id = id,
                LibraryId = lib.Id,
                FullPath = path,
                RelativePath = name,
                FileName = name,
                Kind = MediaKind.Image,
                FormatCode = isRaw ? "ARW" : "JPG",
                RawFormat = isRaw ? RawFormat.Arw : RawFormat.None,
                SizeBytes = 4,
                Rating = rating,
                FileModified = DateTimeOffset.Now,
                FileCreated = DateTimeOffset.Now,
            };
            if (id == "fu-jpg-hi")
            {
                var side = path + ".xmp";
                await File.WriteAllTextAsync(side, "<x:xmpmeta/>");
                asset.Sidecars.Add(side);
            }
            assets.Add(asset);
        }
        await s.Store.UpsertManyAsync(assets);

        var target = new CloudTarget
        {
            Kind = CloudProviderKind.LocalFolder,
            Name = "筛选上传测试",
            LocalFolderPath = local,
            RemoteFolderPath = remote,
            Direction = CloudSyncDirection.UploadOnly,
        };

        var svc = new CloudSyncService(store: s.Store);
        var report = await svc.UploadFilteredAsync(target, new AssetQuery { MinRating = 4, Limit = 0 });
        var uploaded = report.Uploaded;
        Check("按筛选上传：评分筛选生效",
              uploaded >= 2 &&
              File.Exists(Path.Combine(remote, "hi.jpg")) &&
              File.Exists(Path.Combine(remote, "hi.raw")) &&
              !File.Exists(Path.Combine(remote, "lo.jpg")) &&
              !File.Exists(Path.Combine(remote, "lo.raw")),
              $"{report.Summary}");

        Directory.CreateDirectory(remote);
        var reportRaw = await svc.UploadFilteredAsync(target, new AssetQuery { IsRaw = true, Limit = 0 });
        var raws = Directory.Exists(remote) ? Directory.GetFiles(remote).Select(Path.GetFileName).ToList() : new List<string?>();
        // 云端按三级目录存放：一级"文件格式" → 云端 JPG/xxx.jpg
        var layoutRemote = Path.Combine(tempData, "filtered-remote-layout");
        Directory.CreateDirectory(layoutRemote);
        var layoutTarget = new CloudTarget
        {
            Kind = CloudProviderKind.LocalFolder,
            Name = "筛选上传布局测试",
            LocalFolderPath = local,
            RemoteFolderPath = layoutRemote,
            Direction = CloudSyncDirection.UploadOnly,
        };
        var layoutReport = await svc.UploadFilteredAsync(
            layoutTarget, new AssetQuery { MinRating = 4, Limit = 0 }, progress: null,
            remoteLayout: new[] { StjMediaViewer.Core.Write.OrganizeDimension.Format });
        var layoutFiles = Directory.Exists(layoutRemote)
            ? Directory.GetFiles(layoutRemote, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList()
            : new List<string?>();
        Check("按筛选上传：云端按三级目录存放",
              layoutReport.Uploaded >= 2 && layoutFiles.Contains("hi.jpg") &&
              Directory.Exists(Path.Combine(layoutRemote, "JPG")),
              $"{layoutReport.Summary}（{string.Join(",", Directory.GetDirectories(layoutRemote).Select(Path.GetFileName))}）");
        Check("按筛选上传：边车跟随三级目录",
              File.Exists(Path.Combine(layoutRemote, "JPG", "hi.jpg.xmp")) &&
              !File.Exists(Path.Combine(layoutRemote, "hi.jpg.xmp")),
              "XMP 应在 JPG/ 下与主文件同目录");

        Check("按筛选上传：RAW 筛选生效",
              reportRaw.Uploaded == 2 && raws.Contains("hi.raw") && raws.Contains("lo.raw"),
              $"{reportRaw.Summary}（云端 {raws.Count} 个文件）");
        // 来自云同步：下载后素材应被标记 origin（素材库可按来源筛选）
        var originLocal = Path.Combine(tempData, "origin-local");
        var originRemote = Path.Combine(tempData, "origin-remote");
        Directory.CreateDirectory(originLocal);
        Directory.CreateDirectory(originRemote);
        await File.WriteAllTextAsync(Path.Combine(originRemote, "cloud.jpg"), "xyz");
        var originLib = await s.Library.AddLibraryAsync(originLocal);
        await s.Store.UpsertManyAsync(new[]
        {
            new MediaAsset
            {
                Id = "origin-asset", LibraryId = originLib.Id,
                FullPath = Path.Combine(originLocal, "cloud.jpg"),
                RelativePath = "cloud.jpg", FileName = "cloud.jpg",
                Kind = MediaKind.Image, FormatCode = "JPG",
                SizeBytes = 3, FileModified = DateTimeOffset.Now, FileCreated = DateTimeOffset.Now,
            },
        });
        var originTarget = new CloudTarget
        {
            Kind = CloudProviderKind.LocalFolder,
            Name = "我的云盘",
            LocalFolderPath = originLocal,
            RemoteFolderPath = originRemote,
            Direction = CloudSyncDirection.Bidirectional,
        };
        var originReport = await svc.SyncAsync(originTarget);
        var originAsset = await s.Store.GetByPathAsync(Path.Combine(originLocal, "cloud.jpg"));
        var originHits = await s.Store.QueryAsync(new AssetQuery { Origins = { "我的云盘" }, Limit = 0 });
        Check("素材库：下载的素材标记为「来自云同步」",
              originHits.Any(a => a.Id == "origin-asset"),
              $"{originReport.Summary} · 文件存在={File.Exists(Path.Combine(originLocal, "cloud.jpg"))} · origin={originAsset?.Origin ?? "(null)"} · 命中 {originHits.Count}");

        // 暂停：暂停中不继续上传，继续后能完成
        var pauseRemote = Path.Combine(tempData, "filtered-remote-pause");
        Directory.CreateDirectory(pauseRemote);
        var pauseTarget = new CloudTarget
        {
            Kind = CloudProviderKind.LocalFolder,
            Name = "暂停测试",
            LocalFolderPath = local,
            RemoteFolderPath = pauseRemote,
            Direction = CloudSyncDirection.UploadOnly,
        };
        var pause = new PauseTokenSource();
        pause.Pause();
        var task = svc.UploadFilteredAsync(pauseTarget, new AssetQuery { MinRating = 4, Limit = 0 },
            progress: null, remoteLayout: null, ct: default, pause: pause);
        await Task.Delay(400);
        var blocked = !task.IsCompleted;
        pause.Resume();
        var pausedReport = await task;
        Check("按筛选上传：支持暂停/继续", blocked && pausedReport.Uploaded >= 2,
              $"暂停时未完成={blocked}，继续后上传 {pausedReport.Uploaded}");

        // 取消：预先取消的令牌应抛 OperationCanceledException
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledOk = false;
        try
        {
            await svc.UploadFilteredAsync(pauseTarget, new AssetQuery { MinRating = 4, Limit = 0 },
                progress: null, remoteLayout: null, ct: canceled.Token);
        }
        catch (OperationCanceledException) { canceledOk = true; }
        Check("按筛选上传：支持取消", canceledOk, canceledOk ? "已按取消退出" : "未响应取消");
    }
}