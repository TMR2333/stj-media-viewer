using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 云同步过滤：macOS AppleDouble（._xxx）、Thumbs.db、@eaDir、隐藏目录、App 缓存目录
    /// 都不应该被当成素材同步。
    /// </summary>
    private static async Task CheckCloudJunkFilter(Services s, string tempData)
    {
        var root = Path.Combine(tempData, "junk-filter", "library");
        var remote = Path.Combine(tempData, "junk-filter", "remote");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(remote);

        void Touch(string baseDir, string rel, int size = 32)
        {
            var path = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }

        // 正常素材
        Touch(root, "real.jpg");
        Touch(root, "sub/photo2.jpg");
        Touch(root, "sub/clip.mp4");

        // 垃圾文件 / 目录
        Touch(root, "._real.jpg");                // macOS AppleDouble
        Touch(root, ".DS_Store");
        Touch(root, "Thumbs.db");
        Touch(root, ".hidden-photo.jpg");
        Touch(root, "@eaDir/thumb.jpg");          // 群晖缩略图索引
        Touch(root, "$RECYCLE.BIN/old.jpg");
        Touch(root, "node_modules/pkg/a.jpg");
        Touch(root, ".__cache__/cache.jpg");

        // 云端也放几个垃圾文件，确认比对时被忽略
        Touch(remote, "._remote.jpg");
        Touch(remote, "Thumbs.db");
        Touch(remote, "real.jpg");

        CloudSyncService.ExcludeDirectory(Path.Combine(root, "excluded-cache"));
        Touch(root, "excluded-cache/thumb.jpg");

        var service = new CloudSyncService(store: s.Store, xmp: s.Xmp);
        var target = new CloudTarget
        {
            Id = "selfcheck-junk",
            Name = "过滤自检",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = root,
            RemoteFolderPath = remote,
            Direction = CloudSyncDirection.Bidirectional,
        };

        var plan = await service.BuildPlanAsync(target);
        var localPaths = plan.Actions.Select(a => a.RelativePath).ToList();

        Check("过滤：正常素材保留", localPaths.Contains("real.jpg") && localPaths.Contains("sub/photo2.jpg"));
        Check("过滤：视频保留", localPaths.Contains("sub/clip.mp4"));
        Check("过滤：AppleDouble 不参与同步",
              !localPaths.Any(p => p.Contains("._", StringComparison.Ordinal)));
        Check("过滤：系统垃圾不参与同步",
              !localPaths.Any(p => p.Contains(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                                   p.Contains("Thumbs.db", StringComparison.OrdinalIgnoreCase)));
        Check("过滤：隐藏文件/目录不参与同步",
              !localPaths.Any(p => p.Contains(".hidden-photo", StringComparison.OrdinalIgnoreCase) ||
                                   p.Contains("__cache__", StringComparison.OrdinalIgnoreCase)));
        Check("过滤：NAS 索引目录不参与同步",
              !localPaths.Any(p => p.Contains("@eaDir", StringComparison.OrdinalIgnoreCase) ||
                                   p.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)));
        Check("过滤：排除目录不参与同步",
              !localPaths.Any(p => p.Contains("excluded-cache", StringComparison.OrdinalIgnoreCase)));
        Check("过滤：忽略计数上报", plan.IgnoredJunkFiles >= 8, $"{plan.IgnoredJunkFiles} 个");

        // 云端垃圾不应触达下载/冲突动作
        var remoteJunkActions = plan.Actions
            .Where(a => a.Kind is CloudActionKind.Download or CloudActionKind.Conflict)
            .Select(a => a.RelativePath)
            .Where(p => p.Contains("._", StringComparison.Ordinal) || p.Contains("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check("过滤：云端垃圾文件被忽略", remoteJunkActions.Count == 0,
              string.Join("/", remoteJunkActions));

        // 单元层：规则函数本身
        Check("过滤：IsJunkRelativePath 规则",
              CloudSyncService.IsJunkRelativePath("._A.jpg") &&
              CloudSyncService.IsJunkRelativePath(".DS_Store") &&
              CloudSyncService.IsJunkRelativePath("@eaDir/x.jpg") &&
              !CloudSyncService.IsJunkRelativePath("photos/DSC0001.jpg"),
              "");
    }
}