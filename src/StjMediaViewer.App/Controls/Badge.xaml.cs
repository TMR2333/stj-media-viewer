using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>小徽标的语义类型；颜色全部来自 token（BadgeXxxBrush / BadgeXxxTextBrush）。</summary>
public enum BadgeKind
{
    /// <summary>中性（无强语义）—— 走 AppSurfaceAltBrush + TextSecondaryBrush。</summary>
    Neutral = 0,
    Raw = 1,
    Log = 2,
    Video = 3,
    Image = 4,
    Audio = 5,
    Ai = 6,
}

/// <summary>
/// 小徽标：RAW / Log / 视频 / 音频 / AI 等。
///
/// 用法：
/// <code>
/// &lt;controls:Badge Text="RAW" Kind="Raw" /&gt;
/// &lt;controls:Badge Text="S-Log3" Kind="Log" /&gt;
/// </code>
/// 颜色走 token：三套皮肤各自的 BadgeXxxBrush/BadgeXxxTextBrush 自动生效；
/// 明暗切换由 ThemeResource 自动重解析。
/// </summary>
public sealed partial class Badge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Badge), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(BadgeKind), typeof(Badge), new PropertyMetadata(BadgeKind.Neutral, OnKindChanged));

    public Badge()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    /// <summary>徽标文字（如 RAW / Log / 视频 / AI）。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>语义类型（决定配色）。</summary>
    public BadgeKind Kind
    {
        get => (BadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Badge)d).UpdateVisuals();

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Badge)d).UpdateVisuals();

    private void UpdateVisuals()
    {
        var text = Text ?? string.Empty;

        NeutralBadge.Visibility = Kind == BadgeKind.Neutral ? Visibility.Visible : Visibility.Collapsed;
        RawBadge.Visibility = Kind == BadgeKind.Raw ? Visibility.Visible : Visibility.Collapsed;
        LogBadge.Visibility = Kind == BadgeKind.Log ? Visibility.Visible : Visibility.Collapsed;
        VideoBadge.Visibility = Kind == BadgeKind.Video ? Visibility.Visible : Visibility.Collapsed;
        ImageBadge.Visibility = Kind == BadgeKind.Image ? Visibility.Visible : Visibility.Collapsed;
        AudioBadge.Visibility = Kind == BadgeKind.Audio ? Visibility.Visible : Visibility.Collapsed;
        AiBadge.Visibility = Kind == BadgeKind.Ai ? Visibility.Visible : Visibility.Collapsed;

        NeutralText.Text = text;
        RawText.Text = text;
        LogText.Text = text;
        VideoText.Text = text;
        ImageText.Text = text;
        AudioText.Text = text;
        AiText.Text = text;

        // 空文字时不占位
        Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}