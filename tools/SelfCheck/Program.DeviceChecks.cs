using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Write;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 自定义设备名 + 常用设备（素材量 &gt; 100 才进快捷筛选）：
    /// 别名要能用在显示、整理分目录；常用设备规则要严格大于 100 且按素材量降序。
    /// </summary>
    private static async Task CheckDeviceAliases(Services s, string tempData)
    {
        // ---------------- 显示名 ----------------
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SONY ILCE-7M4"] = "我的 A7M4",
        };
        Check("设备名：自定义名称优先",
              DeviceNames.Display("SONY ILCE-7M4", aliases) == "我的 A7M4", DeviceNames.Display("SONY ILCE-7M4", aliases));
        Check("设备名：没设置就用原名",
              DeviceNames.Display("Canon EOS R5", aliases) == "Canon EOS R5");
        Check("设备名：空值显示未知设备",
              DeviceNames.Display(null, aliases) == "未知设备");

        // ---------------- 常用设备规则（>100） ----------------
        var counts = new Dictionary<string, int>
        {
            ["A"] = 101,
            ["B"] = 100,     // 刚好 100 → 不算
            ["C"] = 500,
            ["D"] = 3,
        };
        var frequent = DeviceNames.Frequent(counts);
        Check("常用设备：阈值严格大于 100",
              frequent.Count == 2 && frequent[0].Key == "C" && frequent[1].Key == "A",
              string.Join("/", frequent.Select(kv => $"{kv.Key}:{kv.Value}")));
        Check("常用设备：没有符合的返回空",
              DeviceNames.Frequent(new Dictionary<string, int> { ["X"] = 10 }).Count == 0);

        // ---------------- 整理：设备目录用别名 ----------------
        var dir = Path.Combine(tempData, "device-alias");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.jpg");
        File.WriteAllBytes(path, new byte[64]);
        var asset = NewAsset("device-alias-1", "device-lib", path, "a.jpg");
        asset.DeviceKey = "SONY ILCE-7M4";
        await s.Store.UpsertManyAsync(new[] { asset });

        var roots = new Dictionary<string, string>(StringComparer.Ordinal) { ["device-lib"] = dir };
        var organizer = new OrganizerService(tempData);

        var withAlias = organizer.BuildLocalPlan(new[] { asset }, roots, new OrganizeOptions
        {
            Primary = OrganizeDimension.Device,
            DeviceAliases = aliases,
        });
        Check("整理：设备目录用自定义名称",
              withAlias.Entries.FirstOrDefault()?.CategoryPath == "我的 A7M4",
              withAlias.Entries.FirstOrDefault()?.CategoryPath ?? "(无)");

        var withoutAlias = organizer.BuildLocalPlan(new[] { asset }, roots, new OrganizeOptions
        {
            Primary = OrganizeDimension.Device,
        });
        Check("整理：没别名时用设备原名",
              withoutAlias.Entries.FirstOrDefault()?.CategoryPath == "SONY ILCE-7M4",
              withoutAlias.Entries.FirstOrDefault()?.CategoryPath ?? "(无)");
    }
}