using System.Reflection;
using SkiaSharp;
using Svg.Skia;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Watermark;

/// <summary>
/// 水印渲染器：把 Watermark Studio 网页的绘制逻辑搬到本地（SkiaSharp）。
///
/// 与网页一致的规则：
/// - 水印宽度 = 画面宽度 × Size%；
/// - X 居中；Y = 画面高度 × Position%，并对上下做 clamp；
/// - 不透明度 = Opacity%；classic 预设带柔和投影；
/// - 画廊预设（白框 / 黑框）先给照片加边框，再把水印画在下方留白里。
///
/// 品牌差异：STJARNA 用亮度提取的白色字标（网页 luminance 模式），HASSELBLAD / SONY 用 alpha 字标，
/// Canon 可切红色原标，Nikon 是黄底黑字方块。
/// </summary>
public static class WatermarkRenderer
{
    private const string ResourceRoot = "StjMediaViewer.Core.Watermark.Assets.";
    private const string GalleryPaper = "#f4f3ef";
    private const string GalleryDark = "#080808";

    private static readonly Dictionary<WatermarkBrand, SKBitmap> Cache = new();
    private static readonly object CacheLock = new();

    // ==================================================================== 品牌字标

    /// <summary>品牌字标的宽度（用于界面缩略图）。</summary>
    public static byte[] GetBrandPreviewPng(WatermarkBrand brand)
    {
        var bitmap = LoadBrand(brand);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static (int Width, int Height) GetBrandPixelSize(WatermarkBrand brand)
    {
        var bitmap = LoadBrand(brand);
        return (bitmap.Width, bitmap.Height);
    }

    private static SKBitmap LoadBrand(WatermarkBrand brand)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(brand, out var cached)) return cached;
            var bitmap = brand switch
            {
                WatermarkBrand.Sony => LoadSvg("sony.svg"),
                WatermarkBrand.Nikon => LoadPng("nikon.png"),
                WatermarkBrand.Canon => LoadPng("canon.png"),
                WatermarkBrand.Hasselblad => LoadPng("hasselblad.png"),
                _ => LoadPng("stjarna.png"),
            };
            Cache[brand] = bitmap;
            return bitmap;
        }
    }

    private static SKBitmap LoadPng(string fileName)
    {
        using var stream = OpenResource(fileName);
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"水印资源无法解码：{fileName}");
    }

    private static SKBitmap LoadSvg(string fileName)
    {
        string svgText;
        using (var stream = OpenResource(fileName))
        using (var reader = new StreamReader(stream))
        {
            svgText = reader.ReadToEnd();
        }

        using var svg = new SKSvg();
        svg.FromSvg(svgText);

        var picture = svg.Picture ?? throw new InvalidOperationException($"水印 SVG 无法解析：{fileName}");
        var bounds = picture.CullRect;
        var width = (int)Math.Ceiling(bounds.Width);
        var height = (int)Math.Ceiling(bounds.Height);
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / bounds.Width, height / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }
        return bitmap;
    }

    private static Stream OpenResource(string fileName)
    {
        var asm = typeof(WatermarkRenderer).Assembly;
        var stream = asm.GetManifestResourceStream(ResourceRoot + fileName);
        if (stream is null)
        {
            var available = string.Join(", ", asm.GetManifestResourceNames().Where(n => n.Contains("Watermark")));
            throw new InvalidOperationException($"缺少水印资源 {fileName}；现有：{available}");
        }
        return stream;
    }

    // ==================================================================== 渲染

    /// <summary>在给定画布上画完整成品（照片 + 可选边框 + 水印）。</summary>
    public static void Draw(SKCanvas canvas, SKImage photo, WatermarkStyle style)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(style);

        var width = canvas.LocalClipBounds.Width;
        var height = canvas.LocalClipBounds.Height;

        var isGallery = style.Preset is WatermarkPreset.Gallery or WatermarkPreset.BlackGallery;
        if (isGallery)
        {
            canvas.Clear(SKColor.Parse(style.Preset == WatermarkPreset.BlackGallery ? GalleryDark : GalleryPaper));
            var marginX = width * 0.035;
            var marginTop = height * 0.035;
            var band = height * 0.13;
            var areaW = width - marginX * 2;
            var areaH = height - marginTop - band;
            var scale = Math.Min(areaW / photo.Width, areaH / photo.Height);
            var drawW = photo.Width * scale;
            var drawH = photo.Height * scale;
            canvas.DrawImage(photo, new SKRect(
                (float)((width - drawW) / 2),
                (float)(marginTop + (areaH - drawH) / 2),
                (float)((width - drawW) / 2 + drawW),
                (float)(marginTop + (areaH - drawH) / 2 + drawH)));
        }
        else
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(photo, new SKRect(0, 0, (float)width, (float)height));
        }

        DrawMark(canvas, style, width, height);
    }

    private static void DrawMark(SKCanvas canvas, WatermarkStyle style, double width, double height)
    {
        var logo = CreateTintedMark(style);
        if (logo is null) return;

        using (logo)
        {
            var markWidth = width * Math.Clamp(style.Size, 2, 80) / 100.0;
            var markHeight = markWidth * logo.Height / logo.Width;
            var x = (width - markWidth) / 2.0;
            var y = height * Math.Clamp(style.Position, 0, 100) / 100.0;
            y = Math.Max(0, Math.Min(height - markHeight, y - markHeight / 2));

            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)Math.Round(Math.Clamp(style.Opacity, 0, 100) / 100.0 * 255)),
                IsAntialias = true,
            };

            // 投影：网页阴影 = rgba(0,0,0,.55) + blur 4‰画宽 + 1‰偏移
            var wantsShadow = style.Shadow && !(style.Brand == WatermarkBrand.Canon && style.Ink == WatermarkInk.Red);
            if (wantsShadow)
            {
                using var shadow = new SKPaint
                {
                    IsAntialias = true,
                    ImageFilter = SKImageFilter.CreateDropShadow(
                        0,
                        (float)(width * 0.001),
                        (float)Math.Max(2, width * 0.004),
                        (float)Math.Max(2, width * 0.004),
                        new SKColor(0, 0, 0, 140)),
                };
                canvas.DrawBitmap(logo, new SKRect((float)x, (float)y, (float)(x + markWidth), (float)(y + markHeight)), shadow);
            }

            canvas.DrawBitmap(logo, new SKRect((float)x, (float)y, (float)(x + markWidth), (float)(y + markHeight)), paint);
        }
    }

    /// <summary>按品牌 / 墨色生成一张要画的字标（Canon 白/黑 = 用 alpha 重新上色，红 = 原标）。</summary>
    private static SKBitmap? CreateTintedMark(WatermarkStyle style)
    {
        var source = LoadBrand(style.Brand);
        if (style.Brand == WatermarkBrand.Nikon) return source.Copy();
        if (style.Brand == WatermarkBrand.Canon && style.Ink == WatermarkInk.Red) return source.Copy();

        var tint = style.Ink == WatermarkInk.Black ? new SKColor(8, 8, 8, 255) : SKColors.White;
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = tint, IsAntialias = true };
            canvas.DrawBitmap(source, 0, 0, paint);
            using var maskPaint = new SKPaint { BlendMode = SKBlendMode.SrcIn, Color = SKColors.White };
            canvas.DrawRect(new SKRect(0, 0, source.Width, source.Height), maskPaint);
        }
        return result;
    }

    // ==================================================================== 导出

    /// <summary>渲染一张图到目标文件；返回写入的字节数。</summary>
    public static long RenderToFile(string sourcePath, string destinationPath, WatermarkStyle style, WatermarkFormat format, int quality = 92)
    {
        using var source = SKBitmap.Decode(sourcePath) ?? throw new InvalidOperationException($"无法读取图片：{sourcePath}");
        var (canvasWidth, canvasHeight) = ComputeCanvasSize(source.Width, source.Height, style);
        using var output = new SKBitmap(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(source))
        {
            Draw(canvas, image, style);
        }

        using var encoded = format switch
        {
            WatermarkFormat.Png => SKImage.FromBitmap(output).Encode(SKEncodedImageFormat.Png, 100),
            WatermarkFormat.Webp => SKImage.FromBitmap(output).Encode(SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100)),
            _ => SKImage.FromBitmap(output).Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100)),
        };
        if (encoded is null) throw new InvalidOperationException("图片编码失败");

        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var file = File.Create(destinationPath);
        encoded.SaveTo(file);
        return file.Length;
    }

    /// <summary>预览用：渲染到内存 PNG（缩放到 maxSize 内）。</summary>
    public static byte[] RenderPreviewPng(string sourcePath, WatermarkStyle style, int maxSize = 1400)
    {
        using var source = SKBitmap.Decode(sourcePath) ?? throw new InvalidOperationException($"无法读取图片：{sourcePath}");
        var (canvasWidth, canvasHeight) = ComputeCanvasSize(source.Width, source.Height, style);

        var scale = Math.Min(1.0, maxSize / (double)Math.Max(canvasWidth, canvasHeight));
        using var output = new SKBitmap(
            Math.Max(1, (int)Math.Round(canvasWidth * scale)),
            Math.Max(1, (int)Math.Round(canvasHeight * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Scale((float)scale);
            Draw(canvas, image, style);
        }

        using var encoded = SKImage.FromBitmap(output).Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>目标画布尺寸：画廊预设会把画面加高（下方留白）。</summary>
    public static (int Width, int Height) ComputeCanvasSize(int photoWidth, int photoHeight, WatermarkStyle style)
    {
        if (style.Preset is WatermarkPreset.Gallery or WatermarkPreset.BlackGallery)
        {
            // 网页画廊：下边留 13% 白边、四周 3.5%（整体高度约 +0.13/0.965）
            var height = (int)Math.Round(photoHeight * 1.155);
            return (photoWidth, Math.Max(photoHeight, height));
        }
        return (photoWidth, photoHeight);
    }

    /// <summary>批量导出到目录：文件名 = 原名 + "_wm" + 扩展名。</summary>
    public static WatermarkExportSummary ExportFolder(
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        WatermarkStyle style,
        WatermarkFormat format,
        int quality = 92,
        IProgress<int>? progress = null)
    {
        var summary = new WatermarkExportSummary();
        var index = 0;
        foreach (var source in sourcePaths)
        {
            index++;
            try
            {
                var name = Path.GetFileNameWithoutExtension(source) + "_wm" + WatermarkPresets.Extension(format);
                var target = Path.Combine(destinationFolder, name);
                var bytes = RenderToFile(source, target, style, format, quality);
                summary.Succeeded++;
                summary.Bytes += bytes;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                summary.Errors.Add($"{Path.GetFileName(source)}：{ex.Message}");
                Log.Warn($"[watermark] 导出失败 {source}: {ex.Message}");
            }
            progress?.Report(index);
        }
        return summary;
    }
}