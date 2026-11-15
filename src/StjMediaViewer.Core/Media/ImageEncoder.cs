using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// JPEG 编码（WinRT BitmapEncoder = WIC）。缩略图落盘与 AI 上传图都走这里。
/// 缩放在托管侧完成（面积平均），编码器只做纯编码，行为更可预测。
/// </summary>
public sealed class ImageEncoder : IImageEncoder
{
    private const int MinQuality = 30;

    public async Task<byte[]?> EncodeJpegAsync(DecodedImage image, int quality, CancellationToken ct = default)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        try
        {
            var tight = ToTightBgra(image);
            if (tight is null) return null;
            return await EncodeTightAsync(tight, image.Width, image.Height, quality, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] jpeg encode failed {image.Width}x{image.Height}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> ResizeToJpegAsync(DecodedImage image, int maxDimension, int quality, CancellationToken ct = default)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        try
        {
            var resized = maxDimension > 0 ? ImageOps.Fit(image, maxDimension) : image;
            return await EncodeJpegAsync(resized, quality, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[media] resize+jpeg failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> EncodeTightAsync(byte[] bgra, int width, int height, int quality, CancellationToken ct)
    {
        int q = Math.Clamp(quality <= 0 ? 82 : quality, MinQuality, 100);
        ct.ThrowIfCancellationRequested();

        return await Task.Run(async () =>
        {
            using var ras = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras).AsTask(ct).ConfigureAwait(false);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width, (uint)height,
                96.0, 96.0,
                bgra);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            long size = (long)ras.Size;
            if (size <= 0) return null;
            ras.Seek(0);
            var reader = new DataReader(ras.GetInputStreamAt(0));
            try
            {
                await reader.LoadAsync((uint)size).AsTask(ct).ConfigureAwait(false);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                return (byte[]?)bytes;
            }
            finally { reader.Dispose(); }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>把可能带 padding 的 BGRA 缓冲整理成 width*height*4 紧密排列。</summary>
    private static byte[]? ToTightBgra(DecodedImage image)
    {
        int needed = checked(image.Width * image.Height * 4);
        if (image.Bgra is null || image.Bgra.Length < needed) return null;
        if (image.Stride == image.Width * 4 && image.Bgra.Length == needed) return image.Bgra;

        var dst = new byte[needed];
        int rowBytes = image.Width * 4;
        for (int y = 0; y < image.Height; y++)
        {
            int src = y * image.Stride;
            if (src + rowBytes > image.Bgra.Length) break;
            System.Buffer.BlockCopy(image.Bgra, src, dst, y * rowBytes, rowBytes);
        }
        return dst;
    }
}