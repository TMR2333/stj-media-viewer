using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 5 星评分（不支持半星）。
///
/// 用法：
/// <code>
/// &lt;controls:RatingStars Rating="{x:Bind Vm.Rating, Mode=TwoWay}" /&gt;
/// &lt;controls:RatingStars Rating="4" IsReadOnly="True" /&gt;
/// </code>
/// 交互规则：点第 n 颗 = n 星；再点当前星值 = 归 0。IsReadOnly 时不可点、无 hover 预览。
/// </summary>
public sealed partial class RatingStars : UserControl
{
    public static readonly DependencyProperty RatingProperty = DependencyProperty.Register(
        nameof(Rating), typeof(int), typeof(RatingStars), new PropertyMetadata(0, OnRatingChanged));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RatingStars), new PropertyMetadata(false, OnIsReadOnlyChanged));

    private readonly Button[] _stars;
    private int _hoveredIndex = -1;

    public RatingStars()
    {
        InitializeComponent();

        _stars = new[] { Star0, Star1, Star2, Star3, Star4 };
        for (var i = 0; i < _stars.Length; i++)
        {
            var index = i;
            _stars[i].Tag = i + 1;
            _stars[i].Click += (_, _) => OnStarClicked(index + 1);
            _stars[i].PointerEntered += (_, _) => OnStarPointerEntered(index);
            _stars[i].PointerExited += (_, _) => OnStarPointerExited();
            AutomationProperties.SetName(_stars[i], $"{i + 1} 星");
        }

        UpdateReadOnlyState();
        UpdateVisuals();
    }

    /// <summary>用户操作（点击）改变评分时触发；代码赋值不触发。</summary>
    public event EventHandler<int>? RatingChanged;

    /// <summary>当前评分 0..5（双向可用）。</summary>
    public int Rating
    {
        get => (int)GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    /// <summary>只读模式：仅展示，不响应点击与 hover。</summary>
    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (RatingStars)d;
        var clamped = Math.Clamp((int)e.NewValue, 0, 5);
        if (clamped != (int)e.NewValue)
        {
            control.Rating = clamped;
            return;
        }

        control.UpdateVisuals();
    }

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RatingStars)d).UpdateReadOnlyState();

    private void OnStarClicked(int value)
    {
        if (IsReadOnly) return;

        var next = Rating == value ? 0 : value;
        _hoveredIndex = -1;

        if (Rating != next)
        {
            Rating = next;
            UpdateVisuals();
            RatingChanged?.Invoke(this, next);
        }
    }

    private void OnStarPointerEntered(int index)
    {
        if (IsReadOnly) return;
        _hoveredIndex = index;
        UpdateVisuals();
    }

    private void OnStarPointerExited()
    {
        if (IsReadOnly) return;
        _hoveredIndex = -1;
        UpdateVisuals();
    }

    private void UpdateReadOnlyState()
    {
        foreach (var star in _stars)
        {
            star.IsHitTestVisible = !IsReadOnly;
            star.IsTabStop = !IsReadOnly;
            star.UseSystemFocusVisuals = !IsReadOnly;
        }
    }

    private void UpdateVisuals()
    {
        if (_stars is null) return;

        // hover 预览优先于实际值（只读模式不预览）
        var effective = _hoveredIndex >= 0 ? _hoveredIndex + 1 : Rating;
        effective = Math.Clamp(effective, 0, 5);

        for (var i = 0; i < _stars.Length; i++)
        {
            var filled = i < effective;
            if (_stars[i].Content is Grid grid && grid.Children.Count >= 2)
            {
                if (grid.Children[0] is UIElement filledIcon) filledIcon.Visibility = filled ? Visibility.Visible : Visibility.Collapsed;
                if (grid.Children[1] is UIElement emptyIcon) emptyIcon.Visibility = filled ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }
}