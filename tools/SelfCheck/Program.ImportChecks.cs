using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 自检的"外部设备 / JPG+RAW 导入 / 完整性核验"部分（partial，复用 Program 的 Check/Section 助手）。
/// </summary>
public static partial class Program
{
    // ==================================================================================
    //  JPG / RAW 分组
    // ==================================================================================
    private static async Task CheckGrouping(Services s)
    {
        var all = await s.Store.QueryAsync(new AssetQuery { Limit = 0, CollapseGroups = false });
        var collapsed = await s.Store.QueryAsync(new AssetQuery { Limit = 0, CollapseGroups = true });

        var groups = all.Where(a => !string.IsNullOrEmpty(a.GroupId)).ToList();
        var paired = groups.Where(a => a.GroupHasRaw && a.GroupHasJpeg)
                           .GroupBy(a => a.GroupId!)
                           .ToList();

        Check("扫描阶段识别出 RAW+JPEG 配对", paired.Count >= 4, $"{paired.Count} 组");
        Check("同组共享 GroupId", groups.GroupBy(a => a.GroupId!).Any(g => g.Count() >= 2),
              $"{groups.Select(a => a.GroupId).Distinct().Count()} 组 / {groups.Count} 个文件");

        if (paired.Count > 0)
        {
            var sample = paired[0];
            var cover = sample.FirstOrDefault(a => a.IsGroupCover);
            var members = sample.ToList();
            Check("每组只有一个组代表", sample.Count(a => a.IsGroupCover) == 1, null);
            Check("组代表优先 JPEG", cover is not null && cover.ImageFlavor == ImageFlavor.Jpeg,
                  cover?.FileName);
            var raw = members.FirstOrDefault(a => a.RawFormat != RawFormat.None);
            Check("组内能找到 RAW 原件", raw is not null, raw?.FileName);
            Check("成员带同组其它文件路径",
                  members.All(m => m.CompanionPaths.Count == members.Count - 1),
                  $"{members[0].CompanionPaths.Count} 个");
            Check("组内标 RAW&JPEG", members.All(m => m.IsRawJpegPair), null);
        }

        // 折叠后每张照片只出现一次（非代表行被隐藏）
        var nonCovers = all.Count(a => !a.IsGroupCover);
        Check("折叠分组后行数变少", collapsed.Count == all.Count - nonCovers,
              $"{all.Count} → {collapsed.Count}（隐藏 {nonCovers} 个组内副本）");
        Check("折叠结果里没有组内副本", collapsed.All(a => a.IsGroupCover), null);
    }

    // ==================================================================================
    //  外部设备枚举
    // ==================================================================================
    private static async Task CheckDevices(Services s)
    {
        var quick = s.Devices.GetDevices();
        Check("枚举到本地卷", quick.Count > 0, $"{quick.Count} 个");
        Check("卷路径 / 类型 / 容量字段齐全",
              quick.Any(d => !string.IsNullOrEmpty(d.RootPath) && !string.IsNullOrEmpty(d.DriveType) && d.TotalBytes > 0),
              quick.FirstOrDefault(d => d.TotalBytes > 0)?.DisplayName);

        var probed = await s.Devices.ProbeDevicesAsync();
        Check("探测总线类型不抛异常", probed.Count > 0, $"{probed.Count} 个");
        var system = probed.FirstOrDefault(d => d.RootPath.StartsWith(
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:", StringComparison.OrdinalIgnoreCase));
        Check("系统盘被识别", system is not null, system?.BusType);
        Check("外部设备标记是布尔值",
              probed.All(d => d.IsExternal is true || d.IsExternal is false), null);

        var dev = s.Devices.GetDeviceForPath(Environment.SystemDirectory);
        Check("按路径反查所属设备", dev is not null, dev?.RootPath);
    }

    // ==================================================================================
    //  导入：JPG 预览 + RAW 同时导入 + 边拷贝边核验
    // ==================================================================================
    private static async Task CheckImport(Services s, string tempData)
    {
        var deviceRoot = Path.Combine(tempData, "device");
        var dcim = Path.Combine(deviceRoot, "DCIM", "100CANON");
        var destRoot = Path.Combine(tempData, "imported");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destRoot);

        // 造一张"相机卡"：3 组 RAW+JPEG + 1 张只有 JPG + 1 张只有 RAW
        int made = 0;
        foreach (var (name, withRaw, withJpg) in new[]
                 {
                     ("IMG_0001", true, true),
                     ("IMG_0002", true, true),
                     ("IMG_0003", true, true),
                     ("IMG_0004", false, true),
                     ("IMG_0005", true, false),
                 })
        {
            if (withJpg)
            {
                var p = Path.Combine(dcim, name + ".JPG");
                await File.WriteAllBytesAsync(p, MakeFakeJpeg(2048 + made));
                made++;
            }
            if (withRaw)
            {
                var p = Path.Combine(dcim, name + ".CR3");
                await File.WriteAllBytesAsync(p, MakeFakeRaw(4096 + made));
                made++;
            }
        }
        // 边车
        await File.WriteAllTextAsync(Path.Combine(dcim, "IMG_0001.XMP"), "<x:xmpmeta/>");

        var options = new ImportOptions
        {
            DestinationRoot = destRoot,
            SubfolderTemplate = "{yyyy}/{yyyy-MM-dd}",
            CopyInsteadOfMove = true,
            IncludeSidecars = true,
            SkipExisting = true,
            VerifyAfterCopy = true,
            StrictVerify = true,
            IndexAfterCopy = false,
        };

        var plan = await s.Import.BuildPlanAsync(deviceRoot, options);
        Check("导入计划识别分组", plan.Groups.Count == 5, $"{plan.Groups.Count} 组");
        Check("计划里 3 组是 RAW+JPEG", plan.Groups.Count(g => g.HasRawJpegPair) == 3,
              $"{plan.Groups.Count(g => g.HasRawJpegPair)} 组");
        Check("JPG 作为预览（组代表）", plan.Groups.Where(g => g.HasRawJpegPair).All(g => g.JpegPath is not null), null);
        Check("计划统计总字节", plan.TotalBytes > 0, $"{plan.TotalBytes / 1024.0:F1} KB");
        Check("边车被纳入计划", plan.Groups.Sum(g => g.SidecarPaths.Count) == 1, null);
        Check("多文件组计数正确", plan.TotalFiles == 9, $"{plan.TotalFiles} 个文件");

        var report = await s.Import.ExecuteAsync(plan);
        Check("导入成功（无失败）", report.FilesFailed == 0, report.Summary);
        Check("所有文件都落盘", report.FilesCopied == plan.TotalFiles, $"{report.FilesCopied}/{plan.TotalFiles}");
        Check("逐文件核验通过", report.FilesVerified == report.FilesCopied && report.FilesCorrupted == 0,
              $"核验 {report.FilesVerified} / 损坏 {report.FilesCorrupted}");
        Check("移动/复制模式未丢源文件", plan.Groups.All(g => g.AllFiles().Any(File.Exists)), null);

        // 目录级对账：源 12 个 → 目标 12 个，无损坏
        var verify = await s.Verify.VerifyFolderAsync(deviceRoot, destRoot, strict: true);
        Check("目录级核验通过", verify.Success, verify.Summary);
        Check("没有漏导", verify.Missing == 0 && verify.Passed == verify.Total,
              $"{verify.Passed}/{verify.Total}");

        // 重复导入应全部跳过
        var plan2 = await s.Import.BuildPlanAsync(deviceRoot, options);
        Check("预判重复文件数", plan2.EstimatedDuplicates > 0, $"{plan2.EstimatedDuplicates} 个");
        var report2 = await s.Import.ExecuteAsync(plan2);
        Check("重复导入全部跳过", report2.FilesSkipped == plan2.TotalFiles,
              $"跳过 {report2.FilesSkipped}/{plan2.TotalFiles}");
        Check("重复导入不产生损坏", report2.FilesCorrupted == 0, null);

        // 人为损坏一个已导出文件 → 核验必须报出来
        var victim = Directory.EnumerateFiles(destRoot, "IMG_0002.CR3", SearchOption.AllDirectories).FirstOrDefault();
        if (victim is not null)
        {
            await File.AppendAllTextAsync(victim, "CORRUPTED");
            var damaged = await s.Verify.VerifyFolderAsync(deviceRoot, destRoot, strict: true);
            Check("损坏文件被检出", damaged.Corrupted >= 1, $"{damaged.Corrupted} 个");
            Check("损坏时整体不通过", !damaged.Success, damaged.Summary);
        }
        else
        {
            Check("损坏文件被检出", false, "找不到用于破坏的样本");
        }

        // 漏导：删掉一个目标文件 → Missing
        var missingCandidate = Directory.EnumerateFiles(destRoot, "IMG_0004.JPG", SearchOption.AllDirectories).FirstOrDefault();
        if (missingCandidate is not null)
        {
            File.Delete(missingCandidate);
            var incomplete = await s.Verify.VerifyFolderAsync(deviceRoot, destRoot, strict: true);
            Check("漏导文件被检出", incomplete.Missing >= 1, $"{incomplete.Missing} 个");
        }
        else
        {
            Check("漏导文件被检出", false, "找不到用于删除的样本");
        }
    }

    // ==================================================================================
    //  导出核验（按"源→目标"对照表）
    // ==================================================================================
    private static async Task CheckVerifyPairs(Services s, string tempData)
    {
        var src = Path.Combine(tempData, "vp-src");
        var dst = Path.Combine(tempData, "vp-dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var pairs = new List<VerifyPair>();
        for (int i = 0; i < 5; i++)
        {
            var name = $"V{i}.jpg";
            var a = Path.Combine(src, name);
            var b = Path.Combine(dst, name);
            var bytes = MakeFakeJpeg(1024 * (i + 1));
            await File.WriteAllBytesAsync(a, bytes);
            await File.WriteAllBytesAsync(b, bytes);
            pairs.Add(new VerifyPair(a, b));
        }

        var ok = await s.Verify.VerifyPairsAsync(pairs, strict: true);
        Check("批量核验全部通过", ok.Success && ok.Passed == 5, ok.Summary);

        // 改一个字节
        var victim = pairs[2].DestinationPath;
        var data = await File.ReadAllBytesAsync(victim);
        data[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(victim, data);

        var bad = await s.Verify.VerifyPairsAsync(pairs, strict: true);
        Check("字节级损坏被检出", bad.Corrupted == 1, bad.Summary);
        Check("快速核验也能发现大小/指纹差异",
              (await s.Verify.VerifyPairsAsync(pairs, strict: false)).Corrupted == 1, null);

        var missing = await s.Verify.VerifyFileAsync(pairs[0].SourcePath,
            Path.Combine(dst, "NOT-EXIST.jpg"), strict: true);
        Check("目标缺失被标为 Missing", missing.Status == "Missing", missing.Error);
    }

    // ---------------------------------------------------------------- 假文件

    private static byte[] MakeFakeJpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        for (int i = 4; i < size; i++) bytes[i] = (byte)(i * 31 + 7);
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    private static byte[] MakeFakeRaw(int size)
    {
        var bytes = new byte[size];
        var header = System.Text.Encoding.ASCII.GetBytes("II*\0CR3\0");
        Array.Copy(header, bytes, Math.Min(header.Length, size));
        for (int i = header.Length; i < size; i++) bytes[i] = (byte)(i * 17 + 3);
        return bytes;
    }
}