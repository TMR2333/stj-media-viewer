using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 纯托管像素操作：缩放 / 灰度化。解码器已经把大图缩到目标尺寸，
/// 这里只处理编码前的小幅缩放与 aHash 需要的定尺寸重采样，避免再走一次 WIC。
/// </summary>
public static class ImageOps
{
    /// <summary>等比缩放到 maxDimension 以内（只缩不放）。</summary>
    public static DecodedImage Fit(DecodedImage src, int maxDimension)
    {
        if (maxDimension <= 0) return src;
        int longer = Math.Max(src.Width, src.Height);
        if (longer <= maxDimension) return src;
        double s = (double)maxDimension / longer;
        int w = Math.Max(1, (int)Math.Round(src.Width * s));
        int h = Math.Max(1, (int)Math.Round(src.Height * s));
        return ResizeExact(src, w, h);
    }

    /// <summary>精确缩放到 dstW×dstH：缩小用面积平均（box），放大用最近邻。输出紧密 BGRA8。</summary>
    public static DecodedImage ResizeExact(DecodedImage src, int dstW, int dstH)
    {
        if (dstW <= 0 || dstH <= 0) throw new ArgumentOutOfRangeException(nameof(dstW));
        if (src.Width == dstW && src.Height == dstH) return src;

        var dst = new byte[dstW * dstH * 4];
        bool downscale = dstW < src.Width && dstH < src.Height;

        for (int y = 0; y < dstH; y++)
        {
            int y0 = (int)((long)y * src.Height / dstH);
            int y1 = (int)(((long)y + 1) * src.Height / dstH);
            if (y1 <= y0) y1 = y0 + 1;
            if (y1 > src.Height) y1 = src.Height;

            for (int x = 0; x < dstW; x++)
            {
                int x0 = (int)((long)x * src.Width / dstW);
                int x1 = (int)(((long)x + 1) * src.Width / dstW);
                if (x1 <= x0) x1 = x0 + 1;
                if (x1 > src.Width) x1 = src.Width;

                int di = (y * dstW + x) * 4;
                if (!downscale)
                {
                    int si = (y0 * src.Width + x0) * 4;
                    dst[di] = src.Bgra[si];
                    dst[di + 1] = src.Bgra[si + 1];
                    dst[di + 2] = src.Bgra[si + 2];
                    dst[di + 3] = src.Bgra[si + 3];
                    continue;
                }

                long sb = 0, sg = 0, sr = 0, sa = 0;
                int n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int rowBase = sy * src.Width;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int si = (rowBase + sx) * 4;
                        sb += src.Bgra[si];
                        sg += src.Bgra[si + 1];
                        sr += src.Bgra[si + 2];
                        sa += src.Bgra[si + 3];
                        n++;
                    }
                }
                if (n == 0) n = 1;
                dst[di] = (byte)(sb / n);
                dst[di + 1] = (byte)(sg / n);
                dst[di + 2] = (byte)(sr / n);
                dst[di + 3] = (byte)(sa / n);
            }
        }
        return new DecodedImage(dst, dstW, dstH, dstW * 4);
    }

    /// <summary>把 BGRA 转成 8×8 灰度（0..255），用于 aHash。非 8×8 输入先重采样。</summary>
    public static byte[] ToGray8x8(DecodedImage img)
    {
        var small = img.Width == 8 && img.Height == 8 ? img : ResizeExact(img, 8, 8);
        var gray = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int p = i * 4;
            int b = small.Bgra[p], g = small.Bgra[p + 1], r = small.Bgra[p + 2];
            gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
        }
        return gray;
    }

    /// <summary>按 stride 安全取一行像素（解码器可能给非紧密行）。</summary>
    public static int PixelOffset(DecodedImage img, int x, int y) => y * img.Stride + x * 4;

    /// <summary>顺时针旋转 90°（BGRA 像素级）。</summary>
    public static DecodedImage Rotate90Clockwise(DecodedImage src)
    {
        int w = src.Width, h = src.Height;
        int stride = h * 4;   // 旋转后宽度 = 原高度
        var dst = new byte[stride * w];   // 旋转后高度 = 原宽度
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * src.Stride;
            for (int x = 0; x < w; x++)
            {
                // 目标坐标： (h-1-y, x)
                int dstX = h - 1 - y;
                int dstY = x;
                int si = srcRow + x * 4;
                int di = dstY * stride + dstX * 4;
                dst[di] = src.Bgra[si];
                dst[di + 1] = src.Bgra[si + 1];
                dst[di + 2] = src.Bgra[si + 2];
                dst[di + 3] = src.Bgra[si + 3];
            }
        }
        return new DecodedImage(dst, h, w, stride);
    }

    /// <summary>水平/垂直翻转。</summary>
    public static DecodedImage Flip(DecodedImage src, bool horizontal)
    {
        int w = src.Width, h = src.Height;
        int stride = w * 4;
        var dst = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * src.Stride;
            int dstRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                int dx = horizontal ? w - 1 - x : x;
                int dy = horizontal ? y : h - 1 - y;
                int si = srcRow + x * 4;
                int di = dy * stride + dx * 4;
                dst[di] = src.Bgra[si];
                dst[di + 1] = src.Bgra[si + 1];
                dst[di + 2] = src.Bgra[si + 2];
                dst[di + 3] = src.Bgra[si + 3];
            }
        }
        return new DecodedImage(dst, w, h, stride);
    }

    /// <summary>按素材的预览编辑（顺时针角度 + 翻转）处理图像；没有编辑时原样返回。</summary>
    public static DecodedImage ApplyEdits(DecodedImage src, int rotationDegrees, bool flipH, bool flipV)
    {
        var result = src;
        var steps = ((rotationDegrees % 360) + 360) % 360 / 90;
        for (int i = 0; i < steps; i++) result = Rotate90Clockwise(result);
        if (flipH) result = Flip(result, horizontal: true);
        if (flipV) result = Flip(result, horizontal: false);
        return result;
    }
}