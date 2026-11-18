using SkiaSharp;
using StjMediaViewer.Core.Watermark;

namespace StjMediaViewer.SelfCheck;

public static partial class Program
{
    /// <summary>
    /// 水印工作台：五个品牌字标资源、六种预设参数、三种导出格式、画廊留白、水印确实画上去了。
    /// </summary>
    private static async Task CheckWatermark(Services s, string tempData)
    {
        var source = Path.Combine(tempData, "watermark-source.jpg");
        Directory.CreateDirectory(tempData);
        await File.WriteAllBytesAsync(source, MakeSampleJpeg(1600, 1067));

        // ---------- 品牌字标资源 ----------
        foreach (var brand in Enum.GetValues<WatermarkBrand>())
        {
            var png = WatermarkRenderer.GetBrandPreviewPng(brand);
            var (w, h) = WatermarkRenderer.GetBrandPixelSize(brand);
            Check($"水印：{WatermarkPresets.Display(brand)} 字标可渲染",
                  png.Length > 512 && w > 10 && h > 10,
                  $"{w}x{h} / {png.Length} B");
        }

        // ---------- 预设参数与网页一致 ----------
        var classic = WatermarkPresets.Get(WatermarkPreset.Classic);
        Check("水印：经典预设参数", classic is { Size: 20, Opacity: 95, Position: 11 } && classic.Shadow);
        var dark = WatermarkPresets.Get(WatermarkPreset.Dark);
        Check("水印：暗调预设用黑墨", dark.Ink == WatermarkInk.Black && !dark.Shadow);
        Check("水印：Canon 品牌默认值覆盖",
              WatermarkPresets.BrandDefaults(WatermarkBrand.Canon) is { Size: 12, Opacity: 83, Position: 8 });

        // ---------- 水印确实画上去了（对比 opacity=0 的同一张图） ----------
        var style = new WatermarkStyle { Brand = WatermarkBrand.Stjarna, Size = 40, Opacity = 100, Position = 20 };
        var none = new WatermarkStyle { Brand = WatermarkBrand.Stjarna, Size = 40, Opacity = 0, Position = 20 };

        var outputDir = Path.Combine(tempData, "watermark-out");
        Directory.CreateDirectory(outputDir);
        var markedPath = Path.Combine(outputDir, "marked.png");
        var plainPath = Path.Combine(outputDir, "plain.png");
        WatermarkRenderer.RenderToFile(source, markedPath, style, WatermarkFormat.Png);
        WatermarkRenderer.RenderToFile(source, plainPath, none, WatermarkFormat.Png);

        static byte[] Decode(string path)
        {
            using var bitmap = SKBitmap.Decode(path);
            return bitmap.GetPixelSpan().ToArray();
        }

        var marked = Decode(markedPath);
        var plain = Decode(plainPath);
        var diff = 0;
        for (int i = 0; i < marked.Length && i < plain.Length; i += 4)
            if (marked[i] != plain[i] || marked[i + 1] != plain[i + 1] || marked[i + 2] != plain[i + 2]) diff++;
        Check("水印：像素确实被绘制", diff > 500, $"{diff} 个像素不同");

        // ---------- 三种导出格式 ----------
        var jpg = Path.Combine(outputDir, "out.jpg");
        var webp = Path.Combine(outputDir, "out.webp");
        WatermarkRenderer.RenderToFile(source, jpg, style, WatermarkFormat.Jpeg, 92);
        WatermarkRenderer.RenderToFile(source, webp, style, WatermarkFormat.Webp, 82);
        var jpgHead = await File.ReadAllBytesAsync(jpg);
        var webpHead = await File.ReadAllBytesAsync(webp);
        Check("水印：JPG 导出", jpgHead.Length > 1024 && jpgHead[0] == 0xFF && jpgHead[1] == 0xD8,
              $"{jpgHead.Length} B");
        Check("水印：WEBP 导出",
              webpHead.Length > 1024 && webpHead[0] == (byte)'R' && webpHead[1] == (byte)'I' &&
              webpHead[2] == (byte)'F' && webpHead[3] == (byte)'F',
              $"{webpHead.Length} B");

        // ---------- 画廊预设加留白 ----------
        using (var bitmap = SKBitmap.Decode(source))
        {
            var (gw, gh) = WatermarkRenderer.ComputeCanvasSize(bitmap.Width, bitmap.Height,
                new WatermarkStyle { Preset = WatermarkPreset.Gallery });
            Check("水印：画廊预设加高留白", gw == bitmap.Width && gh > bitmap.Height, $"{gw}x{gh}");
        }

        // ---------- 批量导出 ----------
        var second = Path.Combine(tempData, "watermark-source-2.jpg");
        File.Copy(source, second, overwrite: true);
        var batch = WatermarkRenderer.ExportFolder(new[] { source, second }, outputDir, style, WatermarkFormat.Png);
        Check("水印：批量导出", batch.Succeeded == 2 && batch.Failed == 0 &&
              File.Exists(Path.Combine(outputDir, "watermark-source_wm.png")) &&
              File.Exists(Path.Combine(outputDir, "watermark-source-2_wm.png")),
              $"{batch.Describe()}（{batch.Bytes} B）");
    }

    private static byte[] MakeSampleJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(width, height),
                new[] { new SKColor(40, 60, 90), new SKColor(180, 140, 90) },
                null, SKShaderTileMode.Clamp) };
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
            using var blob = new SKPaint { Color = new SKColor(220, 220, 220), IsAntialias = true };
            canvas.DrawCircle(width * 0.5f, height * 0.5f, Math.Min(width, height) * 0.25f, blob);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}