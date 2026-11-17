using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// 设备 ↔ LUT 绑定规则。
///
/// 命中条件（必须同时满足）：
/// 1. 素材是视频或图片（音频不套）；
/// 2. <see cref="MediaAsset.IsLog"/> 明确为 Log（null / false 都不套）；
/// 3. 设备名命中某条已启用的绑定，且该绑定配了 LUT id。
///
/// 这样就不会再出现"别的设备拍 Log 也被套 Sony LUT"的误判。
/// </summary>
public static class DeviceLutRules
{
    /// <summary>返回该素材命中的绑定 LUT id；没命中返回 null。</summary>
    public static string? ResolveLutId(MediaAsset? asset, IEnumerable<DeviceLutBinding>? bindings)
    {
        if (asset is null || bindings is null) return null;
        if (asset.Kind != MediaKind.Video && asset.Kind != MediaKind.Image) return null;
        if (asset.IsLog != true) return null;

        foreach (var binding in bindings)
        {
            if (binding is null || !binding.Enabled) continue;
            if (string.IsNullOrWhiteSpace(binding.LutId)) continue;
            if (!Matches(asset, binding.DeviceKey)) continue;
            return binding.LutId;
        }
        return null;
    }

    /// <summary>设备键匹配：优先 DeviceKey，其次显示名（别名），都不区分大小写。</summary>
    public static bool Matches(MediaAsset asset, string? deviceKey)
    {
        if (asset is null || string.IsNullOrWhiteSpace(deviceKey)) return false;
        var key = deviceKey.Trim();
        if (!string.IsNullOrWhiteSpace(asset.DeviceKey) &&
            string.Equals(asset.DeviceKey.Trim(), key, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(asset.DisplayDevice, key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 给设备推荐一条内置 Log LUT（只做"建议"，不会自动套）。
    /// 返回 null 表示没有可靠的内置公式（例如 D-Log / C-Log 需要用户自己的官方 LUT）。
    /// </summary>
    public static string? SuggestBuiltInLutId(string? deviceKey)
    {
        var key = (deviceKey ?? "").ToUpperInvariant();
        if (key.Length == 0) return null;

        bool sony = key.Contains("SONY") || key.Contains("ILCE") || key.Contains("ZV-") ||
                    key.Contains("FX3") || key.Contains("FX30") || key.Contains("A7");
        if (sony) return "builtin:sony-slog3-cine-709";

        // DJI / Canon / Panasonic / Nikon 等没有公开公式化的内置转换，
        // 交给用户导入官方 .cube 后手动绑定，避免猜错。
        return null;
    }
}
