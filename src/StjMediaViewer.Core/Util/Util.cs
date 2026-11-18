using System.Globalization;

namespace StjMediaViewer.Core.Util;

/// <summary>人类可读格式化。</summary>
public static class FormatUtil
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(double? seconds)
    {
        if (seconds is null or <= 0) return "--:--";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Resolution(int? w, int? h) =>
        w is > 0 && h is > 0 ? $"{w}×{h}" : "—";

    public static string Megapixels(int? w, int? h) =>
        w is > 0 && h is > 0 ? $"{w.Value * h.Value / 1_000_000.0:0.#} MP" : "—";

    public static string Aperture(double? f) => f is > 0 ? $"f/{f.Value:0.#}" : "—";

    public static string Shutter(double? seconds)
    {
        if (seconds is null or <= 0) return "—";
        return seconds.Value >= 0.3
            ? $"{seconds.Value:0.#}s"
            : $"1/{Math.Round(1.0 / seconds.Value):0}s";
    }

    public static string Focal(double? mm) => mm is > 0 ? $"{mm.Value:0.#}mm" : "—";

    public static string Iso(int? iso) => iso is > 0 ? $"ISO {iso}" : "—";

    public static string Date(DateTimeOffset? d) =>
        d is null ? "—" : d.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset? d) =>
        d is null ? "—" : d.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string RelativeDate(DateTimeOffset? d, string locale = "zh-CN")
    {
        if (d is null) return "—";
        var span = DateTimeOffset.Now - d.Value;
        var zh = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (span.TotalMinutes < 1) return zh ? "刚刚" : "just now";
        if (span.TotalMinutes < 60) return zh ? $"{(int)span.TotalMinutes} 分钟前" : $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return zh ? $"{(int)span.TotalHours} 小时前" : $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return zh ? $"{(int)span.TotalDays} 天前" : $"{(int)span.TotalDays}d ago";
        return Date(d);
    }

    public static string Coord(double? lat, double? lon) =>
        lat is null || lon is null
            ? "—"
            : $"{Math.Abs(lat.Value):0.####}°{(lat.Value >= 0 ? "N" : "S")}, {Math.Abs(lon.Value):0.####}°{(lon.Value >= 0 ? "E" : "W")}";
}

/// <summary>EXIF 日期、时区、GPS 等杂项解析。</summary>
public static class TimeUtil
{
    private static readonly string[] DateFormats =
    {
        "yyyy:MM:dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:ss.fff",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.fffK",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy:MM:dd",
        "yyyy-MM-dd",
    };

    public static DateTimeOffset? ParseFlexible(string? text, TimeSpan? offset = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimEnd('\0');
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return offset.HasValue ? new DateTimeOffset(dto.DateTime, offset.Value) : dto;

        foreach (var f in DateFormats)
        {
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                return offset.HasValue ? new DateTimeOffset(dt, offset.Value) : new DateTimeOffset(dt);
        }

        // QuickTime 常见 "2024-05-01T12:00:00+0800"
        var idx = s.IndexOf('+');
        if (idx > 10)
        {
            var basePart = s[..idx];
            var tz = s[(idx + 1)..];
            if (tz.Length is 4 or 5 && int.TryParse(tz[..2], out var h) && int.TryParse(tz[^2..], out var m) &&
                DateTime.TryParse(basePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt2))
                return new DateTimeOffset(dt2, new TimeSpan(s[idx] == '-' ? -h : h, m, 0));
        }
        return null;
    }

    /// <summary>EXIF "2024:05:01 12:00:00" + OffsetTimeOriginal "+08:00" 组合。</summary>
    public static DateTimeOffset? ParseExif(string? dateTimeOriginal, string? offset)
    {
        if (string.IsNullOrWhiteSpace(dateTimeOriginal)) return null;
        TimeSpan? off = null;
        if (!string.IsNullOrWhiteSpace(offset) && offset.Length >= 5 &&
            offset[0] is '+' or '-' &&
            int.TryParse(offset.Substring(1, 2), out var hh) &&
            int.TryParse(offset.Substring(3, 2), out var mm))
            off = new TimeSpan(offset[0] == '-' ? -hh : hh, mm, 0);

        if (off is null) return ParseFlexible(dateTimeOriginal);

        // 关键：EXIF 记的是"墙上时间 + 时区偏移"，必须用时间分量重新构造 DateTimeOffset，
        // 不能先按本地时间解析再套 offset（那会抛 ArgumentException 或双加偏移）。
        var s = dateTimeOriginal.Trim().TrimEnd('\0');
        foreach (var f in new[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss" })
        {
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
                return new DateTimeOffset(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, wall.Second, off.Value);
        }
        return ParseFlexible(dateTimeOriginal, off);
    }
}

/// <summary>简单日志：写文件 + 控制台，带滚动上限。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _written;
    private const long MaxBytes = 4 * 1024 * 1024;

    public static void Init(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "stj-media-viewer.log");
        }
        catch { _path = null; }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            if (_path is null) return;
            try
            {
                if (_written > MaxBytes)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old, true);
                    _written = 0;
                }
                // FileShare.ReadWrite：多开/被杀毒软件扫描时不会因为独占打开而 Access denied
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(line + Environment.NewLine);
                }
                _written += line.Length;
            }
            catch { }
        }
    }
}

public static class PathUtil
{
    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    public static bool IsUnder(string candidate, string root)
    {
        var c = Normalize(candidate);
        var r = Normalize(root);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "untitled" : name;
    }

    /// <summary>把重复文件名变成 name (1).ext / name (2).ext。</summary>
    public static string MakeUnique(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}