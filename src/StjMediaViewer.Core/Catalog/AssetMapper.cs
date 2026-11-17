using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StjMediaViewer.Core.Models;

namespace StjMediaViewer.Core.Catalog;

/// <summary>
/// <see cref="MediaAsset"/> ↔ SQLite 行映射。列顺序在这里定义一次：
/// SELECT 列表、INSERT 参数、读取顺序全部由 <see cref="Columns"/> 派生，避免漂移。
/// </summary>
internal static class AssetMapper
{
    /// <summary>持久化列（顺序即参数顺序，必须与 <see cref="CatalogSchema"/> 的 DDL 对齐）。</summary>
    public static readonly string[] Columns =
    {
        "id", "library_id", "full_path", "file_name", "relative_path", "size_bytes",
        "file_modified", "file_modified_ms", "file_created",
        "kind", "format_code", "mime_type", "container_name", "image_flavor", "raw_format",
        "width", "height", "duration_seconds", "frame_rate", "bitrate_bps", "bit_depth",
        "video_codec", "audio_codec", "audio_channels", "audio_sample_rate", "has_audio",
        "color_primaries", "transfer_characteristics", "color_matrix", "color_profile_name",
        "is_log", "log_profile", "color_temp_k",
        "camera_make", "camera_model", "device_key", "lens_model",
        "focal_length_mm", "aperture_f", "shutter_speed_seconds", "iso",
        "captured_at", "captured_ms", "captured_day", "captured_at_source",
        "latitude", "longitude", "altitude_m", "country", "province", "city", "city_source",
        "title", "caption", "creator", "copyright",
        "keywords_json", "keywords_flat", "themes_json",
        "post_process", "has_develop_edits", "develop_engine", "xmp_path",
        "sidecars_json", "has_embedded_xmp", "origin",
        "group_id", "is_group_cover", "group_has_raw", "group_has_jpeg", "companions_json", "lut_id",
        "theme", "tone", "rating", "color_label", "is_pick",
        "ai_subject", "ai_mood", "quality_score", "provenance_json",
        "quick_hash", "full_hash", "thumb_key",
        "indexed_at", "indexed_ms", "metadata_parsed_at", "last_error",
    };

    public static readonly string SelectList = string.Join(", ", Columns);

    /// <summary>UPSERT：以 full_path 唯一索引为冲突目标，冲突时整体覆盖。</summary>
    public static readonly string UpsertSql = BuildUpsertSql();

    // ------------------------------------------------------------------ SQL 生成

    private static string BuildUpsertSql()
    {
        var sb = new StringBuilder(8192);
        sb.Append("INSERT INTO ").Append(CatalogSchema.TableAssets).Append(" (")
          .Append(string.Join(", ", Columns)).Append(") VALUES (")
          .Append(string.Join(", ", Columns.Select(c => "@" + c))).Append(')');

        sb.Append(" ON CONFLICT(full_path) DO UPDATE SET ");
        sb.Append(string.Join(", ", Columns
            .Where(c => c != "full_path")
            .Select(c => c + "=excluded." + c)));
        return sb.ToString();
    }

    /// <summary>为 UPSERT 命令一次性建好参数对象，后续只改变量值（prepared statement）。</summary>
    public static void PrepareUpsertParameters(SqliteCommand cmd)
    {
        foreach (var c in Columns)
            cmd.Parameters.Add(new SqliteParameter("@" + c, DBNull.Value));
    }

    // ------------------------------------------------------------------ 写入

    public static void Bind(SqliteParameterCollection ps, MediaAsset a)
    {
        foreach (var c in Columns)
        {
            var p = ps["@" + c];
            p.Value = Value(a, c) ?? DBNull.Value;
        }
    }

    private static object? Value(MediaAsset a, string col) => col switch
    {
        "id" => a.Id,
        "library_id" => a.LibraryId ?? "",
        "full_path" => a.FullPath,
        "file_name" => a.FileName ?? "",
        "relative_path" => N(a.RelativePath),
        "size_bytes" => a.SizeBytes,
        "file_modified" => Dto(a.FileModified),
        "file_modified_ms" => Ms(a.FileModified),
        "file_created" => a.FileCreated.HasValue ? Dto(a.FileCreated.Value) : null,
        "kind" => (int)a.Kind,
        "format_code" => a.FormatCode ?? "",
        "mime_type" => N(a.MimeType),
        "container_name" => N(a.ContainerName),
        "image_flavor" => (int)a.ImageFlavor,
        "raw_format" => (int)a.RawFormat,
        "width" => a.Width,
        "height" => a.Height,
        "duration_seconds" => a.DurationSeconds,
        "frame_rate" => a.FrameRate,
        "bitrate_bps" => a.BitrateBps,
        "bit_depth" => a.BitDepth,
        "video_codec" => N(a.VideoCodec),
        "audio_codec" => N(a.AudioCodec),
        "audio_channels" => a.AudioChannels,
        "audio_sample_rate" => a.AudioSampleRate,
        "has_audio" => a.HasAudio ? 1 : 0,
        "color_primaries" => N(a.ColorPrimaries),
        "transfer_characteristics" => N(a.TransferCharacteristics),
        "color_matrix" => N(a.ColorMatrix),
        "color_profile_name" => N(a.ColorProfileName),
        "is_log" => a.IsLog.HasValue ? (a.IsLog.Value ? 1 : 0) : null,
        "log_profile" => N(a.LogProfile),
        "color_temp_k" => a.ColorTemperatureK,
        "camera_make" => N(a.CameraMake),
        "camera_model" => N(a.CameraModel),
        "device_key" => N(a.DeviceKey),
        "lens_model" => N(a.LensModel),
        "focal_length_mm" => a.FocalLengthMm,
        "aperture_f" => a.ApertureF,
        "shutter_speed_seconds" => a.ShutterSpeedSeconds,
        "iso" => a.Iso,
        "captured_at" => a.CapturedAt.HasValue ? Dto(a.CapturedAt.Value) : null,
        "captured_ms" => a.CapturedAt.HasValue ? Ms(a.CapturedAt.Value) : null,
        "captured_day" => a.CapturedAt.HasValue ? Day(a.CapturedAt.Value) : null,
        "captured_at_source" => N(a.CapturedAtSource),
        "latitude" => a.Latitude,
        "longitude" => a.Longitude,
        "altitude_m" => a.AltitudeMeters,
        "country" => N(a.Country),
        "province" => N(a.Province),
        "city" => N(a.City),
        "city_source" => N(a.CitySource),
        "title" => N(a.Title),
        "caption" => N(a.Caption),
        "creator" => N(a.Creator),
        "copyright" => N(a.Copyright),
        "keywords_json" => JsonKeywords(a.Keywords),
        "themes_json" => ThemesJson(a),
        "keywords_flat" => KeywordsFlat(a.Keywords),
        "post_process" => (int)a.PostProcess,
        "has_develop_edits" => a.HasDevelopEdits ? 1 : 0,
        "develop_engine" => N(a.DevelopEngine),
        "xmp_path" => N(a.XmpPath),
        "sidecars_json" => JsonList(a.Sidecars),
        "origin" => N(a.Origin),
        "has_embedded_xmp" => a.HasEmbeddedXmp ? 1 : 0,
        "group_id" => N(a.GroupId),
        "is_group_cover" => a.IsGroupCover ? 1 : 0,
        "group_has_raw" => a.GroupHasRaw ? 1 : 0,
        "group_has_jpeg" => a.GroupHasJpeg ? 1 : 0,
        "companions_json" => JsonList(a.CompanionPaths),
        "lut_id" => a.LutId,
        "theme" => N(a.Theme),
        "tone" => N(a.Tone),
        "rating" => a.Rating,
        "color_label" => N(a.ColorLabel),
        "is_pick" => a.IsPick.HasValue ? (a.IsPick.Value ? 1 : 0) : null,
        "ai_subject" => N(a.AiSubject),
        "ai_mood" => N(a.AiMood),
        "quality_score" => a.QualityScore,
        "provenance_json" => JsonProvenance(a.Provenance),
        "quick_hash" => N(a.QuickHash),
        "full_hash" => N(a.FullHash),
        "thumb_key" => N(a.ThumbKey),
        "indexed_at" => Dto(a.IndexedAt),
        "indexed_ms" => Ms(a.IndexedAt),
        "metadata_parsed_at" => a.MetadataParsedAt.HasValue ? Dto(a.MetadataParsedAt.Value) : null,
        "last_error" => N(a.LastError),
        _ => null,
    };

    // ------------------------------------------------------------------ 读取

    public static MediaAsset Read(SqliteDataReader r)
    {
        var a = new MediaAsset
        {
            Id = GetS(r, "id"),
            LibraryId = GetS(r, "library_id"),
            FullPath = GetS(r, "full_path"),
            FileName = GetS(r, "file_name"),
            RelativePath = GetN(r, "relative_path"),
            SizeBytes = GetL(r, "size_bytes"),
            FileModified = GetDto(r, "file_modified") ?? DateTimeOffset.MinValue,
            FileCreated = GetDto(r, "file_created"),
            Kind = (MediaKind)GetI(r, "kind"),
            FormatCode = GetS(r, "format_code"),
            MimeType = GetN(r, "mime_type"),
            ContainerName = GetN(r, "container_name"),
            ImageFlavor = (ImageFlavor)GetI(r, "image_flavor"),
            RawFormat = (RawFormat)GetI(r, "raw_format"),
            Width = GetIN(r, "width"),
            Height = GetIN(r, "height"),
            DurationSeconds = GetD(r, "duration_seconds"),
            FrameRate = GetD(r, "frame_rate"),
            BitrateBps = GetLN(r, "bitrate_bps"),
            BitDepth = GetIN(r, "bit_depth"),
            VideoCodec = GetN(r, "video_codec"),
            AudioCodec = GetN(r, "audio_codec"),
            AudioChannels = GetIN(r, "audio_channels"),
            AudioSampleRate = GetIN(r, "audio_sample_rate"),
            HasAudio = GetB(r, "has_audio"),
            ColorPrimaries = GetN(r, "color_primaries"),
            TransferCharacteristics = GetN(r, "transfer_characteristics"),
            ColorMatrix = GetN(r, "color_matrix"),
            ColorProfileName = GetN(r, "color_profile_name"),
            IsLog = GetBN(r, "is_log"),
            LogProfile = GetN(r, "log_profile"),
            ColorTemperatureK = GetD(r, "color_temp_k"),
            CameraMake = GetN(r, "camera_make"),
            CameraModel = GetN(r, "camera_model"),
            DeviceKey = GetN(r, "device_key"),
            LensModel = GetN(r, "lens_model"),
            FocalLengthMm = GetD(r, "focal_length_mm"),
            ApertureF = GetD(r, "aperture_f"),
            ShutterSpeedSeconds = GetD(r, "shutter_speed_seconds"),
            Iso = GetIN(r, "iso"),
            CapturedAt = GetDto(r, "captured_at"),
            CapturedAtSource = GetN(r, "captured_at_source"),
            Latitude = GetD(r, "latitude"),
            Longitude = GetD(r, "longitude"),
            AltitudeMeters = GetD(r, "altitude_m"),
            Country = GetN(r, "country"),
            Province = GetN(r, "province"),
            City = GetN(r, "city"),
            CitySource = GetN(r, "city_source"),
            Title = GetN(r, "title"),
            Caption = GetN(r, "caption"),
            Creator = GetN(r, "creator"),
            Copyright = GetN(r, "copyright"),
            PostProcess = (PostProcessState)GetI(r, "post_process"),
            HasDevelopEdits = GetB(r, "has_develop_edits"),
            DevelopEngine = GetN(r, "develop_engine"),
            XmpPath = GetN(r, "xmp_path"),
            HasEmbeddedXmp = GetB(r, "has_embedded_xmp"),
            GroupId = GetS(r, "group_id"),
            IsGroupCover = GetB(r, "is_group_cover"),
            GroupHasRaw = GetB(r, "group_has_raw"),
            GroupHasJpeg = GetB(r, "group_has_jpeg"),
            Theme = GetN(r, "theme"),
            Tone = GetN(r, "tone"),
            Rating = GetI(r, "rating"),
            ColorLabel = GetN(r, "color_label"),
            IsPick = GetBN(r, "is_pick"),
            AiSubject = GetN(r, "ai_subject"),
            AiMood = GetN(r, "ai_mood"),
            QualityScore = GetD(r, "quality_score"),
            QuickHash = GetN(r, "quick_hash"),
            FullHash = GetN(r, "full_hash"),
            ThumbKey = GetN(r, "thumb_key"),
            IndexedAt = GetDto(r, "indexed_at") ?? DateTimeOffset.Now,
            MetadataParsedAt = GetDto(r, "metadata_parsed_at"),
            LastError = GetN(r, "last_error"),
        };

        a.Keywords = JsonToKeywords(GetS(r, "keywords_json"));
        var themes = JsonToList(GetS(r, "themes_json"));
        if (themes.Count == 0 && !string.IsNullOrWhiteSpace(a.Theme)) themes.Add(a.Theme!);
        a.Themes = themes;
        a.Sidecars = JsonToList(GetS(r, "sidecars_json"));
        a.Origin = GetN(r, "origin");
        a.CompanionPaths = JsonToList(GetS(r, "companions_json"));
        a.LutId = GetS(r, "lut_id");
        a.Provenance = JsonToProvenance(GetS(r, "provenance_json"));
        return a;
    }

    // ------------------------------------------------------------------ 基础转换

    public static string? N(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static string Dto(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    public static long Ms(DateTimeOffset d) => d.ToUnixTimeMilliseconds();

    /// <summary>本地自然日（时间线 / 年份分面用），与捕获时区无关的稳定分组键。</summary>
    public static string Day(DateTimeOffset d) =>
        d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseDto(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    // ------------------------------------------------------------------ JSON 列

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string JsonList(List<string>? list)
    {
        if (list is null || list.Count == 0) return "[]";
        return JsonSerializer.Serialize(list, JsonOpts);
    }

    public static List<string> JsonToList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public static string JsonKeywords(List<string>? list) => JsonList(list);

    /// <summary>主题 JSON：没有单独设置 Themes 时用主主题兜底。</summary>
    public static string ThemesJson(MediaAsset a)
    {
        var list = a.Themes.Count > 0
            ? a.Themes
            : (string.IsNullOrWhiteSpace(a.Theme) ? new List<string>() : new List<string> { a.Theme! });
        return JsonList(list);
    }

    public static List<string> JsonToKeywords(string? json) => JsonToList(json);

    /// <summary>关键词的规范化扁平串："|a|b|c|"，用 LIKE '%|kw|%' 做无 JSON 依赖的关键词过滤。</summary>
    public static string KeywordsFlat(List<string>? list)
    {
        if (list is null || list.Count == 0) return "";
        var sb = new StringBuilder("|");
        foreach (var k in list)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var v = k.Trim().ToLowerInvariant().Replace('|', ' ');
            if (v.Length == 0) continue;
            sb.Append(v).Append('|');
        }
        return sb.Length == 1 ? "" : sb.ToString();
    }

    private sealed class ProvDto
    {
        public int d { get; set; }
        public int s { get; set; }
        public double c { get; set; }
        public string? m { get; set; }
        public long t { get; set; }
        public string? n { get; set; }
    }

    public static string JsonProvenance(Dictionary<ClassDimension, FieldProvenance>? prov)
    {
        if (prov is null || prov.Count == 0) return "{}";
        var list = new List<ProvDto>(prov.Count);
        foreach (var kv in prov)
        {
            if (kv.Value is null) continue;
            list.Add(new ProvDto
            {
                d = (int)kv.Key,
                s = (int)kv.Value.Source,
                c = kv.Value.Confidence,
                m = kv.Value.ModelId,
                t = kv.Value.At.ToUnixTimeMilliseconds(),
                n = kv.Value.Note,
            });
        }
        return JsonSerializer.Serialize(list, JsonOpts);
    }

    public static Dictionary<ClassDimension, FieldProvenance> JsonToProvenance(string? json)
    {
        var dict = new Dictionary<ClassDimension, FieldProvenance>();
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return dict;
        try
        {
            var list = JsonSerializer.Deserialize<List<ProvDto>>(json, JsonOpts);
            if (list is null) return dict;
            foreach (var d in list)
            {
                dict[(ClassDimension)d.d] = new FieldProvenance
                {
                    Source = (ClassSource)d.s,
                    Confidence = d.c,
                    ModelId = d.m,
                    At = DateTimeOffset.FromUnixTimeMilliseconds(d.t),
                    Note = d.n,
                };
            }
        }
        catch { /* 坏 JSON 不应让整条素材不可读 */ }
        return dict;
    }

    // ------------------------------------------------------------------ reader 取值

    private static string GetS(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    private static string? GetN(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static int GetI(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : r.GetInt32(i);
    }

    private static int? GetIN(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    private static long GetL(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : r.GetInt64(i);
    }

    private static long? GetLN(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static double? GetD(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    private static bool GetB(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return !r.IsDBNull(i) && r.GetInt64(i) != 0;
    }

    private static bool? GetBN(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i) != 0;
    }

    private static DateTimeOffset? GetDto(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : ParseDto(r.GetString(i));
    }

    // ------------------------------------------------------------------ 查询辅助

    /// <summary>SQL 通配符转义（配合 ESCAPE '\'）。</summary>
    public static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}