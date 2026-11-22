using StjMediaViewer.Core.Models;

namespace StjMediaViewer.App.ViewModels;

/// <summary>扫描 / 状态栏进度快照。</summary>
public sealed class ScanStatus
{
    public bool IsActive { get; init; }
    public bool IsIndeterminate { get; init; }
    public double Percent { get; init; }
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
}

/// <summary>
/// 页面之间的轻量事件总线：View 之间不直接持有对方引用（Frame 会重建页面），
/// 全部通过这里解耦。只做 UI 内通信，不承载业务。
/// </summary>
public static class AppEvents
{
    /// <summary>顶栏搜索框文本变化（已做 250ms 防抖）。</summary>
    public static event EventHandler<string>? SearchChanged;
    public static void RaiseSearchChanged(string text) => SearchChanged?.Invoke(null, text);

    /// <summary>把一行文字写到 Shell 状态栏。</summary>
    public static event EventHandler<ScanStatus>? StatusChanged;
    public static void SetStatus(string text) => StatusChanged?.Invoke(null, new ScanStatus { Text = text });
    public static void SetError(string text) => StatusChanged?.Invoke(null, new ScanStatus { Text = text, IsError = true });

    /// <summary>扫描进度（顶部细进度条 + 状态栏）。</summary>
    public static event EventHandler<ScanStatus>? ScanProgressChanged;
    public static void ReportScan(ScanStatus status) => ScanProgressChanged?.Invoke(null, status);

    /// <summary>素材被就地修改（评分 / 色标 / 分类 / 保存），网格与检视面板都要跟着刷新。</summary>
    public static event EventHandler<MediaAsset>? AssetUpdated;
    public static void RaiseAssetUpdated(MediaAsset asset) => AssetUpdated?.Invoke(null, asset);

    /// <summary>网格密度被外部修改（例如导入跨平台交换文件），素材库需要重新应用布局。</summary>
    public static event EventHandler? DensityChanged;
    public static void RaiseDensityChanged() => DensityChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>索引库内容发生结构性变化（首次扫描完成 / 重新扫描），列表需要整页重载。</summary>
    public static event EventHandler? LibraryContentChanged;
    public static void RaiseLibraryContentChanged() => LibraryContentChanged?.Invoke(null, EventArgs.Empty);
}