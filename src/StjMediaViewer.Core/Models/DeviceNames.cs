namespace StjMediaViewer.Core.Models;

/// <summary>
/// 设备显示名与"常用设备"规则：
///  - 自定义名称：device_key → 用户起的名字（没设置就用 device_key 本身）；
///  - 常用设备：拍片量超过 100 张的设备（素材量 &gt; 100 才进快捷选项）。
/// </summary>
public static class DeviceNames
{
    /// <summary>「常用设备」的门槛：素材量 &gt; 100。</summary>
    public const int FrequentThreshold = 100;

    /// <summary>设备显示名：优先用自定义名称。</summary>
    public static string Display(string? deviceKey, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return "未知设备";
        if (aliases is not null && aliases.TryGetValue(deviceKey, out var alias) && !string.IsNullOrWhiteSpace(alias))
            return alias.Trim();
        return deviceKey;
    }

    /// <summary>
    /// 常用设备：按素材量降序、只保留超过 <paramref name="threshold"/> 张的。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, int>> Frequent(
        IReadOnlyDictionary<string, int>? counts,
        int threshold = FrequentThreshold)
    {
        if (counts is null || counts.Count == 0) return Array.Empty<KeyValuePair<string, int>>();
        return counts
            .Where(kv => kv.Value > threshold && !string.IsNullOrWhiteSpace(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
