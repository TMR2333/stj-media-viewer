using System.Buffers.Binary;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Util;
using MeReader = MetadataExtractor.ImageMetadataReader;
using Directory = MetadataExtractor.Directory;

namespace StjMediaViewer.Core.Metadata;

/// <summary>
/// 图片元数据读取器：
/// - EXIF（Make/Model/Lens/曝光/时间/偏移/GPS/描述/版权/评分）来自 MetadataExtractor；
/// - IPTC、ICC 作为补充；
/// - 内嵌 XMP 用自研 <see cref="XmpReader"/> 解析（JPEG APP1 / TIFF tag 700 / PNG iTXt / WebP / HEIC）；
/// - MetadataExtractor 失败时用自研 TIFF 解析器兜底。
/// </summary>
public static class ImageMetadataReader
{
    // ---- 自研兜底用到的 TIFF 标签号（与 TIFF/EXIF 规范一致，避免依赖第三方常量名） ----
    private const int TagImageWidth = 0x0100;
    private const int TagImageHeight = 0x0101;
    private const int TagBitsPerSample = 0x0102;
    private const int TagImageDescription = 0x010E;
    private const int TagMake = 0x010F;
    private const int TagModel = 0x0110;
    private const int TagArtist = 0x013B;
    private const int TagCopyright = 0x8298;
    private const int TagDateTimeOriginal = 0x9003;
    private const int TagDateTimeDigitized = 0x9004;
    private const int TagDateTime = 0x0132;
    private const int TagOffsetTimeOriginal = 0x9011;
    private const int TagOffsetTime = 0x9010;
    private const int TagExposureTime = 0x829A;
    private const int TagFNumber = 0x829D;
    private const int TagIso = 0x8827;
    private const int TagIsoSpeed = 0x8833;
    private const int TagFocalLength = 0x920A;
    private const int TagLensModel = 0xA434;
    private const int TagLensMake = 0xA433;
    private const int TagPixelXDimension = 0xA002;
    private const int TagPixelYDimension = 0xA003;
    private const int TagColorSpace = 0xA001;
    private const int TagXmp = 0x02BC;             // TIFF tag 700
    private const int TagSubIfds = 330;
    private const int TagExifIfd = 34665;
    private const int TagGpsIfd = 34853;

    /// <summary>读取图片（含 RAW）元数据，全部异常在内部吞掉并记录 warning。</summary>
    public static void Read(string fullPath, MediaMetadata m, CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<Directory> dirs;
            try
            {
                dirs = MeReader.ReadMetadata(fullPath);
            }
            catch (Exception ex)
            {
                m.Warnings.Add("EXIF 解析失败: " + ex.Message);
                Log.Warn($"[ImageMetadataReader] MetadataExtractor 失败: {fullPath}", ex);
                dirs = Array.Empty<Directory>();
            }

            int? pixelW = null, pixelH = null, frameW = null, frameH = null, ifd0W = null, ifd0H = null;
            int exifOrientation = 1;   // EXIF 0x0112：5–8 表示需要旋转 90/270，宽高要交换
            var sawIcc = false;

            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (dir.GetType().Name)
                    {
                        case "ExifIfd0Directory":
                        {
                            // EXIF 0x0112 Orientation：5–8 表示需要旋转 90/270，宽高要交换
                            var exifOrientationTag = TryInt(dir, ExifDirectoryBase.TagOrientation);
                            if (exifOrientationTag is >= 1 and <= 8) exifOrientation = exifOrientationTag.Value;

                            ReadIfd0(dir, m, ref ifd0W, ref ifd0H, out var xmpBytes);
                            if (xmpBytes is not null) XmpApplier.ApplyEmbedded(XmpReader.Parse(xmpBytes), m, preferOverOtherText: true);
                            break;
                        }
                        case "ExifSubIfdDirectory":
                            ReadSubIfd(dir, m, ref pixelW, ref pixelH);
                            break;
                        case "GpsDirectory":
                            ReadGps(dir, m);
                            break;
                        case "IptcDirectory":
                            ReadIptc(dir, m);
                            break;
                        case "IccDirectory":
                            ReadIcc(dir, m);
                            sawIcc = true;
                            break;
                        case "JpegDirectory":
                        {
                            var jd = (JpegDirectory)dir;
                            if (jd.GetImageWidth() > 0) frameW = jd.GetImageWidth();
                            if (jd.GetImageHeight() > 0) frameH = jd.GetImageHeight();
                            var precision = TryInt(jd, JpegDirectory.TagDataPrecision);
                            if (precision is > 0 and <= 64) m.BitDepth ??= precision;
                            break;
                        }
                        case "PngDirectory":
                        {
                            var w = TryInt(dir, PngDirectory.TagImageWidth);
                            var h = TryInt(dir, PngDirectory.TagImageHeight);
                            if (w is > 0) frameW = w;
                            if (h is > 0) frameH = h;
                            var bits = TryInt(dir, PngDirectory.TagBitsPerSample);
                            if (bits is > 0 and <= 64) m.BitDepth ??= bits;
                            var iccName = TryString(dir, PngDirectory.TagIccProfileName);
                            if (!string.IsNullOrWhiteSpace(iccName)) m.ColorProfileName ??= MetaUtil.CleanText(iccName);
                            break;
                        }
                        case "WebPDirectory":
                            if (TryInt(dir, WebPDirectory.TagImageWidth) is > 0 and var w2) frameW = w2;
                            if (TryInt(dir, WebPDirectory.TagImageHeight) is > 0 and var h2) frameH = h2;
                            break;
                        case "XmpDirectory":
                            // MetadataExtractor 的 XMP 只作最后兜底（键名格式随版本变化）
                            if (!m.HasEmbeddedXmp) IngestDirectoryXmp(dir, m);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    m.Warnings.Add($"元数据目录 {dir.Name} 解析失败: {ex.Message}");
                }
            }

            // ---- 自研内嵌 XMP 提取（不依赖第三方，覆盖 JPEG APP1 / TIFF 700 / PNG / WebP） ----
            ExtractEmbeddedXmp(fullPath, m, ct);

            // ---- 尺寸决策 ----
            if (m.Kind == MediaKind.Image && m.RawFormat != RawFormat.None)
            {
                // RAW：优先 TIFF 结构里的全尺寸（SubIFD 的 ImageWidth/Height），其次 EXIF 像素尺寸，最后缩略图
                var (rawW, rawH) = FindRawDimensions(fullPath, ct);
                if (rawW is > 0 && rawH is > 0)
                {
                    m.Width ??= rawW;
                    m.Height ??= rawH;
                }
            }

            m.Width ??= pixelW ?? frameW ?? ifd0W;
            m.Height ??= pixelH ?? frameH ?? ifd0H;

            // 如果只有 JPEG 帧尺寸而 EXIF 像素尺寸更大（RAW 常见），取更大者
            if (m.Width is { } cw && m.Height is { } ch)
            {
                if (pixelW is > 0 && pixelH is > 0 && (long)pixelW * pixelH > (long)cw * ch)
                {
                    m.Width = pixelW;
                    m.Height = pixelH;
                }
                if (frameW is > 0 && frameH is > 0 && (long)frameW * frameH > (long)m.Width * m.Height)
                {
                    m.Width = frameW;
                    m.Height = frameH;
                }
            }

            // EXIF 方向 5–8 = 需要旋转 90/270：宽高交换成正立后的尺寸（Photos/资源管理器也是这么显示）
            if (exifOrientation is 5 or 6 or 7 or 8 && m.Width is { } ow && m.Height is { } oh && ow != oh)
            {
                m.Width = oh;
                m.Height = ow;
            }

            if (!sawIcc && m.ColorProfileName is null && m.Kind == MediaKind.Image &&
                TryGetEmbeddedIccName(fullPath, ct) is { } iccFallback)
                m.ColorProfileName = iccFallback;
        }
        catch (OperationCanceledException)
        {
            m.Warnings.Add("图片解析被取消");
        }
        catch (Exception ex)
        {
            m.Warnings.Add("图片元数据解析失败: " + ex.Message);
            Log.Warn($"[ImageMetadataReader] 解析失败: {fullPath}", ex);
        }
    }

    // ============================================================ EXIF 目录映射

    private static void ReadIfd0(Directory dir, MediaMetadata m, ref int? width, ref int? height, out byte[]? xmpBytes)
    {
        xmpBytes = null;
        m.CameraMake ??= MetaUtil.CleanText(TryString(dir, ExifDirectoryBase.TagMake), 128);
        m.CameraModel ??= MetaUtil.CleanText(TryString(dir, ExifDirectoryBase.TagModel), 128);
        var desc = TryString(dir, ExifDirectoryBase.TagImageDescription);
        if (!string.IsNullOrWhiteSpace(desc)) m.Caption ??= MetaUtil.CleanText(desc);
        m.Creator ??= MetaUtil.CleanText(TryString(dir, ExifDirectoryBase.TagArtist), 256);
        m.Copyright ??= MetaUtil.CleanText(TryString(dir, ExifDirectoryBase.TagCopyright), 512);

        if (TryInt(dir, ExifDirectoryBase.TagRating) is { } rating) m.Rating ??= Math.Clamp(rating, 0, 5);
        else if (TryInt(dir, ExifDirectoryBase.TagRatingPercent) is { } pct) m.Rating ??= Math.Clamp(pct / 20, 0, 5);

        width = TryInt(dir, ExifDirectoryBase.TagImageWidth);
        height = TryInt(dir, ExifDirectoryBase.TagImageHeight);

        // TIFF tag 700 = XMP 包
        try
        {
            var raw = dir.GetByteArray(TagXmp);
            if (raw is { Length: > 16 } && XmpReader.LooksLikeXmp(raw)) xmpBytes = raw;
        }
        catch { /* 标签缺失/类型不符 */ }
    }

    private static void ReadSubIfd(Directory dir, MediaMetadata m, ref int? pixelW, ref int? pixelH)
    {
        var date = TryString(dir, ExifDirectoryBase.TagDateTimeOriginal);
        var offset = TryString(dir, ExifDirectoryBase.TagTimeZoneOriginal) ?? TryString(dir, ExifDirectoryBase.TagTimeZone);
        var captured = MetaUtil.ParseExifDate(date, offset);
        if (captured is null)
        {
            var digitized = TryString(dir, ExifDirectoryBase.TagDateTimeDigitized);
            captured = MetaUtil.ParseExifDate(digitized, offset);
            if (captured is not null) m.CapturedAtSource ??= "exif:DateTimeDigitized";
        }
        else
        {
            m.CapturedAtSource ??= "exif:DateTimeOriginal";
        }
        if (m.CapturedAt is null && captured is not null) m.CapturedAt = captured;

        m.LensModel ??= MetaUtil.CleanText(TryString(dir, ExifDirectoryBase.TagLensModel), 256);

        if (TryRational(dir, ExifDirectoryBase.TagFocalLength) is { } focal && focal > 0) m.FocalLengthMm ??= focal;
        if (TryRational(dir, ExifDirectoryBase.TagFNumber) is { } fn && fn > 0) m.ApertureF ??= fn;
        if (TryRational(dir, ExifDirectoryBase.TagExposureTime) is { } exp && exp > 0) m.ShutterSpeedSeconds ??= exp;
        if (TryInt(dir, ExifDirectoryBase.TagIsoEquivalent) is { } iso1 && iso1 > 0) m.Iso ??= iso1;
        else if (TryInt(dir, ExifDirectoryBase.TagIsoSpeed) is { } iso2 && iso2 > 0) m.Iso ??= iso2;

        pixelW = TryInt(dir, ExifDirectoryBase.TagExifImageWidth);
        pixelH = TryInt(dir, ExifDirectoryBase.TagExifImageHeight);

        var colorSpace = TryInt(dir, ExifDirectoryBase.TagColorSpace);
        if (colorSpace == 1)
        {
            m.ColorProfileName ??= "sRGB";
            m.ColorPrimaries ??= "sRGB";
        }

        
    }

    private static void ReadGps(Directory dir, MediaMetadata m)
    {
        if (m.Latitude is not null) return;
        if (dir is not GpsDirectory gps) return;
        GeoLocation? geo;
        try { geo = gps.GetGeoLocation(); }
        catch { geo = null; }
        if (geo is not { } g || g.IsZero) return;
        m.Latitude = g.Latitude;
        m.Longitude = g.Longitude;
        try
        {
            var alt = TryRational(dir, GpsDirectory.TagAltitude);
            if (alt is { } a)
            {
                var below = TryInt(dir, GpsDirectory.TagAltitudeRef) == 1;
                m.AltitudeMeters ??= below ? -a : a;
            }
        }
        catch { /* 无高度 */ }
    }

    private static void ReadIptc(Directory dir, MediaMetadata m)
    {
        var iptc = dir as IptcDirectory;
        if (iptc is null) return;
        m.Title ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagObjectName), 512);
        m.Caption ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagCaption), 4096);
        if (m.Caption is null) m.Caption = MetaUtil.CleanText(TryString(dir, IptcDirectory.TagHeadline), 1024);
        m.Creator ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagByLine), 256);
        m.Copyright ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagCopyrightNotice), 512);
        if (m.City is null && MetaUtil.CleanText(TryString(dir, IptcDirectory.TagCity), 128) is { } iptcCity)
        {
            m.City = iptcCity;
            m.CitySource ??= "iptc";    // 文件元数据来源（IPTC IIM）
        }
        m.Province ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagProvinceOrState), 128);
        m.Country ??= MetaUtil.CleanText(TryString(dir, IptcDirectory.TagCountryOrPrimaryLocationName), 128);
        try
        {
            var keywords = iptc.GetKeywords();
            if (keywords is not null)
            {
                foreach (var kw in keywords)
                {
                    var clean = MetaUtil.CleanText(kw, 128);
                    if (clean is not null && !m.Keywords.Contains(clean, StringComparer.OrdinalIgnoreCase))
                        m.Keywords.Add(clean);
                }
            }
        }
        catch { /* 忽略 */ }
    }

    private static void ReadIcc(Directory dir, MediaMetadata m)
    {
        string? name = null;
        try { name = TryString(dir, IccDirectory.TagTagDesc); } catch { }
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = dir.GetDescription(IccDirectory.TagTagDesc); } catch { }
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = TryString(dir, IccDirectory.TagDeviceModel); } catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        name = MetaUtil.CleanText(name, 256);
        if (string.IsNullOrEmpty(name)) return;
        m.ColorProfileName ??= name;
        ApplyProfileHints(name, m);
    }

    private static void ApplyProfileHints(string profileName, MediaMetadata m)
    {
        var p = profileName.ToLowerInvariant();
        if (m.ColorPrimaries is not null) return;
        if (p.Contains("srgb")) m.ColorPrimaries = "sRGB";
        else if (p.Contains("adobe rgb")) m.ColorPrimaries = "AdobeRGB";
        else if (p.Contains("prophoto")) m.ColorPrimaries = "ProPhoto RGB";
        else if (p.Contains("display p3")) m.ColorPrimaries = "P3-D65";
        else if (p.Contains("rec. 2020") || p.Contains("rec.2020") || p.Contains("bt.2020")) m.ColorPrimaries = "BT.2020";
        else if (p.Contains("cie rgb")) m.ColorPrimaries = "CIE RGB";
    }

    private static void IngestDirectoryXmp(Directory dir, MediaMetadata m)
    {
        try
        {
            var props = GetXmpPropertiesSafe(dir);
            if (props is null || props.Count == 0) return;
            var xmp = new XmpData();
            foreach (var kv in props) xmp.AddValue(kv.Key, kv.Value);
            XmpApplier.ApplyEmbedded(xmp, m, preferOverOtherText: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageMetadataReader] 第三方 XMP 兜底失败: {m.SourcePath}", ex);
        }
    }

    /// <summary>反射式调用 GetXmpProperties（避免把 XmpCore 类型拖进编译期依赖）。</summary>
    private static IDictionary<string, string>? GetXmpPropertiesSafe(Directory dir)
    {
        try
        {
            var method = dir.GetType().GetMethod("GetXmpProperties", Type.EmptyTypes);
            return method?.Invoke(dir, null) as IDictionary<string, string>;
        }
        catch { return null; }
    }

    // ============================================================ 内嵌 XMP 提取

    private static void ExtractEmbeddedXmp(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        try
        {
            var kind = SniffFormat(fullPath);
            switch (kind)
            {
                case "jpeg":
                    ExtractJpegXmpAndExif(fullPath, m, ct);
                    break;
                case "png":
                    ExtractPngXmp(fullPath, m, ct);
                    break;
                case "webp":
                    ExtractWebpXmp(fullPath, m, ct);
                    break;
                case "tiff":
                    ExtractTiffXmp(fullPath, m, ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            m.Warnings.Add("XMP 提取被取消");
        }
        catch (Exception ex)
        {
            m.Warnings.Add("内嵌 XMP 提取失败: " + ex.Message);
            Log.Warn($"[ImageMetadataReader] XMP 提取失败: {fullPath}", ex);
        }
    }

    private static void ExtractJpegXmpAndExif(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        long exifOffset = -1;
        long exifLength = 0;
        using (var fs = MetaUtil.OpenSequential(fullPath))
        {
            var fileLen = fs.Length;
            var head = new byte[2];
            if (fs.Read(head, 0, 2) != 2 || head[0] != 0xFF || head[1] != 0xD8) return;

            var segCount = 0;
            while (fs.Position + 2 <= fileLen && segCount++ < 512)
            {
                ct.ThrowIfCancellationRequested();
                var b0 = fs.ReadByte();
                if (b0 < 0) break;
                if (b0 != 0xFF) break;
                var marker = fs.ReadByte();
                if (marker < 0) break;
                while (marker == 0xFF) marker = fs.ReadByte();
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                if (marker == 0xD9 || marker == 0xDA) break; // 进入图像数据前停止

                Span<byte> lenBuf = stackalloc byte[2];
                if (!ReadExact(fs, lenBuf)) break;
                var segLen = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                if (segLen < 2) break;
                var payloadStart = fs.Position;
                var payloadLen = segLen - 2;
                if (payloadLen < 0 || payloadStart + payloadLen > fileLen) break;

                if (marker == 0xE1 && payloadLen > 16)
                {
                    var identify = new byte[Math.Min(payloadLen, 64)];
                    var n = fs.Read(identify, 0, identify.Length);
                    var span = identify.AsSpan(0, n);
                    if (span.StartsWith("Exif\0\0"u8))
                    {
                        // EXIF TIFF 结构起点 = payloadStart + 6
                        exifOffset = payloadStart + 6;
                        exifLength = payloadLen - 6;
                    }
                    else if (span.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                    {
                        var packet = ReadSegmentPacket(fs, payloadStart, payloadLen, fs.Position);
                        if (packet.Length > 0) XmpApplier.ApplyEmbedded(XmpReader.Parse(packet), m, preferOverOtherText: true);
                    }
                }

                fs.Position = payloadStart + payloadLen;
            }
        }

        // MetadataExtractor 完全失败时用自研 TIFF 解析 EXIF APP1 兜底
        if (exifOffset >= 0 && exifLength > 8 && m.CameraMake is null && m.CameraModel is null)
        {
            try
            {
                using var fs = MetaUtil.OpenRandomAccess(fullPath);
                var tiff = TiffParser.ParseAt(fs.SafeFileHandle, exifOffset, exifLength);
                if (tiff is not null) ApplyOwnExif(tiff, m);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ImageMetadataReader] EXIF APP1 兜底失败: {fullPath}", ex);
            }
        }
        else if (m.Width is null || m.Height is null)
        {
            // 尺寸兜底：读 JPEG SOF
            TryReadJpegFrameSize(fullPath, m);
        }
    }

    private static byte[] ReadSegmentPacket(FileStream fs, long payloadStart, int payloadLen, long resumePosition)
    {
        fs.Position = payloadStart;
        var buf = new byte[payloadLen];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        fs.Position = resumePosition;
        return read == 0 ? Array.Empty<byte>() : buf.AsSpan(0, read).ToArray();
    }

    private static void ExtractTiffXmp(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        if (m.HasEmbeddedXmp) return;
        using var fs = MetaUtil.OpenRandomAccess(fullPath);
        var tiff = TiffParser.ParseAt(fs.SafeFileHandle, 0, fs.Length);
        if (tiff is null) return;
        foreach (var ifd in tiff.AllIfds())
        {
            ct.ThrowIfCancellationRequested();
            var entry = ifd.Find(TagXmp);
            if (entry?.Data is { Length: > 16 } data && XmpReader.LooksLikeXmp(data))
            {
                XmpApplier.ApplyEmbedded(XmpReader.Parse(data), m, preferOverOtherText: true);
                return;
            }
        }
    }

    private static void ExtractPngXmp(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        if (m.HasEmbeddedXmp) return;
        using var fs = MetaUtil.OpenSequential(fullPath);
        Span<byte> sig = stackalloc byte[8];
        if (!ReadExact(fs, sig) || sig[0] != 0x89 || sig[1] != (byte)'P' || sig[2] != (byte)'N' || sig[3] != (byte)'G')
            return;

        Span<byte> chunkHead = stackalloc byte[8];
        var guard = 0;
        while (fs.Position + 8 <= fs.Length && guard++ < 256)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadExact(fs, chunkHead)) break;
            var len = BinaryPrimitives.ReadUInt32BigEndian(chunkHead);
            var type = Encoding.ASCII.GetString(chunkHead[4..8]);
            if (len > 64L * 1024 * 1024) break;
            var data = new byte[len];
            if (!ReadExact(fs, data)) break;
            fs.Position += 4; // CRC
            if (type == "IDAT" || type == "IEND") break;

            if (type == "iTXt" && len > 32)
            {
                var nul = Array.IndexOf(data, (byte)0);
                if (nul > 0)
                {
                    var keyword = Encoding.ASCII.GetString(data, 0, nul);
                    if (keyword == "XML:com.adobe.xmp")
                    {
                        var idx = nul + 1;
                        if (idx + 2 < data.Length)
                        {
                            var compressionFlag = data[idx];
                            idx += 2; // flag + method
                            var langEnd = Array.IndexOf(data, (byte)0, idx);
                            if (langEnd >= 0)
                            {
                                idx = langEnd + 1;
                                var transEnd = Array.IndexOf(data, (byte)0, idx);
                                if (transEnd >= 0) idx = transEnd + 1;
                            }
                            if (compressionFlag == 0 && idx < data.Length)
                                XmpApplier.ApplyEmbedded(XmpReader.Parse(data.AsSpan(idx)), m, preferOverOtherText: true);
                        }
                    }
                }
            }
            else if (type == "tEXt" && len > 32)
            {
                var nul = Array.IndexOf(data, (byte)0);
                if (nul > 0 && Encoding.ASCII.GetString(data, 0, nul) == "XML:com.adobe.xmp")
                    XmpApplier.ApplyEmbedded(XmpReader.Parse(data.AsSpan(nul + 1)), m, preferOverOtherText: true);
            }
        }
    }

    private static void ExtractWebpXmp(string fullPath, MediaMetadata m, CancellationToken ct)
    {
        if (m.HasEmbeddedXmp) return;
        using var fs = MetaUtil.OpenSequential(fullPath);
        Span<byte> head = stackalloc byte[12];
        if (!ReadExact(fs, head)) return;
        if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F') return;

        Span<byte> chunkHead = stackalloc byte[8];
        var guard = 0;
        while (fs.Position + 8 <= fs.Length && guard++ < 512)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadExact(fs, chunkHead)) break;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(chunkHead);
            var type = Encoding.ASCII.GetString(chunkHead[4..8]);
            if (len > 64L * 1024 * 1024) break;
            if (type == "XMP ")
            {
                var data = new byte[Math.Min(len, 16L * 1024 * 1024)];
                if (!ReadExact(fs, data)) break;
                XmpApplier.ApplyEmbedded(XmpReader.Parse(data), m, preferOverOtherText: true);
                return;
            }
            fs.Position += len + (len & 1);
        }
    }

    private static void TryReadJpegFrameSize(string fullPath, MediaMetadata m)
    {
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            var len = (int)Math.Min(fs.Length, 256 * 1024);
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = RandomAccess.Read(fs.SafeFileHandle, buf.AsSpan(read), read);
                if (n <= 0) break;
                read += n;
            }
            if (RawPreviewExtractor.TryReadJpegDimensions(buf.AsSpan(0, read), out var w, out var h))
            {
                m.Width ??= w;
                m.Height ??= h;
            }
        }
        catch { /* 尺寸拿不到不影响主流程 */ }
    }

    // ============================================================ 自研 EXIF 兜底

    private static void ApplyOwnExif(TiffFile tiff, MediaMetadata m)
    {
        foreach (var ifd in tiff.AllIfds())
        {
            var make = ifd.GetAscii(TagMake);
            if (make is not null) m.CameraMake ??= MetaUtil.CleanMake(make);
            var model = ifd.GetAscii(TagModel);
            if (model is not null) m.CameraModel ??= MetaUtil.CleanText(model, 128);
            m.Caption ??= MetaUtil.CleanText(ifd.GetAscii(TagImageDescription), 4096);
            m.Creator ??= MetaUtil.CleanText(ifd.GetAscii(TagArtist), 256);
            m.Copyright ??= MetaUtil.CleanText(ifd.GetAscii(TagCopyright), 512);

            var date = ifd.GetAscii(TagDateTimeOriginal) ?? ifd.GetAscii(TagDateTimeDigitized) ?? ifd.GetAscii(TagDateTime);
            var offset = ifd.GetAscii(TagOffsetTimeOriginal) ?? ifd.GetAscii(TagOffsetTime);
            if (m.CapturedAt is null)
            {
                var parsed = MetaUtil.ParseExifDate(date, offset);
                if (parsed is not null)
                {
                    m.CapturedAt = parsed;
                    m.CapturedAtSource ??= "exif:DateTimeOriginal";
                }
            }

            m.LensModel ??= MetaUtil.CleanText(ifd.GetAscii(TagLensModel), 256);
            m.FocalLengthMm ??= ifd.GetDouble(TagFocalLength) is { } f && f > 0 ? f : null;
            m.ApertureF ??= ifd.GetDouble(TagFNumber) is { } n && n > 0 ? n : null;
            m.ShutterSpeedSeconds ??= ifd.GetDouble(TagExposureTime) is { } e && e > 0 ? e : null;
            var iso = ifd.GetInt(TagIso) ?? ifd.GetInt(TagIsoSpeed);
            if (iso is > 0) m.Iso ??= iso;

            m.Width ??= ifd.GetInt(TagPixelXDimension) ?? ifd.GetInt(TagImageWidth);
            m.Height ??= ifd.GetInt(TagPixelYDimension) ?? ifd.GetInt(TagImageHeight);
            if (ifd.GetInt(TagColorSpace) == 1) m.ColorProfileName ??= "sRGB";
        }
    }

    /// <summary>RAW 全尺寸：遍历所有 IFD，取 ImageWidth × ImageLength 最大的那个（跳过缩略图）。</summary>
    private static (int? Width, int? Height) FindRawDimensions(string fullPath, CancellationToken ct)
    {
        try
        {
            var tiff = TiffParser.Parse(fullPath);
            if (tiff is null) return (null, null);
            int bestW = 0, bestH = 0;
            foreach (var ifd in tiff.AllIfds())
            {
                ct.ThrowIfCancellationRequested();
                var w = ifd.GetInt(TagImageWidth);
                var h = ifd.GetInt(TagImageHeight);
                if (w is > 0 and var ww && h is > 0 and var hh && (long)ww * hh > (long)bestW * bestH)
                {
                    bestW = ww;
                    bestH = hh;
                }
                // SubIFD 里也有 PixelXDimension/PixelYDimension 形式
                var pw = ifd.GetInt(TagPixelXDimension);
                var ph = ifd.GetInt(TagPixelYDimension);
                if (pw is > 0 and var pww && ph is > 0 and var phh && (long)pww * phh > (long)bestW * bestH)
                {
                    bestW = pww;
                    bestH = phh;
                }
            }
            return bestW > 0 && bestH > 0 ? (bestW, bestH) : (null, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageMetadataReader] RAW 尺寸探测失败: {fullPath}", ex);
            return (null, null);
        }
    }

    private static string? TryGetEmbeddedIccName(string fullPath, CancellationToken ct)
    {
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            var tiff = TiffParser.ParseAt(fs.SafeFileHandle, 0, fs.Length);
            if (tiff is null) return null;
            foreach (var ifd in tiff.AllIfds())
            {
                ct.ThrowIfCancellationRequested();
                var entry = ifd.Find(0x8773); // InterColorProfile
                if (entry?.Data is { Length: > 132 } icc)
                    return IccProfileName(icc);
            }
        }
        catch { }
        return null;
    }

    /// <summary>从 ICC profile 的 tag table 里取 'desc' 名称。</summary>
    internal static string? IccProfileName(ReadOnlySpan<byte> profile)
    {
        try
        {
            if (profile.Length < 132) return null;
            var count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..132]);
            if (count is 0 or > 4096) return null;
            var baseOffset = 132;
            for (var i = 0; i < count; i++)
            {
                var o = baseOffset + i * 12;
                if (o + 12 > profile.Length) break;
                if (!(profile[o] == 'd' && profile[o + 1] == 'e' && profile[o + 2] == 's' && profile[o + 3] == 'c')) continue;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(profile[(o + 4)..(o + 8)]);
                var size = BinaryPrimitives.ReadUInt32BigEndian(profile[(o + 8)..(o + 12)]);
                if (offset + size > profile.Length || size < 12) return null;
                var data = profile.Slice((int)offset, (int)size);
                if (data[0] != 'd' || data[1] != 'e' || data[2] != 's' || data[3] != 'c') return null;
                var len = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);
                if (len == 0 || len > size - 12) len = size - 12;
                var name = Encoding.ASCII.GetString(data.Slice(12, (int)len)).TrimEnd('\0');
                return MetaUtil.CleanText(name, 256);
            }
        }
        catch { }
        return null;
    }

    // ============================================================ 工具

    private static string SniffFormat(string fullPath)
    {
        Span<byte> head = stackalloc byte[12];
        try
        {
            using var fs = MetaUtil.OpenRandomAccess(fullPath);
            if (!ReadExact(fs, head)) return "other";
        }
        catch { return "other"; }

        if (head[0] == 0xFF && head[1] == 0xD8) return "jpeg";
        if (head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G') return "png";
        if (head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
            head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return "webp";
        if ((head[0] == (byte)'I' && head[1] == (byte)'I' && head[2] == 42 && head[3] == 0) ||
            (head[0] == (byte)'M' && head[1] == (byte)'M' && head[2] == 0 && head[3] == 42)) return "tiff";
        return "other";
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static string? TryString(Directory dir, int tag)
    {
        try { return dir.ContainsTag(tag) ? dir.GetString(tag) : null; }
        catch { return null; }
    }

    private static int? TryInt(Directory dir, int tag)
    {
        try { return dir.ContainsTag(tag) ? dir.GetInt32(tag) : null; }
        catch { return null; }
    }

    private static double? TryRational(Directory dir, int tag)
    {
        try
        {
            if (!dir.ContainsTag(tag)) return null;
            var r = dir.GetRational(tag);
            var v = r.ToDouble();
            return double.IsFinite(v) ? v : null;
        }
        catch { return null; }
    }
}