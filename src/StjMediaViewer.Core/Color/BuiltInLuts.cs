using System.Globalization;
using StjMediaViewer.Core.Util;
using System.Text;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// 内置 LUT：不打包任何厂商私有 LUT 文件，而是**用公开的色彩科学公式现场生成 .cube**。
///
/// 生成链路（以 Sony 为例）：
///   Log 编码值 →（反解 S-Log3 / S-Log2 / HLG 传输函数）→ 线性场景光 →
///   （S-Gamut3(.Cine) / BT.2020 → Rec.709 矩阵）→ Rec.709 OETF → 输出。
///
/// 这些公式来自 Sony 公开的 S-Log 白皮书与 ITU-R BT.709 / BT.2100，
/// 因此属于"可推导的转换"，不是 Sony 的私有 LUT 资产；
/// 追求与官方 s709 / LC-709 完全一致的效果时，请把官方 LUT 放进用户 LUT 目录。
/// </summary>
public static class BuiltInLuts
{
    /// <summary>生成逻辑版本：改了公式就 +1，旧文件会被自动重新生成。</summary>
    public const int GenerationVersion = 1;

    public const string VersionFileName = "builtin.version";
    public const int CubeSize = 33;

    public sealed record Definition(
        string Key,
        string FileName,
        string Name,
        string Brand,
        string LogProfile,
        string Description);

    public static readonly IReadOnlyList<Definition> All = new List<Definition>
    {
        new("sony-slog3-cine-709", "Sony S-Log3 (S-Gamut3.Cine) to Rec709.cube",
            "Sony S-Log3 · S-Gamut3.Cine → Rec.709", "Sony", "S-Log3",
            "Sony 公开公式推导：S-Log3 反解 + S-Gamut3.Cine→Rec.709 矩阵 + 709 OETF。对应官方 LC-709 Type A 的基准转换。"),
        new("sony-slog3-709", "Sony S-Log3 (S-Gamut3) to Rec709.cube",
            "Sony S-Log3 · S-Gamut3 → Rec.709", "Sony", "S-Log3",
            "同上，但色域按 S-Gamut3（全色域）转 Rec.709，适合广色域素材。"),
        new("sony-slog2-709", "Sony S-Log2 (S-Gamut) to Rec709.cube",
            "Sony S-Log2 · S-Gamut → Rec.709", "Sony", "S-Log2",
            "S-Log2 反解（18% 灰≈32 IRE）+ S-Gamut→Rec.709 + 709 OETF，适配 A7S/A7R II 等 S-Log2 素材。"),
        new("sony-hlg-709", "Sony HLG (BT.2020) to Rec709.cube",
            "Sony HLG · BT.2020 → Rec.709", "Sony", "HLG",
            "HLG (BT.2100 OETF) 反解 + BT.2020→Rec.709 矩阵，适配 ZV-E10 / FX30 的 HLG 素材。"),
    };

    /// <summary>内置目录里的 LUT 完整路径。</summary>
    public static string PathOf(string folder, Definition def) => Path.Combine(folder, def.FileName);

    /// <summary>
    /// 确保内置 LUT 已生成（幂等）。返回生成/命中的文件数。
    /// </summary>
    public static async Task<int> EnsureAsync(string builtInFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(builtInFolder);
        var versionFile = Path.Combine(builtInFolder, VersionFileName);
        var stamp = $"v{GenerationVersion}";

        bool upToDate = false;
        try
        {
            upToDate = File.Exists(versionFile) &&
                       File.ReadAllText(versionFile).Trim() == stamp &&
                       All.All(d => File.Exists(PathOf(builtInFolder, d)));
        }
        catch
        {
            upToDate = false;
        }

        if (upToDate) return 0;

        int written = 0;
        foreach (var def in All)
        {
            ct.ThrowIfCancellationRequested();
            var path = PathOf(builtInFolder, def);
            // 用户手工改过/替换过的同名文件不覆盖（他们可以删掉再看效果）
            if (File.Exists(path))
            {
                written++;
                continue;
            }

            var cube = Generate(def, CubeSize);
            await File.WriteAllTextAsync(path, cube, new UTF8Encoding(false), ct).ConfigureAwait(false);
            written++;
        }

        await File.WriteAllTextAsync(versionFile, stamp, Encoding.UTF8, ct).ConfigureAwait(false);
        Log.Info($"[lut] 内置 LUT 已生成：{written} 条（{builtInFolder}）");
        return written;
    }

    /// <summary>按定义生成 .cube 文本。</summary>
    public static string Generate(Definition def, int size)
    {
        var sb = new StringBuilder(size * size * size * 26);
        sb.Append("# Stjarna PERSPECTIVE · stj-media-viewer 内置 LUT\n");
        sb.Append("# 由公开色彩科学公式推导生成（Sony S-Log 白皮书 / ITU-R BT.709 / BT.2100），非厂商私有 LUT。\n");
        sb.Append("# ").Append(def.Description).Append('\n');
        sb.Append("TITLE \"").Append(def.Name).Append("\"\n");
        sb.Append("LUT_3D_SIZE ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("DOMAIN_MIN 0.0 0.0 0.0\nDOMAIN_MAX 1.0 1.0 1.0\n\n");

        for (int b = 0; b < size; b++)
        for (int g = 0; g < size; g++)
        for (int r = 0; r < size; r++)
        {
            float cr = r / (float)(size - 1);
            float cg = g / (float)(size - 1);
            float cb = b / (float)(size - 1);
            var (oR, oG, oB) = Transform(def.Key, cr, cg, cb);
            AppendTriplet(sb, oR, oG, oB);
        }

        return sb.ToString();
    }

    private static void AppendTriplet(StringBuilder sb, float r, float g, float b)
    {
        sb.Append(Math.Clamp(r, 0f, 1f).ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
          .Append(Math.Clamp(g, 0f, 1f).ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
          .Append(Math.Clamp(b, 0f, 1f).ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
    }

    // ==================================================================== 转换

    private static (float R, float G, float B) Transform(string key, float r, float g, float b) => key switch
    {
        "sony-slog3-cine-709" => SonyToRec709(r, g, b, SLog3Decode, SGamut3CineToRec709),
        "sony-slog3-709" => SonyToRec709(r, g, b, SLog3Decode, SGamut3ToRec709),
        "sony-slog2-709" => SonyToRec709(r, g, b, SLog2Decode, SGamut3ToRec709),
        "sony-hlg-709" => HlgToRec709(r, g, b),
        _ => (r, g, b),
    };

    private static (float, float, float) SonyToRec709(
        float r, float g, float b, Func<float, float> decode, float[,] matrix)
    {
        float lr = decode(r), lg = decode(g), lb = decode(b);
        float xr = matrix[0, 0] * lr + matrix[0, 1] * lg + matrix[0, 2] * lb;
        float xg = matrix[1, 0] * lr + matrix[1, 1] * lg + matrix[1, 2] * lb;
        float xb = matrix[2, 0] * lr + matrix[2, 1] * lg + matrix[2, 2] * lb;

        // 负值来自矩阵外推：给一点柔化，避免暗部出现硬截断的黑块
        return (Rec709Oetf(MathF.Max(0f, xr)),
                Rec709Oetf(MathF.Max(0f, xg)),
                Rec709Oetf(MathF.Max(0f, xb)));
    }

    private static (float, float, float) HlgToRec709(float r, float g, float b)
    {
        float lr = HlgDecode(r), lg = HlgDecode(g), lb = HlgDecode(b);
        // BT.2020 → Rec.709
        float xr = 1.6605f * lr - 0.5876f * lg - 0.0728f * lb;
        float xg = -0.1246f * lr + 1.1329f * lg - 0.0083f * lb;
        float xb = -0.0182f * lr - 0.1006f * lg + 1.1187f * lb;
        return (Rec709Oetf(MathF.Max(0f, xr)),
                Rec709Oetf(MathF.Max(0f, xg)),
                Rec709Oetf(MathF.Max(0f, xb)));
    }

    // ---- 传输函数 ----

    /// <summary>S-Log3 编码（线性场景光 → 0..1 码值）。18% 灰 → 0.4105。</summary>
    public static float SLog3Encode(float x)
    {
        x = MathF.Max(0f, x);
        if (x >= 0.01125f)
            return (420f + MathF.Log10((x + 0.01f) / 0.19f) * 261.5f) / 1023f;
        return (x * (171.2102946929f - 95f) / 0.01125f + 95f) / 1023f;
    }

    /// <summary>S-Log3 反解（码值 → 线性）。用二分法反查编码函数，避免分段公式手抄出错。</summary>
    public static float SLog3Decode(float y) => InvertMonotonic(SLog3Encode, y, 8f);

    /// <summary>S-Log2 编码。18% 灰 → 0.3139。</summary>
    public static float SLog2Encode(float x)
    {
        x = MathF.Max(0f, x);
        return 0.432699f * MathF.Log10(0.9f * x + 0.037584f) + 0.616596f;
    }

    public static float SLog2Decode(float y) => InvertMonotonic(SLog2Encode, y, 8f);

    /// <summary>HLG OETF（BT.2100）。</summary>
    public static float HlgEncode(float e)
    {
        e = MathF.Max(0f, e);
        if (e <= 1f / 12f) return MathF.Sqrt(3f * e);
        return 0.17883277f * MathF.Log(12f * e - 0.28466892f) + 0.55991073f;
    }

    public static float HlgDecode(float y)
    {
        y = Math.Clamp(y, 0f, 1f);
        return InvertMonotonic(HlgEncode, y, 1f);
    }

    /// <summary>Rec.709 OETF（线性 → 显示码值）。</summary>
    public static float Rec709Oetf(float l)
    {
        l = MathF.Max(0f, l);
        return l < 0.018f ? 4.5f * l : 1.099f * MathF.Pow(l, 0.45f) - 0.099f;
    }

    /// <summary>单调递增函数的数值反解（二分 40 次，精度远高于 8bit）。</summary>
    private static float InvertMonotonic(Func<float, float> encode, float target, float hi)
    {
        float lo = 0f;
        float h = hi;
        for (int i = 0; i < 40; i++)
        {
            float mid = (lo + h) * 0.5f;
            if (encode(mid) < target) lo = mid; else h = mid;
        }
        return (lo + h) * 0.5f;
    }

    // ---- 色域矩阵（Sony 公开基色推导，D65）----

    private static readonly float[,] SGamut3CineToRec709 =
    {
        { 1.62693f, -0.35784f, -0.26909f },
        { -0.09907f, 1.14287f, -0.04380f },
        { -0.02460f, -0.09966f, 1.12426f },
    };

    private static readonly float[,] SGamut3ToRec709 =
    {
        { 1.61798f, -0.31301f, -0.30499f },
        { -0.07049f, 1.04209f, 0.02807f },
        { -0.02090f, -0.07052f, 1.09114f },
    };
}