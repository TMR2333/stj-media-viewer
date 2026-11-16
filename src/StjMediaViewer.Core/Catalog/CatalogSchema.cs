using Microsoft.Data.Sqlite;

namespace StjMediaViewer.Core.Catalog;

/// <summary>
/// SQLite 索引库的物理结构。集中定义连接优化参数、DDL 与版本号，
/// 这样 <see cref="AssetMapper"/> 的列顺序、查询语句和迁移都只有一个真相来源。
/// </summary>
internal static class CatalogSchema
{
    public const int Version = 2;

    /// <summary>数据库文件名（由 App 拼到 settings.AppDataDirectory 下）。</summary>
    public const string DefaultFileName = "catalog.db";

    public const string TableAssets = "assets";
    public const string TableLibraries = "libraries";
    public const string TableMeta = "meta";

    /// <summary>建表 + 索引 + PRAGMA。可重复执行（幂等）。</summary>
    public static void Apply(SqliteConnection conn)
    {
        Execute(conn, "PRAGMA journal_mode=WAL;");
        Execute(conn, "PRAGMA synchronous=NORMAL;");
        Execute(conn, "PRAGMA temp_store=MEMORY;");
        Execute(conn, "PRAGMA cache_size=-32000;");       // ~32MB 页缓存
        Execute(conn, "PRAGMA mmap_size=134217728;");     // 128MB mmap，加速顺序扫描
        Execute(conn, "PRAGMA busy_timeout=8000;");
        Execute(conn, "PRAGMA foreign_keys=OFF;");

        Execute(conn, DdlAssets);
        Execute(conn, DdlLibraries);
        Execute(conn, DdlMeta);

        // 迁移：v1 老库补 JPG/RAW 分组列（幂等）
        EnsureColumn(conn, TableAssets, "group_id", "TEXT");
        EnsureColumn(conn, TableAssets, "is_group_cover", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(conn, TableAssets, "group_has_raw", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, TableAssets, "group_has_jpeg", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, TableAssets, "companions_json", "TEXT NOT NULL DEFAULT '[]'");
        // LUT：素材选用的调色 LUT（空 = 自动匹配内置基准 LUT）
        EnsureColumn(conn, TableAssets, "lut_id", "TEXT");

        // 迁移：v2 之前"不属于任何 JPG/RAW 组"的素材（视频 / 音频 / 单张图）没被标成组封面，
        // 折叠模式下这些素材会被 `is_group_cover = 1` 过滤掉 → 按"视频"筛选出现 0 条。
        // 这里把"没有组"的行补成封面（幂等，可重复执行）。
        Execute(conn, "UPDATE assets SET is_group_cover = 1 WHERE COALESCE(group_id, '') = '' AND is_group_cover = 0;");

        Execute(conn, "CREATE UNIQUE INDEX IF NOT EXISTS ux_assets_full_path ON assets(full_path);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_library ON assets(library_id);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_kind ON assets(kind);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_format ON assets(format_code);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_captured ON assets(captured_ms);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_captured_day ON assets(captured_day);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_device ON assets(device_key);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_city ON assets(city);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_theme ON assets(theme);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_tone ON assets(tone);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_raw ON assets(raw_format);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_is_log ON assets(is_log);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_rating ON assets(rating);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_quickhash ON assets(quick_hash);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_size ON assets(size_bytes);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_file_modified ON assets(file_modified_ms);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_indexed ON assets(indexed_ms);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_postprocess ON assets(post_process);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_group ON assets(group_id);");
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_assets_cover ON assets(is_group_cover);");

        Execute(conn,
            "INSERT INTO meta(key, value) VALUES('schema_version', '" + Version + "') " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value;");
    }

    private const string DdlAssets =
        """
        CREATE TABLE IF NOT EXISTS assets (
            id                      TEXT PRIMARY KEY NOT NULL,
            library_id              TEXT NOT NULL DEFAULT '',
            full_path               TEXT NOT NULL,
            file_name               TEXT NOT NULL DEFAULT '',
            relative_path           TEXT,
            size_bytes              INTEGER NOT NULL DEFAULT 0,
            file_modified           TEXT,
            file_modified_ms        INTEGER,
            file_created            TEXT,
            kind                    INTEGER NOT NULL DEFAULT 0,
            format_code             TEXT NOT NULL DEFAULT '',
            mime_type               TEXT,
            container_name          TEXT,
            image_flavor            INTEGER NOT NULL DEFAULT 0,
            raw_format              INTEGER NOT NULL DEFAULT 0,
            width                   INTEGER,
            height                  INTEGER,
            duration_seconds        REAL,
            frame_rate              REAL,
            bitrate_bps             INTEGER,
            bit_depth               INTEGER,
            video_codec             TEXT,
            audio_codec             TEXT,
            audio_channels          INTEGER,
            audio_sample_rate       INTEGER,
            has_audio               INTEGER NOT NULL DEFAULT 0,
            color_primaries         TEXT,
            transfer_characteristics TEXT,
            color_matrix            TEXT,
            color_profile_name      TEXT,
            is_log                  INTEGER,
            log_profile             TEXT,
            color_temp_k            REAL,
            camera_make             TEXT,
            camera_model            TEXT,
            device_key              TEXT,
            lens_model              TEXT,
            focal_length_mm         REAL,
            aperture_f              REAL,
            shutter_speed_seconds   REAL,
            iso                     INTEGER,
            captured_at             TEXT,
            captured_ms             INTEGER,
            captured_day            TEXT,
            captured_at_source      TEXT,
            latitude                REAL,
            longitude               REAL,
            altitude_m              REAL,
            country                 TEXT,
            province                TEXT,
            city                    TEXT,
            city_source             TEXT,
            title                   TEXT,
            caption                 TEXT,
            creator                 TEXT,
            copyright               TEXT,
            keywords_json           TEXT NOT NULL DEFAULT '[]',
            keywords_flat           TEXT NOT NULL DEFAULT '',
            post_process            INTEGER NOT NULL DEFAULT 0,
            has_develop_edits       INTEGER NOT NULL DEFAULT 0,
            develop_engine          TEXT,
            xmp_path                TEXT,
            sidecars_json           TEXT NOT NULL DEFAULT '[]',
            has_embedded_xmp        INTEGER NOT NULL DEFAULT 0,
            theme                   TEXT,
            tone                    TEXT,
            rating                  INTEGER NOT NULL DEFAULT 0,
            color_label             TEXT,
            is_pick                 INTEGER,
            ai_subject              TEXT,
            ai_mood                 TEXT,
            quality_score           REAL,
            provenance_json         TEXT NOT NULL DEFAULT '{}',
            quick_hash              TEXT,
            full_hash               TEXT,
            thumb_key               TEXT,
            indexed_at              TEXT,
            indexed_ms              INTEGER,
            metadata_parsed_at      TEXT,
            last_error              TEXT,
            group_id                TEXT,
            is_group_cover          INTEGER NOT NULL DEFAULT 1,
            group_has_raw           INTEGER NOT NULL DEFAULT 0,
            group_has_jpeg          INTEGER NOT NULL DEFAULT 0,
            companions_json         TEXT NOT NULL DEFAULT '[]'
        );
        """;

    private const string DdlLibraries =
        """
        CREATE TABLE IF NOT EXISTS libraries (
            id                  TEXT PRIMARY KEY NOT NULL,
            path                TEXT NOT NULL,
            name                TEXT,
            include_subfolders  INTEGER NOT NULL DEFAULT 1,
            watch_for_changes   INTEGER NOT NULL DEFAULT 1,
            added_at            TEXT,
            last_scan_at        TEXT,
            asset_count         INTEGER NOT NULL DEFAULT 0,
            total_bytes         INTEGER NOT NULL DEFAULT 0,
            sort_order          INTEGER NOT NULL DEFAULT 0
        );
        """;

    private const string DdlMeta =
        """
        CREATE TABLE IF NOT EXISTS meta (
            key     TEXT PRIMARY KEY NOT NULL,
            value   TEXT
        );
        """;

    /// <summary>补列迁移：列不存在才 ALTER（SQLite 没有 ADD COLUMN IF NOT EXISTS）。</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string ddlType)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        Execute(conn, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddlType + ";");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}