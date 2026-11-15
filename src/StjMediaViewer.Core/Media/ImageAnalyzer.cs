using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Media;

/// <summary>
/// 图像统计。纯托管逐像素采样（不是全图，按面积自适应步长，≤ ~26 万采样点），
/// 4K 图单次分析在 10~25ms 量级，可以直接给扫描流程用。
/// 直方图/均值/中位数/标准差/饱和度/拉普拉斯方差锐度/黑白/裁切比/主色/色温指数。
/// </summary>
public sealed class ImageAnalyzer : IImageAnalyzer
{
    private const int SampleBudget = 262_144;   // 262144 ≈ 512×512
    private const int SharpBudget = 65_536;     // 拉普拉斯采样点数

    public ImageStats Analyze(DecodedImage image)
    {
        var stats = new ImageStats
        {
            LumaHistogram = new double[256],
            RedHistogram = new double[256],
            GreenHistogram = new double[256],
            BlueHistogram = new double[256],
            DominantColorHex = "#808080",
        };
        if (image is null || image.Width <= 0 || image.Height <= 0 ||
            image.Bgra is null || image.Stride < image.Width * 4)
            return stats;

        int w = image.Width, h = image.Height;
        var bgra = image.Bgra;
        int stride = image.Stride;
        long totalPixels = (long)w * h;
        int step = Math.Max(1, (int)Math.Sqrt(totalPixels / (double)SampleBudget));

        var lumaHist = new int[256];
        var rHist = new int[256];
        var gHist = new int[256];
        var bHist = new int[256];
        var binCount = new int[4096];
        var binR = new int[4096];
        var binG = new int[4096];
        var binB = new int[4096];

        long n = 0;
        double sumL = 0, sumL2 = 0, sumSat = 0;
        long sumR = 0, sumG = 0, sumB = 0;
        long satPixels = 0, shadow = 0, highlight = 0;

        for (int y = 0; y < h; y += step)
        {
            int rowBase = y * stride;
            for (int x = 0; x < w; x += step)
            {
                int i = rowBase + x * 4;
                if (i + 2 >= bgra.Length) continue;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int lum = (r * 77 + g * 150 + b * 29) >> 8;

                lumaHist[lum]++;
                rHist[r]++;
                gHist[g]++;
                bHist[b]++;

                n++;
                sumL += lum;
                sumL2 += (double)lum * lum;

                int mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                double sat = mx == 0 ? 0 : (double)(mx - mn) / mx;
                sumSat += sat;
                if (sat > 0.18) satPixels++;

                sumR += r; sumG += g; sumB += b;
                if (lum <= 5) shadow++;
                else if (lum >= 250) highlight++;

                int bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                binCount[bin]++;
                binR[bin] += r;
                binG[bin] += g;
                binB[bin] += b;
            }
        }

        if (n == 0) return stats;

        double mean = sumL / n;
        double variance = Math.Max(0, sumL2 / n - mean * mean);
        stats.MeanLuma = mean;
        stats.StdDevLuma = Math.Sqrt(variance);
        stats.MeanSaturation = sumSat / n;
        stats.ShadowClipRatio = (double)shadow / n;
        stats.HighlightClipRatio = (double)highlight / n;
        stats.Contrast = Percentile(lumaHist, n, 0.95) - Percentile(lumaHist, n, 0.05);
        stats.MedianLuma = Percentile(lumaHist, n, 0.5);
        stats.WarmthIndex = (sumR - sumB) / (double)n / 255.0 * 100.0;
        stats.IsMonochrome = stats.MeanSaturation < 0.08 && (double)satPixels / n < 0.012;

        for (int i = 0; i < 256; i++)
        {
            stats.LumaHistogram[i] = (double)lumaHist[i] / n;
            stats.RedHistogram[i] = (double)rHist[i] / n;
            stats.GreenHistogram[i] = (double)gHist[i] / n;
            stats.BlueHistogram[i] = (double)bHist[i] / n;
        }

        stats.DominantColorHex = DominantHex(binCount, binR, binG, binB);
        stats.Sharpness = LaplacianVariance(bgra, w, h, stride);
        return stats;
    }

    // ------------------------------------------------------------------ 分位数

    private static double Percentile(int[] hist, long total, double p)
    {
        long target = (long)Math.Round(total * p);
        if (target < 1) target = 1;
        long acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    // ------------------------------------------------------------------ 主色

    private static string DominantHex(int[] count, int[] sr, int[] sg, int[] sb)
    {
        int best = -1, bestCount = 0;
        int bestMid = -1, bestMidCount = 0;
        for (int bin = 0; bin < 4096; bin++)
        {
            int c = count[bin];
            if (c <= 0) continue;
            if (c > bestCount) { bestCount = c; best = bin; }
            int r = (bin >> 8) & 0xF, g = (bin >> 4) & 0xF, b = bin & 0xF;
            int lum = ((r * 16 + 8) * 77 + (g * 16 + 8) * 150 + (b * 16 + 8) * 29) >> 8;
            // 中间调优先：纯黑/纯白背景不该被当成"主色"
            if (lum is > 24 and < 232 && c > bestMidCount) { bestMidCount = c; bestMid = bin; }
        }

        // 中间调像素占比够高（≥ 主色峰的 25%）就用它，否则退回绝对主色
        int pick = bestMid >= 0 && bestMidCount * 4 >= bestCount ? bestMid : best;
        if (pick < 0) return "#808080";

        int cnt = Math.Max(1, count[pick]);
        int rr = sr[pick] / cnt, gg = sg[pick] / cnt, bb = sb[pick] / cnt;
        return $"#{rr:x2}{gg:x2}{bb:x2}";
    }

    // ------------------------------------------------------------------ 锐度

    /// <summary>拉普拉斯方差（原生分辨率邻域，采样网格），值越大越锐。</summary>
    private static double LaplacianVariance(byte[] bgra, int w, int h, int stride)
    {
        if (w < 3 || h < 3) return 0;
        int step = Math.Max(1, (int)Math.Sqrt((long)w * h / (double)SharpBudget));
        if (step * 2 >= w || step * 2 >= h) return 0;

        long n = 0;
        double sum = 0, sumSq = 0;
        for (int y = step; y < h - step; y += step)
        {
            int rowBase = y * stride;
            for (int x = step; x < w - step; x += step)
            {
                int c = Luma(bgra, rowBase + x * 4);
                int up = Luma(bgra, rowBase - step * stride + x * 4);
                int down = Luma(bgra, rowBase + step * stride + x * 4);
                int left = Luma(bgra, rowBase + (x - step) * 4);
                int right = Luma(bgra, rowBase + (x + step) * 4);

                double lap = 4.0 * c - up - down - left - right;
                n++;
                sum += lap;
                sumSq += lap * lap;
            }
        }
        if (n == 0) return 0;
        double mean = sum / n;
        return Math.Max(0, sumSq / n - mean * mean);
    }

    private static int Luma(byte[] bgra, int i)
    {
        if (i < 0 || i + 2 >= bgra.Length) return 0;
        int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
        return (r * 77 + g * 150 + b * 29) >> 8;
    }
}