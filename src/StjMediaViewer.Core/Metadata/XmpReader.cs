using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 一个 XMP 包（一张图/一段视频可能有多层 XMP）。属性名统一规范化成
/// <c>dc:title</c> / <c>xmp:Rating</c> / <c>photoshop:City</c> / <c>crs:HasSettings</c> 这种
/// "规范前缀:本地名" 形式，与磁盘上的前缀写法无关（按命名空间 URI 归一）。
/// 支持 RDF/XML 的三种常见写法：属性式、简单元素式、rdf:Alt/Bag/Seq 容器式。
/// </summary>
public sealed class XmpData
{
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string? Lang, string Value)>> _alt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>宽松解析时的错误信息（正常情况下为 null）。</summary>
    public string? ParseError { get; internal set; }

    public bool IsEmpty => _props.Count == 0;

    /// <summary>所有规范化属性名 → 首个（或首选）值。</summary>
    public IReadOnlyDictionary<string, string> Properties => _props;

    public string? Get(string key) => _props.TryGetValue(key, out var v) ? v : null;

    public bool Has(string key) => _props.ContainsKey(key);

    public IReadOnlyList<string> GetValues(string key) =>
        _values.TryGetValue(key, out var list) ? list : Array.Empty<string>();

    /// <summary>XMP 里是否带 Camera Raw / Lightroom 的 crs: 段。</summary>
    public bool HasCrsSection => _props.Keys.Any(k => k.StartsWith("crs:", StringComparison.OrdinalIgnoreCase));

    internal void AddValue(string key, string? value, string? lang = null)
    {
        value = MetaUtil.CleanText(value);
        if (string.IsNullOrEmpty(value)) return;
        key = key.Trim();
        if (key.Length == 0) return;

        if (!_values.TryGetValue(key, out var list))
        {
            list = new List<string>(1);
            _values[key] = list;
        }
        // 同一个值在 rdf:li + 属性双写时会出现两次，去重
        if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);

        if (lang is not null)
        {
            if (!_alt.TryGetValue(key, out var alts))
            {
                alts = new List<(string?, string)>(2);
                _alt[key] = alts;
            }
            if (!alts.Any(a => a.Value == value)) alts.Add((lang, value));
        }

        if (!_props.TryGetValue(key, out var existing) || string.IsNullOrEmpty(existing))
            _props[key] = value;
    }

    private string? PreferredAlt(string key)
    {
        if (_alt.TryGetValue(key, out var alts) && alts.Count > 0)
        {
            foreach (var (lang, value) in alts)
                if (string.Equals(lang, "x-default", StringComparison.OrdinalIgnoreCase)) return value;
            return alts[0].Value;
        }
        return Get(key);
    }

    private string? FirstValue(string key)
    {
        var values = GetValues(key);
        return values.Count > 0 ? values[0] : Get(key);
    }

    // ============================================================== 文本 / 分类

    public string? Title => PreferredAlt("dc:title");
    public string? Caption => PreferredAlt("dc:description") ?? Get("photoshop:Headline");
    public string? Headline => Get("photoshop:Headline");
    public string? Creator => FirstValue("dc:creator") ?? Get("photoshop:Credit");
    public string? Copyright => PreferredAlt("dc:rights");
    public string? Software => Get("xmp:CreatorTool") ?? Get("tiff:Software") ?? Get("exif:Software");
    /// <summary>x:xmpmeta/@x:xmptk —— 写包工具的原始标识（"Adobe Lightroom" / "Capture One 23" …）。</summary>
    public string? Toolkit => Get("x:xmptk");
    public string? Instructions => Get("photoshop:Instructions");

    public IReadOnlyList<string> Keywords
    {
        get
        {
            var list = new List<string>();
            foreach (var k in GetValues("dc:subject").Concat(GetValues("lr:hierarchicalSubject")))
            {
                var v = k.Replace("|", " / ").Trim();
                if (v.Length == 0) continue;
                if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
            }
            return list;
        }
    }

    public int? Rating
    {
        get
        {
            var raw = Get("xmp:Rating") ?? Get("xmp:RatingPercent");
            if (raw is null) return null;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return null;
            return Math.Clamp(r, 0, 5);
        }
    }

    public string? ColorLabel
    {
        get
        {
            var raw = Get("xmp:Label");
            return string.IsNullOrWhiteSpace(raw) ? null : NormalizeLabel(raw);
        }
    }

    public bool? IsPick
    {
        get
        {
            var raw = Get("xmp:Pick") ?? Get("xmp:PickFlag") ?? Get("xmp:IsPick");
            return ParseBool(raw);
        }
    }

    private static string NormalizeLabel(string raw)
    {
        var t = raw.Trim();
        // Lightroom 的本地化标签（"红"、"蓝色"）也认；未知原样返回
        var lower = t.ToLowerInvariant();
        return lower switch
        {
            "red" or "红" or "红色" => "Red",
            "yellow" or "黄" or "黄色" => "Yellow",
            "green" or "绿" or "绿色" => "Green",
            "blue" or "蓝" or "蓝色" => "Blue",
            "purple" or "紫" or "紫色" => "Purple",
            "none" or "无" => "None",
            _ => t,
        };
    }

    // ============================================================== 地点

    public string? City => Get("photoshop:City") ?? Get("iptc:City") ?? Get("exif:GPSAreaInformation");
    public string? Province => Get("photoshop:State") ?? Get("iptc:ProvinceState");
    public string? Country => Get("photoshop:Country") ?? Get("iptc:Country");
    public string? CountryCode => Get("photoshop:CountryCode") ?? Get("iptc:CountryCode");

    public double? GpsLatitude => ParseGps(Get("exif:GPSLatitude"), Get("exif:GPSLatitudeRef"), 'N', 90);
    public double? GpsLongitude => ParseGps(Get("exif:GPSLongitude"), Get("exif:GPSLongitudeRef"), 'E', 180);

    public double? GpsAltitude
    {
        get
        {
            var raw = Get("exif:GPSAltitude");
            if (raw is null) return null;
            raw = raw.Replace("m", "", StringComparison.OrdinalIgnoreCase).Replace("above sea level", "", StringComparison.OrdinalIgnoreCase);
            var v = MetaUtil.ParseDouble(raw);
            if (v is null) return null;
            var below = (Get("exif:GPSAltitudeRef") ?? "").Trim() is "1" or "2";
            return below ? -v.Value : v.Value;
        }
    }

    private static double? ParseGps(string? text, string? reference, char positiveHemisphere, double limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        s = s.Replace('，', ',').Replace('°', ' ').Replace('\'', ' ').Replace('"', ' ')
             .Replace('\u2032', ' ').Replace('\u2033', ' ');

        var hemisphere = reference?.Trim().ToUpperInvariant() is { Length: > 0 } r
            ? r[0]
            : '\0';

        // 末尾带 N/S/E/W，例如 "31,14.584N"
        var trimmed = s.TrimEnd();
        if (trimmed.Length > 0 && char.IsLetter(trimmed[^1]))
        {
            var h = char.ToUpperInvariant(trimmed[^1]);
            if (h is 'N' or 'S' or 'E' or 'W') hemisphere = h;
            s = trimmed[..^1];
        }

        var matches = Regex.Matches(s, @"[-+]?\d+(?:[.,]\d+)?");
        if (matches.Count == 0) return null;

        var parts = new double[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            if (!double.TryParse(matches[i].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
                return null;
        }

        double value;
        if (parts.Length >= 3) value = Math.Abs(parts[0]) + Math.Abs(parts[1]) / 60.0 + Math.Abs(parts[2]) / 3600.0;
        else if (parts.Length == 2) value = Math.Abs(parts[0]) + Math.Abs(parts[1]) / 60.0;
        else value = Math.Abs(parts[0]);

        // 负号 / 半球决定正负
        var negative = matches[0].Value.StartsWith('-') || hemisphere is 'S' or 'W';
        if (!negative && hemisphere != '\0' && hemisphere != positiveHemisphere) negative = true;
        value = negative ? -value : value;
        return Math.Abs(value) <= limit ? value : null;
    }

    // ============================================================== 设备 / 曝光

    public string? Make => Get("tiff:Make") ?? Get("exif:Make") ?? Get("aux:Make");
    public string? Model => Get("tiff:Model") ?? Get("exif:Model") ?? Get("aux:Model");
    public string? Lens => Get("aux:Lens") ?? Get("exifEX:LensModel") ?? Get("exif:LensModel") ?? Get("tiff:LensModel");
    public string? LensMake => Get("aux:LensMake") ?? Get("exifEX:LensMake");
    public string? LensId => Get("aux:LensID") ?? Get("exif:LensSpecification");

    public double? FocalLengthMm
    {
        get
        {
            var v = MetaUtil.ParseDouble(Get("exif:FocalLength"));
            if (v is > 0) return v;
            // exif:FocalLength 有时是 rational 文本 "70/1"
            return null;
        }
    }

    public double? FocalLength35mm
    {
        get
        {
            var raw = Get("exif:FocalLengthIn35mmFormat") ?? Get("exif:FocalLengthIn35mmFilm") ?? Get("aux:ApproximateFocusDistance");
            var v = MetaUtil.ParseDouble(raw);
            return v is > 0 ? v : null;
        }
    }

    public double? FNumber => MetaUtil.ParseDouble(Get("exif:FNumber")) is { } f && f > 0 ? f : null;

    public double? ExposureTimeSeconds
    {
        get
        {
            var v = MetaUtil.ParseDouble(Get("exif:ExposureTime"));
            return v is > 0 ? v : null;
        }
    }

    public int? Iso
    {
        get
        {
            foreach (var raw in GetValues("exif:ISOSpeedRatings")
                         .Concat(GetValues("exif:PhotographicSensitivity"))
                         .Concat(new[] { Get("exifEX:PhotographicSensitivity") ?? "", Get("exif:ISOSpeed") ?? "" }))
            {
                if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iso) && iso > 0)
                    return iso;
            }
            return null;
        }
    }

    public string? ExposureProgram => Get("exif:ExposureProgram");
    public string? WhiteBalance => Get("crs:WhiteBalance") ?? Get("exif:WhiteBalance");
    public string? ColorSpace => Get("exif:ColorSpace") ?? Get("photoshop:ColorMode");

    // ============================================================== 时间

    /// <summary>XMP 里最可信的拍摄时间文本（按优先级）。</summary>
    public string? DateCreatedRaw =>
        Get("photoshop:DateCreated") ?? Get("exif:DateTimeOriginal") ??
        Get("exif:DateTimeDigitized") ?? Get("xmp:CreateDate");

    public DateTimeOffset? CapturedAt
    {
        get
        {
            var raw = DateCreatedRaw;
            if (raw is null) return null;
            var offset = Get("exif:OffsetTimeOriginal") ?? Get("exif:OffsetTime") ?? Get("exif:OffsetTimeDigitized");
            var parsed = MetaUtil.ParseExifDate(raw, offset) ?? TimeUtil.ParseFlexible(raw);
            if (parsed is null) return null;
            // 明显不合理的年份（相机未设置时间）当没有
            var y = parsed.Value.Year;
            return y is >= 1900 and <= 2200 ? parsed : null;
        }
    }

    // ============================================================== 后期 / 色彩

    /// <summary>crs:HasSettings —— Lightroom/Camera Raw 的核心"已改过"标志。</summary>
    public bool? HasDevelopSettings => ParseBool(Get("crs:HasSettings"));

    public string? ProcessVersion => Get("crs:ProcessVersion") ?? Get("crs:Version");

    /// <summary>crs:CameraProfile（Adobe Standard / Camera Standard / S-Log3 …）。</summary>
    public string? CameraProfile => Get("crs:CameraProfile");

    public string? CameraProfileDigest => Get("crs:CameraProfileDigest");

    public double? TemperatureK
    {
        get
        {
            var v = MetaUtil.ParseDouble(Get("crs:Temperature"));
            return v is >= 1000 and <= 50000 ? v : null;
        }
    }

    public double? Tint => MetaUtil.ParseDouble(Get("crs:Tint"));

    public string? LookName => Get("crs:LookName") ?? Get("crs:Look");

    // ============================================================== 工具

    private static bool? ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    /// <summary>把本 XMP 包的内容灌进 <see cref="MediaMetadata"/>。技术字段只在目标为空时写；</summary>
    /// <param name="overwriteText">true = 文本/评分/地点等"用户编辑过的软信息"覆盖已有值（边车优先于内嵌）。</param>
    public void ApplyTo(MediaMetadata m, bool overwriteText = false)
    {
        if (m is null) return;

        var title = Title;
        if (title is not null && (overwriteText || string.IsNullOrEmpty(m.Title))) m.Title = title;
        var caption = Caption;
        if (caption is not null && (overwriteText || string.IsNullOrEmpty(m.Caption))) m.Caption = caption;
        var creator = Creator;
        if (creator is not null && (overwriteText || string.IsNullOrEmpty(m.Creator))) m.Creator = creator;
        var copyright = Copyright;
        if (copyright is not null && (overwriteText || string.IsNullOrEmpty(m.Copyright))) m.Copyright = copyright;

        foreach (var k in Keywords)
            if (!m.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) m.Keywords.Add(k);

        if (Rating is { } rating && (overwriteText || m.Rating is null)) m.Rating = rating;
        if (ColorLabel is { } label && (overwriteText || string.IsNullOrEmpty(m.ColorLabel))) m.ColorLabel = label;
        if (IsPick is { } pick && (overwriteText || m.IsPick is null)) m.IsPick = pick;

        if (City is { } city && (overwriteText || string.IsNullOrEmpty(m.City)))
        {
            m.City = city;
            m.CitySource = "xmp";       // 文件元数据来源（XMP photoshop:City）
        }
        if (Province is { } province && (overwriteText || string.IsNullOrEmpty(m.Province))) m.Province = province;
        if (Country is { } country && (overwriteText || string.IsNullOrEmpty(m.Country))) m.Country = country;

        if (m.CapturedAt is null && CapturedAt is { } captured)
        {
            m.CapturedAt = captured;
            m.CapturedAtSource ??= "xmp";
        }

        if (m.Latitude is null && GpsLatitude is { } lat && GpsLongitude is { } lon)
        {
            m.Latitude = lat;
            m.Longitude = lon;
            if (m.AltitudeMeters is null && GpsAltitude is { } alt) m.AltitudeMeters = alt;
        }

        // 技术字段：内嵌 EXIF 更权威，XMP 只补空位
        m.CameraMake ??= MetaUtil.CleanMake(Make);
        m.CameraModel ??= MetaUtil.CleanText(Model, 128);
        if (m.CameraMake is null && Make is not null) m.CameraMake = MetaUtil.CleanMake(Make);
        if (m.CameraModel is null && Model is not null) m.CameraModel = MetaUtil.CleanText(Model, 128);
        m.LensModel ??= MetaUtil.CleanText(Lens, 256);
        m.FocalLengthMm ??= FocalLengthMm;
        m.ApertureF ??= FNumber;
        m.ShutterSpeedSeconds ??= ExposureTimeSeconds;
        m.Iso ??= Iso;
        m.ColorTemperatureK ??= TemperatureK;

        if (HasDevelopSettings == true) m.HasDevelopEdits = true;
    }

    public override string ToString() => $"XmpData({_props.Count} props)";
}

/// <summary>
/// XMP（RDF/XML）读取器。可独立用于 JPEG APP1、TIFF tag 700、MP4 uuid、HEIC item、.xmp 边车。
/// 只做只读解析，线程安全。解析失败时降级为宽松正则提取并记录 <see cref="XmpData.ParseError"/>。
/// </summary>
public static class XmpReader
{
    private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const string XapPacketHeader = "http://ns.adobe.com/xap/1.0/";
    private const int MaxSidecarBytes = 16 * 1024 * 1024;

    /// <summary>命名空间 URI → 规范前缀。磁盘上的前缀可能被改写成任意名字，这里统一回来。</summary>
    private static readonly Dictionary<string, string> NsToPrefix = new(StringComparer.Ordinal)
    {
        ["http://purl.org/dc/elements/1.1/"] = "dc",
        ["http://ns.adobe.com/xap/1.0/"] = "xmp",
        ["http://ns.adobe.com/photoshop/1.0/"] = "photoshop",
        ["http://ns.adobe.com/camera-raw-settings/1.0/"] = "crs",
        ["http://ns.adobe.com/camera-raw-embedded-settings/1.0/"] = "crss",
        ["http://ns.adobe.com/exif/1.0/"] = "exif",
        ["http://cipa.jp/exif/1.0/"] = "exifEX",
        ["http://ns.adobe.com/tiff/1.0/"] = "tiff",
        ["http://ns.adobe.com/exif/1.0/aux/"] = "aux",
        ["http://ns.adobe.com/lightroom/1.0/"] = "lr",
        ["http://ns.adobe.com/xap/1.0/mm/"] = "xmpMM",
        ["http://ns.adobe.com/xap/1.0/rights/"] = "xmpRights",
        ["http://ns.adobe.com/xap/1.0/note/"] = "xmpNote",
        ["http://ns.adobe.com/xmp/1.0/DynamicMedia/"] = "xmpDM",
        ["http://ns.adobe.com/xmp/Identifier/qual/1.0/"] = "xmpidq",
        ["http://ns.adobe.com/pdf/1.3/"] = "pdf",
        ["http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/"] = "iptcCore",
        ["http://iptc.org/std/Iptc4xmpExt/2008-02-29/"] = "iptcExt",
        ["http://ns.google.com/photos/1.0/container/"] = "GCamera",
        ["http://ns.google.com/photos/1.0/image/"] = "GImage",
        ["http://ns.google.com/photos/1.0/depthmap/"] = "GDepth",
        ["http://ns.adobe.com/album/1.0/"] = "album",
        ["http://ns.adobe.com/camera-raw-settings/1.0/"] = "crs",
        ["http://ns.apple.com/faceinfo/1.0/"] = "apple-fi",
        ["http://ns.apple.com/person/1.0/"] = "apple-person",
        ["http://ns.adobe.com/xap/1.0/t/pg/"] = "xmpTPg",
        ["http://ns.adobe.com/xap/1.0/sType/ResourceRef#"] = "stRef",
        ["http://ns.adobe.com/xap/1.0/sType/Dimensions#"] = "stDim",
        ["http://ns.adobe.com/xap/1.0/sType/Font#"] = "stFnt",
        ["http://ns.adobe.com/xap/1.0/g/img/"] = "xmpGImg",
        ["http://ns.adobe.com/dc/dc/"] = "dc",
        ["adobe:ns:meta/"] = "x",
    };

    private static readonly HashSet<string> ContainerLocals = new(StringComparer.Ordinal)
    {
        "Bag", "Seq", "Alt", "Description", "RDF", "xmpmeta", "XMP", "Value", "Struct", "Field",
    };

    private sealed class ElemCtx
    {
        public string Local = "";
        public string Prefix = "";
        public string? Key;
        public string? Lang;
        public string? Resource;
        public bool HasChild;
        public readonly StringBuilder Text = new();
    }

    // ============================================================ 公开 API

    /// <summary>解析 UTF-8/UTF-16 的 XMP 包字节（可带 APP1 命名空间前缀、xpacket 包装）。</summary>
    public static XmpData Parse(ReadOnlySpan<byte> bytes)
    {
        var data = new XmpData();
        if (bytes.Length == 0) return data;
        var xml = DecodePacket(bytes);
        if (string.IsNullOrWhiteSpace(xml)) return data;
        ParseXmlLayers(xml, data);
        return data;
    }

    public static XmpData Parse(string? xml)
    {
        var data = new XmpData();
        if (string.IsNullOrWhiteSpace(xml)) return data;
        ParseXmlLayers(xml, data);
        return data;
    }

    /// <summary>从一个 .xmp 边车文件读（最多读取 16 MB，防御异常大文件）。</summary>
    public static XmpData ParseFile(string path)
    {
        var data = new XmpData();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return data;
        try
        {
            using var fs = MetaUtil.OpenSequential(path);
            var len = (int)Math.Min(fs.Length, MaxSidecarBytes);
            if (len <= 0) return data;
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return data;
            return Parse(buf.AsSpan(0, read));
        }
        catch (Exception ex)
        {
            Log.Warn($"[XmpReader] 读取边车失败: {path}", ex);
            data.ParseError = ex.Message;
            return data;
        }
    }

    /// <summary>粗判一段字节是否像 XMP（用于在 JPEG/MP4/HEIC 里定位）。</summary>
    public static bool LooksLikeXmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        return data.IndexOf("x:xmpmeta"u8) >= 0 || data.IndexOf("xmpmeta"u8) >= 0 ||
               data.IndexOf("xpacket"u8) >= 0 || data.IndexOf("rdf:RDF"u8) >= 0;
    }

    // ============================================================ 解码

    private static string? DecodePacket(ReadOnlySpan<byte> bytes)
    {
        // APP1 前缀："http://ns.adobe.com/xap/1.0/\0"
        if (bytes.Length > XapPacketHeader.Length + 1)
        {
            var head = bytes[..Math.Min(64, bytes.Length)];
            ReadOnlySpan<byte> xapHeader = "http://ns.adobe.com/xap/1.0/"u8;
            if (head.StartsWith(xapHeader))
            {
                var nul = head.IndexOf((byte)0);
                bytes = bytes[(nul >= 0 ? nul + 1 : XapPacketHeader.Length)..];
            }
            else if (head.StartsWith("http://ns.adobe.com/xmp/extension/"u8))
            {
                var nul = head.IndexOf((byte)0);
                bytes = bytes[(nul >= 0 ? nul + 1 : 0)..];
            }
        }

        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes[2..]);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        else if (LooksUtf16(bytes))
            text = Encoding.Unicode.GetString(bytes);
        else
            text = Encoding.UTF8.GetString(bytes);

        // 裁到第一个 '<' 和最后一个 '>'
        var start = text.IndexOf('<');
        var end = text.LastIndexOf('>');
        if (start < 0 || end <= start) return MetaUtil.CleanText(text, 4096);
        return text[start..(end + 1)];
    }

    private static bool LooksUtf16(ReadOnlySpan<byte> b)
    {
        var n = Math.Min(b.Length, 512);
        if (n < 8) return false;
        var zeroAtOdd = 0;
        var zeroAtEven = 0;
        for (var i = 0; i < n; i++)
        {
            if (b[i] != 0) continue;
            if ((i & 1) == 1) zeroAtOdd++; else zeroAtEven++;
        }
        return zeroAtEven > n / 8 && zeroAtEven > zeroAtOdd * 2;
    }

    // ============================================================ XML 解析

    private static void ParseXmlLayers(string xml, XmpData data)
    {
        // 常见情况：单根 xmpmeta。多层 XMP 偶尔被拼在同一个缓冲区里，按 xmpmeta 边界切开逐层解析。
        var layers = SplitLayers(xml);
        foreach (var layer in layers)
        {
            try { ParseXmlLayer(layer, data); }
            catch (Exception ex) { data.ParseError ??= ex.Message; }
        }
        if (data.IsEmpty && data.ParseError is not null)
            ParseLoose(xml, data);
    }

    private static List<string> SplitLayers(string xml)
    {
        var layers = new List<string>(1);
        var search = 0;
        while (search < xml.Length)
        {
            var open = xml.IndexOf("<x:xmpmeta", search, StringComparison.Ordinal);
            if (open < 0) open = xml.IndexOf("<xmpmeta", search, StringComparison.Ordinal);
            if (open < 0) break;
            var close = xml.IndexOf("</x:xmpmeta>", open, StringComparison.Ordinal);
            var closeLen = "</x:xmpmeta>".Length;
            if (close < 0)
            {
                close = xml.IndexOf("</xmpmeta>", open, StringComparison.Ordinal);
                closeLen = "</xmpmeta>".Length;
            }
            if (close < 0)
            {
                layers.Add(xml[open..]);
                break;
            }
            layers.Add(xml[open..(close + closeLen)]);
            search = close + closeLen;
        }
        if (layers.Count == 0) layers.Add(xml);
        return layers;
    }

    private static void ParseXmlLayer(string xml, XmpData data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CheckCharacters = false,
            CloseInput = true,
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var stack = new List<ElemCtx>(16);

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var local = reader.LocalName;
                    var prefix = CanonicalPrefix(reader.NamespaceURI, reader.Prefix);
                    var ctx = new ElemCtx { Local = local, Prefix = prefix };

                    // rdf:Description 上的属性式属性（xmp:Rating="5" photoshop:City="…"）
                    if (reader.HasAttributes)
                    {
                        reader.MoveToFirstAttribute();
                        do
                        {
                            var ans = reader.NamespaceURI;
                            var alocal = reader.LocalName;
                            var aprefix = reader.Prefix;
                            var value = reader.Value;
                            if (ans == RdfNs)
                            {
                                if (alocal == "resource") ctx.Resource = value;
                                continue; // rdf:about / parseType / nodeID 不是属性值
                            }
                            if (ans == XmlNs || aprefix == "xml") continue;
                            if (ans.Length == 0 && aprefix.Length == 0) continue;
                            var key = CanonicalPrefix(ans, aprefix) + ":" + alocal;
                            data.AddValue(key, value);
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    ctx.Lang = reader.GetAttribute("lang", XmlNs);
                    ctx.Key = ResolveKey(local, prefix, stack, out var isContainer);
                    _ = isContainer;

                    if (reader.IsEmptyElement)
                    {
                        if (ctx.Key is not null && !string.IsNullOrEmpty(ctx.Resource))
                            data.AddValue(ctx.Key, ctx.Resource, ctx.Lang);
                        // 空元素不推栈
                    }
                    else
                    {
                        if (stack.Count > 0) stack[^1].HasChild = true;
                        stack.Add(ctx);
                    }
                    break;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    if (stack.Count > 0) stack[^1].Text.Append(reader.Value);
                    break;

                case XmlNodeType.EndElement:
                {
                    if (stack.Count == 0) continue;
                    var ctx = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (ctx.Key is null || ctx.HasChild) continue;
                    var text = MetaUtil.CleanText(ctx.Text.ToString());
                    if (text is not null) data.AddValue(ctx.Key, text, ctx.Lang);
                    else if (!string.IsNullOrEmpty(ctx.Resource)) data.AddValue(ctx.Key, ctx.Resource, ctx.Lang);
                    break;
                }
            }
        }
    }

    private static string? ResolveKey(string local, string prefix, List<ElemCtx> stack, out bool isContainer)
    {
        isContainer = false;
        if (local is "li")
        {
            // rdf:li 的值归属于它外层最近的属性元素（dc:subject / dc:creator / dc:title …）
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                var p = stack[i];
                if (p.Key is not null) return p.Key;
                if (ContainerLocals.Contains(p.Local)) continue;
                if (!string.Equals(p.Prefix, "rdf", StringComparison.Ordinal) && !string.IsNullOrEmpty(p.Prefix))
                    return p.Prefix + ":" + p.Local;
            }
            return null;
        }

        if (ContainerLocals.Contains(local) || string.Equals(prefix, "rdf", StringComparison.Ordinal))
        {
            isContainer = true;
            return null;
        }

        if (string.IsNullOrEmpty(prefix)) return null;
        return prefix + ":" + local;
    }

    private static string CanonicalPrefix(string ns, string declaredPrefix)
    {
        if (string.IsNullOrEmpty(ns)) return declaredPrefix ?? "";
        if (NsToPrefix.TryGetValue(ns, out var p)) return p;
        if (ns == XmlNs) return "xml";
        if (string.IsNullOrEmpty(declaredPrefix) || declaredPrefix is "xmlns")
            return "ns" + Math.Abs(ns.GetHashCode(StringComparison.Ordinal)) % 1000;
        return declaredPrefix;
    }

    // ============================================================ 宽松降级解析

    private static readonly Regex LooseElement = new(
        @"<(?<p>[A-Za-z_][\w.\-]*):(?<l>[A-Za-z_][\w.\-]*)(?<attrs>[^>]*)>(?<v>[^<>]*)</\k<p>:\k<l>>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LooseAttr = new(
        @"(?<p>[A-Za-z_][\w.\-]*):(?<l>[A-Za-z_][\w.\-]*)\s*=\s*""(?<v>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ParseLoose(string xml, XmpData data)
    {
        try
        {
            foreach (Match m in LooseElement.Matches(xml))
            {
                var key = CanonicalLoosePrefix(m.Groups["p"].Value) + ":" + m.Groups["l"].Value;
                data.AddValue(key, System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value));
            }
            foreach (Match m in LooseAttr.Matches(xml))
            {
                var p = m.Groups["p"].Value;
                if (p is "xmlns" or "xml") continue;
                var key = CanonicalLoosePrefix(p) + ":" + m.Groups["l"].Value;
                data.AddValue(key, System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value));
            }
        }
        catch (Exception ex)
        {
            data.ParseError ??= ex.Message;
        }
    }

    private static string CanonicalLoosePrefix(string p) => p.ToLowerInvariant() switch
    {
        "xap" => "xmp",
        "ps" => "photoshop",
        "crs" or "crd" => "crs",
        "exif" => "exif",
        "exifex" => "exifEX",
        "tiff" => "tiff",
        "aux" => "aux",
        "lr" => "lr",
        _ => p,
    };
}

/// <summary>把 XMP 的原始文本字段安全地转成 <see cref="MediaMetadata"/>（带边车优先策略）。</summary>
public static class XmpApplier
{
    /// <summary>
    /// 内嵌 XMP：默认只补空位（EXIF/IPTC 先到先得）。
    /// <paramref name="preferOverOtherText"/>=true 时文本/评分/地点以 XMP 为准（JPEG 等"EXIF+IPTC+XMP 同时存在"
    /// 的场景，XMP 是更现代的权威来源）。
    /// </summary>
    public static void ApplyEmbedded(XmpData? xmp, MediaMetadata m, bool preferOverOtherText = false)
    {
        if (xmp is null || xmp.IsEmpty) return;
        try
        {
            xmp.ApplyTo(m, overwriteText: preferOverOtherText);
            m.HasEmbeddedXmp = true;
            ApplyDevelopState(xmp, m, embedded: true);
        }
        catch (Exception ex)
        {
            m.Warnings.Add("XMP 应用失败: " + ex.Message);
            Log.Warn($"[XmpReader] 内嵌 XMP 应用失败: {m.SourcePath}", ex);
        }
    }

    /// <summary>边车 XMP：文本/评分/地点等软信息覆盖内嵌值；技术字段仍只补空位。</summary>
    public static void ApplySidecar(XmpData? xmp, MediaMetadata m)
    {
        if (xmp is null || xmp.IsEmpty) return;
        try
        {
            xmp.ApplyTo(m, overwriteText: true);
            ApplyDevelopState(xmp, m, embedded: false);
        }
        catch (Exception ex)
        {
            m.Warnings.Add("边车 XMP 应用失败: " + ex.Message);
            Log.Warn($"[XmpReader] 边车 XMP 应用失败: {m.SourcePath}", ex);
        }
    }

    /// <summary>根据 crs:HasSettings / ProcessVersion 判断"是否已后期"，并推断后期引擎。</summary>
    private static void ApplyDevelopState(XmpData xmp, MediaMetadata m, bool embedded)
    {
        var hasSettings = xmp.HasDevelopSettings == true;
        var hasProcessVersion = xmp.ProcessVersion is not null;
        if (!hasSettings && !hasProcessVersion && !xmp.HasCrsSection) return;
        if (hasSettings || hasProcessVersion) m.HasDevelopEdits = true;

        var guessed = SidecarScanner.GuessEngineFromXmp(xmp);
        m.DevelopEngine ??= guessed switch
        {
            "Lightroom" => embedded ? "Lightroom" : "Adobe Lightroom",
            "Capture One" => "CaptureOne",
            null when xmp.HasCrsSection => embedded ? "Lightroom" : "Adobe Lightroom",
            _ => guessed,
        };
    }
}