using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 玻璃面板：内容任意，外观由三套皮肤的同名 token 自动决定。
///
/// 默认样式（见 Themes/Tokens.Base.xaml 里的隐式 Style）分层：
///   1) 卡片底座：AppSurfaceRaisedBrush + AppCardStrokeBrush + AppCardRadius
///   2) 玻璃 tint：AppGlassTintBrush（只有 Glass 皮肤可见）
///   3) 45° 镜面 sheen：AppGlassSheenBrush
///   4) 顶部渐变高光：AppGlassHighlightBrush（不是实心白边）
///   5) 底部内阴影：AppGlassInnerShadowBrush
///   6) 柔和外阴影：ThemeShadow（由 AppGlassShadowOpacity/Depth 控制，仅 Glass 挂）
/// 在 Fluent / MD3 下 AppGlass* 全是透明的，于是它自动退化成一张普通卡片；
/// 在 Liquid Glass 下才真的"像玻璃"。因此本控件里没有任何 if-else 或硬编码颜色。
///
/// 用法：
/// <code>
/// &lt;controls:GlassPanel ContentPadding="20"&gt;
///     &lt;TextBlock Text="内容" /&gt;
/// &lt;/controls:GlassPanel&gt;
/// </code>
/// 注意：模板不在本类里，而在 Tokens.Base.xaml（App.xaml 只合并那一个词典，保证
/// 任何页面在皮肤服务就绪前也能正常渲染）。
/// </summary>
public sealed class GlassPanel : ContentControl
{
    /// <summary>内容区内边距。默认取 AppCardPadding token。</summary>
    public static readonly DependencyProperty ContentPaddingProperty = DependencyProperty.Register(
        nameof(ContentPadding),
        typeof(Thickness),
        typeof(GlassPanel),
        new PropertyMetadata(default(Thickness)));

    public GlassPanel()
    {
        // 默认可交互性交给内容决定：面板本身不抢焦点。
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // 模板里的 ShadowHost = 多层 Border 的最外层。
        // Glass 皮肤下挂柔和外阴影；Fluent / MD3 的 AppGlassShadowOpacity=0 → 自动不挂。
        GlassEffects.ApplyShadow(GetTemplateChild("ShadowHost") as FrameworkElement);
    }

    /// <summary>内容区内边距（默认由模板里的 AppCardPadding 提供）。</summary>
    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }
}