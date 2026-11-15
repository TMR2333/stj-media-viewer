using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 筛选胶囊：一个可勾选的 pill，支持右侧计数徽标。
///
/// 用法：
/// <code>
/// &lt;controls:FacetChip Label="Sony" Count="128" IsChecked="{x:Bind Vm.SonyOnly, Mode=TwoWay}" /&gt;
/// </code>
/// 外观完全走 AppChipStyle（三套皮肤各自覆盖）：
///   Fluent = 胶囊 + AppSurfaceAltBrush；MD3 = FilterChip（选中 12% 主色容器 + 主色描边）；
///   Glass = 玻璃胶囊（镜面描边）。
/// </summary>
public sealed partial class FacetChip : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FacetChip), new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(FacetChip), new PropertyMetadata(-1, OnCountChanged));

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(FacetChip), new PropertyMetadata(false, OnIsCheckedChanged));

    private bool _syncing;

    public FacetChip()
    {
        InitializeComponent();
        UpdateLabel();
        UpdateCount();
    }

    /// <summary>勾选状态变化（用户点击或代码设置都会触发）。</summary>
    public event EventHandler<bool>? CheckedChanged;

    /// <summary>胶囊文字。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>右侧计数徽标；&lt; 0 表示不显示。</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>是否选中（双向可用）。</summary>
    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FacetChip)d).UpdateLabel();

    private static void OnCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FacetChip)d).UpdateCount();

    private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chip = (FacetChip)d;
        if (chip._syncing) return;

        chip._syncing = true;
        try
        {
            chip.ChipToggle.IsChecked = chip.IsChecked;
        }
        finally
        {
            chip._syncing = false;
        }

        chip.CheckedChanged?.Invoke(chip, chip.IsChecked);
    }

    private void UpdateLabel()
    {
        LabelText.Text = Label ?? string.Empty;
        AutomationProperties.SetName(ChipToggle, Label ?? string.Empty);
    }

    private void UpdateCount()
    {
        if (Count < 0)
        {
            CountBadge.Visibility = Visibility.Collapsed;
            return;
        }

        CountBadge.Visibility = Visibility.Visible;
        CountText.Text = Count > 99 ? "99+" : Count.ToString();
    }

    private void ChipToggle_Checked(object sender, RoutedEventArgs e) => SyncCheckState(true);

    private void ChipToggle_Unchecked(object sender, RoutedEventArgs e) => SyncCheckState(false);

    private void SyncCheckState(bool isChecked)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            IsChecked = isChecked;
        }
        finally
        {
            _syncing = false;
        }

        CheckedChanged?.Invoke(this, isChecked);
    }
}