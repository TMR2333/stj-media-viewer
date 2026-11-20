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
              uploaded == 2 &&
              File.Exists(Path.Combine(remote, "hi.jpg")) &&
              File.Exists(Path.Combine(remote, "hi.raw")) &&
              !File.Exists(Path.Combine(remote, "lo.jpg")) &&
              !File.Exists(Path.Combine(remote, "lo.raw")),
              $"{report.Summary}");

        Directory.CreateDirectory(remote);
        var reportRaw = await svc.UploadFilteredAsync(target, new AssetQuery { IsRaw = true, Limit = 0 });
        var raws = Directory.Exists(remote) ? Directory.GetFiles(remote).Select(Path.GetFileName).ToList() : new List<string?>();
        Check("按筛选上传：RAW 筛选生效",
              reportRaw.Uploaded == 2 && raws.Contains("hi.raw") && raws.Contains("lo.raw"),
              $"{reportRaw.Summary}（云端 {raws.Count} 个文件）");
    }
}