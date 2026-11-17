using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Color;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.Media.Core;
#pragma warning disable MVVMTK0045 // 本应用未启用 AOT；[ObservableProperty] 字段写法在 WinUI 3 下等价且更易读


namespace StjMediaViewer.App.ViewModels;

/// <summary>
/// 全屏预览层：图片缩放/平移、视频播放、音频封面、左右切换与序号。
/// 打开时把当前筛选结果当作播放列表，因此翻页与网格顺序完全一致。
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    public static PreviewViewModel Shared { get; } = new();

    private IReadOnlyList<MediaAsset> _assets = Array.Empty<MediaAsset>();
    private CancellationTokenSource? _loadCts;

    private PreviewViewModel() { }

    // ---------------- 状态 ----------------
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private Visibility _overlayVisibility = Visibility.Collapsed;

    partial void OnIsOpenChanged(bool value) =>
        OverlayVisibility = value ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private MediaAsset? _current;

    [ObservableProperty]
    private string _indexText = "";

    [ObservableProperty]
    private string _titleText = "";

    [ObservableProperty]
    private string _metaText = "";

    [ObservableProperty]
    private string _zoomText = "适应";

    // ---------------- 媒体显示 ----------------
    [ObservableProperty]
    private ImageSource? _image;

    [ObservableProperty]
    private MediaSource? _videoSource;

    [ObservableProperty]
    private Visibility _imageVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _videoVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _audioVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _placeholderVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _loadingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string _placeholderGlyph = "\uE91B";

    [ObservableProperty]
    private string _placeholderText = "暂无可显示的画面";

    [ObservableProperty]
    private string _audioTitle = "";

    [ObservableProperty]
    private string _audioSubtitle = "";

    // ---------------- 评分 / 色标 ----------------
    [ObservableProperty]
    private int _rating;

    [ObservableProperty]
    private string _colorLabelName = "无";

    [ObservableProperty]
    private string _pickText = "未定";

    [ObservableProperty]
    private string _themeText = "";

    [ObservableProperty]
    private string _toneText = "";

    public bool IsImage => Current?.Kind == MediaKind.Image;
    public bool IsVideo => Current?.Kind == MediaKind.Video;
    public bool IsAudio => Current?.Kind == MediaKind.Audio;
    public bool HasNext => Index < _assets.Count - 1;
    public bool HasPrevious => Index > 0;
    public int Total => _assets.Count;

    // ---------------- 打开 / 关闭 ----------------
    public void Open(IReadOnlyList<MediaAsset> assets, int index)
    {
        _assets = assets;
        Index = Math.Clamp(index, 0, Math.Max(0, assets.Count - 1));
        IsOpen = assets.Count > 0;
        _ = LoadCurrentAsync();
    }

    public void Close()
    {
        IsOpen = false;
        _loadCts?.Cancel();
        VideoSource = null;
        Current = null;
    }

    public void SetPlaylist(IReadOnlyList<MediaAsset> assets)
    {
        _assets = assets;
    }

    public void Next()
    {
        if (!HasNext) return;
        Index++;
        _ = LoadCurrentAsync();
    }

    public void Previous()
    {
        if (!HasPrevious) return;
        Index--;
        _ = LoadCurrentAsync();
    }

    public void JumpTo(int index)
    {
        if (index < 0 || index >= _assets.Count || index == Index) return;
        Index = index;
        _ = LoadCurrentAsync();
    }

    partial void OnIndexChanged(int value) => IndexText = _assets.Count == 0 ? "" : $"{value + 1} / {_assets.Count}";

    // ---------------- 加载当前素材 ----------------
    private async Task LoadCurrentAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var asset = Index >= 0 && Index < _assets.Count ? _assets[Index] : null;
        Current = asset;
        if (asset is null) return;

        var locale = "zh-CN";
        TitleText = asset.FileName;
        MetaText = $"{asset.DisplayDevice} · {Formatting.DateTime(asset.CapturedAt ?? asset.FileModified)} · " +
                   $"{asset.FormatCode}" +
                   (asset.Width is > 0 ? $" · {Formatting.Dimensions(asset.Width, asset.Height)}" : "") +
                   (asset.DurationSeconds is > 0 ? $" · {Formatting.Duration(asset.DurationSeconds)}" : "");
        Rating = Math.Clamp(asset.Rating, 0, 5);
        ColorLabelName = Formatting.ColorLabelName(asset.ColorLabel);
        PickText = Formatting.PickName(asset.IsPick);
        ThemeText = asset.Theme is null ? "未分类" : Vocabularies.ThemeDisplay(asset.Theme, locale);
        ToneText = asset.Tone is null ? "未分类" : Vocabularies.ToneDisplay(asset.Tone, locale);
        IndexText = $"{Index + 1} / {_assets.Count}";
        ZoomText = "适应";

        Image = null;
        VideoSource = null;
        ImageVisibility = Visibility.Collapsed;
        VideoVisibility = Visibility.Collapsed;
        AudioVisibility = Visibility.Collapsed;
        PlaceholderVisibility = Visibility.Collapsed;
        PlaceholderGlyph = Formatting.KindGlyph(asset.Kind);
        PlaceholderText = "暂无可显示的画面";
        LoadingVisibility = Visibility.Visible;
        LoadEdits();
        RefreshEditBar();
        _proxyAttempted = false;
        Interlocked.Increment(ref _lutProxyVersion);
        VideoStatusText = "";
        VideoStatusVisibility = Visibility.Collapsed;
        VideoErrorVisibility = Visibility.Collapsed;

        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(Total));

        try
        {
            switch (asset.Kind)
            {
                case MediaKind.Video:
                    VideoSource = TryCreateVideoSource(asset);
                    VideoVisibility = VideoSource is not null ? Visibility.Visible : Visibility.Collapsed;
                    if (VideoSource is null)
                    {
                        var poster = await ImageSourceLoader.LoadVideoPosterAsync(asset, 1600, ct);
                        ApplyImage(poster, "视频首帧不可用");
                    }

                    // 设备绑定了监视 LUT 且这条视频是 Log：用 ffmpeg 把 LUT 烧进代理再播放
                    var videoLut = ResolveBoundLut(asset);
                    if (videoLut is not null)
                        _ = ApplyVideoLutProxyAsync(asset, videoLut, ct);
                    break;

                case MediaKind.Audio:
                    var cover = await ImageSourceLoader.LoadAudioCoverAsync(asset, ct);
                    AudioTitle = string.IsNullOrWhiteSpace(asset.Title) ? asset.FileName : asset.Title!;
                    AudioSubtitle = $"{asset.DisplayDevice} · {Formatting.Duration(asset.DurationSeconds)}";
                    if (cover is not null)
                    {
                        Image = cover;
                        ImageVisibility = Visibility.Visible;
                    }
                    else
                    {
                        PlaceholderVisibility = Visibility.Visible;
                        PlaceholderText = "音频素材（无内嵌封面）";
                    }
                    AudioVisibility = Visibility.Visible;
                    break;

                default:
                    var image = await ImageSourceLoader.LoadAsync(asset, 2400, ct);
                    if (ct.IsCancellationRequested) return;
                    if (image is null && File.Exists(asset.FullPath))
                        image = await ImageSourceLoader.LoadFromFileAsync(asset.FullPath, 4096, ct);
                    ApplyImage(image, "原图不可读或尚未生成预览");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"预览加载失败 {asset.FileName}: {ex.Message}");
            PlaceholderVisibility = Visibility.Visible;
            PlaceholderText = "预览加载失败";
        }
        finally
        {
            if (!ct.IsCancellationRequested) LoadingVisibility = Visibility.Collapsed;
        }
    }

    private void ApplyImage(ImageSource? image, string failText)
    {
        if (image is not null)
        {
            Image = image;
            ImageVisibility = Visibility.Visible;
            PlaceholderVisibility = Visibility.Collapsed;
        }
        else
        {
            Image = null;
            ImageVisibility = Visibility.Collapsed;
            PlaceholderVisibility = Visibility.Visible;
            PlaceholderText = failText;
        }
    }

    // ==================================================================================
    //  预览编辑：旋转 / 翻转 / 缩放（缩放由视图层 ZoomScroll 执行，这里只给状态）
    // ==================================================================================
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private bool _flipVertical;
    [ObservableProperty] private string _editSummary = "";
    [ObservableProperty] private Visibility _editBarVisibility = Visibility.Collapsed;

    /// <summary>操作栏是否显示（图片才显示旋转/翻转）。</summary>
    public bool CanEdit => IsImage;

    private void RefreshEditBar()
    {
        EditBarVisibility = IsImage ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(CanEdit));
        UpdateEditSummary();
    }

    private void LoadEdits()
    {
        RotationDegrees = Current?.RotationDegrees % 360 ?? 0;
        FlipHorizontal = Current?.FlipHorizontal ?? false;
        FlipVertical = Current?.FlipVertical ?? false;
        UpdateEditSummary();
    }

    private void UpdateEditSummary()
    {
        var parts = new List<string>();
        if (RotationDegrees % 360 != 0) parts.Add($"旋转 {RotationDegrees % 360}°");
        if (FlipHorizontal) parts.Add("水平翻转");
        if (FlipVertical) parts.Add("垂直翻转");
        EditSummary = parts.Count == 0 ? "未编辑" : string.Join(" · ", parts);
    }

    /// <summary>顺时针旋转（delta 可为负）。</summary>
    public Task RotateAsync(int delta)
    {
        if (Current is not { IsImage: true } asset) return Task.CompletedTask;
        RotationDegrees = ((RotationDegrees + delta) % 360 + 360) % 360;
        return PersistEditsAsync(asset);
    }

    public Task ToggleFlipHorizontalAsync()
    {
        if (Current is not { IsImage: true } asset) return Task.CompletedTask;
        FlipHorizontal = !FlipHorizontal;
        return PersistEditsAsync(asset);
    }

    public Task ToggleFlipVerticalAsync()
    {
        if (Current is not { IsImage: true } asset) return Task.CompletedTask;
        FlipVertical = !FlipVertical;
        return PersistEditsAsync(asset);
    }

    public Task ResetEditsAsync()
    {
        if (Current is null) return Task.CompletedTask;
        RotationDegrees = 0;
        FlipHorizontal = false;
        FlipVertical = false;
        return PersistEditsAsync(Current);
    }

    private async Task PersistEditsAsync(MediaAsset asset)
    {
        UpdateEditSummary();
        try
        {
            asset.RotationDegrees = RotationDegrees;
            asset.FlipHorizontal = FlipHorizontal;
            asset.FlipVertical = FlipVertical;

            await AppServices.Store.UpdateImageEditsAsync(asset.Id, RotationDegrees, FlipHorizontal, FlipVertical);
            AppServices.Thumbs.Invalidate(asset.Id);   // 网格缩略图按"编辑后的样子"重新生成
            AppEvents.RaiseAssetUpdated(asset);
            Log.Info($"[preview] 预览编辑 {asset.FileName}: 旋转 {RotationDegrees}°，翻转 H={FlipHorizontal} V={FlipVertical}");
        }
        catch (Exception ex)
        {
            Log.Warn("保存预览编辑失败", ex);
        }
    }

    /// <summary>
    /// 系统解码器不支持该视频（4:2:2 / 10-bit H.264、HEVC、ProRes…）时：
    /// 用 ffmpeg 生成一份 720p H.264 8-bit 代理再播放；没有 ffmpeg 就给出明确的设置指引。
    /// </summary>
    public async Task SwitchToProxyAsync()
    {
        if (_proxyAttempted || Current is not { } asset) return;
        _proxyAttempted = true;

        var proxy = AppServices.VideoProxy;
        if (proxy is null || !proxy.IsAvailable)
        {
            VideoStatusText = "系统解码器不支持这个视频的格式（4:2:2 / 10-bit / HEVC 等常见于相机原片）。" +
                              "在「设置 → 工具链」里指定 ffmpeg.exe 后，会自动生成预览代理来播放。";
            VideoStatusVisibility = Visibility.Visible;
            VideoErrorVisibility = Visibility.Visible;
            return;
        }

        try
        {
            VideoStatusText = "系统解码器不支持该格式，正在用 ffmpeg 生成预览代理（首次稍慢，之后秒开）…";
            VideoStatusVisibility = Visibility.Visible;
            LoadingVisibility = Visibility.Visible;

            var fallbackLut = ResolveBoundLut(asset);
            var proxyPath = await proxy.EnsureProxyAsync(asset.FullPath, fallbackLut?.FilePath, CancellationToken.None);
            if (string.IsNullOrEmpty(proxyPath))
            {
                VideoStatusText = "生成预览代理失败：可以在「设置 → 工具链」确认 ffmpeg 可用，或用外部播放器打开。";
                VideoErrorVisibility = Visibility.Visible;
                return;
            }

            VideoSource = MediaSource.CreateFromUri(new Uri(proxyPath));
            VideoStatusText = "已用 ffmpeg 预览代理播放（H.264 8-bit · 720p，代理已缓存）";
            VideoErrorVisibility = Visibility.Collapsed;
            LoadingVisibility = Visibility.Collapsed;
            Log.Info($"[preview] 切换预览代理：{asset.FileName} → {proxyPath}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            VideoStatusText = $"生成预览代理失败：{ex.Message}";
            VideoErrorVisibility = Visibility.Visible;
            Log.Warn("切换视频预览代理失败", ex);
        }
    }

    /// <summary>按"设备 ↔ LUT 绑定"解析出该素材该套的 LUT（未绑定 / 非 Log 返回 null）。</summary>
    private static LutEntry? ResolveBoundLut(MediaAsset asset)
    {
        try
        {
            return AppServices.Luts?.ResolveForAsset(asset);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析自动 LUT 失败 {asset.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Log 视频的监视预览：把绑定 LUT 烧进 ffmpeg 代理再播放（代理按 LUT 分缓存，切换素材不重复转码）。
    /// ffmpeg 不可用 / 转码失败时保持原始播放，只在状态条说明。
    /// </summary>
    private async Task ApplyVideoLutProxyAsync(MediaAsset asset, LutEntry lut, CancellationToken ct)
    {
        var proxy = AppServices.VideoProxy;
        if (proxy is null || !proxy.IsAvailable)
        {
            VideoStatusText = $"按设备绑定应套用「{lut.Name}」，但没找到 ffmpeg，无法生成带 LUT 的监视代理（当前为原始画面）。";
            VideoStatusVisibility = Visibility.Visible;
            return;
        }

        var token = Interlocked.Increment(ref _lutProxyVersion);
        try
        {
            VideoStatusText = $"Log 监视：正在套用「{lut.Name}」（设备绑定：{asset.DisplayDevice}），首次生成稍慢…";
            VideoStatusVisibility = Visibility.Visible;
            LoadingVisibility = Visibility.Visible;

            var proxyPath = await proxy.EnsureProxyAsync(asset.FullPath, lut.FilePath, ct);
            if (token != _lutProxyVersion) return;
            if (string.IsNullOrEmpty(proxyPath))
            {
                VideoStatusText = $"「{lut.Name}」监视代理生成失败，当前播放原始画面（可检查 ffmpeg 设置）。";
                VideoErrorVisibility = Visibility.Visible;
                return;
            }

            VideoSource = MediaSource.CreateFromUri(new Uri(proxyPath));
            VideoVisibility = Visibility.Visible;
            VideoStatusText = $"已按设备绑定自动套用监视 LUT「{lut.Name}」（{asset.DisplayDevice} · Log），代理已缓存";
            VideoErrorVisibility = Visibility.Collapsed;
            LoadingVisibility = Visibility.Collapsed;
            Log.Info($"[preview] Log 监视 LUT：「{lut.Name}」→ {asset.FileName}（{asset.DeviceKey}）");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            VideoStatusText = $"套用监视 LUT 失败：{ex.Message}（当前播放原始画面）";
            VideoErrorVisibility = Visibility.Visible;
            Log.Warn("套用视频监视 LUT 失败", ex);
        }
    }

    private int _lutProxyVersion;

    /// <summary>用系统默认播放器打开当前视频。</summary>
    public void OpenInExternalPlayer()
    {
        try
        {
            if (Current is not { } asset || !File.Exists(asset.FullPath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(asset.FullPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("外部播放器打开失败", ex);
        }
    }

    private bool _proxyAttempted;

    [ObservableProperty] private string _videoStatusText = "";
    [ObservableProperty] private Visibility _videoStatusVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _videoErrorVisibility = Visibility.Collapsed;

    private static MediaSource? TryCreateVideoSource(MediaAsset asset)
    {
        try
        {
            if (!File.Exists(asset.FullPath)) return null;
            return MediaSource.CreateFromUri(new Uri(asset.FullPath));
        }
        catch (Exception ex)
        {
            Log.Warn($"创建视频源失败 {asset.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>当前素材被外部修改（评分/色标）后刷新底部条。</summary>
    public void RefreshCurrent()
    {
        var asset = Current;
        if (asset is null) return;
        Rating = Math.Clamp(asset.Rating, 0, 5);
        ColorLabelName = Formatting.ColorLabelName(asset.ColorLabel);
        PickText = Formatting.PickName(asset.IsPick);
    }
}