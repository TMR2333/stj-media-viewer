using System.Runtime.InteropServices.WindowsRuntime;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Metadata;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 媒体解码：图片走 WinRT（Windows.Graphics.Imaging = WIC），RAW 用内嵌 JPEG 预览，
/// 视频首选 ffmpeg 抽帧、否则回落到系统缩略图，音频取内嵌封面。
/// 所有 WinRT 调用都跑在线程池线程（MTA），避免 UI 线程 COM 套间问题。
/// 任何失败都返回 null 并记日志，绝不向外抛异常（取消除外）。
/// </summary>
public sealed class MediaDecoder : IMediaDecoder
{
    /// <summary>单帧解码像素上限，防止 1 亿像素图把内存打爆。</summary>
    private const long MaxDecodePixels = 96_000_000;

    private readonly IMetadataService _metadata;
    private readonly IFfmpegService _ffmpeg;

    /// <summary>Shell 缩略图处理器是进程内第三方 DLL，并发进入是已知崩溃源，这里串行化。</summary>
    private static readonly SemaphoreSlim _systemThumbGate = new(1, 1);

    public MediaDecoder(IMetadataService metadata, IFfmpegService ffmpeg)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
    }

    // ==================================================================== 入口

    public bool CanHandle(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        if (MediaTypeCatalog.IsSupportedMedia(fullPath)) return true;
        return SniffKind(fullPath) != MediaKind.Unknown;
    }

    public async Task<DecodedImage?> DecodeAsync(string fullPath, int maxDimension, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        try
        {
            if (!File.Exists(fullPath)) return null;
            var kind = KindOf(fullPath);
            return kind switch
            {
                MediaKind.Image => await DecodeImageAsync(fullPath, maxDimension, ct).ConfigureAwait(false),
                MediaKind.Video => await DecodeVideoAsync(fullPath, maxDimension, ct).ConfigureAwait(false),
                MediaKind.Audio => await DecodeAudioAsync(fullPath, maxDimension, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] decode failed {fullPath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Task<byte[]?> ExtractEmbeddedPreviewAsync(string fullPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return Task.FromResult<byte[]?>(null);
        return Task.Run<byte[]?>(() =>
        {
            try
            {
                if (!File.Exists(fullPath)) return null;
                var kind = KindOf(fullPath);
                MediaTypeCatalog.TryGet(fullPath, out _, out _, out var flavor, out var raw, out _);
                bool isRaw = kind == MediaKind.Image && raw != RawFormat.None;
                bool bigTiff = kind == MediaKind.Image && flavor == ImageFlavor.Tiff && SafeLength(fullPath) > 64L * 1024 * 1024;

                if (isRaw || bigTiff)
                {
                    // 元数据引擎的预览提取器优先（TIFF IFD 513/514 快路径 ~0.2ms、分块读取不整读文件；
                    // 普通 TIFF 也能找到 IFD 缩略图）；它没找到/失败时 RAW 退回本机扫描实现。
                    var fromMetadata = TryMetadataPreview(fullPath, isRaw ? raw : RawFormat.None, ct);
                    if (fromMetadata is { Length: > 0 }) return fromMetadata;
                    if (isRaw) return EmbeddedArtwork.FindLargestJpeg(fullPath, ct);
                    return null;
                }

                if (kind == MediaKind.Audio)
                    return EmbeddedArtwork.TryGetAudioCover(fullPath, ct);

                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[media] embedded preview failed {fullPath}: {ex.Message}");
                return null;
            }
        }, ct);
    }

    /// <summary>调用元数据引擎的 RAW 预览提取器（跨模块边界，失败一律降级到本机实现）。</summary>
    private static byte[]? TryMetadataPreview(string fullPath, RawFormat raw, CancellationToken ct)
    {
        try
        {
            var info = RawPreviewExtractor.Find(fullPath, raw, ct);
            if (!info.Found) return null;
            var bytes = RawPreviewExtractor.Extract(fullPath, info, ct);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] metadata RAW preview failed {fullPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> ExtractVideoFrameAsync(string fullPath, double positionSeconds, int maxSize, CancellationToken ct = default)
    {
        try
        {
            var frame = await _ffmpeg.ExtractFrameAsync(fullPath, positionSeconds, maxSize, ct).ConfigureAwait(false);
            if (frame is { Length: > 0 }) return frame;
            return await SystemThumbnailBytesAsync(fullPath, maxSize, ThumbnailMode.VideosView, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] video frame failed {fullPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> ExtractAudioCoverAsync(string fullPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        try
        {
            if (!File.Exists(fullPath)) return null;
            // 只取内嵌封面（ID3 APIC / FLAC PICTURE / MP4 covr / Ogg base64）；
            // 不用系统缩略图：无封面时 Windows 会返回通用音符图标，网格里比占位图更丑。
            return EmbeddedArtwork.TryGetAudioCover(fullPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] audio cover failed {fullPath}: {ex.Message}");
            return null;
        }
    }

    // ==================================================================== 分类型解码

    private async Task<DecodedImage?> DecodeImageAsync(string fullPath, int maxDimension, CancellationToken ct)
    {
        bool isRaw = MediaTypeCatalog.TryGet(fullPath, out _, out _, out _, out var raw, out _) && raw != RawFormat.None;
        var flavor = MediaTypeCatalog.TryGet(fullPath, out _, out _, out var f, out _, out _) ? f : ImageFlavor.None;

        // 大 TIFF（>64MB）和 RAW：先找内嵌预览/降采样图，命中就只解码预览字节，
        // 避免把上亿像素整图塞进 WIC（既慢又吃内存）。
        bool bigTiff = !isRaw && flavor == ImageFlavor.Tiff && SafeLength(fullPath) > 64L * 1024 * 1024;
        if (isRaw || bigTiff)
        {
            var preview = await ExtractEmbeddedPreviewAsync(fullPath, ct).ConfigureAwait(false);
            if (preview is { Length: > 0 })
            {
                var img = await DecodeBytesAsync(preview, maxDimension, ct).ConfigureAwait(false);
                if (img is not null) return img;
            }
            // 装了 RAW 编解码器 / TIFF 自带降采样 IFD 时 WIC 也可能直接解；
            // 实在没有预览才回落到整图解码。
        }
        return await DecodeFileAsync(fullPath, maxDimension, ct).ConfigureAwait(false);
    }

    private static long SafeLength(string fullPath)
    {
        try { return new FileInfo(fullPath).Length; } catch { return 0; }
    }

    private async Task<DecodedImage?> DecodeVideoAsync(string fullPath, int maxDimension, CancellationToken ct)
    {
        // 诊断开关：STJ_DISABLE_VIDEO_THUMBS=1 时视频缩略图直接走占位（定位视频网格崩溃用）
        if (Environment.GetEnvironmentVariable("STJ_DISABLE_VIDEO_THUMBS") == "1") return null;

        int size = maxDimension > 0 ? maxDimension : 640;

        // 1) ffmpeg：优先 1s（避开常见黑场），再退回首帧
        if (_ffmpeg.IsAvailable)
        {
            foreach (var pos in new[] { 1.0, 0.0 })
            {
                var bytes = await _ffmpeg.ExtractFrameAsync(fullPath, pos, size, ct).ConfigureAwait(false);
                if (bytes is not { Length: > 0 }) continue;
                var img = await DecodeBytesAsync(bytes, maxDimension, ct).ConfigureAwait(false);
                if (img is not null) return img;
            }
        }

        // 2) 系统缩略图（Windows 自带解码器，不需要 ffmpeg）
        if (Environment.GetEnvironmentVariable("STJ_NO_SYSTEM_THUMBS") == "1") return null;
        return await SystemThumbnailAsync(fullPath, size, ThumbnailMode.VideosView, maxDimension, ct).ConfigureAwait(false);
    }

    private async Task<DecodedImage?> DecodeAudioAsync(string fullPath, int maxDimension, CancellationToken ct)
    {
        var cover = await ExtractAudioCoverAsync(fullPath, ct).ConfigureAwait(false);
        if (cover is not { Length: > 0 }) return null;
        return await DecodeBytesAsync(cover, maxDimension, ct).ConfigureAwait(false);
    }

    // ==================================================================== WinRT 解码核心

    private static async Task<DecodedImage?> DecodeFileAsync(string fullPath, int maxDimension, CancellationToken ct)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var abs = Path.GetFullPath(fullPath);
                var file = await StorageFile.GetFileFromPathAsync(abs).AsTask(ct).ConfigureAwait(false);
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
                return await DecodeStreamAsync(stream, maxDimension, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] WIC decode failed {fullPath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<DecodedImage?> DecodeBytesAsync(byte[] bytes, int maxDimension, CancellationToken ct)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            return await Task.Run(async () =>
            {
                using var ras = new InMemoryRandomAccessStream();
                var writer = new DataWriter(ras);
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
                writer.DetachStream();
                ras.Seek(0);
                return await DecodeStreamAsync(ras, maxDimension, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] decode bytes failed ({bytes.Length}B): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<DecodedImage?> DecodeStreamAsync(IRandomAccessStream stream, int maxDimension, CancellationToken ct)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);

        // ── 关键：不要在 WIC 里做 EXIF 方向 ────────────────────────────────────────────
        // 用 RespectExifOrientation 时，ScaledWidth/Height 是"旋转前"的坐标系，90°/270° 旋转的图
        // 返回缓冲的行宽和「推断出的宽高」会对不上 → 重编码后整行错位（斜向重影）。
        // 所以这里：按存储方向解码（尺寸确定、行宽严格 = 宽×4），方向在托管侧自己转。
        uint storedW = decoder.PixelWidth;
        uint storedH = decoder.PixelHeight;
        if (storedW == 0 || storedH == 0) return null;

        var orientation = await TryReadOrientationAsync(decoder).ConfigureAwait(false);
        bool swapDims = orientation is 5 or 6 or 7 or 8;

        // 正立后尺寸（用于按 maxDimension 等比缩放）
        uint uprightW = swapDims ? storedH : storedW;
        uint uprightH = swapDims ? storedW : storedH;

        double scale = 1.0;
        if (maxDimension > 0)
            scale = Math.Min(1.0, Math.Min((double)maxDimension / uprightW, (double)maxDimension / uprightH));

        double pixels = (double)uprightW * uprightH * scale * scale;
        if (pixels > MaxDecodePixels)
            scale *= Math.Sqrt(MaxDecodePixels / pixels);

        // 缩放要作用在「存储方向」的尺寸上
        uint scaledStoredW = (uint)Math.Max(1, Math.Round(storedW * scale));
        uint scaledStoredH = (uint)Math.Max(1, Math.Round(storedH * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledStoredW,
            ScaledHeight = scaledStoredH,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

        var bgra = provider.DetachPixelData();
        int expected = checked((int)scaledStoredW * (int)scaledStoredH * 4);
        if (bgra.Length < expected)
        {
            Log.Warn($"[media] 解码缓冲偏小：{bgra.Length} < {expected}（{decoder.DecoderInformation?.CodecId}）");
            return null;
        }

        var src = new DecodedImage(bgra, (int)scaledStoredW, (int)scaledStoredH, (int)scaledStoredW * 4);
        if (orientation is 1 or 0) return src;

        return ApplyOrientation(src, (int)orientation);
    }

    /// <summary>读 EXIF Orientation（1–8）。读不到就当 1（不旋转）。</summary>
    private static async Task<ushort> TryReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
            if (props.TryGetValue("System.Photo.Orientation", out var value) &&
                value.Value is ushort orientation && orientation is >= 1 and <= 8)
                return orientation;
        }
        catch (Exception ex)
        {
            Log.Info($"[media] 读取 EXIF 方向失败（按 1 处理）：{ex.Message}");
        }
        return 1;
    }

    /// <summary>把"存储方向"的 BGRA 转成"正立"方向（EXIF 1–8 全支持）。</summary>
    private static DecodedImage ApplyOrientation(DecodedImage src, int orientation)
    {
        int sw = src.Width, sh = src.Height;
        int dw = orientation is 5 or 6 or 7 or 8 ? sh : sw;
        int dh = orientation is 5 or 6 or 7 or 8 ? sw : sh;

        var dst = new byte[dw * dh * 4];
        int srcStride = src.Stride;
        int dstStride = dw * 4;

        for (int dy = 0; dy < dh; dy++)
        {
            int dstRow = dy * dstStride;
            for (int dx = 0; dx < dw; dx++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = sw - 1 - dx; sy = dy; break;                   // 水平镜像
                    case 3: sx = sw - 1 - dx; sy = sh - 1 - dy; break;          // 旋转 180
                    case 4: sx = dx; sy = sh - 1 - dy; break;                   // 垂直镜像
                    case 5: sx = dy; sy = dx; break;                            // 转置
                    case 6: sx = dy; sy = sh - 1 - dx; break;                   // 顺时针 90
                    case 7: sx = sw - 1 - dy; sy = sh - 1 - dx; break;          // 反转置
                    case 8: sx = sw - 1 - dy; sy = dx; break;                   // 逆时针 90
                    default: sx = dx; sy = dy; break;
                }

                int srcOffset = sy * srcStride + sx * 4;
                int dstOffset = dstRow + dx * 4;
                dst[dstOffset] = src.Bgra[srcOffset];
                dst[dstOffset + 1] = src.Bgra[srcOffset + 1];
                dst[dstOffset + 2] = src.Bgra[srcOffset + 2];
                dst[dstOffset + 3] = src.Bgra[srcOffset + 3];
            }
        }

        return new DecodedImage(dst, dw, dh, dstStride);
    }

    // ==================================================================== 系统缩略图回落

    private static async Task<DecodedImage?> SystemThumbnailAsync(
        string fullPath, int size, ThumbnailMode mode, int maxDimension, CancellationToken ct)
    {
        var bytes = await SystemThumbnailBytesAsync(fullPath, size, mode, ct).ConfigureAwait(false);
        if (bytes is not { Length: > 0 }) return null;
        return await DecodeBytesAsync(bytes, maxDimension, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> SystemThumbnailBytesAsync(
        string fullPath, int size, ThumbnailMode mode, CancellationToken ct)
    {
        var trace = Environment.GetEnvironmentVariable("STJ_TRACE_DECODE") == "1";
        await _systemThumbGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var abs = Path.GetFullPath(fullPath);
                if (trace) Log.Info($"[decode] system-thumb begin {abs} mode={mode} size={size}");
                var file = await StorageFile.GetFileFromPathAsync(abs).AsTask(ct).ConfigureAwait(false);
                uint request = (uint)Math.Clamp(size <= 0 ? 320 : size, 32, 2048);
                using StorageItemThumbnail thumb = await file
                    .GetThumbnailAsync(mode, request, ThumbnailOptions.UseCurrentScale)
                    .AsTask(ct).ConfigureAwait(false);
                if (trace) Log.Info($"[decode] system-thumb got {fullPath} bytes={thumb?.Size}");
                if (thumb is null || thumb.Size == 0) return null;

                var reader = new DataReader(thumb);
                try
                {
                    await reader.LoadAsync((uint)thumb.Size).AsTask(ct).ConfigureAwait(false);
                    var bytes = new byte[thumb.Size];
                    reader.ReadBytes(bytes);
                    return bytes;
                }
                finally { reader.Dispose(); }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] system thumbnail failed {fullPath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally { _systemThumbGate.Release(); }
    }

    // ==================================================================== 类型判定

    private MediaKind KindOf(string fullPath)
    {
        if (MediaTypeCatalog.TryGet(fullPath, out var kind, out _, out _, out _, out _) && kind != MediaKind.Unknown)
            return kind;
        return SniffKind(fullPath);
    }

    private MediaKind SniffKind(string fullPath)
    {
        try
        {
            var kind = _metadata.Identify(fullPath);
            if (kind != MediaKind.Unknown) return kind;
        }
        catch { /* Identify 失败不影响后续嗅探 */ }

        try
        {
            Span<byte> head = stackalloc byte[32];
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int n = fs.Read(head);
            return MediaTypeCatalog.SniffKind(head[..n]);
        }
        catch { return MediaKind.Unknown; }
    }
}