using StjMediaViewer.Core.Models;

namespace StjMediaViewer.SelfCheck;

/// <summary>
/// 自检的"图像解码完整性"部分：
/// 1) 解码结果必须是紧密 BGRA（Bgra.Length == W×H×4，Stride == W×4），否则重编码会出现整行错位（斜向重影）；
/// 2) EXIF 方向必须真正生效：4 个 orientation 样本解码后都应是 240×160 正立图，且四个角颜色正确。
/// </summary>
public static partial class Program
{
    private static async Task CheckImageDecode(Services s)
    {
        var fixtures = Path.Combine(FindRepoRoot(), "tools", "SampleLibrary", "fixtures", "orientation");
        if (!Directory.Exists(fixtures))
        {
            Check("找到 EXIF 方向样本", false, fixtures);
            return;
        }
        Check("找到 EXIF 方向样本", true, "4 个 orientation");

        // 正立后四个角的期望颜色（生成脚本约定）
        var expected = new (string Name, int Orientation)[]
        {
            ("orientation-1.jpg", 1),
            ("orientation-3.jpg", 3),
            ("orientation-6.jpg", 6),
            ("orientation-8.jpg", 8),
        };

        foreach (var (name, orientation) in expected)
        {
            var path = Path.Combine(fixtures, name);
            var image = await s.Decoder.DecodeAsync(path, 512);
            if (image is null)
            {
                Check($"解码 {name}", false, "返回 null");
                continue;
            }

            // 1) 紧密缓冲不变量：stride 必须等于 W×4，长度必须等于 W×H×4
            var tight = image.Bgra.Length == image.Width * image.Height * 4 && image.Stride == image.Width * 4;
            Check($"{name} 解码为紧密 BGRA（无 stride 错位）", tight,
                  $"{image.Width}×{image.Height} stride={image.Stride} bytes={image.Bgra.Length} 期望={image.Width * image.Height * 4}");

            // 2) EXIF 方向生效：正立后应是横图 240×160
            Check($"{name} 方向已摆正为 240×160", image.Width == 240 && image.Height == 160,
                  $"{image.Width}×{image.Height}");

            // 2b) 元数据层：宽高必须是正立后的尺寸（存储 160×240 + Orientation=6 → 正立 240×160）
            var meta = s.Metadata.Read(path);
            if (orientation is 6 or 8)
            {
                Check($"{name} 元数据宽高已按方向交换",
                      meta.Width == 240 && meta.Height == 160,
                      $"{meta.Width}×{meta.Height}");
            }

            // 3) 四角颜色（宽容差：JPEG 压缩 + 缩放）
            if (image.Width > 0 && image.Height > 0)
            {
                var tl = Pixel(image, 4, 4);
                var tr = Pixel(image, image.Width - 5, 4);
                var bl = Pixel(image, 4, image.Height - 5);
                var br = Pixel(image, image.Width - 5, image.Height - 5);

                Check($"{name} 左上角=红", IsRed(tl), Format(tl));
                Check($"{name} 右上角=绿", IsGreen(tr), Format(tr));
                Check($"{name} 左下角=蓝", IsBlue(bl), Format(bl));
                Check($"{name} 右下角=黄", IsYellow(br), Format(br));
            }
        }

        // 4) 抽一张样本库真实大图，确认缩放解码后仍是紧密缓冲
        var bigImage = Directory.EnumerateFiles(
                Path.Combine(FindRepoRoot(), "tools", "SampleLibrary", "out", "library"),
                "*.jpg", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(FindRepoRoot(), "tools", "SampleLibrary", "out", "library"),
                "*.jpeg", SearchOption.AllDirectories))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        if (bigImage is not null)
        {
            var decoded = await s.Decoder.DecodeAsync(bigImage, 1024);
            if (decoded is not null)
            {
                Check("真实素材解码后是紧密 BGRA",
                      decoded.Bgra.Length == decoded.Width * decoded.Height * 4 && decoded.Stride == decoded.Width * 4,
                      $"{decoded.Width}×{decoded.Height} stride={decoded.Stride} bytes={decoded.Bgra.Length}");
                Check("真实素材缩放解码尺寸受限",
                      Math.Max(decoded.Width, decoded.Height) <= 1024,
                      $"{decoded.Width}×{decoded.Height}");
            }
            else
            {
                Check("真实素材可解码", false, Path.GetFileName(bigImage));
            }
        }
    }

    private static (int R, int G, int B) Pixel(DecodedImage image, int x, int y)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        int offset = y * image.Stride + x * 4;
        if (offset + 3 >= image.Bgra.Length) return (-1, -1, -1);
        return (image.Bgra[offset + 2], image.Bgra[offset + 1], image.Bgra[offset]);   // BGRA
    }

    private static bool IsRed((int R, int G, int B) c) => c.R > 150 && c.G < 100 && c.B < 100;
    private static bool IsGreen((int R, int G, int B) c) => c.G > 150 && c.R < 110 && c.B < 110;
    private static bool IsBlue((int R, int G, int B) c) => c.B > 150 && c.R < 100 && c.G < 100;
    private static bool IsYellow((int R, int G, int B) c) => c.R > 150 && c.G > 150 && c.B < 110;
    private static string Format((int R, int G, int B) c) => $"rgb({c.R},{c.G},{c.B})";
}