using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 自检的"云同步（本地挂载目录）"部分：双向同步、只上传/只下载、冲突策略、核验与"不删文件"。
/// </summary>
public static partial class Program
{
    private static async Task CheckCloudLocalSync(Services s, string tempData)
    {
        var root = Path.Combine(tempData, "cloud");
        var local = Path.Combine(root, "local");
        var cloud = Path.Combine(root, "cloud-mounted");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(Cloud(cloud, "2026"));

        // 初始：本地 3 个文件（2 张图 + 1 个边车），云端 1 个文件
        await File.WriteAllBytesAsync(Path.Combine(local, "A.jpg"), MakeFakeJpeg(4096));
        await File.WriteAllBytesAsync(Path.Combine(local, "B.CR3"), MakeFakeRaw(8192));
        await File.WriteAllTextAsync(Path.Combine(local, "A.xmp"), "<x:xmpmeta>local</x:xmpmeta>");
        await File.WriteAllBytesAsync(Path.Combine(Cloud(cloud, "2026"), "C.jpg"), MakeFakeJpeg(2048));

        var target = new CloudTarget
        {
            Id = "selfcheck-cloud",
            Name = "自检云目标",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = local,
            RemoteFolderPath = cloud,
            Direction = CloudSyncDirection.Bidirectional,
            ConflictPolicy = CloudConflictPolicy.PreferNewer,
            VerifyAfterTransfer = true,
            AutoSync = false,
        };

        var cloudSync = new StjMediaViewer.Core.Cloud.CloudSyncService();

        // ---- 1) 计划 ----
        var plan = await cloudSync.BuildPlanAsync(target);
        Check("云同步：识别本地独有文件", plan.Actions.Count(a => a.Kind == CloudActionKind.Upload) == 3,
              $"{plan.Actions.Count(a => a.Kind == CloudActionKind.Upload)} 个待上传");
        Check("云同步：识别云端独有文件", plan.Actions.Count(a => a.Kind == CloudActionKind.Download) == 1,
              $"{plan.Actions.Count(a => a.Kind == CloudActionKind.Download)} 个待下载");
        Check("云同步：计划统计", plan.LocalFileCount == 3 && plan.RemoteFileCount == 1,
              $"本地 {plan.LocalFileCount} / 云端 {plan.RemoteFileCount}");

        // ---- 2) 执行 ----
        var report = await cloudSync.ExecuteAsync(plan);
        Check("云同步：双向同步成功", report.Success && report.Uploaded == 3 && report.Downloaded == 1, report.Summary);
        Check("云同步：云端已有本地文件",
              File.Exists(Path.Combine(cloud, "A.jpg")) && File.Exists(Path.Combine(cloud, "B.CR3")),
              null);
        Check("云同步：本地已下载云端文件", File.Exists(Path.Combine(local, "2026", "C.jpg")),
              "2026/C.jpg");
        Check("云同步：传输后哈希一致",
              FileHasher.FullHash(Path.Combine(local, "A.jpg")) == FileHasher.FullHash(Path.Combine(cloud, "A.jpg")),
              null);

        // ---- 3) 幂等 ----
        var plan2 = await cloudSync.BuildPlanAsync(target);
        Check("云同步：第二轮无差异", plan2.IsEmpty, plan2.Summary);

        // ---- 4) 冲突：较新者胜 ----
        await File.WriteAllBytesAsync(Path.Combine(local, "A.jpg"), MakeFakeJpeg(5000)); // 本地更新更"新"
        var conflictPlan = await cloudSync.BuildPlanAsync(target);
        var conflict = conflictPlan.Actions.FirstOrDefault(a => a.Kind == CloudActionKind.Conflict);
        Check("云同步：内容变化被识别为冲突", conflict is not null, conflictPlan.Summary);
        Check("云同步：冲突解析为本地优先（本地较新）",
              conflict?.Resolution == CloudConflictPolicy.PreferLocal, conflict?.Resolution.ToString());
        var conflictReport = await cloudSync.ExecuteAsync(conflictPlan);
        Check("云同步：冲突已按策略解决", conflictReport.Success && conflictReport.Conflicts >= 1, conflictReport.Summary);
        Check("云同步：冲突后两边一致",
              FileHasher.FullHash(Path.Combine(local, "A.jpg")) == FileHasher.FullHash(Path.Combine(cloud, "A.jpg")),
              null);

        // ---- 5) 只下载模式：本地多出来的不上传 ----
        await File.WriteAllBytesAsync(Path.Combine(local, "LOCAL-ONLY.jpg"), MakeFakeJpeg(1024));
        var downloadOnly = new CloudTarget
        {
            Id = "selfcheck-cloud-dl",
            Name = "只下载",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = local,
            RemoteFolderPath = cloud,
            Direction = CloudSyncDirection.DownloadOnly,
        };
        var dlPlan = await cloudSync.BuildPlanAsync(downloadOnly);
        Check("云同步：只下载模式不上传",
              dlPlan.Actions.All(a => a.Kind != CloudActionKind.Upload), dlPlan.Summary);
        await cloudSync.ExecuteAsync(dlPlan);
        Check("云同步：只下载模式仍保留本地独有文件", File.Exists(Path.Combine(local, "LOCAL-ONLY.jpg")), null);

        // ---- 6) 只上传模式：云端多出来的不下载 ----
        await File.WriteAllBytesAsync(Path.Combine(cloud, "CLOUD-ONLY.jpg"), MakeFakeJpeg(1024));
        var uploadOnly = new CloudTarget
        {
            Id = "selfcheck-cloud-up",
            Name = "只上传",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = local,
            RemoteFolderPath = cloud,
            Direction = CloudSyncDirection.UploadOnly,
        };
        var upPlan = await cloudSync.BuildPlanAsync(uploadOnly);
        Check("云同步：只上传模式不下载", upPlan.Actions.All(a => a.Kind != CloudActionKind.Download), upPlan.Summary);
        await cloudSync.ExecuteAsync(upPlan);
        Check("云同步：只上传模式不删除本地文件", File.Exists(Path.Combine(local, "A.jpg")), null);

        // ---- 7) 两边都保留策略 ----
        await File.WriteAllBytesAsync(Path.Combine(local, "D.jpg"), MakeFakeJpeg(3000));
        await File.WriteAllBytesAsync(Path.Combine(cloud, "D.jpg"), MakeFakeJpeg(3500));
        var keepBoth = new CloudTarget
        {
            Id = "selfcheck-cloud-keep",
            Name = "两边都保留",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = local,
            RemoteFolderPath = cloud,
            ConflictPolicy = CloudConflictPolicy.KeepBoth,
            Direction = CloudSyncDirection.Bidirectional,
        };
        var kbPlan = await cloudSync.BuildPlanAsync(keepBoth);
        var kbReport = await cloudSync.ExecuteAsync(kbPlan);
        Check("云同步：两边都保留不丢文件",
              kbReport.Success && File.Exists(Path.Combine(local, "D.jpg")) &&
              Directory.GetFiles(cloud, "*D*").Length >= 2,
              kbReport.Summary);

        // ---- 8) 不存在的目录给出可读错误 ----
        var bad = new CloudTarget
        {
            Id = "selfcheck-cloud-bad",
            Name = "坏目标",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = Path.Combine(root, "does-not-exist"),
            RemoteFolderPath = cloud,
        };
        var threw = false;
        try { await cloudSync.BuildPlanAsync(bad); }
        catch (DirectoryNotFoundException) { threw = true; }
        catch (Exception) { threw = true; }
        Check("云同步：本地目录不存在时报可读错误", threw, null);
    }

    private static string Cloud(string root, string sub) => Path.Combine(root, sub);
}