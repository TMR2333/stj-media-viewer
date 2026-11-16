using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.Services;

/// <summary>设计语言（皮肤）服务：切 token 字典、切控件字典、切窗口背板。</summary>
public interface IDesignLanguageService
{
    string CurrentId { get; }
    event EventHandler? Changed;

    Task ApplyAsync(string languageId, bool persist = true);
    Task ApplySavedAsync();

    /// <summary>明暗模式（system/light/dark）解析后交给控件层。</summary>
    ElementTheme ResolveTheme();

    Task SetThemeModeAsync(string mode, bool persist = true);
}

/// <summary>缩略图加载器（App 侧）：把字节流转成可绑定的 ImageSource，并带 LRU 缓存。</summary>
public interface IThumbnailLoader
{
    Task<ImageSource?> LoadAsync(MediaAsset asset, int size, CancellationToken ct = default);
    /// <summary>后台预热一批缩略图（只落磁盘缓存，不碰 UI 对象）。</summary>
    void Prefetch(IEnumerable<MediaAsset> assets, int size, int maxCount = 48);
    void Invalidate(string assetId);
    void Clear();
}

/// <summary>窗口镀层：标题栏、系统背板（Mica/Acrylic）、圆角。</summary>
public interface IWindowChromeService
{
    void Configure(Window window);
    Task RefreshBackdropAsync();
    void SetTitleBarElement(UIElement? element);
}

/// <summary>轻量对话框 / 提示。</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消");
    Task<string?> PromptAsync(string title, string message, string? initial = null, string placeholder = "");
    Task InfoAsync(string title, string message);
    Task ErrorAsync(string title, string message);
    Task<double?> SliderAsync(string title, string message, double min, double max, double value);
}