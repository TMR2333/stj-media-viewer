using Microsoft.UI.Xaml.Controls;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 应用内背景光场（Liquid Glass 的"地基"）。
///
/// 玻璃必须有东西可折射：把本控件铺在窗口内容的最底层（Stretch、IsHitTestVisible=False），
/// 它在 Glass 皮肤下会画出底色渐变 + 四个大半径彩色光斑 + 四周压暗；
/// 在 Fluent / MD3 皮肤下对应 token 全透明，所以它什么都不画（自动退化）。
///
/// 用法（ShellPage 根 Grid 的第一个子元素）：
/// <code>
/// &lt;Grid&gt;
///     &lt;controls:AmbientBackdrop /&gt;
///     &lt;!-- 其余界面 --&gt;
/// &lt;/Grid&gt;
/// </code>
/// 视觉参数全部定义在 Tokens.Glass.xaml 的 AppAmbient* 系列 token 里。
/// </summary>
public sealed partial class AmbientBackdrop : UserControl
{
    public AmbientBackdrop()
    {
        InitializeComponent();
        IsHitTestVisible = false;
    }
}