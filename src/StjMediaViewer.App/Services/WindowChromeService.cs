using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.UI;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 窗口镀层服务：系统背板（Mica / Acrylic / 纯色）+ 标题栏配色 + 圆角。
///
/// 三套设计语言对应的"系统背板"策略：
///   Fluent → MicaBackdrop（Win11 的 Mica，窗口底色半透明透过壁纸；不可用时降级 DesktopAcrylicBackdrop）
///   MD3    → 纯色（MD3 的规矩是"色调表面 + 零透明"，背板必须关掉）
///   Glass  → DesktopAcrylicBackdrop（WinUI 能拿到的最接近"毛玻璃"的系统背板）
/// 旧系统（Win10）不支持 Mica 时全部落到 Acrylic / 纯色，不抛异常、不崩。
/// </summary>
public sealed class WindowChromeService : IWindowChromeService
{
    private Window? _window;
    private UIElement? _titleBarElement;
    private string _appliedBackdropKey = "";

    /// <summary>由 DesignLanguageService 在设计语言切换后调用（Configure 时也会走一遍）。</summary>
    public void Configure(Window window)
    {
        _window = window;

        try
        {
            ApplyChrome();
        }
        catch (Exception ex)
        {
            Log.Warn("窗口镀层初始化失败（降级为系统默认）", ex);
        }

        // 明暗模式在"跟随系统"时会随系统主题变化：重新给标题栏上色。
        try
        {
            if (window.Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, _) => ApplyChrome();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("订阅主题变化失败", ex);
        }

        // 窗口晚于服务创建：这里补一次明暗主题，保证启动时 RequestedTheme 正确。
        try
        {
            if (AppServices.Design is DesignLanguageService design)
            {
                design.ApplyThemeToRoot();
            }

            // 如果调用方先设置过自定义标题栏元素，窗口重建后补挂一次。
            if (_titleBarElement is not null)
            {
                SetTitleBarElement(_titleBarElement);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("窗口创建后应用明暗主题失败", ex);
        }
    }

    /// <summary>换皮肤 / 换明暗模式后刷新背板与标题栏配色。幂等、可重复调用。</summary>
    /// <summary>强制重设背板：清掉缓存 key 再应用（引导页关过 Mica 时要能恢复）。</summary>
    public Task RestoreBackdropAsync()
    {
        _appliedBackdropKey = null;
        return RefreshBackdropAsync();
    }

    public Task RefreshBackdropAsync()
    {
        try
        {
            ApplyChrome();
        }
        catch (Exception ex)
        {
            Log.Warn("刷新窗口背板失败", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 自定义标题栏拖拽区。默认窗口（MainWindow）没有 ExtendsContentIntoTitleBar，
    /// 这里只在调用方已经开启扩展时生效，避免抢走系统标题栏。
    /// </summary>
    public void SetTitleBarElement(UIElement? element)
    {
        _titleBarElement = element;
        try
        {
            if (_window is { ExtendsContentIntoTitleBar: true })
            {
                _window.SetTitleBar(element);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置自定义标题栏元素失败", ex);
        }
    }

    // =====================================================================================
    //  背板
    // =====================================================================================

    private void ApplyChrome()
    {
        if (_window is null) return;
        ApplyBackdrop();
        ApplyTitleBarColors();
    }

    private void ApplyBackdrop()
    {
        if (_window is null) return;

        // 单一设计语言（Fluent）：优先 Mica，Win10 / 远程桌面等不支持时退 Acrylic，再退纯色。
        var key = "fluent";
        SystemBackdrop? backdrop = CreateMica();
        if (backdrop is null)
        {
            backdrop = CreateAcrylic();
            key = "fluent-acrylic";
        }

        if (string.Equals(_appliedBackdropKey, key, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _window.SystemBackdrop = backdrop;
            _appliedBackdropKey = key;
            Log.Info($"系统背板 → {backdrop?.GetType().Name ?? "null（纯色）"}（fluent）");
        }
        catch (Exception ex)
        {
            // 老系统/远程桌面下设置背板可能失败：降级为纯色，不影响使用。
            Log.Warn("设置系统背板失败，降级为纯色窗口", ex);
            try { _window.SystemBackdrop = null; } catch { /* 已尽力 */ }
            _appliedBackdropKey = "none";
        }
    }

    private static SystemBackdrop? CreateMica()
    {
        try
        {
            return MicaController.IsSupported() ? new MicaBackdrop() : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Mica 背板不可用", ex);
            return null;
        }
    }

    private static SystemBackdrop? CreateAcrylic()
    {
        try
        {
            return DesktopAcrylicController.IsSupported() ? new DesktopAcrylicBackdrop() : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Acrylic 背板不可用", ex);
            return null;
        }
    }

    // =====================================================================================
    //  标题栏
    // =====================================================================================

    private void ApplyTitleBarColors()
    {
        if (_window is null) return;

        try
        {
            // Win10 上自定义标题栏配色不受支持（IsCustomizationSupported=false）。
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var appWindow = _window.AppWindow;
            var titleBar = appWindow?.TitleBar;
            if (titleBar is null) return;

            var languageId = ResolveDesignLanguageId();
            var dark = IsDarkTheme();
            var transparent = Color.FromArgb(0, 0, 0, 0);

            var palette = TitleBarPalette.For(languageId, dark);

            // 背板透出：Fluent(Mica) / Glass(Acrylic) 用透明标题栏，颜色交给系统背板；
            // MD3 零透明 → 标题栏用实色表面，跟内容区连成一片。
            titleBar.BackgroundColor = palette.Background;
            titleBar.InactiveBackgroundColor = palette.Background;
            titleBar.ForegroundColor = palette.Foreground;
            titleBar.InactiveForegroundColor = palette.InactiveForeground;

            titleBar.ButtonBackgroundColor = transparent;
            titleBar.ButtonInactiveBackgroundColor = transparent;
            titleBar.ButtonForegroundColor = palette.Foreground;
            titleBar.ButtonInactiveForegroundColor = palette.InactiveForeground;
            titleBar.ButtonHoverBackgroundColor = palette.Hover;
            titleBar.ButtonHoverForegroundColor = palette.Foreground;
            titleBar.ButtonPressedBackgroundColor = palette.Pressed;
            titleBar.ButtonPressedForegroundColor = palette.Foreground;
        }
        catch (Exception ex)
        {
            // 标题栏配色只是锦上添花：失败就保持系统默认。
            Log.Warn("设置标题栏配色失败", ex);
        }
    }

    /// <summary>标题栏配色表（按设计语言 + 明暗）。这是唯一允许出现在代码里的颜色：
    /// AppWindowTitleBar 只接受 Windows.UI.Color，XAML token 在这里用不上。</summary>
    private sealed record TitleBarColors(Color? Background, Color Foreground, Color InactiveForeground, Color Hover, Color Pressed);

    private static class TitleBarPalette
    {
        private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

        private static readonly Color DarkForeground = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color DarkInactiveForeground = Color.FromArgb(255, 0x73, 0x73, 0x73);
        private static readonly Color DarkHover = Color.FromArgb(36, 255, 255, 255);
        private static readonly Color DarkPressed = Color.FromArgb(51, 255, 255, 255);

        private static readonly Color LightForeground = Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        private static readonly Color LightInactiveForeground = Color.FromArgb(255, 0x8A, 0x8A, 0x8A);
        private static readonly Color LightHover = Color.FromArgb(20, 0, 0, 0);
        private static readonly Color LightPressed = Color.FromArgb(31, 0, 0, 0);

        public static TitleBarColors For(string languageId, bool dark)
        {
            var foreground = dark ? DarkForeground : LightForeground;
            var inactive = dark ? DarkInactiveForeground : LightInactiveForeground;
            var hover = dark ? DarkHover : LightHover;
            var pressed = dark ? DarkPressed : LightPressed;

            // Fluent：透明标题栏，让 Mica / Acrylic 透出来
            return new TitleBarColors(
                Transparent,
                foreground,
                inactive,
                hover,
                pressed);
        }
    }

    // =====================================================================================
    //  当前状态解析
    // =====================================================================================

    private static string ResolveDesignLanguageId()
    {
        try
        {
            var id = AppServices.Design?.CurrentId;
            return DesignLanguageIds.Normalize(id);
        }
        catch
        {
            return DesignLanguageIds.Fluent;
        }
    }

    private static bool IsDarkTheme()
    {
        try
        {
            if (AppServices.Design is DesignLanguageService design)
            {
                return design.ResolveTheme() switch
                {
                    ElementTheme.Dark => true,
                    ElementTheme.Light => false,
                    _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
                };
            }
        }
        catch
        {
            // 忽略：退回系统主题判断
        }

        return Application.Current?.RequestedTheme != ApplicationTheme.Light;
    }
}