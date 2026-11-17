using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Cloud;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Write;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 文件整理：本地真实文件按分类移动 → 索引路径更新 → 撤销；云端 MOVE；群组作者名与来源标记。
    /// </summary>
    private static async Task CheckOrganize(Services s, string tempData)
    {
        var roots = await s.Library.GetLibrariesAsync();
        var libraryRoot = roots.FirstOrDefault()?.Path;
        if (string.IsNullOrEmpty(libraryRoot) || !Directory.Exists(libraryRoot))
        {
            Warn("没有可用媒体库，跳过整理检查");
            return;
        }

        // 复制几份样本文件到临时库，避免动到仓库里的样本
        var tempLib = Path.Combine(tempData, "organize-lib");
        Directory.CreateDirectory(tempLib);
        var sourceFiles = Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && File.Exists(f) &&
                        new FileInfo(f).Length > 4096 &&
                        !Path.GetFileName(f).Contains("broken", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        if (sourceFiles.Count == 0) { Warn("样本里没有 JPG，跳过整理检查"); return; }

        foreach (var file in sourceFiles)
            File.Copy(file, Path.Combine(tempLib, Path.GetFileName(file)), overwrite: true);

        var lib = await s.Library.AddLibraryAsync(tempLib);
        await s.Library.ScanLibraryAsync(lib, new ScanOptions { ComputeQuickHash = false }, new Progress<ScanProgress>(_ => { }));

        var assets = await s.Store.QueryAsync(new AssetQuery { LibraryId = lib.Id, Limit = 0 });
        Check("整理：临时库已入库", assets.Count >= 1, $"{assets.Count} 条");
        if (assets.Count == 0) return;

        var organizer = new OrganizerService(tempData);

        // 三级目录：设备 / 年月 / 月份 → 三个目录层级；重复维度只保留一层
        var threeLevel = OrganizerService.CategoryPathFor(assets[0], new OrganizeOptions
        {
            Primary = OrganizeDimension.Device,
            Secondary = OrganizeDimension.YearMonth,
            Tertiary = OrganizeDimension.Month,
        });
        Check("整理：三级目录生成三段路径",
              threeLevel.Count(c => c == '/') == 2,
              threeLevel);

        var deduped = OrganizerService.CategoryPathFor(assets[0], new OrganizeOptions
        {
            Primary = OrganizeDimension.Year,
            Secondary = OrganizeDimension.Year,
            Tertiary = OrganizeDimension.Month,
        });
        Check("整理：同一维度重复选只保留一层",
              deduped.Count(c => c == '/') == 1,
              deduped);

        var options = new OrganizeOptions { Primary = OrganizeDimension.Device };
        var rootMap = new Dictionary<string, string>(StringComparer.Ordinal) { [lib.Id] = tempLib };

        var plan = organizer.BuildLocalPlan(assets, rootMap, options);
        Check("整理：计划非空", plan.FileCount >= 1, $"{plan.FileCount} 个文件");
        Check("整理：目标在设备目录下",
              plan.Entries.Where(e => !e.IsSidecar).All(e => e.CategoryPath.Length > 0 &&
                  e.TargetPath.Contains(e.CategoryPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)),
              plan.Entries.FirstOrDefault(e => !e.IsSidecar)?.TargetPath ?? "");

        var result = await organizer.ExecuteLocalAsync(plan, s.Store, options);
        Check("整理：执行成功", result.Moved >= 1 && result.Failed == 0, result.Summary);
        Check("整理：文件真的被移动了",
              plan.Entries.Where(e => !e.IsSidecar).All(e => !File.Exists(e.SourcePath) && File.Exists(e.TargetPath)));

        var moved = await s.Store.QueryAsync(new AssetQuery { LibraryId = lib.Id, Limit = 0 });
        var movedAsset = moved.FirstOrDefault(a => a.Id == assets[0].Id) ?? moved.First();
        Check("整理：索引路径已更新", File.Exists(movedAsset.FullPath), movedAsset.FullPath);
        Check("整理：相对路径已更新", !string.IsNullOrEmpty(movedAsset.RelativePath), movedAsset.RelativePath ?? "");

        // 撤销
        Check("整理：有撤销记录", organizer.CanUndo);
        var undo = await organizer.UndoLocalAsync(s.Store);
        Check("整理：撤销成功", undo.Moved >= 1 && undo.Failed == 0, undo.Summary);
        Check("整理：撤销后文件回到原位",
              plan.Entries.Where(e => !e.IsSidecar).All(e => File.Exists(e.SourcePath) && !File.Exists(e.TargetPath)));
        Check("整理：撤销后索引路径恢复",
              (await s.Store.GetByIdsAsync(new[] { assets[0].Id })).FirstOrDefault()?.FullPath == assets[0].FullPath);

        // ---------------- 云端整理（LocalFolder 适配器当"云盘"） ----------------
        var cloudRoot = Path.Combine(tempData, "organize-cloud");
        Directory.CreateDirectory(cloudRoot);
        var cloudFile = assets[0];
        var remoteRelative = cloudFile.RelativePath ?? Path.GetFileName(cloudFile.FullPath);
        var remotePath = Path.Combine(cloudRoot, remoteRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(remotePath)!);
        File.WriteAllText(remotePath, "cloud copy");

        var target = new CloudTarget
        {
            Id = "selfcheck-cloud",
            Name = "自检云盘",
            Kind = CloudProviderKind.LocalFolder,
            LocalFolderPath = tempLib,
            RemoteFolderPath = cloudRoot,
        };
        var cloudService = new CloudSyncService(store: s.Store, xmp: s.Xmp);
        var organizerCloud = new OrganizerService(tempData, cloudService);
        var catalog = await s.Store.QueryAsync(new AssetQuery { Limit = 0 });
        var cloudPlan = await organizerCloud.BuildCloudPlanAsync(target, catalog, new OrganizeOptions
        {
            Primary = OrganizeDimension.Device,
        });
        Check("云端整理：计划非空", cloudPlan.FileCount >= 1, $"{cloudPlan.FileCount} 个文件");
        if (cloudPlan.FileCount > 0)
        {
            var cloudResult = await organizerCloud.ExecuteCloudAsync(target, cloudPlan, new OrganizeOptions { Primary = OrganizeDimension.Device });
            Check("云端整理：MOVE 成功", cloudResult.Moved >= 1 && cloudResult.Failed == 0, cloudResult.Summary);
            var movedTo = Path.Combine(cloudRoot, cloudPlan.Entries[0].RelativeTo.Replace('/', Path.DirectorySeparatorChar));
            Check("云端整理：文件真被移动", File.Exists(movedTo));
            var cloudUndo = await organizerCloud.UndoCloudAsync(target);
            Check("云端整理：撤销成功", cloudUndo.Moved >= 1 && cloudUndo.Failed == 0, cloudUndo.Summary);
            Check("云端整理：撤销后回到原位", File.Exists(remotePath));
        }

        // ---------------- 群组作者名 + 来源标记 ----------------
        var syncRoot = Path.Combine(tempData, "group-local");
        Directory.CreateDirectory(syncRoot);
        var groupSource = assets[0].FullPath;
        if (File.Exists(groupSource))
        {
            File.Copy(groupSource, Path.Combine(syncRoot, Path.GetFileName(groupSource)), overwrite: true);
            var groupTarget = new CloudTarget
            {
                Id = "selfcheck-group",
                Name = "群组云盘",
                Kind = CloudProviderKind.LocalFolder,
                LocalFolderPath = syncRoot,
                RemoteFolderPath = Path.Combine(tempData, "group-remote"),
                GroupEnabled = true,
                AuthorName = "苏俊宇",
                Direction = CloudSyncDirection.UploadOnly,
            };
            Directory.CreateDirectory(groupTarget.RemoteFolderPath!);

            var report = await cloudService.SyncAsync(groupTarget);
            Check("群组：同步完成", report.Failed == 0, report.Summary);

            var manifest = Path.Combine(groupTarget.RemoteFolderPath!, CloudSyncService.AuthorManifestName);
            Check("群组：云端作者清单已写入", File.Exists(manifest));
            if (File.Exists(manifest))
            {
                var json = await File.ReadAllTextAsync(manifest);
                bool hasAuthor = false;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("authors", out var map))
                        hasAuthor = map.EnumerateObject().Any(p => p.Value.GetString() == "苏俊宇");
                }
                catch { }
                Check("群组：作者清单含用户名", hasAuthor, json[..Math.Min(120, json.Length)]);
            }
        }

        // 来源标记：「来自 xx 云同步」的筛选与分面
        var originAsset = (await s.Store.QueryAsync(new AssetQuery { Limit = 1 })).FirstOrDefault();
        if (originAsset is not null)
        {
            await s.Store.UpdateAssetMetaByPathAsync(originAsset.FullPath, origin: "群组云盘");
            var byOrigin = new AssetQuery { Limit = 0 };
            byOrigin.Origins.Add("群组云盘");
            Check("来源：按云同步来源筛选命中", (await s.Store.QueryAsync(byOrigin)).Any(a => a.Id == originAsset.Id));
            var facets = await s.Store.GetFacetsAsync(new AssetQuery { Limit = 0 });
            Check("来源：分面里有该来源", facets.ByOrigin.ContainsKey("群组云盘"),
                  string.Join("/", facets.ByOrigin.Keys));
            Check("来源：本地文件计数", facets.OriginUnsetCount >= 1, $"{facets.OriginUnsetCount}");

            var localOnly = new AssetQuery { Limit = 0 };
            localOnly.Origins.Add("__unset__");
            Check("来源：本地文件筛选可用", (await s.Store.QueryAsync(localOnly)).All(a => string.IsNullOrEmpty(a.Origin)));
        }
    }
}