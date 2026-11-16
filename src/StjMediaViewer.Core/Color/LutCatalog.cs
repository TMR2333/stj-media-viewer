using System.Globalization;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Color;

/// <summary>
/// LUT 目录扫描与分类：从文件名/目录名推断品牌、Log 曲线与分类，
/// 并为素材挑选"该自动套用"的内置 LUT（Sony 相机自动命中官方基准转换）。
/// </summary>
public static class LutCatalog
{
    private static readonly string[] KnownBrands =
    {
        "Sony", "Canon", "Panasonic", "Nikon", "Fujifilm", "DJI", "Blackmagic", "ARRI", "RED", "Leica",
    };

    /// <summary>扫描目录里的 .cube / .3dl 文件（不递归太深，最多 2 层）。</summary>
    public static IReadOnlyList<LutEntry> Scan(IEnumerable<string> folders, string? builtInFolder = null)
    {
        var list = new List<LutEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".cube", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            foreach (var path in files)
            {
                if (!seen.Add(path)) continue;
                var isBuiltIn = IsUnder(path, builtInFolder);
                var entry = TryDescribe(path, isBuiltIn);
                if (entry is not null) list.Add(entry);
            }
        }

        return list
            .OrderBy(e => e.Category)
            .ThenBy(e => e.Brand, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LutEntry? TryDescribe(string path, bool isBuiltIn)
    {
        try
        {
            var info = new FileInfo(path);
            var header = ReadHeader(path, maxLines: 80);
            if (header is null) return null;

            var (is1D, size, title) = header.Value;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var (category, brand, logProfile) = Classify(fileName, path, isBuiltIn);

            return new LutEntry
            {
                Id = IdForPath(path),
                Name = string.IsNullOrWhiteSpace(title) ? fileName : title!,
                FilePath = path,
                Size = size,
                Is1D = is1D,
                Category = category,
                Brand = brand,
                LogProfile = logProfile,
                Description = BuildDescription(category, brand, logProfile, size, is1D),
                FileSizeBytes = info.Exists ? info.Length : 0,
                ModifiedAt = info.Exists ? info.LastWriteTime : DateTimeOffset.Now,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>只读文件开头，拿到 LUT_3D_SIZE / LUT_1D_SIZE / TITLE。</summary>
    private static (bool Is1D, int Size, string? Title)? ReadHeader(string path, int maxLines)
    {
        bool is1D = false;
        int size = 0;
        string? title = null;

        using var reader = new StreamReader(path);
        for (int i = 0; i < maxLines && !reader.EndOfStream; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                var a = line.IndexOf('"');
                if (a >= 0)
                {
                    var b = line.LastIndexOf('"');
                    title = b > a ? line[(a + 1)..b] : line[(a + 1)..];
                }
            }
            else if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseSize(line);
                is1D = false;
            }
            else if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size = ParseSize(line);
                is1D = true;
            }
            else if (char.IsDigit(line[0]) || line[0] == '-' || line[0] == '+')
            {
                break; // 数据区开始
            }
        }

        return size > 0 ? (is1D, size, title) : null;
    }

    private static int ParseSize(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
        return 0;
    }

    /// <summary>按文件名 / 目录名分类（内置目录里的一律算内置）。</summary>
    public static (LutCategory Category, string Brand, string LogProfile) Classify(
        string fileName, string fullPath, bool isBuiltIn = false)
    {
        var name = fileName.ToLowerInvariant();
        var dir = Path.GetDirectoryName(fullPath)?.ToLowerInvariant() ?? "";
        var hint = name + " " + dir;   // 品牌经常只写在目录名里（D:\LUTs\Sony\...）

        var brand = KnownBrands.FirstOrDefault(b => hint.Contains(b.ToLowerInvariant())) ?? "通用";
        if (brand == "通用")
        {
            if (hint.Contains("vlog") || hint.Contains("v-log")) brand = "Panasonic";
            else if (hint.Contains("clog") || hint.Contains("c-log")) brand = "Canon";
            else if (hint.Contains("nlog") || hint.Contains("n-log")) brand = "Nikon";
            else if (hint.Contains("dlog") || hint.Contains("d-log")) brand = "DJI";
        }

        string logProfile =
            name.Contains("slog3") || name.Contains("s-log3") ? "S-Log3" :
            name.Contains("slog2") || name.Contains("s-log2") ? "S-Log2" :
            name.Contains("hlg") ? "HLG" :
            name.Contains("vlog") || name.Contains("v-log") ? "V-Log" :
            name.Contains("clog3") || name.Contains("c-log3") ? "C-Log3" :
            name.Contains("clog") || name.Contains("c-log") ? "C-Log" :
            name.Contains("nlog") || name.Contains("n-log") ? "N-Log" :
            name.Contains("dlog") || name.Contains("d-log") ? "D-Log" :
            "通用";

        if (isBuiltIn) return (LutCategory.BuiltIn, brand, logProfile);

        // 官方风格特征：厂商名 + 官方命名习惯（LC-709 / s709 / SGamut / toRec709 / 机型号）
        bool official = brand != "通用" &&
                        (name.Contains("709") || name.Contains("sgamut") || name.Contains("s-gamut") ||
                         name.Contains("venice") || name.Contains("cine") || name.Contains("log") ||
                         dir.Contains("official") || dir.Contains("official"));

        return (official ? LutCategory.Official : LutCategory.Creative, brand, logProfile);
    }

    /// <summary>判断文件是否位于目录内（正确带分隔符，避免 "user" 前缀误匹配）。</summary>
    private static bool IsUnder(string fullPath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(fullPath);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string BuildDescription(LutCategory cat, string brand, string logProfile, int size, bool is1D)
    {
        var kind = cat switch
        {
            LutCategory.BuiltIn => "按公开公式生成的基准转换",
            LutCategory.Official => "厂商风格（按文件名识别）",
            _ => "创意风格",
        };
        return $"{kind} · {brand} · {logProfile} · {(is1D ? $"1D {size}" : $"{size}³")}";
    }

    /// <summary>稳定 ID：内置用 key，外部文件用路径哈希（重命名会变，但内容不变时也可接受）。</summary>
    public static string IdForPath(string path)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return "file:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ==================================================================== 自动匹配

    /// <summary>
    /// 判断素材是否"应该自动套 LUT"：相机是 Sony 且是 Log / HLG 素材 →
    /// 返回对应的内置基准 LUT key（S-Log3 → S-Gamut3.Cine，S-Log2 → S-Gamut2，HLG → BT.2020）。
    /// </summary>
    public static string? AutoMatchBuiltInKey(MediaAsset asset)
    {
        if (asset is null) return null;
        if (asset.Kind != MediaKind.Video && asset.Kind != MediaKind.Image) return null;

        var brand = (asset.CameraMake ?? "") + " " + (asset.DeviceKey ?? "") + " " + (asset.ColorPrimaries ?? "");
        bool isSony = brand.Contains("sony", StringComparison.OrdinalIgnoreCase) ||
                      brand.Contains("ilce", StringComparison.OrdinalIgnoreCase) ||
                      brand.Contains("zv-", StringComparison.OrdinalIgnoreCase) ||
                      brand.Contains("fx3", StringComparison.OrdinalIgnoreCase) ||
                      brand.Contains("fx30", StringComparison.OrdinalIgnoreCase) ||
                      brand.Contains("a7", StringComparison.OrdinalIgnoreCase);

        if (!isSony) return null;

        var profile = (asset.LogProfile ?? "").ToLowerInvariant();
        bool isLog = asset.IsLog == true || profile.Length > 0 ||
                     brand.Contains("slog", StringComparison.OrdinalIgnoreCase) ||
                     brand.Contains("s-log", StringComparison.OrdinalIgnoreCase);

        if (!isLog) return null;

        if (profile.Contains("s-log2") || profile.Contains("slog2")) return "sony-slog2-709";
        if (profile.Contains("hlg")) return "sony-hlg-709";
        // 默认 S-Log3（Sony 新机默认，且最常用）
        return "sony-slog3-cine-709";
    }
}