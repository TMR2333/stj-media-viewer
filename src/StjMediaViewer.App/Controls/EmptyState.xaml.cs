using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 空状态：图标 + 标题 + 说明 + 可选操作按钮。
///
/// 用法：
/// <code>
/// &lt;controls:EmptyState Glyph="&#xE8B9;" Title="还没有素材"
///                        Description="选择媒体库文件夹后开始扫描"
///                        ActionText="选择文件夹" ActionInvoked="OnPickLibrary" /&gt;
/// </code>
/// </summary>
public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata("\uE7C5", OnGlyphChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnActionTextChanged));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    public EmptyState()
    {
        InitializeComponent();
        ApplyGlyph();
        ApplyAction();
    }

    /// <summary>点击操作按钮（没有设置 ActionCommand 时用它）。</summary>
    public event EventHandler? ActionInvoked;

    /// <summary>图标（Segoe Fluent Icons 字形）。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>标题。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>说明文字。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>操作按钮文字；空 = 不显示按钮。</summary>
    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>操作命令（可选，MVVM 用）。</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmptyState)d).ApplyGlyph();

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmptyState)d).TitleText.Text = (string?)e.NewValue ?? string.Empty;

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmptyState)d).DescriptionText.Text = (string?)e.NewValue ?? string.Empty;

    private static void OnActionTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmptyState)d).ApplyAction();

    private void ApplyGlyph()
    {
        var glyph = Glyph;
        StateGlyph.Glyph = string.IsNullOrWhiteSpace(glyph) ? string.Empty : glyph;
        StateGlyph.Visibility = string.IsNullOrWhiteSpace(glyph) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyAction()
    {
        var text = ActionText;
        var hasAction = !string.IsNullOrWhiteSpace(text);
        ActionButton.Content = hasAction ? text : string.Empty;
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActionCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }

        ActionInvoked?.Invoke(this, EventArgs.Empty);
    }
}