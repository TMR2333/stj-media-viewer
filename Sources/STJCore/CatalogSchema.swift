import Foundation

/// 与 Windows 版 `CatalogSchema`（src/StjMediaViewer.Core/Catalog/CatalogSchema.cs）对齐的建库脚本。
///
/// macOS 端在用户选择空目录导入时用它创建 `catalog.db`，保证 Windows 端可直接打开；
/// 表结构、列名、默认值均取自 Windows DDL（见 docs/macos-port.md 数据兼容说明）。
public enum CatalogSchema {
    /// 当前写出的 schema 版本，写入 `meta.schema_version`。
    public static let version = "1.25.2"

    /// 全量建表脚本（幂等，可对已有库执行以补齐缺失表/索引）。
    public static let ddl: String = """
CREATE TABLE IF NOT EXISTS assets (
    id TEXT PRIMARY KEY NOT NULL,
    library_id TEXT NOT NULL DEFAULT '',
    full_path TEXT NOT NULL,
    file_name TEXT NOT NULL DEFAULT '',
    relative_path TEXT,
    size_bytes INTEGER NOT NULL DEFAULT 0,
    file_modified TEXT,
    file_modified_ms INTEGER,
    file_created TEXT,
    kind INTEGER NOT NULL DEFAULT 0,
    format_code TEXT NOT NULL DEFAULT '',
    mime_type TEXT,
    container_name TEXT,
    image_flavor INTEGER NOT NULL DEFAULT 0,
    raw_format INTEGER NOT NULL DEFAULT 0,
    width INTEGER,
    height INTEGER,
    duration_seconds REAL,
    frame_rate REAL,
    bitrate_bps INTEGER,
    bit_depth INTEGER,
    video_codec TEXT,
    audio_codec TEXT,
    audio_channels INTEGER,
    audio_sample_rate INTEGER,
    has_audio INTEGER NOT NULL DEFAULT 0,
    color_primaries TEXT,
    transfer_characteristics TEXT,
    color_matrix TEXT,
    color_profile_name TEXT,
    is_log INTEGER,
    log_profile TEXT,
    color_temp_k REAL,
    camera_make TEXT,
    camera_model TEXT,
    device_key TEXT,
    lens_model TEXT,
    focal_length_mm REAL,
    aperture_f REAL,
    shutter_speed_seconds REAL,
    iso INTEGER,
    captured_at TEXT,
    captured_ms INTEGER,
    captured_day TEXT,
    captured_at_source TEXT,
    latitude REAL,
    longitude REAL,
    altitude_m REAL,
    country TEXT,
    province TEXT,
    city TEXT,
    city_source TEXT,
    title TEXT,
    caption TEXT,
    creator TEXT,
    copyright TEXT,
    keywords_json TEXT NOT NULL DEFAULT '[]',
    keywords_flat TEXT NOT NULL DEFAULT '',
    post_process INTEGER NOT NULL DEFAULT 0,
    has_develop_edits INTEGER NOT NULL DEFAULT 0,
    develop_engine TEXT,
    xmp_path TEXT,
    sidecars_json TEXT NOT NULL DEFAULT '[]',
    has_embedded_xmp INTEGER NOT NULL DEFAULT 0,
    theme TEXT,
    themes_json TEXT NOT NULL DEFAULT '[]',
    rotation_degrees INTEGER NOT NULL DEFAULT 0,
    flip_h INTEGER NOT NULL DEFAULT 0,
    flip_v INTEGER NOT NULL DEFAULT 0,
    tone TEXT,
    rating INTEGER NOT NULL DEFAULT 0,
    color_label TEXT,
    is_pick INTEGER,
    ai_subject TEXT,
    ai_mood TEXT,
    quality_score REAL,
    provenance_json TEXT NOT NULL DEFAULT '{}',
    quick_hash TEXT,
    full_hash TEXT,
    thumb_key TEXT,
    indexed_at TEXT,
    indexed_ms INTEGER,
    metadata_parsed_at TEXT,
    last_error TEXT,
    group_id TEXT,
    is_group_cover INTEGER NOT NULL DEFAULT 1,
    group_has_raw INTEGER NOT NULL DEFAULT 0,
    group_has_jpeg INTEGER NOT NULL DEFAULT 0,
    companions_json TEXT NOT NULL DEFAULT '[]',
    lut_id TEXT,
    origin TEXT,
    color_space TEXT
);
CREATE TABLE IF NOT EXISTS libraries (
    id TEXT PRIMARY KEY NOT NULL,
    path TEXT NOT NULL,
    root_path TEXT,
    name TEXT,
    include_subfolders INTEGER NOT NULL DEFAULT 1,
    watch_for_changes INTEGER NOT NULL DEFAULT 1,
    added_at TEXT,
    last_scan_at TEXT,
    asset_count INTEGER NOT NULL DEFAULT 0,
    total_bytes INTEGER NOT NULL DEFAULT 0,
    sort_order INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT
);
CREATE TABLE IF NOT EXISTS asset_custom (
    asset_id TEXT NOT NULL,
    dimension TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY (asset_id, dimension)
);
CREATE TABLE IF NOT EXISTS collections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    description TEXT,
    created_at TEXT,
    created_ms INTEGER,
    sort_order INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS collection_assets (
    collection_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    added_at TEXT,
    added_ms INTEGER,
    PRIMARY KEY (collection_id, asset_id)
);
CREATE TABLE IF NOT EXISTS merge_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    cover_asset_id TEXT,
    created_at TEXT,
    created_ms INTEGER,
    sort_order INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS merge_group_assets (
    group_id TEXT NOT NULL,
    asset_id TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0,
    added_at TEXT,
    added_ms INTEGER,
    PRIMARY KEY (group_id, asset_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_assets_full_path ON assets(full_path);
CREATE INDEX IF NOT EXISTS ix_assets_library ON assets(library_id);
CREATE INDEX IF NOT EXISTS ix_assets_kind ON assets(kind);
CREATE INDEX IF NOT EXISTS ix_assets_format ON assets(format_code);
CREATE INDEX IF NOT EXISTS ix_assets_captured ON assets(captured_ms);
CREATE INDEX IF NOT EXISTS ix_assets_captured_day ON assets(captured_day);
CREATE INDEX IF NOT EXISTS ix_assets_device ON assets(device_key);
CREATE INDEX IF NOT EXISTS ix_assets_city ON assets(city);
CREATE INDEX IF NOT EXISTS ix_assets_theme ON assets(theme);
CREATE INDEX IF NOT EXISTS ix_assets_tone ON assets(tone);
CREATE INDEX IF NOT EXISTS ix_assets_raw ON assets(raw_format);
CREATE INDEX IF NOT EXISTS ix_assets_is_log ON assets(is_log);
CREATE INDEX IF NOT EXISTS ix_assets_rating ON assets(rating);
CREATE INDEX IF NOT EXISTS ix_assets_quickhash ON assets(quick_hash);
CREATE INDEX IF NOT EXISTS ix_assets_size ON assets(size_bytes);
CREATE INDEX IF NOT EXISTS ix_assets_file_modified ON assets(file_modified_ms);
CREATE INDEX IF NOT EXISTS ix_assets_indexed ON assets(indexed_ms);
CREATE INDEX IF NOT EXISTS ix_assets_postprocess ON assets(post_process);
CREATE INDEX IF NOT EXISTS ix_assets_group ON assets(group_id);
CREATE INDEX IF NOT EXISTS ix_assets_cover ON assets(is_group_cover);
CREATE INDEX IF NOT EXISTS ix_asset_custom_dim ON asset_custom(dimension, value);
CREATE INDEX IF NOT EXISTS idx_collection_assets_asset ON collection_assets(asset_id);
CREATE INDEX IF NOT EXISTS idx_merge_group_assets_asset ON merge_group_assets(asset_id);
"""

    /// 在给定数据库上建表（幂等），并写入 schema 版本。
    public static func create(in database: Database) throws {
        try database.execute(ddl)
        let statement = try database.prepare(
            "INSERT INTO meta(key, value) VALUES('schema_version', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value;"
        )
        try statement.bindAll([version])
        _ = try statement.step()
        statement.reset()
    }
}
