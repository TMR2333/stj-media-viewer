using System.Globalization;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// Adobe <c>.cube</c> LUT（3D 或 1D）。解析 + 三线性插值采样。
/// 文件格式：<c>TITLE</c> / <c>LUT_3D_SIZE</c> / <c>DOMAIN_MIN</c> / <c>DOMAIN_MAX</c> + 若干行 <c>R G B</c>。
/// 3D 数据排列：**R 变化最快**（第一组是 (0,0,0)，第二组是 (1/N,0,0)）。
/// </summary>
public sealed class CubeLut
{
    public string? Title { get; private init; }
    public int Size { get; private init; }
    public bool Is1D { get; private init; }
    public float[] DomainMin { get; private init; } = { 0f, 0f, 0f };
    public float[] DomainMax { get; private init; } = { 1f, 1f, 1f };

    /// <summary>3D：长度 Size³×3；1D：长度 Size×3。</summary>
    public float[] Data { get; private init; } = Array.Empty<float>();

    public bool IsIdentity { get; private init; }

    /// <summary>解析 .cube 文本。</summary>
    public static CubeLut Parse(string text, string? sourceName = null)
    {
        string? title = null;
        int size = 0;
        bool is1D = false;
        float[] dmin = { 0f, 0f, 0f };
        float[] dmax = { 1f, 1f, 1f };
        var values = new List<float>(33 * 33 * 33 * 3);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf('"');
                if (idx >= 0)
                {
                    var end = line.LastIndexOf('"');
                    title = end > idx ? line[(idx + 1)..end] : line[(idx + 1)..];
                }
                continue;
            }

            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseInt(line);
                is1D = false;
                continue;
            }

            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseInt(line);
                is1D = true;
                continue;
            }

            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
            {
                dmin = ParseFloats(line, 3);
                continue;
            }

            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
            {
                dmax = ParseFloats(line, 3);
                continue;
            }

            if (line.StartsWith("LUT_1D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) continue;
            values.Add(r);
            values.Add(g);
            values.Add(b);
        }

        if (size <= 0)
            throw new InvalidDataException($"{sourceName ?? "LUT"} 缺少 LUT_3D_SIZE / LUT_1D_SIZE");

        var expected = is1D ? size * 3 : size * size * size * 3;
        if (values.Count < expected)
            throw new InvalidDataException($"{sourceName ?? "LUT"} 数据行不足（期望 {expected / 3} 行，实际 {values.Count / 3} 行）");

        var data = values.Count == expected ? values.ToArray() : values.Take(expected).ToArray();
        var identity = IsIdentityTable(data, size, is1D);

        return new CubeLut
        {
            Title = title,
            Size = size,
            Is1D = is1D,
            Data = data,
            DomainMin = dmin,
            DomainMax = dmax,
            IsIdentity = identity,
        };
    }

    public static CubeLut ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        var lut = Parse(text, Path.GetFileName(path));
        return lut;
    }

    private static bool IsIdentityTable(float[] data, int size, bool is1D)
    {
        if (is1D) return false;
        int idx = 0;
        for (int b = 0; b < size; b++)
        for (int g = 0; g < size; g++)
        for (int r = 0; r < size; r++)
        {
            var er = r / (float)(size - 1);
            var eg = g / (float)(size - 1);
            var eb = b / (float)(size - 1);
            if (MathF.Abs(data[idx] - er) > 0.004f ||
                MathF.Abs(data[idx + 1] - eg) > 0.004f ||
                MathF.Abs(data[idx + 2] - eb) > 0.004f) return false;
            idx += 3;
        }
        return true;
    }

    private static int ParseInt(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
        return 0;
    }

    private static float[] ParseFloats(string line, int count)
    {
        var result = new float[count];
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int j = 0;
        for (int i = 1; i < parts.Length && j < count; i++)
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result[j++] = v;
        return result;
    }

    // ==================================================================== 采样

    /// <summary>对 0..1 的 RGB 做 LUT 采样（三线性插值）。</summary>
    public void Sample(float r, float g, float b, out float outR, out float outG, out float outB)
    {
        if (IsIdentity)
        {
            outR = r; outG = g; outB = b;
            return;
        }

        // 还原到 LUT 的定义域
        float nr = Normalize(r, DomainMin[0], DomainMax[0]);
        float ng = Normalize(g, DomainMin[1], DomainMax[1]);
        float nb = Normalize(b, DomainMin[2], DomainMax[2]);

        if (Is1D)
        {
            outR = Lookup1D(0, nr);
            outG = Lookup1D(1, ng);
            outB = Lookup1D(2, nb);
            return;
        }

        int n = Size;
        float fr = Math.Clamp(nr, 0f, 1f) * (n - 1);
        float fg = Math.Clamp(ng, 0f, 1f) * (n - 1);
        float fb = Math.Clamp(nb, 0f, 1f) * (n - 1);

        int r0 = (int)fr, g0 = (int)fg, b0 = (int)fb;
        int r1 = Math.Min(r0 + 1, n - 1), g1 = Math.Min(g0 + 1, n - 1), b1 = Math.Min(b0 + 1, n - 1);
        float tr = fr - r0, tg = fg - g0, tb = fb - b0;

        Lerp3(r0, g0, b0, r1, g1, b1, tr, tg, tb, out outR, out outG, out outB);
    }

    private float Lookup1D(int channel, float t)
    {
        int n = Size;
        float f = Math.Clamp(t, 0f, 1f) * (n - 1);
        int i0 = (int)f;
        int i1 = Math.Min(i0 + 1, n - 1);
        float frac = f - i0;
        var a = Data[i0 * 3 + channel];
        var b = Data[i1 * 3 + channel];
        return a + (b - a) * frac;
    }

    private void Lerp3(int r0, int g0, int b0, int r1, int g1, int b1, float tr, float tg, float tb,
                       out float outR, out float outG, out float outB)
    {
        int n = Size;

        int i000 = ((b0 * n + g0) * n + r0) * 3;
        int i100 = ((b0 * n + g0) * n + r1) * 3;
        int i010 = ((b0 * n + g1) * n + r0) * 3;
        int i110 = ((b0 * n + g1) * n + r1) * 3;
        int i001 = ((b1 * n + g0) * n + r0) * 3;
        int i101 = ((b1 * n + g0) * n + r1) * 3;
        int i011 = ((b1 * n + g1) * n + r0) * 3;
        int i111 = ((b1 * n + g1) * n + r1) * 3;

        var d = Data;

        float c000r = d[i000], c000g = d[i000 + 1], c000b = d[i000 + 2];
        float c100r = d[i100], c100g = d[i100 + 1], c100b = d[i100 + 2];
        float c010r = d[i010], c010g = d[i010 + 1], c010b = d[i010 + 2];
        float c110r = d[i110], c110g = d[i110 + 1], c110b = d[i110 + 2];
        float c001r = d[i001], c001g = d[i001 + 1], c001b = d[i001 + 2];
        float c101r = d[i101], c101g = d[i101 + 1], c101b = d[i101 + 2];
        float c011r = d[i011], c011g = d[i011 + 1], c011b = d[i011 + 2];
        float c111r = d[i111], c111g = d[i111 + 1], c111b = d[i111 + 2];

        float r00 = c000r + (c100r - c000r) * tr;
        float g00 = c000g + (c100g - c000g) * tr;
        float b00 = c000b + (c100b - c000b) * tr;

        float r01 = c001r + (c101r - c001r) * tr;
        float g01 = c001g + (c101g - c001g) * tr;
        float b01 = c001b + (c101b - c001b) * tr;

        float r10 = c010r + (c110r - c010r) * tr;
        float g10 = c010g + (c110g - c010g) * tr;
        float b10 = c010b + (c110b - c010b) * tr;

        float r11 = c011r + (c111r - c011r) * tr;
        float g11 = c011g + (c111g - c011g) * tr;
        float b11 = c011b + (c111b - c011b) * tr;

        float r0v = r00 + (r10 - r00) * tg;
        float g0v = g00 + (g10 - g00) * tg;
        float b0v = b00 + (b10 - b00) * tg;

        float r1v = r01 + (r11 - r01) * tg;
        float g1v = g01 + (g11 - g01) * tg;
        float b1v = b01 + (b11 - b01) * tg;

        outR = r0v + (r1v - r0v) * tb;
        outG = g0v + (g1v - g0v) * tb;
        outB = b0v + (b1v - b0v) * tb;
    }

    private static float Normalize(float v, float lo, float hi)
        => MathF.Abs(hi - lo) < 1e-6f ? v : (v - lo) / (hi - lo);
}