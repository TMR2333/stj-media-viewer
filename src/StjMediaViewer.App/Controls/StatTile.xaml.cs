using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 数字 + 标签的小卡片（仪表盘 / 详情页统计）。
///
/// 用法：
/// <code>
/// &lt;controls:StatTile Value="1,284" Label="素材" Glyph="&#xE8B9;" Hint="本周 +36" /&gt;
/// </code>
/// 卡片外观 = AppCardStyle（三套皮肤自动不同：Fluent 8px / MD3 16px / Glass 20px 玻璃）。
/// </summary>
public sealed partial class StatTile : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty, OnValueChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty, OnGlyphChanged));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty, OnHintChanged));

    public StatTile()
    {
        InitializeComponent();
        ApplyGlyph();
        ApplyHint();
    }

    /// <summary>主数字（已经是格式化好的字符串，控件不做数字格式化）。</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>标签文案。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>可选图标（Segoe Fluent Icons 字形）。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>可选补充说明（同比 / 环比等）。</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatTile)d).ValueText.Text = (string?)e.NewValue ?? string.Empty;

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatTile)d).LabelText.Text = (string?)e.NewValue ?? string.Empty;

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatTile)d).ApplyGlyph();

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatTile)d).ApplyHint();

    private void ApplyGlyph()
    {
        var glyph = Glyph;
        var hasGlyph = !string.IsNullOrWhiteSpace(glyph);
        TileGlyph.Glyph = hasGlyph ? glyph : string.Empty;
        TileGlyph.Visibility = hasGlyph ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyHint()
    {
        var hint = Hint;
        var hasHint = !string.IsNullOrWhiteSpace(hint);
        HintText.Text = hasHint ? hint : string.Empty;
        HintText.Visibility = hasHint ? Visibility.Visible : Visibility.Collapsed;
    }
}