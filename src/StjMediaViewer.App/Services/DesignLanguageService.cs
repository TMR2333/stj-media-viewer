using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.App.Services;

/// <summary>
/// 设计语言（皮肤）服务：运行时切换 Fluent / Material Design 3 / Liquid Glass。
///
/// 三套皮肤共存的关键是 Tokens.Base.xaml 定义的 token 契约：
/// 每套皮肤提供"同名同类型"的键，本服务只负责把当前皮肤的词典追加到
/// <c>Application.Resources.MergedDictionaries</c> 的末尾（后合并者胜出），
/// 同时移除上一次皮肤的词典，避免内存膨胀与多层覆盖。
///
/// 为什么换完词典还要重建视觉树：
/// WinUI 的 <c>{ThemeResource}</c> 解析结果会缓存在已实例化元素上，单纯替换
/// MergedDictionaries 不会让既有控件重新取值（只有明暗主题变化才会触发重解析）。
/// 所以切皮肤后必须让界面重新实例化一次：把 MainWindow 的 RootFrame 重新
/// Navigate 到当前页面类型（Frame 默认 NavigationCacheMode=Disabled，会创建新页面），
/// 用户无需重启即可看到整套皮肤生效。明暗切换（SetThemeModeAsync）只需要改根元素的
/// RequestedTheme，已实例化元素会由框架自动重解析，不需要重建。
/// </summary>
public sealed class DesignLanguageService : IDesignLanguageService
{
    private static readonly string[] SupportedThemeModes = { "system", "light", "dark" };

    /// <summary>
    /// 皮肤词典表。产品已收敛为**单一设计语言 Fluent**（Material Design 3 / Liquid Glass
    /// 因观感不达标下线，词典文件已删除）。保留这张表是为了让"换皮肤 → 换窗口背板 →
    /// 必要时重建视觉树"的机制仍然成立，将来若要再加皮肤，只需在这里补条目。
    /// </summary>
    private static readonly Dictionary<string, string[]> SkinDictionaries = new(StringComparer.OrdinalIgnoreCase)
    {
        [DesignLanguageIds.Fluent] = new[]
        {
            "ms-appx:///Themes/Tokens.Fluent.xaml",
        },
    };

    private readonly List<ResourceDictionary> _activeDictionaries = new();
    private bool _applied;
    private bool _rebuildQueued;

    public string CurrentId { get; private set; } = DesignLanguageIds.Fluent;

    public event EventHandler? Changed;

    /// <summary>
    /// 切换设计语言。persist=true 时写回设置；persist=false 用于启动恢复（避免回写）。
    /// 完成后 <see cref="Changed"/> 触发，并重建界面使皮肤立即生效。
    /// </summary>
    public async Task ApplyAsync(string languageId, bool persist = true)
        => await ApplyCoreAsync(languageId, persist, rebuildVisualTree: true);

    /// <summary>
    /// App 启动时由 Lead 调用：从设置恢复上次的设计语言（不回写设置）。
    /// 启动时 MainWindow 已经创建（App.xaml.cs: Configure → ApplySavedAsync），
    /// 此时 ShellPage 是用 Base 词典实例化的，因此同样需要重建一次视觉树，
    /// 保证恢复的皮肤（以及框架控件桥）在第一帧就生效。
    /// </summary>
    public async Task ApplySavedAsync()
    {
        var saved = TryGetSettings()?.Current.DesignLanguage;
        await ApplyCoreAsync(DesignLanguageIds.Normalize(saved), persist: false, rebuildVisualTree: true);
    }

    /// <summary>
    /// 明暗模式：system | light | dark。只改根元素 RequestedTheme —— 框架会让既有元素里的
    /// ThemeResource 自动重解析，因此不需要重建页面（与切皮肤不同）。
    /// </summary>
    public async Task SetThemeModeAsync(string mode, bool persist = true)
    {
        var normalized = NormalizeThemeMode(mode);
        var settings = TryGetSettings();
        if (settings is not null)
        {
            settings.Current.ThemeMode = normalized;
            if (persist)
            {
                try { await settings.SaveAsync(); }
                catch (Exception ex) { Log.Warn("保存明暗模式失败", ex); }
            }
        }

        ApplyThemeToRoot();
        await RefreshWindowChromeAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把设置里的明暗模式解析成 ElementTheme（system → Default，跟随系统）。</summary>
    public ElementTheme ResolveTheme()
    {
        var mode = TryGetSettings()?.Current.ThemeMode;
        return (mode ?? "system").Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>
    /// 把当前明暗模式应用到窗口根元素。窗口在服务初始化之后才创建，所以由
    /// WindowChromeService.Configure 与 ApplySavedAsync 两处机会调用（幂等）。
    /// </summary>
    public void ApplyThemeToRoot()
    {
        try
        {
            if (App.MainWindow?.Content is not FrameworkElement root) return;
            var theme = ResolveTheme();
            if (root.RequestedTheme != theme)
            {
                root.RequestedTheme = theme;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("应用明暗主题失败", ex);
        }
    }

    // =====================================================================================
    //  核心流程
    // =====================================================================================

    private async Task ApplyCoreAsync(string languageId, bool persist, bool rebuildVisualTree)
    {
        var id = DesignLanguageIds.Normalize(languageId);

        // ResourceDictionary 的增删必须在 UI 线程；正常调用都来自 UI（设置页 / OnLaunched）。
        if (!HasUiThreadAccess())
        {
            await EnqueueOnUiThreadAsync(() => ApplyCoreAsync(languageId, persist, rebuildVisualTree));
            return;
        }

        var unchanged = _applied && string.Equals(CurrentId, id, StringComparison.OrdinalIgnoreCase);
        if (!unchanged)
        {
            SwapDictionaries(id);
            CurrentId = id;
            _applied = true;
        }

        ApplyThemeToRoot();

        if (persist)
        {
            var settings = TryGetSettings();
            if (settings is not null)
            {
                settings.Current.DesignLanguage = id;
                try { await settings.SaveAsync(); }
                catch (Exception ex) { Log.Warn("保存设计语言失败", ex); }
            }
        }

        if (!unchanged || rebuildVisualTree)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        await RefreshWindowChromeAsync();

        if (rebuildVisualTree && !unchanged)
        {
            RebuildVisualTree(id);
        }

        Log.Info($"设计语言 → {id}（词典 {_activeDictionaries.Count} 个，persist={persist}, rebuild={rebuildVisualTree}）");
    }

    /// <summary>移除上一次皮肤的词典，再追加当前皮肤的词典（顺序：token → controls）。</summary>
    private void SwapDictionaries(string id)
    {
        var application = Application.Current;
        if (application is null) return;

        var merged = application.Resources.MergedDictionaries;

        // 先移除我们之前加过的实例（含兜底清理：万一旧实例被别处改过仍能识别）。
        foreach (var dictionary in _activeDictionaries.ToList())
        {
            try { merged.Remove(dictionary); }
            catch (Exception ex) { Log.Warn("移除旧设计语言词典失败", ex); }
        }
        _activeDictionaries.Clear();

        if (!SkinDictionaries.TryGetValue(id, out var uris)) return;

        foreach (var uri in uris)
        {
            try
            {
                var dictionary = new ResourceDictionary { Source = new Uri(uri) };
                merged.Add(dictionary);
                _activeDictionaries.Add(dictionary);
            }
            catch (Exception ex)
            {
                // 最坏情况：结构性控件词典（Controls.*.xaml）加载失败 → 皮肤 token 仍然生效，
                // 只是按钮回到 WinUI 默认模板。绝不因为皮肤问题让整个 App 起不来。
                Log.Warn($"加载设计语言词典失败：{uri}", ex);
            }
        }
    }

    /// <summary>
    /// 让整棵树重新实例化一次，使新皮肤的 token 真正落到每个控件上。
    /// 做法：取 MainWindow 的 RootFrame，保存当前 Page 类型后重新 Navigate（Frame 默认不缓存页面）。
    /// 实际重建动作**投递到 DispatcherQueue**：切皮肤通常是从页面的 ComboBox 事件里发起的，
    /// 直接销毁"正在执行事件的页面"虽然多数情况没问题，但延后一帧更稳。
    /// </summary>
    private void RebuildVisualTree(string id)
    {
        try
        {
            var frame = FindRootFrame();
            if (frame is null || frame.CurrentSourcePageType is null)
            {
                // 没有 Frame（启动早期）→ 用"明暗主题抖一下"触发 ThemeResource 重解析兜底。
                ForceThemeResourceRefresh();
                return;
            }

            var dispatcher = frame.DispatcherQueue;
            if (dispatcher is not null && dispatcher.HasThreadAccess && !_rebuildQueued)
            {
                _rebuildQueued = true;
                if (dispatcher.TryEnqueue(() =>
                    {
                        _rebuildQueued = false;
                        RebuildRootFrameNow(id);
                    }))
                {
                    return;
                }

                _rebuildQueued = false;
            }

            RebuildRootFrameNow(id);
        }
        catch (Exception ex)
        {
            Log.Warn("切皮肤后重建视觉树失败，回退到主题重解析", ex);
            ForceThemeResourceRefresh();
        }
    }

    private void RebuildRootFrameNow(string id)
    {
        try
        {
            var frame = FindRootFrame();
            if (frame is null) return;

            var pageType = frame.CurrentSourcePageType;
            if (pageType is null) return;

            // 清掉 Frame 缓存，确保新页面实例（否则 NavigationCacheMode=Required 的页面会复用旧实例）。
            try { frame.CacheSize = 0; } catch { /* CacheSize 在某些状态不可写，忽略 */ }

            frame.Content = null;
            var navigated = frame.Navigate(pageType);
            if (!navigated)
            {
                Log.Warn($"切皮肤后重建页面失败：{pageType.Name}，回退到主题重解析");
                ForceThemeResourceRefresh();
            }
            else
            {
                Log.Info($"已重建视觉树：{pageType.Name}（皮肤 {id}）");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("切皮肤后重建视觉树失败，回退到主题重解析", ex);
            ForceThemeResourceRefresh();
        }
    }

    /// <summary>
    /// 兜底：在"深色/浅色"之间快速切一次再切回来，迫使框架重新解析主题资源。
    /// 会有一帧闪烁，只在拿不到 Frame 时使用。
    /// </summary>
    private void ForceThemeResourceRefresh()
    {
        try
        {
            if (App.MainWindow?.Content is not FrameworkElement root) return;
            var desired = ResolveTheme();
            var temporary = desired == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = temporary;
            if (root.DispatcherQueue is { } dispatcher)
            {
                dispatcher.TryEnqueue(() => root.RequestedTheme = desired);
            }
            else
            {
                root.RequestedTheme = desired;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("兜底主题重解析失败", ex);
        }
    }

    /// <summary>MainWindow.RootFrame；拿不到时沿视觉树找第一个 Frame（ui-views 改结构时的兜底）。</summary>
    private static Frame? FindRootFrame()
    {
        try
        {
            if (App.MainWindow is MainWindow mainWindow && mainWindow.Frame is { } frame)
            {
                return frame;
            }

            if (App.MainWindow?.Content is DependencyObject root)
            {
                return FindFirstFrame(root);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("定位 RootFrame 失败", ex);
        }

        return null;
    }

    private static Frame? FindFirstFrame(DependencyObject parent)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Frame frame) return frame;
            if (FindFirstFrame(child) is { } nested) return nested;
        }

        return null;
    }

    private static async Task RefreshWindowChromeAsync()
    {
        try
        {
            if (AppServices.WindowChrome is { } chrome)
            {
                await chrome.RefreshBackdropAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("刷新窗口镀层失败", ex);
        }
    }

    private static ISettingsService? TryGetSettings()
    {
        try { return AppServices.Settings; }
        catch { return null; }
    }

    private static string NormalizeThemeMode(string? mode)
    {
        var normalized = (mode ?? "system").Trim().ToLowerInvariant();
        return SupportedThemeModes.Contains(normalized) ? normalized : "system";
    }

    // =====================================================================================
    //  UI 线程调度
    // =====================================================================================

    private static bool HasUiThreadAccess()
        => DispatcherQueue.GetForCurrentThread() is { HasThreadAccess: true };

    private static async Task EnqueueOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            await action();
            return;
        }

        await completion.Task;
    }
}