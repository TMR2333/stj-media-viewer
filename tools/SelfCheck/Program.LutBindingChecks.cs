using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Media;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 设备 ↔ LUT 绑定：只有"设备命中 + 素材确实是 Log"才自动套，
    /// 其他设备的 Log 不会被误套（修复 Sony LUT 误判）。
    /// </summary>
    private static void CheckDeviceLutBindings(Services s, string tempData)
    {
        var bindings = new List<DeviceLutBinding>
        {
            new() { DeviceKey = "SONY ILCE-7M4", LutId = "builtin:sony-slog3-cine-709" },
            new() { DeviceKey = "DJI FC8582", LutId = "user:dlog" },
            new() { DeviceKey = "SONY ILCE-7M3", LutId = "builtin:sony-slog2-709", Enabled = false },
            new() { DeviceKey = "Canon EOS R5", LutId = "" },
        };

        var sonyLog = NewVideo("lut-sony-log", "SONY ILCE-7M4", isLog: true);
        var sonyNotLog = NewVideo("lut-sony-nonlog", "SONY ILCE-7M4", isLog: false);
        var sonyUnknown = NewVideo("lut-sony-unknown", "SONY ILCE-7M4", isLog: null);
        var djiLog = NewVideo("lut-dji-log", "DJI FC8582", isLog: true);
        var nikonLog = NewVideo("lut-nikon-log", "NIKON Z6", isLog: true);
        var disabled = NewVideo("lut-disabled", "SONY ILCE-7M3", isLog: true);
        var emptyLut = NewVideo("lut-empty", "Canon EOS R5", isLog: true);
        var audio = NewVideo("lut-audio", "SONY ILCE-7M4", isLog: true);
        audio.Kind = MediaKind.Audio;

        Check("绑定：Sony Log 视频命中绑定",
              DeviceLutRules.ResolveLutId(sonyLog, bindings) == "builtin:sony-slog3-cine-709");
        Check("绑定：非 Log 不套（IsLog=false）", DeviceLutRules.ResolveLutId(sonyNotLog, bindings) is null);
        Check("绑定：未判定 Log 不套（IsLog=null）", DeviceLutRules.ResolveLutId(sonyUnknown, bindings) is null);
        Check("绑定：其他设备 Log 不会被误套 Sony LUT（回归）",
              DeviceLutRules.ResolveLutId(nikonLog, bindings) is null);
        Check("绑定：DJI 绑定的是它自己的 LUT",
              DeviceLutRules.ResolveLutId(djiLog, bindings) == "user:dlog");
        Check("绑定：关闭的绑定不生效", DeviceLutRules.ResolveLutId(disabled, bindings) is null);
        Check("绑定：LUT 为空的绑定不生效", DeviceLutRules.ResolveLutId(emptyLut, bindings) is null);
        Check("绑定：音频不套 LUT", DeviceLutRules.ResolveLutId(audio, bindings) is null);

        // 别名 / 大小写：显示设备名也能命中
        var aliased = NewVideo("lut-alias", "SONY ILCE-7M4", isLog: true);
        aliased.DeviceKey = "sony ilce-7m4";
        Check("绑定：设备名不区分大小写", DeviceLutRules.ResolveLutId(aliased, bindings) is not null);

        // 推荐值
        Check("绑定：Sony 推荐内置 S-Log3 LUT",
              DeviceLutRules.SuggestBuiltInLutId("SONY ILCE-7M4") == "builtin:sony-slog3-cine-709");
        Check("绑定：非 Sony 不乱推荐", DeviceLutRules.SuggestBuiltInLutId("DJI FC8582") is null);

        // ---------- 监视代理（烧录 LUT）----------
        var noLutFilter = FfmpegService.BuildProxyVideoFilter(null);
        var withLutFilter = FfmpegService.BuildProxyVideoFilter("lut-abc123.cube");
        Check("监视代理：无 LUT 只做缩放", noLutFilter.Contains("scale=") && !noLutFilter.Contains("lut3d"));
        Check("监视代理：有 LUT 时加 lut3d",
              withLutFilter.StartsWith("lut3d=file=lut-abc123.cube", StringComparison.Ordinal) &&
              withLutFilter.Contains("scale="));

        var keyA = VideoProxyService.CacheKey("C:/v/a.mp4", 1024, new DateTime(2026, 1, 1), null);
        var keyB = VideoProxyService.CacheKey("C:/v/a.mp4", 1024, new DateTime(2026, 1, 1), "C:/lut/sony.cube");
        Check("监视代理：缓存键包含 LUT（换 LUT 重新生成）", keyA != keyB, $"{keyA[..6]} / {keyB[..6]}");
    }

    private static MediaAsset NewVideo(string id, string deviceKey, bool? isLog)
    {
        return new MediaAsset
        {
            Id = id,
            LibraryId = "lut-lib",
            FullPath = $"C:/lut/{id}.mp4",
            FileName = $"{id}.mp4",
            Kind = MediaKind.Video,
            DeviceKey = deviceKey,
            IsLog = isLog,
            LogProfile = isLog == true ? "S-Log3" : null,
        };
    }
}