using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 带标题的分区卡片：标题 + 可选图标/副标题 + 分隔线 + 任意内容。
///
/// 外观 = GlassPanel 的同款三层结构（底座 / glass tint / 镜面描边），
/// 所以三套皮肤下自动变形：Fluent 是 Win11 卡片，MD3 是 16 圆角 tonal 卡片，
/// Glass 是 20 圆角玻璃卡片。
///
/// 用法：
/// <code>
/// &lt;controls:SectionCard Header="筛选" Subtitle="按设备 / 色调" Glyph="&#xE71C;"&gt;
///     &lt;StackPanel&gt;...&lt;/StackPanel&gt;
/// &lt;/controls:SectionCard&gt;
/// </code>
/// 模板位于 Themes/Tokens.Base.xaml（隐式 Style）。
/// </summary>
public sealed class SectionCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SectionCard), new PropertyMetadata(string.Empty, OnHeaderPartChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SectionCard), new PropertyMetadata(string.Empty, OnHeaderPartChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SectionCard), new PropertyMetadata(string.Empty, OnHeaderPartChanged));

    public SectionCard()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>卡片标题（空字符串 = 不显示标题区）。</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>副标题（可选，空则不显示）。</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>标题前图标（Segoe Fluent Icons 字形，可选）。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private static void OnHeaderPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SectionCard)d).UpdateHeaderVisibility();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Glass 皮肤下挂柔和外阴影（AppGlassShadowOpacity=0 时自动跳过）
        GlassEffects.ApplyShadow(GetTemplateChild("ShadowHost") as FrameworkElement);
        UpdateHeaderVisibility();
    }

    private void UpdateHeaderVisibility()
    {
        if (GetTemplateChild("HeaderPanel") is not UIElement headerPanel) return;

        var hasHeader = !string.IsNullOrWhiteSpace(Header) || !string.IsNullOrWhiteSpace(Subtitle);
        headerPanel.Visibility = hasHeader ? Visibility.Visible : Visibility.Collapsed;

        if (GetTemplateChild("HeaderDivider") is UIElement divider)
        {
            divider.Visibility = hasHeader ? Visibility.Visible : Visibility.Collapsed;
        }

        if (GetTemplateChild("HeaderIcon") is FontIcon icon)
        {
            icon.Visibility = string.IsNullOrWhiteSpace(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (GetTemplateChild("SubtitleText") is TextBlock subtitle)
        {
            subtitle.Visibility = string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}