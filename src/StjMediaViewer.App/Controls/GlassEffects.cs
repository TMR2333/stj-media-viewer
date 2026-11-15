using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 玻璃材质的公共"深度"效果：柔和外阴影（ThemeShadow）。
///
/// WinUI 没有 CSS 的 box-shadow / inset shadow，能做"厚度"的手段只有三样：
/// 1) 多层 Border 叠（底座 / tint / sheen / 顶部渐变高光 / 底部内阴影）—— 见 Tokens.Base.xaml 模板；
/// 2) ThemeShadow 给外阴影（本类）；
/// 3) 圆角 + 统一光源（左上）。
///
/// 是否启用由 token 决定：<c>AppGlassShadowOpacity</c> / <c>AppGlassShadowDepth</c> &gt; 0 才挂阴影。
/// Fluent / MD3 皮肤下这两个 token 是 0，所以卡片不会有多余的浮起阴影（尊重各自的扁平语言）。
/// </summary>
internal static class GlassEffects
{
    /// <summary>给卡片宿主挂柔和外阴影（幂等，可重复调用）。</summary>
    public static void ApplyShadow(FrameworkElement? host)
    {
        if (host is null) return;

        try
        {
            var opacity = ReadDouble("AppGlassShadowOpacity", 0);
            var depth = ReadDouble("AppGlassShadowDepth", 0);

            if (opacity <= 0 || depth <= 0)
            {
                // 非 Glass 皮肤：确保没有残留阴影
                if (host.Shadow is not null) host.Shadow = null;
                host.Translation = default;
                return;
            }

            if (host.Shadow is ThemeShadow) return;

            var shadow = new ThemeShadow();
            if (App.MainWindow?.Content is UIElement receiver)
            {
                shadow.Receivers.Add(receiver);
            }

            host.Shadow = shadow;
            host.Translation = new System.Numerics.Vector3(0, 0, (float)depth);
        }
        catch (Exception ex)
        {
            // 阴影只是锦上添花：老系统 / 远程桌面失败时静默降级
            Log.Warn("玻璃阴影应用失败（降级为无阴影）", ex);
        }
    }

    private static double ReadDouble(string key, double fallback)
    {
        try
        {
            if (Application.Current?.Resources[key] is double value) return value;
        }
        catch
        {
            // 忽略资源查找异常
        }

        return fallback;
    }
}