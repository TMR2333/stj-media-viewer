namespace StjMediaViewer.Core.Models;

/// <summary>
/// 设备 ↔ LUT 绑定：指定某个设备拍的 **Log 素材**自动套哪条 LUT 做监视。
///
/// 为什么要绑定而不是按品牌猜：以前"品牌像 Sony 就套 Sony LUT"会把别的设备的 Log 素材也套上，
/// 现在只有"设备命中绑定 + 素材确实是 Log"才套；没绑定 = 不套。
/// </summary>
public sealed class DeviceLutBinding
{
    /// <summary>设备键（<see cref="MediaAsset.DeviceKey"/>，例如 "SONY ILCE-7M4"）。</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>LUT 条目 id（内置为 "builtin:xxx"，用户 LUT 为扫描出的 id）；空 = 不套用。</summary>
    public string? LutId { get; set; }

    /// <summary>关闭后等同没绑定，但保留配置。</summary>
    public bool Enabled { get; set; } = true;

    public DeviceLutBinding Clone() => new()
    {
        DeviceKey = DeviceKey,
        LutId = LutId,
        Enabled = Enabled,
    };
}
