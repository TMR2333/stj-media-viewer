using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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