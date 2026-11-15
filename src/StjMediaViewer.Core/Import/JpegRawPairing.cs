using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Import;

/// <summary>
/// JPG / RAW 配对规则（导入与扫描共用，避免两套事实）。
///
/// 规则：**同目录 + 同基名（去扩展名，大小写不敏感）= 同一组**。
/// - 组内 JPEG（.jpg/.jpeg/.jpe/.jfif）作为预览与代表（cover）；
/// - 组内 RAW（.cr2/.cr3/.nef/.arw/...）作为原件；
/// - 同基名的边车（.xmp/.dop/.cos/...）挂到该组；
/// - 只有 JPG 或只有 RAW 时也自成一組，只是不显示 RAW&amp;JPEG 标签。
/// </summary>
public static class JpegRawPairing
{
    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf",
        ".rw2", ".raw", ".pef", ".srw", ".x3f", ".mos", ".iiq", ".3fr", ".fff", ".mef",
        ".erf", ".kdc", ".dcr", ".mrw", ".rwl", ".dng", ".gpr",
    };

    public static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
    };

    public static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmp", ".dop", ".cos", ".on1", ".pp3", ".arp",
    };

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));
    public static bool IsJpeg(string path) => JpegExtensions.Contains(Path.GetExtension(path));
    public static bool IsSidecar(string path) => SidecarExtensions.Contains(Path.GetExtension(path)) ||
                                                 path.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>分组键：目录（小写、规范化）+ 基名（小写）。</summary>
    public static string GroupKey(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            return dir.ToLowerInvariant() + "|" + name.ToLowerInvariant();
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }

    /// <summary>把一批路径按"同目录同基名"分组成 raw/jpeg/sidecar/other 四类。</summary>
    public static Dictionary<string, PairBucket> Group(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, PairBucket>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            var key = GroupKey(p);
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new PairBucket(key);
                map[key] = bucket;
            }

            if (IsSidecar(p)) bucket.Sidecars.Add(p);
            else if (IsRaw(p)) bucket.Raws.Add(p);
            else if (IsJpeg(p)) bucket.Jpegs.Add(p);
            else bucket.Others.Add(p);
        }
        return map;
    }

    public sealed class PairBucket
    {
        public PairBucket(string key) => Key = key;
        public string Key { get; }
        public List<string> Jpegs { get; } = new();
        public List<string> Raws { get; } = new();
        public List<string> Sidecars { get; } = new();
        public List<string> Others { get; } = new();

        public bool HasRawJpegPair => Jpegs.Count > 0 && Raws.Count > 0;

        /// <summary>组代表：优先 JPEG（预览快、通用），没有则 RAW。</summary>
        public string? Cover => Jpegs.FirstOrDefault() ?? Raws.FirstOrDefault();

        public IEnumerable<string> AllFiles()
        {
            foreach (var p in Jpegs) yield return p;
            foreach (var p in Raws) yield return p;
            foreach (var p in Others) yield return p;
            foreach (var p in Sidecars) yield return p;
        }

        public int FileCount => Jpegs.Count + Raws.Count + Others.Count + Sidecars.Count;
    }
}