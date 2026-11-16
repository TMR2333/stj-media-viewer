using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StjMediaViewer.App.Services;
using StjMediaViewer.Core.Models;
using Windows.UI;

namespace StjMediaViewer.App.Controls;

/// <summary>
/// 异步缩略图：从 <see cref="AppServices.Thumbs"/> 取图；取不到时画"程序化渐变占位"。
///
/// 程序化占位 = 用文件路径的稳定 hash 决定色相（同一文件永远同一颜色，不闪），
/// 明度按当前明暗主题取值；RAW / 视频 / 音频 / 未知类型各有自己的字形。
/// 这是整个控件库里唯一"计算颜色"的地方：因为它必须是确定性的、不依赖皮肤的渐变，
/// 若从 token 取色会导致所有占位缩略图同色、网格失去辨识度。
///
/// 用法：
/// <code>
/// &lt;controls:ThumbnailImage Asset="{x:Bind Asset}" DecodeSize="320" /&gt;
/// </code>
/// </summary>
public sealed partial class ThumbnailImage : UserControl
{
    public static readonly DependencyProperty AssetProperty = DependencyProperty.Register(
        nameof(Asset), typeof(MediaAsset), typeof(ThumbnailImage), new PropertyMetadata(null, OnAssetChanged));

    public static readonly DependencyProperty DecodeSizeProperty = DependencyProperty.Register(
        nameof(DecodeSize), typeof(int), typeof(ThumbnailImage), new PropertyMetadata(320, OnDecodeSizeChanged));

    public static readonly DependencyProperty ShowTypeGlyphProperty = DependencyProperty.Register(
        nameof(ShowTypeGlyph), typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(true, OnShowTypeGlyphChanged));

    // WinUI 3 的 DependencyProperty 没有 RegisterReadOnly（无 DependencyPropertyKey），
    // 所以这里用普通 DP + 私有 setter 表达"只读"语义。
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ThumbnailImage), new PropertyMetadata(null));

    private CancellationTokenSource? _loadCts;

    public ThumbnailImage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>要显示缩略图的素材。</summary>
    public MediaAsset? Asset
    {
        get => (MediaAsset?)GetValue(AssetProperty);
        set => SetValue(AssetProperty, value);
    }

    /// <summary>请求的缩略图尺寸（px），默认 320。</summary>
    public int DecodeSize
    {
        get => (int)GetValue(DecodeSizeProperty);
        set => SetValue(DecodeSizeProperty, value);
    }

    /// <summary>无缩略图时是否显示类型字形（默认 true）。</summary>
    public bool ShowTypeGlyph
    {
        get => (bool)GetValue(ShowTypeGlyphProperty);
        set => SetValue(ShowTypeGlyphProperty, value);
    }

    /// <summary>是否正在加载（只读，仅供绑定）。</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        private set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>已加载的缩略图（只读，加载失败保持 null）。</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        private set => SetValue(SourceProperty, value);
    }

    private static void OnAssetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbnailImage)d).Reload();

    private static void OnDecodeSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbnailImage)d).Reload();

    private static void OnShowTypeGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ThumbnailImage)d;
        control.UpdateTypeGlyph(control.Source is null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Reload();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdatePlaceholderColors();

    /// <summary>重新加载：先立刻显示占位（不闪白），再异步换图。失败会退避重试（列表虚拟化时前几次可能被取消）。</summary>
    private async void Reload()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        var asset = Asset;
        ShowPlaceholder(asset);

        if (asset is null || string.IsNullOrEmpty(asset.Id))
        {
            Source = null;
            return;
        }

        if (!AppServices.IsReady || asset.IsAudio)
        {
            // 音频没有可用缩略图：直接保留程序化占位 + 音符字形。
            Source = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;

        try
        {
            // 缩略图生成本身不受 UI 取消影响（见 ThumbnailLoader），所以重试几乎必定命中缓存。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;

                var source = await AppServices.Thumbs.LoadAsync(asset, Math.Max(64, DecodeSize), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;

                if (source is not null)
                {
                    Source = source;
                    Thumb.Source = source;
                    Thumb.Visibility = Visibility.Visible;
                    PlaceholderHost.Visibility = Visibility.Collapsed;
                    TypeGlyph.Visibility = Visibility.Collapsed;
                    FadeInThumb();
                    return;
                }

                if (attempt < 2) await Task.Delay(350 * (attempt + 1), cts.Token);
            }

            ShowPlaceholder(asset);
        }
        catch (OperationCanceledException)
        {
            // 正常取消（控件被回收 / 换素材），不处理。
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn($"缩略图加载失败：{asset.FileName}（{ex.Message}）");
            if (!cts.IsCancellationRequested) ShowPlaceholder(asset);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>缩略图淡入：避免图片"啪"地出现（列表虚拟化反复重建时观感差别很大）。</summary>
    private void FadeInThumb()
    {
        try
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(190)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, Thumb);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(
                animation, "Opacity");
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            Thumb.Opacity = 1;   // 动画失败就直出，不影响可用性
        }
    }

    private void ShowPlaceholder(MediaAsset? asset)
    {
        Thumb.Source = null;
        Thumb.Visibility = Visibility.Collapsed;
        PlaceholderHost.Visibility = Visibility.Visible;
        UpdatePlaceholderColors(asset);
        UpdateTypeGlyph(true, asset);
    }

    private void UpdatePlaceholderColors(MediaAsset? asset = null)
    {
        asset ??= Asset;

        var dark = ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
        };

        var (colorA, colorB) = asset is null
            ? NeutralPlaceholder(dark)
            : PlaceholderColors(StableKey(asset), dark);

        StopA.Color = colorA;
        StopB.Color = colorB;
    }

    private void UpdateTypeGlyph(bool visible, MediaAsset? asset = null)
    {
        asset ??= Asset;
        if (!ShowTypeGlyph || !visible || asset is null)
        {
            TypeGlyph.Visibility = Visibility.Collapsed;
            return;
        }

        TypeGlyph.Glyph = GlyphFor(asset);
        TypeGlyph.Visibility = Visibility.Visible;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Grid 的 CornerRadius 只影响自身背景；图片需要方形裁剪避免溢出格子。
        try
        {
            Root.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, Root.ActualWidth), Math.Max(0, Root.ActualHeight)),
            };
        }
        catch
        {
            // 裁剪失败不影响可用性
        }
    }

    // =====================================================================================
    //  程序化占位
    // =====================================================================================

    private static string StableKey(MediaAsset asset)
        => !string.IsNullOrEmpty(asset.FullPath) ? asset.FullPath
            : !string.IsNullOrEmpty(asset.Id) ? asset.Id
            : asset.FileName ?? string.Empty;

    /// <summary>FNV-1a：稳定、跨进程一致（string.GetHashCode 每次启动会变，不能用）。</summary>
    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    private static (Color A, Color B) PlaceholderColors(string key, bool dark)
    {
        var hash = Fnv1a(key);
        var hue = (float)(hash % 360);
        var hue2 = (hue + 36f) % 360f;
        var saturation = 0.30f + (hash >> 8) % 20 / 100f;   // 0.30 – 0.49：专业素材不要花
        var lightA = dark ? 0.16f : 0.84f;
        var lightB = dark ? 0.26f : 0.90f;

        return (FromHsl(hue, saturation, lightA), FromHsl(hue2, saturation, lightB));
    }

    private static (Color A, Color B) NeutralPlaceholder(bool dark)
        => dark
            ? (Color.FromArgb(255, 0x26, 0x26, 0x2C), Color.FromArgb(255, 0x35, 0x35, 0x3D))
            : (Color.FromArgb(255, 0xE4, 0xE4, 0xEA), Color.FromArgb(255, 0xD2, 0xD2, 0xDA));

    private static Color FromHsl(float hue, float saturation, float lightness)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        saturation = Math.Clamp(saturation, 0f, 1f);
        lightness = Math.Clamp(lightness, 0f, 1f);

        var c = (1f - Math.Abs(2f * lightness - 1f)) * saturation;
        var x = c * (1f - Math.Abs(hue / 60f % 2f - 1f));
        var m = lightness - c / 2f;

        var (r, g, b) = hue switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };

        return Color.FromArgb(
            255,
            (byte)Math.Round((r + m) * 255f),
            (byte)Math.Round((g + m) * 255f),
            (byte)Math.Round((b + m) * 255f));
    }

    private static string GlyphFor(MediaAsset asset) => asset switch
    {
        { IsRaw: true } => "\uE722",       // Camera
        { IsVideo: true } => "\uE714",     // Video
        { IsAudio: true } => "\uE8D6",     // MusicInfo
        { IsImage: true } => "\uE8B9",     // Pictures
        _ => "\uE7C5",                     // Page
    };
}