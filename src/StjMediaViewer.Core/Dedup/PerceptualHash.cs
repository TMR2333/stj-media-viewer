using System.Numerics;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Dedup;

/// <summary>
/// 感知哈希（aHash）：解码到 8×8 灰度 → 与均值比较得到 64bit 指纹。
/// 连拍、轻微调色、重新压缩的近似图片汉明距离通常 ≤ 6。
/// </summary>
internal static class PerceptualHash
{
    public static ulong AverageHash(DecodedImage image)
    {
        var gray = ImageOps_ToGray8x8(image);
        int sum = 0;
        for (int i = 0; i < 64; i++) sum += gray[i];
        double mean = sum / 64.0;

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (gray[i] > mean) hash |= 1UL << i;
        return hash;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    // 避免 Dedup → Media 内部类型依赖，这里复刻同一套 BGRA→灰度逻辑
    private static byte[] ImageOps_ToGray8x8(DecodedImage img)
    {
        var small = img.Width == 8 && img.Height == 8 ? img : Resize(img, 8, 8);
        var gray = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int p = i * 4;
            if (p + 2 >= small.Bgra.Length) break;
            int b = small.Bgra[p], g = small.Bgra[p + 1], r = small.Bgra[p + 2];
            gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
        }
        return gray;
    }

    private static DecodedImage Resize(DecodedImage src, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int y0 = (int)((long)y * src.Height / dstH);
            int y1 = Math.Max(y0 + 1, (int)(((long)y + 1) * src.Height / dstH));
            if (y1 > src.Height) y1 = src.Height;
            for (int x = 0; x < dstW; x++)
            {
                int x0 = (int)((long)x * src.Width / dstW);
                int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * src.Width / dstW));
                if (x1 > src.Width) x1 = src.Width;

                long sb = 0, sg = 0, sr = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * src.Stride;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int si = row + sx * 4;
                        if (si + 2 >= src.Bgra.Length) break;
                        sb += src.Bgra[si]; sg += src.Bgra[si + 1]; sr += src.Bgra[si + 2]; n++;
                    }
                }
                if (n == 0) n = 1;
                int di = (y * dstW + x) * 4;
                dst[di] = (byte)(sb / n);
                dst[di + 1] = (byte)(sg / n);
                dst[di + 2] = (byte)(sr / n);
                dst[di + 3] = 255;
            }
        }
        return new DecodedImage(dst, dstW, dstH, dstW * 4);
    }
}