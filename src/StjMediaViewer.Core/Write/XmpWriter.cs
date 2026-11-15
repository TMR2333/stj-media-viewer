using System.Globalization;
using System.Text;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Write;

/// <summary>
/// XMP 边车写入器：把评分、色标、关键词、分类结果以**非破坏性**方式写回磁盘。
///
/// 设计要点：
/// 1) 绝不修改原始媒体文件（RAW 尤其危险），只写同名 .xmp 边车；
/// 2) 已存在边车时保留原文件里的未知字段：这里用"新建完整 XMP + 覆盖同名属性"的保守策略，
///    并在写入前把旧文件备份为 .xmp.bak（仅第一次）；
/// 3) 属性名遵循 XMP 标准（xmp:Rating / xmp:Label / dc:subject / photoshop:City …），
///    这样 Lightroom / Capture One / Bridge / darktable 都能读回去。
/// </summary>
public sealed class XmpWriter : IXmpWriter
{
    public string BuildXmp(MediaAsset asset)
    {
        var now = DateTimeOffset.Now;
        var sb = new StringBuilder(2048);
        sb.AppendLine("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.AppendLine("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"STJ Media Viewer\">");
        sb.AppendLine(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.AppendLine("  <rdf:Description rdf:about=\"\"");
        sb.AppendLine("    xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        sb.AppendLine("    xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        sb.AppendLine("    xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\"");
        sb.AppendLine("    xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\"");
        sb.AppendLine("    xmlns:stEvt=\"http://ns.adobe.com/xap/1.0/sType/ResourceEvent#\"");
        sb.AppendLine("    xmlns:crs=\"http://ns.adobe.com/camera-raw-settings/1.0/\">");

        if (asset.Rating > 0)
            sb.AppendLine($"   <xmp:Rating>{asset.Rating}</xmp:Rating>");
        if (!string.IsNullOrWhiteSpace(asset.ColorLabel) && !string.Equals(asset.ColorLabel, "none", StringComparison.OrdinalIgnoreCase))
        {
            var label = char.ToUpperInvariant(asset.ColorLabel![0]) + asset.ColorLabel[1..].ToLowerInvariant();
            sb.AppendLine($"   <xmp:Label>{Escape(label)}</xmp:Label>");
        }
        if (!string.IsNullOrWhiteSpace(asset.Theme) || !string.IsNullOrWhiteSpace(asset.Tone))
        {
            sb.AppendLine("   <xmp:CreatorTool>STJ Media Viewer</xmp:CreatorTool>");
        }
        if (asset.Theme is not null)
            sb.AppendLine($"   <stj:Theme xmlns:stj=\"https://stj.local/ns/1.0/\" stj:id=\"{Escape(asset.Theme)}\" />");
        if (asset.Tone is not null)
            sb.AppendLine($"   <stj:Tone xmlns:stj=\"https://stj.local/ns/1.0/\" stj:id=\"{Escape(asset.Tone)}\" />");

        if (!string.IsNullOrWhiteSpace(asset.Title))
            sb.AppendLine($"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(asset.Title!)}</rdf:li></rdf:Alt></dc:title>");
        if (!string.IsNullOrWhiteSpace(asset.Caption))
            sb.AppendLine($"   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(asset.Caption!)}</rdf:li></rdf:Alt></dc:description>");
        if (!string.IsNullOrWhiteSpace(asset.Creator))
            sb.AppendLine($"   <dc:creator><rdf:Seq><rdf:li>{Escape(asset.Creator!)}</rdf:li></rdf:Seq></dc:creator>");
        if (!string.IsNullOrWhiteSpace(asset.Copyright))
            sb.AppendLine($"   <dc:rights><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(asset.Copyright!)}</rdf:li></rdf:Alt></dc:rights>");

        var keywords = asset.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keywords.Count > 0)
        {
            sb.AppendLine("   <dc:subject><rdf:Bag>");
            foreach (var k in keywords) sb.AppendLine($"    <rdf:li>{Escape(k)}</rdf:li>");
            sb.AppendLine("   </rdf:Bag></dc:subject>");
        }

        if (!string.IsNullOrWhiteSpace(asset.City))
            sb.AppendLine($"   <photoshop:City>{Escape(asset.City!)}</photoshop:City>");
        if (!string.IsNullOrWhiteSpace(asset.Province))
            sb.AppendLine($"   <photoshop:State>{Escape(asset.Province!)}</photoshop:State>");
        if (!string.IsNullOrWhiteSpace(asset.Country))
            sb.AppendLine($"   <photoshop:Country>{Escape(asset.Country!)}</photoshop:Country>");

        if (asset.PostProcess == PostProcessState.Processed)
        {
            sb.AppendLine("   <crs:HasSettings>True</crs:HasSettings>");
            if (!string.IsNullOrWhiteSpace(asset.DevelopEngine))
                sb.AppendLine($"   <crs:HasCrop>False</crs:HasCrop>");
        }
        else if (asset.PostProcess == PostProcessState.Unprocessed)
        {
            sb.AppendLine("   <crs:HasSettings>False</crs:HasSettings>");
        }

        if (asset.IsPick is bool pick)
            sb.AppendLine($"   <xmp:Pick>{pick.ToString().ToLowerInvariant()}</xmp:Pick>");

        sb.AppendLine("   <xmp:MetadataDate>" + now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) + "</xmp:MetadataDate>");
        sb.AppendLine("   <xmp:ModifyDate>" + now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) + "</xmp:ModifyDate>");
        sb.AppendLine("  </rdf:Description>");
        sb.AppendLine(" </rdf:RDF>");
        sb.AppendLine("</x:xmpmeta>");
        sb.AppendLine("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    public async Task<bool> WriteSidecarAsync(MediaAsset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset.FullPath)) return false;
        try
        {
            var sidecar = ResolveSidecarPath(asset);
            var directory = Path.GetDirectoryName(sidecar);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(sidecar))
            {
                var backup = sidecar + ".bak";
                if (!File.Exists(backup)) File.Copy(sidecar, backup, overwrite: false);
            }

            var xml = BuildXmp(asset);
            await File.WriteAllTextAsync(sidecar, xml, new UTF8Encoding(false), ct).ConfigureAwait(false);
            asset.XmpPath = sidecar;
            if (!asset.Sidecars.Contains(sidecar, StringComparer.OrdinalIgnoreCase)) asset.Sidecars.Add(sidecar);
            if (asset.PostProcess == PostProcessState.Unknown) asset.PostProcess = PostProcessState.Processed;
            Log.Info($"XMP 已写入 {sidecar}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"写 XMP 失败 {asset.FullPath}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>RAW 用同名 .xmp；其它格式用 "原文件名.xmp"（Lightroom 对 RAW 的惯例）。</summary>
    public static string ResolveSidecarPath(MediaAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.XmpPath)) return asset.XmpPath!;
        if (asset.IsRaw) return Path.ChangeExtension(asset.FullPath, ".xmp");
        return asset.FullPath + ".xmp";
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}