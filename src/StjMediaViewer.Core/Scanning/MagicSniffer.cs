using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Scanning;

/// <summary>
/// 魔数嗅探：识别真实类型并纠正错误扩展名（.jpg 实为 PNG、无扩展名实为 MP4 等）。
/// 只在文件头 64 字节内判断，代价可忽略。
/// </summary>
internal static class MagicSniffer
{
    internal readonly record struct SniffResult(MediaKind Kind, string Format, ImageFlavor Flavor, RawFormat Raw, string Mime)
    {
        public static SniffResult Unknown => new(MediaKind.Unknown, "", ImageFlavor.None, RawFormat.None, "");
        public bool IsMedia => Kind != MediaKind.Unknown;
    }

    public static SniffResult Read(string fullPath, int headBytes = 64)
    {
        try
        {
            Span<byte> head = stackalloc byte[128];
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int n = fs.Read(head[..Math.Min(head.Length, headBytes)]);
            return FromHead(head[..n], Path.GetExtension(fullPath));
        }
        catch { return SniffResult.Unknown; }
    }

    public static SniffResult FromHead(ReadOnlySpan<byte> head, string extension = "")
    {
        if (head.Length < 2) return SniffResult.Unknown;
        var kind = MediaTypeCatalog.SniffKind(head);
        if (kind == MediaKind.Unknown) return SniffResult.Unknown;

        // TIFF 家族：可能是 TIFF 也可能是 DNG/CR2/NEF/ARW（都是 TIFF 容器）
        if (head.Length >= 4 && ((head[0] == 'I' && head[1] == 'I' && head[2] == 0x2A) || (head[0] == 'M' && head[1] == 'M' && head[3] == 0x2A)))
        {
            var ext = extension.ToLowerInvariant();
            if (MediaTypeCatalog.TryGet("probe" + ext, out _, out var rawFmtCode, out _, out var rawFmt, out var rawMime) &&
                rawFmt != RawFormat.None)
                return new SniffResult(MediaKind.Image, rawFmtCode, ImageFlavor.None, rawFmt, rawMime);
            if (ext is ".dng" or ".cr2" or ".nef" or ".arw" or ".raf" or ".orf" or ".rw2" or ".pef" or ".srw" or ".x3f" or ".mos" or ".iiq" or ".raw")
                return new SniffResult(MediaKind.Image, ext.TrimStart('.').ToUpperInvariant(), ImageFlavor.None, RawFormat.Other, "image/x-raw");
            return new SniffResult(MediaKind.Image, "TIFF", ImageFlavor.Tiff, RawFormat.None, "image/tiff");
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return new SniffResult(MediaKind.Image, "JPEG", ImageFlavor.Jpeg, RawFormat.None, "image/jpeg");
        if (head.Length >= 4 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
            return new SniffResult(MediaKind.Image, "PNG", ImageFlavor.Png, RawFormat.None, "image/png");
        if (head.Length >= 2 && head[0] == 'B' && head[1] == 'M')
            return new SniffResult(MediaKind.Image, "BMP", ImageFlavor.Bmp, RawFormat.None, "image/bmp");
        if (head.Length >= 3 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
            return new SniffResult(MediaKind.Image, "GIF", ImageFlavor.Gif, RawFormat.None, "image/gif");
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
            return new SniffResult(MediaKind.Video, "MKV", ImageFlavor.None, RawFormat.None, "video/x-matroska");

        if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
        {
            if (head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E')
                return new SniffResult(MediaKind.Audio, "WAV", ImageFlavor.None, RawFormat.None, "audio/wav");
            if (head[8] == 'A' && head[9] == 'V' && head[10] == 'I')
                return new SniffResult(MediaKind.Video, "AVI", ImageFlavor.None, RawFormat.None, "video/x-msvideo");
            if (head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                return new SniffResult(MediaKind.Image, "WEBP", ImageFlavor.WebP, RawFormat.None, "image/webp");
        }

        if (head.Length >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
            return new SniffResult(MediaKind.Audio, "FLAC", ImageFlavor.None, RawFormat.None, "audio/flac");
        if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            return new SniffResult(MediaKind.Audio, "MP3", ImageFlavor.None, RawFormat.None, "audio/mpeg");
        if (head.Length >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
            return new SniffResult(MediaKind.Audio, "OGG", ImageFlavor.None, RawFormat.None, "audio/ogg");
        if (head.Length >= 4 && head[0] == 'F' && head[1] == 'O' && head[2] == 'R' && head[3] == 'M')
            return new SniffResult(MediaKind.Audio, "AIFF", ImageFlavor.None, RawFormat.None, "audio/aiff");

        if (head.Length >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4));
            return brand switch
            {
                "avif" or "avis" => new SniffResult(MediaKind.Image, "AVIF", ImageFlavor.Heif, RawFormat.None, "image/avif"),
                "heic" or "heix" or "hevx" or "heim" or "heis" or "mif1" or "msf1" =>
                    new SniffResult(MediaKind.Image, "HEIC", ImageFlavor.Heif, RawFormat.None, "image/heic"),
                "M4A " or "M4B " => new SniffResult(MediaKind.Audio, "M4A", ImageFlavor.None, RawFormat.None, "audio/mp4"),
                "qt  " => new SniffResult(MediaKind.Video, "MOV", ImageFlavor.None, RawFormat.None, "video/quicktime"),
                "crx " => new SniffResult(MediaKind.Image, "CR3", ImageFlavor.None, RawFormat.Cr3, "image/x-canon-cr3"),
                "isom" or "iso2" or "mp41" or "mp42" or "avc1" or "M4V " or "dash" or "MSNV" or "mp71" =>
                    new SniffResult(MediaKind.Video, "MP4", ImageFlavor.None, RawFormat.None, "video/mp4"),
                _ => new SniffResult(MediaKind.Video, "MP4", ImageFlavor.None, RawFormat.None, "video/mp4"),
            };
        }

        return SniffResult.Unknown;
    }
}