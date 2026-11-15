using System.Text;

namespace StjMediaViewer.Core.Models;

/// <summary>扩展名知识库：格式、大类、RAW 判定、边车、忽略规则、魔数嗅探。</summary>
public static class MediaTypeCatalog
{
    private sealed record Entry(MediaKind Kind, string Format, ImageFlavor Flavor, RawFormat Raw, string Mime);

    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- 视频 ----------
        [".mp4"] = new(MediaKind.Video, "MP4", ImageFlavor.None, RawFormat.None, "video/mp4"),
        [".m4v"] = new(MediaKind.Video, "M4V", ImageFlavor.None, RawFormat.None, "video/x-m4v"),
        [".mov"] = new(MediaKind.Video, "MOV", ImageFlavor.None, RawFormat.None, "video/quicktime"),
        [".qt"] = new(MediaKind.Video, "MOV", ImageFlavor.None, RawFormat.None, "video/quicktime"),
        [".mxf"] = new(MediaKind.Video, "MXF", ImageFlavor.None, RawFormat.None, "application/mxf"),
        [".mkv"] = new(MediaKind.Video, "MKV", ImageFlavor.None, RawFormat.None, "video/x-matroska"),
        [".avi"] = new(MediaKind.Video, "AVI", ImageFlavor.None, RawFormat.None, "video/x-msvideo"),
        [".webm"] = new(MediaKind.Video, "WEBM", ImageFlavor.None, RawFormat.None, "video/webm"),
        [".mpg"] = new(MediaKind.Video, "MPEG", ImageFlavor.None, RawFormat.None, "video/mpeg"),
        [".mpeg"] = new(MediaKind.Video, "MPEG", ImageFlavor.None, RawFormat.None, "video/mpeg"),
        [".m2ts"] = new(MediaKind.Video, "M2TS", ImageFlavor.None, RawFormat.None, "video/mp2t"),
        [".mts"] = new(MediaKind.Video, "MTS", ImageFlavor.None, RawFormat.None, "video/mp2t"),
        [".ts"] = new(MediaKind.Video, "TS", ImageFlavor.None, RawFormat.None, "video/mp2t"),
        [".braw"] = new(MediaKind.Video, "BRAW", ImageFlavor.None, RawFormat.None, "video/x-braw"),
        [".r3d"] = new(MediaKind.Video, "R3D", ImageFlavor.None, RawFormat.None, "video/x-red"),
        [".crm"] = new(MediaKind.Video, "CRM", ImageFlavor.None, RawFormat.None, "video/x-canon-raw"),
        [".insv"] = new(MediaKind.Video, "INSV", ImageFlavor.None, RawFormat.None, "video/x-instav360"),
        [".lrv"] = new(MediaKind.Video, "LRV", ImageFlavor.None, RawFormat.None, "video/x-gopro-lrv"),
        [".gpr"] = new(MediaKind.Image, "GPR", ImageFlavor.None, RawFormat.Other, "image/x-gopro"),
        [".hevc"] = new(MediaKind.Video, "HEVC", ImageFlavor.None, RawFormat.None, "video/hevc"),
        [".h265"] = new(MediaKind.Video, "HEVC", ImageFlavor.None, RawFormat.None, "video/hevc"),
        [".264"] = new(MediaKind.Video, "H264", ImageFlavor.None, RawFormat.None, "video/h264"),

        // ---------- 图片（常规）----------
        [".jpg"] = new(MediaKind.Image, "JPEG", ImageFlavor.Jpeg, RawFormat.None, "image/jpeg"),
        [".jpeg"] = new(MediaKind.Image, "JPEG", ImageFlavor.Jpeg, RawFormat.None, "image/jpeg"),
        [".jpe"] = new(MediaKind.Image, "JPEG", ImageFlavor.Jpeg, RawFormat.None, "image/jpeg"),
        [".jfif"] = new(MediaKind.Image, "JPEG", ImageFlavor.Jpeg, RawFormat.None, "image/jpeg"),
        [".png"] = new(MediaKind.Image, "PNG", ImageFlavor.Png, RawFormat.None, "image/png"),
        [".tif"] = new(MediaKind.Image, "TIFF", ImageFlavor.Tiff, RawFormat.None, "image/tiff"),
        [".tiff"] = new(MediaKind.Image, "TIFF", ImageFlavor.Tiff, RawFormat.None, "image/tiff"),
        [".heic"] = new(MediaKind.Image, "HEIC", ImageFlavor.Heif, RawFormat.None, "image/heic"),
        [".heif"] = new(MediaKind.Image, "HEIF", ImageFlavor.Heif, RawFormat.None, "image/heif"),
        [".avif"] = new(MediaKind.Image, "AVIF", ImageFlavor.Heif, RawFormat.None, "image/avif"),
        [".webp"] = new(MediaKind.Image, "WEBP", ImageFlavor.WebP, RawFormat.None, "image/webp"),
        [".bmp"] = new(MediaKind.Image, "BMP", ImageFlavor.Bmp, RawFormat.None, "image/bmp"),
        [".gif"] = new(MediaKind.Image, "GIF", ImageFlavor.Gif, RawFormat.None, "image/gif"),
        [".psd"] = new(MediaKind.Image, "PSD", ImageFlavor.Psd, RawFormat.None, "image/vnd.adobe.photoshop"),
        [".psb"] = new(MediaKind.Image, "PSB", ImageFlavor.Psd, RawFormat.None, "image/vnd.adobe.photoshop"),
        [".jxl"] = new(MediaKind.Image, "JXL", ImageFlavor.Other, RawFormat.None, "image/jxl"),

        // ---------- 图片（RAW）----------
        [".cr2"] = new(MediaKind.Image, "CR2", ImageFlavor.None, RawFormat.Cr2, "image/x-canon-cr2"),
        [".cr3"] = new(MediaKind.Image, "CR3", ImageFlavor.None, RawFormat.Cr3, "image/x-canon-cr3"),
        [".crw"] = new(MediaKind.Image, "CRW", ImageFlavor.None, RawFormat.Other, "image/x-canon-crw"),
        [".nef"] = new(MediaKind.Image, "NEF", ImageFlavor.None, RawFormat.Nef, "image/x-nikon-nef"),
        [".nrw"] = new(MediaKind.Image, "NRW", ImageFlavor.None, RawFormat.Nef, "image/x-nikon-nrw"),
        [".arw"] = new(MediaKind.Image, "ARW", ImageFlavor.None, RawFormat.Arw, "image/x-sony-arw"),
        [".srf"] = new(MediaKind.Image, "SRF", ImageFlavor.None, RawFormat.Arw, "image/x-sony-srf"),
        [".sr2"] = new(MediaKind.Image, "SR2", ImageFlavor.None, RawFormat.Arw, "image/x-sony-sr2"),
        [".raf"] = new(MediaKind.Image, "RAF", ImageFlavor.None, RawFormat.Raf, "image/x-fuji-raf"),
        [".orf"] = new(MediaKind.Image, "ORF", ImageFlavor.None, RawFormat.Orf, "image/x-olympus-orf"),
        [".rw2"] = new(MediaKind.Image, "RW2", ImageFlavor.None, RawFormat.Rw2, "image/x-panasonic-rw2"),
        [".raw"] = new(MediaKind.Image, "RAW", ImageFlavor.None, RawFormat.Other, "image/x-panasonic-raw"),
        [".pef"] = new(MediaKind.Image, "PEF", ImageFlavor.None, RawFormat.Pef, "image/x-pentax-pef"),
        [".srw"] = new(MediaKind.Image, "SRW", ImageFlavor.None, RawFormat.Srw, "image/x-samsung-srw"),
        [".x3f"] = new(MediaKind.Image, "X3F", ImageFlavor.None, RawFormat.X3f, "image/x-sigma-x3f"),
        [".mos"] = new(MediaKind.Image, "MOS", ImageFlavor.None, RawFormat.Mos, "image/x-leaf-mos"),
        [".iiq"] = new(MediaKind.Image, "IIQ", ImageFlavor.None, RawFormat.Iiq, "image/x-phaseone-iiq"),
        [".3fr"] = new(MediaKind.Image, "3FR", ImageFlavor.None, RawFormat.Other, "image/x-hasselblad-3fr"),
        [".fff"] = new(MediaKind.Image, "FFF", ImageFlavor.None, RawFormat.Other, "image/x-hasselblad-fff"),
        [".mef"] = new(MediaKind.Image, "MEF", ImageFlavor.None, RawFormat.Other, "image/x-mamiya-mef"),
        [".erf"] = new(MediaKind.Image, "ERF", ImageFlavor.None, RawFormat.Other, "image/x-epson-erf"),
        [".kdc"] = new(MediaKind.Image, "KDC", ImageFlavor.None, RawFormat.Other, "image/x-kodak-kdc"),
        [".dcr"] = new(MediaKind.Image, "DCR", ImageFlavor.None, RawFormat.Other, "image/x-kodak-dcr"),
        [".mrw"] = new(MediaKind.Image, "MRW", ImageFlavor.None, RawFormat.Other, "image/x-minolta-mrw"),
        [".rwl"] = new(MediaKind.Image, "RWL", ImageFlavor.None, RawFormat.Other, "image/x-leica-rwl"),
        [".dng"] = new(MediaKind.Image, "DNG", ImageFlavor.None, RawFormat.Dng, "image/x-adobe-dng"),

        // ---------- 音频 ----------
        [".wav"] = new(MediaKind.Audio, "WAV", ImageFlavor.None, RawFormat.None, "audio/wav"),
        [".mp3"] = new(MediaKind.Audio, "MP3", ImageFlavor.None, RawFormat.None, "audio/mpeg"),
        [".flac"] = new(MediaKind.Audio, "FLAC", ImageFlavor.None, RawFormat.None, "audio/flac"),
        [".aac"] = new(MediaKind.Audio, "AAC", ImageFlavor.None, RawFormat.None, "audio/aac"),
        [".m4a"] = new(MediaKind.Audio, "M4A", ImageFlavor.None, RawFormat.None, "audio/mp4"),
        [".ogg"] = new(MediaKind.Audio, "OGG", ImageFlavor.None, RawFormat.None, "audio/ogg"),
        [".oga"] = new(MediaKind.Audio, "OGG", ImageFlavor.None, RawFormat.None, "audio/ogg"),
        [".opus"] = new(MediaKind.Audio, "OPUS", ImageFlavor.None, RawFormat.None, "audio/opus"),
        [".aif"] = new(MediaKind.Audio, "AIFF", ImageFlavor.None, RawFormat.None, "audio/aiff"),
        [".aiff"] = new(MediaKind.Audio, "AIFF", ImageFlavor.None, RawFormat.None, "audio/aiff"),
        [".wma"] = new(MediaKind.Audio, "WMA", ImageFlavor.None, RawFormat.None, "audio/x-ms-wma"),
        [".dsf"] = new(MediaKind.Audio, "DSF", ImageFlavor.None, RawFormat.None, "audio/x-dsf"),
        [".dff"] = new(MediaKind.Audio, "DFF", ImageFlavor.None, RawFormat.None, "audio/x-dff"),
        [".caf"] = new(MediaKind.Audio, "CAF", ImageFlavor.None, RawFormat.None, "audio/x-caf"),
    };

    /// <summary>边车扩展名（会被挂到主素材上，不单独入库）。</summary>
    public static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmp", ".dop", ".cos", ".on1", ".pp3", ".arp", ".lrtemplate", ".xmp.bak",
    };

    /// <summary>要整目录跳过的名称。</summary>
    public static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", ".git", ".svn", ".hg", "node_modules",
        ".thumbnails", ".cache", ".dtrash", "@eadir", "lost+found", ".spotlight-v100",
        ".fseventsd", ".trashes", "$windows.~bt", "windows", "assembly", "installer",
    };

    /// <summary>要忽略的文件名。</summary>
    public static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", ".ds_store", "desktop.ini", "ehthumbs.db", "icon\r", ".directory",
    };

    /// <summary>要忽略的扩展名（非媒体且无价值）。</summary>
    public static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".lnk", ".url",
        ".db", ".ini", ".log", ".bak", ".json", ".xml", ".txt", ".pdf", ".exe", ".dll",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".dmp", ".sys", ".lnk",
    };

    public static bool TryGet(string fullPath, out MediaKind kind, out string format, out ImageFlavor flavor, out RawFormat raw, out string mime)
    {
        var ext = Path.GetExtension(fullPath);
        if (Map.TryGetValue(ext, out var e))
        {
            kind = e.Kind == MediaKind.Image ? MediaKind.Image : e.Kind;
            format = e.Format;
            flavor = e.Flavor;
            raw = e.Raw;
            mime = e.Mime;
            return true;
        }
        kind = MediaKind.Unknown;
        format = ext.TrimStart('.').ToUpperInvariant();
        flavor = ImageFlavor.None;
        raw = RawFormat.None;
        mime = "application/octet-stream";
        return false;
    }

    public static bool IsSupportedMedia(string fullPath) => Map.ContainsKey(Path.GetExtension(fullPath));

    public static bool IsSidecar(string fullPath) =>
        SidecarExtensions.Contains(Path.GetExtension(fullPath)) ||
        fullPath.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase);

    public static bool IsIgnoredFile(string fileName)
    {
        if (IgnoredFileNames.Contains(fileName)) return true;
        if (fileName.StartsWith("._", StringComparison.Ordinal)) return true;   // AppleDouble
        if (fileName.StartsWith(".", StringComparison.Ordinal) && fileName.Length > 1) return true; // 隐藏文件
        if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsIgnoredDirectory(string dirName) => IgnoredDirectoryNames.Contains(dirName);

    /// <summary>用魔数嗅探真实类型，用于纠正错误扩展名或无扩展名文件。</summary>
    public static MediaKind SniffKind(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return MediaKind.Image;      // JPEG
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return MediaKind.Image; // PNG
        if (head.Length >= 4 && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A) || (head[0] == 0x4D && head[1] == 0x4D && head[3] == 0x2A))) return MediaKind.Image; // TIFF/DNG/CR2/NEF/ARW
        if (head.Length >= 2 && head[0] == 0x42 && head[1] == 0x4D) return MediaKind.Image;                              // BMP
        if (head.Length >= 3 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F') return MediaKind.Image;               // GIF
        if (head.Length >= 12 && head[8] == 'f' && head[9] == 't' && head[10] == 'y' && head[11] == 'p') return SniffFtyp(head);
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return MediaKind.Video; // Matroska/WebM
        if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
        {
            if (head.Length >= 12 && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E') return MediaKind.Audio;
            if (head.Length >= 12 && head[8] == 'A' && head[9] == 'V' && head[10] == 'I') return MediaKind.Video;
            if (head.Length >= 12 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return MediaKind.Image;
        }
        if (head.Length >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return MediaKind.Audio; // FLAC
        if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3') return MediaKind.Audio;                 // MP3 w/ ID3
        if (head.Length >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return MediaKind.Audio; // OggS
        if (head.Length >= 4 && head[0] == 'F' && head[1] == 'O' && head[2] == 'R' && head[3] == 'M') return MediaKind.Audio; // AIFF
        // 裸 MPEG 帧同步：跳过，噪声太大
        return MediaKind.Unknown;
    }

    private static MediaKind SniffFtyp(ReadOnlySpan<byte> head)
    {
        if (head.Length < 16) return MediaKind.Unknown;
        var brand = Encoding.ASCII.GetString(head.Slice(8, 4));
        return brand switch
        {
            "avif" or "avis" or "heic" or "heix" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => MediaKind.Image,
            "M4A " => MediaKind.Audio,
            "qt  " or "isom" or "iso2" or "mp41" or "mp42" or "avc1" or "M4V " or "dash" => MediaKind.Video,
            _ => MediaKind.Video,
        };
    }

    /// <summary>用于 UI/统计的格式 → 大类中文名。</summary>
    public static string KindDisplay(MediaKind kind, string locale = "zh-CN") => kind switch
    {
        MediaKind.Image => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Images" : "图片",
        MediaKind.Video => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Videos" : "视频",
        MediaKind.Audio => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Audio" : "音频",
        _ => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Other" : "其他",
    };

    public static string RawDisplay(RawFormat raw) => raw switch
    {
        RawFormat.None => "",
        RawFormat.Dng => "DNG",
        RawFormat.Cr2 => "CR2",
        RawFormat.Cr3 => "CR3",
        RawFormat.Nef => "NEF",
        RawFormat.Arw => "ARW",
        RawFormat.Raf => "RAF",
        RawFormat.Orf => "ORF",
        RawFormat.Rw2 => "RW2",
        RawFormat.Pef => "PEF",
        RawFormat.Srw => "SRW",
        RawFormat.X3f => "X3F",
        RawFormat.Mos => "MOS",
        RawFormat.Iiq => "IIQ",
        _ => "RAW",
    };
}