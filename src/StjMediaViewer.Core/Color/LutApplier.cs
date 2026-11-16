using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// 把 LUT 应用到解码后的 BGRA 帧上（三线性插值 + 8bit 量化）。
/// 纯 CPU 实现：一帧 1600×900 大约几十毫秒，够预览与缩略图用，且能按帧缓存。
/// </summary>
public static class LutApplier
{
    public static DecodedImage Apply(DecodedImage source, CubeLut lut)
    {
        if (lut.IsIdentity) return source;

        var src = source.Bgra;
        var dst = new byte[src.Length];
        int width = source.Width, height = source.Height, stride = source.Stride;

        // 先建 256 级查找表：绝大多数 LUT 对同一输入值输出相同，逐像素算三线性太浪费。
        // 我们的像素是 8bit，先量化到 8bit 表能省 10 倍以上时间；对 33³ LUT 误差 < 1 级。
        var tableR = new byte[256];
        var tableG = new byte[256];
        var tableB = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            lut.Sample(v, v, v, out var r, out var g, out var b);
            tableR[i] = ToByte(r);
            tableG[i] = ToByte(g);
            tableB[i] = ToByte(b);
        }

        // 对角线表只对"通道独立"的 1D LUT 精确；3D LUT 需要逐像素三线性。
        if (lut.Is1D)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 4;
                    byte bb = src[i], gg = src[i + 1], rr = src[i + 2], aa = src[i + 3];
                    dst[i] = tableB[bb];
                    dst[i + 1] = tableG[gg];
                    dst[i + 2] = tableR[rr];
                    dst[i + 3] = aa;
                }
            }
            return new DecodedImage(dst, width, height, stride);
        }

        var inv = 1f / 255f;
        Parallel.For(0, height, y =>
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                int bb = src[i], gg = src[i + 1], rr = src[i + 2];
                lut.Sample(rr * inv, gg * inv, bb * inv, out var outR, out var outG, out var outB);
                dst[i] = ToByte(outB);
                dst[i + 1] = ToByte(outG);
                dst[i + 2] = ToByte(outR);
                dst[i + 3] = src[i + 3];
            }
        });

        return new DecodedImage(dst, width, height, stride);
    }

    /// <summary>快速路径：身份 LUT 直接返回原数组引用（调用方别改）。</summary>
    public static byte[] ApplyToBgra(byte[] bgra, int width, int height, int stride, CubeLut lut)
    {
        var image = new DecodedImage(bgra, width, height, stride);
        return Apply(image, lut).Bgra;
    }

    private static byte ToByte(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 1f) return 255;
        return (byte)(v * 255f + 0.5f);
    }
}