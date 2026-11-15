using System.Text;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>边车/配对发现结果。</summary>
public sealed class SidecarDiscovery
{
    /// <summary>发现的边车文件（.xmp/.dop/.cos/.on1/.pp3/.arp）绝对路径。</summary>
    public List<string> Sidecars { get; } = new();
    /// <summary>可用于读取评分/关键词的 XMP（.xmp 优先；.dop/.cos 等内容是 XMP 的也可）。</summary>
    public string? XmpSidecarPath { get; set; }
    /// <summary>最佳边车路径（XMP 优先，其次后期设置文件），用于 XmpPath 字段。</summary>
    public string? PrimarySidecarPath { get; set; }
    public XmpData? SidecarXmp { get; set; }
    /// <summary>同名的 RAW 或 JPEG 配对文件（不放进 Sidecars，避免与"边车"语义混淆）。</summary>
    public string? PairedFilePath { get; set; }
    public bool HasDevelopEdits { get; set; }
    public string? DevelopEngine { get; set; }
    /// <summary>XMP 里显式写了 crs:HasSettings=false（"未改过"）。</summary>
    public bool ExplicitlyUnprocessed { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 边车发现：同目录同名（大小写不敏感）.xmp / .dop / .cos / .on1 / .pp3 / .arp，
/// 支持 "IMG_1234.CR3.xmp" 双扩展名、RAW↔JPEG 同名配对。
/// 只做文件名匹配 + 读一个边车文件（≤16MB），不递归目录。
/// </summary>
public static class SidecarScanner
{
    private static readonly string[] DevelopSidecarExtensions = { ".dop", ".cos", ".on1", ".pp3", ".arp" };

    private static readonly string[] RawExtensions =
    {
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2",
        ".pef", ".srw", ".x3f", ".mos", ".iiq", ".3fr", ".fff", ".mef", ".erf", ".kdc", ".dcr", ".mrw", ".rwl", ".dng",
    };

    private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

    public static SidecarDiscovery Discover(string mediaPath, RawFormat rawFormat, ImageFlavor flavor, MediaKind kind)
    {
        var result = new SidecarDiscovery();
        if (string.IsNullOrWhiteSpace(mediaPath)) return result;

        try
        {
            var dir = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

            var fileName = Path.GetFileName(mediaPath);
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            var self = Path.GetFullPath(mediaPath);

            // RAW+JPEG 同名时，.xmp/.dop/.cos 属于 RAW（相机 JPEG 只是预览），
            // 不能把 RAW 的后期设置挂到 JPEG 上，否则 JPEG 会被误判为"已后期/已评分"。
            if (kind == MediaKind.Image && flavor == ImageFlavor.Jpeg && rawFormat == RawFormat.None)
            {
                foreach (var rawExt in RawExtensions)
                {
                    if (File.Exists(Path.Combine(dir, stem + rawExt)))
                        return result;   // 交给 RAW 文件去认领边车
                }
            }

            // 用前缀模式枚举同目录同名前缀的文件（Windows 文件名匹配不区分大小写）。
            var names = new List<string>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, stem + ".*", SearchOption.TopDirectoryOnly))
                    names.Add(path);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("目录枚举失败: " + ex.Message);
            }

            // 兜底：直接探测常见组合（处理 stem 含通配符/大小写怪异的边缘情况）
            foreach (var ext in new[] { ".xmp", ".dop", ".cos", ".on1", ".pp3", ".arp", ".xmp.bak" })
            {
                Probe(names, Path.Combine(dir, stem + ext));
                Probe(names, Path.Combine(dir, fileName + ext));   // IMG_1234.CR3.xmp
            }
            foreach (var ext in RawExtensions.Concat(JpegExtensions))
                Probe(names, Path.Combine(dir, stem + ext));

            string? bestXmpPath = null;
            XmpData? bestXmp = null;
            var bestXmpScore = -1;
            string? firstDevelopSidecar = null;
            XmpData? developSidecarXmp = null;
            string? developSidecarXmpPath = null;
            string? developEngine = null;
            var hasDevelopEdits = false;
            var explicitlyUnprocessed = false;

            foreach (var path in names)
            {
                if (string.Equals(Path.GetFullPath(path), self, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(name).ToLowerInvariant();
                var lowerName = name.ToLowerInvariant();

                var isXmp = lowerName.EndsWith(".xmp", StringComparison.Ordinal) || lowerName.EndsWith(".xmp.bak", StringComparison.Ordinal);
                var isDevelopSidecar = DevelopSidecarExtensions.Contains(ext);

                if (isXmp || isDevelopSidecar || MediaTypeCatalog.IsSidecar(path))
                {
                    if (!result.Sidecars.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Sidecars.Add(path);

                    if (isDevelopSidecar)
                    {
                        hasDevelopEdits = true;
                        firstDevelopSidecar ??= path;
                        var engine = DetectDevelopEngineFromFile(path, ext);
                        developEngine = PreferEngine(developEngine, engine);

                        // .dop/.cos 实际内容常是 XMP（Capture One / DxO 都会写 XML），能解析就顺带拿评分/关键词
                        var parsed = TryReadSidecarXmp(path);
                        if (parsed is not null && !parsed.IsEmpty)
                        {
                            developSidecarXmp ??= parsed;
                            developSidecarXmpPath ??= path;
                        }
                        continue;
                    }

                    if (!isXmp) continue;

                    // 选择最匹配的 XMP：<stem>.xmp 优先于 <fileName>.xmp
                    var score = name.Equals(stem + ".xmp", StringComparison.OrdinalIgnoreCase) ? 3
                        : name.Equals(fileName + ".xmp", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    if (score <= bestXmpScore) continue;

                    var xmp = XmpReader.ParseFile(path);
                    if (xmp.IsEmpty && xmp.ParseError is not null)
                    {
                        result.Warnings.Add($"XMP 边车解析失败: {Path.GetFileName(path)}");
                        continue;
                    }
                    bestXmpScore = score;
                    bestXmpPath = path;
                    bestXmp = xmp;

                    if (xmp.HasDevelopSettings == true || xmp.ProcessVersion is not null)
                    {
                        hasDevelopEdits = true;
                        developEngine = PreferEngine(developEngine, SidecarEngineFromXmp(xmp));
                    }
                    else if (xmp.HasDevelopSettings == false)
                    {
                        explicitlyUnprocessed = true;
                    }
                    if (xmp.HasCrsSection)
                        developEngine = PreferEngine(developEngine, SidecarEngineFromXmp(xmp) ?? "Adobe Lightroom");
                    continue;
                }

                // RAW ↔ JPEG 同名配对（不放进 Sidecars，避免与边车混淆）
                if (kind != MediaKind.Image) continue;
                var candidateIsJpeg = JpegExtensions.Contains(ext);
                var candidateIsRaw = RawExtensions.Contains(ext);
                if (!candidateIsJpeg && !candidateIsRaw) continue;

                var weAreRaw = rawFormat != RawFormat.None;
                var weAreJpeg = flavor == ImageFlavor.Jpeg;
                if (weAreRaw && candidateIsJpeg || weAreJpeg && candidateIsRaw)
                    result.PairedFilePath ??= path;
            }

            result.XmpSidecarPath = bestXmpPath ?? developSidecarXmpPath;
            result.SidecarXmp = bestXmp ?? developSidecarXmp;
            result.PrimarySidecarPath = bestXmpPath ?? firstDevelopSidecar;
            result.HasDevelopEdits = hasDevelopEdits;
            result.ExplicitlyUnprocessed = explicitlyUnprocessed;
            result.DevelopEngine = developEngine;
            if (result.DevelopEngine is null && hasDevelopEdits)
                result.DevelopEngine = "Adobe Lightroom";
        }
        catch (Exception ex)
        {
            result.Warnings.Add("边车发现失败: " + ex.Message);
            Log.Warn($"[SidecarScanner] 边车发现失败: {mediaPath}", ex);
        }
        return result;
    }

    private static XmpData? TryReadSidecarXmp(string path)
    {
        try
        {
            var xmp = XmpReader.ParseFile(path);
            return xmp.IsEmpty && xmp.ParseError is not null ? null : xmp;
        }
        catch { return null; }
    }

    private static string? PreferEngine(string? current, string? candidate)
    {
        if (candidate is null) return current;
        return current ?? candidate;
    }

    /// <summary>
    /// 从 .dop/.cos 文件内容（通常是 XML/XMP）或扩展名推断后期引擎。
    /// 内容里的 "Capture One" 优先于扩展名（.dop 也可能被 Capture One 写）。
    /// </summary>
    private static string DetectDevelopEngineFromFile(string path, string ext)
    {
        try
        {
            using var fs = MetaUtil.OpenSequential(path);
            var buf = new byte[(int)Math.Min(fs.Length, 8192)];
            var read = fs.Read(buf, 0, buf.Length);
            var head = Encoding.UTF8.GetString(buf, 0, read);
            if (head.Contains("Capture One", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("CaptureOne", StringComparison.OrdinalIgnoreCase))
                return "CaptureOne";
            if (head.Contains("DxO", StringComparison.OrdinalIgnoreCase)) return "DxO PhotoLab";
            if (head.Contains("ON1", StringComparison.OrdinalIgnoreCase)) return "ON1 Photo RAW";
            if (head.Contains("RawTherapee", StringComparison.OrdinalIgnoreCase)) return "RawTherapee";
            if (head.Contains("darktable", StringComparison.OrdinalIgnoreCase)) return "darktable";
            if (head.Contains("Lightroom", StringComparison.OrdinalIgnoreCase)) return "Adobe Lightroom";
        }
        catch { /* 内容读不到就用扩展名 */ }

        return ext switch
        {
            ".dop" => "DxO PhotoLab",
            ".cos" => "CaptureOne",
            ".on1" => "ON1 Photo RAW",
            ".pp3" => "RawTherapee",
            ".arp" => "ART",
            _ => "Adobe Lightroom",
        };
    }

    /// <summary>边车 XMP 的引擎名采用 Lightroom 官方写法 "Adobe Lightroom"。</summary>
    private static string? SidecarEngineFromXmp(XmpData xmp)
    {
        var guessed = GuessEngineFromXmp(xmp);
        return guessed switch
        {
            "Lightroom" => "Adobe Lightroom",
            "Capture One" => "CaptureOne",
            _ => guessed,
        };
    }

    private static void Probe(List<string> names, string candidate)
    {
        try
        {
            if (!File.Exists(candidate)) return;
            var full = Path.GetFullPath(candidate);
            if (!names.Contains(full, StringComparer.OrdinalIgnoreCase)) names.Add(full);
        }
        catch { /* 忽略非法路径 */ }
    }

    /// <summary>从 XMP 的软件字段推断后期引擎（内部统一名："Lightroom" / "Capture One" / …）。</summary>
    public static string? GuessEngineFromXmp(XmpData? xmp)
    {
        if (xmp is null) return null;
        var tool = ((xmp.Software ?? "") + " " + (xmp.Toolkit ?? "")).ToLowerInvariant();
        if (tool.Contains("lightroom") || tool.Contains("camera raw")) return "Lightroom";
        if (tool.Contains("capture one") || tool.Contains("captureone")) return "Capture One";
        if (tool.Contains("darktable")) return "darktable";
        if (tool.Contains("rawtherapee")) return "RawTherapee";
        if (tool.Contains("dxo")) return "DxO PhotoLab";
        if (tool.Contains("on1")) return "ON1 Photo RAW";
        if (tool.Contains("luminar")) return "Luminar";
        if (tool.Contains("affinity")) return "Affinity Photo";
        if (tool.Contains("photoshop")) return "Photoshop";
        if (xmp.HasCrsSection) return "Lightroom";   // crs: 是 Camera Raw / Lightroom 的标准命名空间
        return null;
    }
}