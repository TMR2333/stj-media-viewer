using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 页面内容版面助手：让「左对齐 + 最大宽度」的页面在全屏时把可用宽度真正用起来。
///
/// 页面根节点是 <c>StackPanel HorizontalAlignment="Left"</c> 时，它只会取内容想要的宽度，
/// 最大化后右侧会空掉一大块。这里按滚动区宽度给内容显式设宽（上限 <see cref="MaxContentWidth"/>），
/// 既保持左对齐，又在宽窗口下铺满。
/// </summary>
public static class PageLayout
{
    /// <summary>内容区最大宽度（DIP）：再宽就保持左对齐留白，避免超宽屏拉成一条。</summary>
    public const double MaxContentWidth = 1760;

    /// <summary>给内容根设宽并跟随窗口尺寸变化。</summary>
    public static void Fit(ScrollViewer scroll, FrameworkElement content, double minWidth = 360)
    {
        if (scroll is null || content is null) return;

        void Apply()
        {
            try
            {
                var pad = scroll.Padding.Left + scroll.Padding.Right;
                var available = scroll.ActualWidth - pad - 2;   // 2 = 滚动条安全余量
                if (available <= 0) return;
                var target = Math.Min(available, MaxContentWidth);
                content.Width = Math.Max(minWidth, target);
            }
            catch { /* 布局还没准备好时忽略 */ }
        }

        scroll.SizeChanged += (_, _) => Apply();
        scroll.Loaded += (_, _) => Apply();
        Apply();
        FadeIn(content);
    }

    /// <summary>内容淡入（默认 200ms / EaseOut）：切页时不再是"硬闪"。</summary>
    public static void FadeIn(UIElement? element, double milliseconds = 200)
    {
        if (element is null) return;
        try
        {
            element.Opacity = 0;
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch { }
    }
}