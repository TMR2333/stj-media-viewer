package com.stjarna.mediaviewer.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import com.stjarna.mediaviewer.core.Collection as MediaCollection
import com.stjarna.mediaviewer.core.LibraryRoot
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import com.stjarna.mediaviewer.core.PostProcessState
import com.stjarna.mediaviewer.core.RawFormat
import com.stjarna.mediaviewer.core.exchange.AssetPatch
import org.json.JSONArray
import org.json.JSONObject

/**
 * 本地索引库（SQLite）。表结构与 Windows 版 `catalog.db` 的 assets 子集保持同名同义，
 * 额外加 `content_uri` 列保存 SAF/MediaStore 的 content:// 地址（移动端平台差异）。
 */
class CatalogDb(context: Context) :
    SQLiteOpenHelper(context, "catalog.db", null, DB_VERSION) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE assets (
              id TEXT PRIMARY KEY,
              library_id TEXT NOT NULL DEFAULT '',
              full_path TEXT NOT NULL,
              content_uri TEXT,
              file_name TEXT NOT NULL,
              relative_path TEXT,
              size_bytes INTEGER NOT NULL DEFAULT 0,
              file_modified_ms INTEGER NOT NULL DEFAULT 0,
              indexed_at_ms INTEGER NOT NULL DEFAULT 0,
              kind INTEGER NOT NULL DEFAULT 0,
              format_code TEXT NOT NULL DEFAULT '',
              raw_format TEXT NOT NULL DEFAULT '',
              width INTEGER, height INTEGER, duration_ms INTEGER,
              captured_at_ms INTEGER, captured_at_source TEXT,
              camera_make TEXT, camera_model TEXT, device_key TEXT,
              latitude REAL, longitude REAL, altitude REAL,
              country TEXT, province TEXT, city TEXT, city_source TEXT,
              title TEXT, caption TEXT,
              keywords_json TEXT NOT NULL DEFAULT '[]',
              themes_json TEXT NOT NULL DEFAULT '[]',
              theme TEXT, tone TEXT,
              rating INTEGER NOT NULL DEFAULT 0,
              color_label TEXT,
              is_pick INTEGER,
              post_process INTEGER NOT NULL DEFAULT 0,
              is_log INTEGER,
              origin TEXT, last_error TEXT
            )
            """.trimIndent()
        )
        db.execSQL("CREATE UNIQUE INDEX idx_assets_path ON assets(full_path)")
        db.execSQL("CREATE INDEX idx_assets_lib ON assets(library_id)")
        db.execSQL(
            """
            CREATE TABLE libraries (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL,
              uri TEXT, created_at_ms INTEGER NOT NULL DEFAULT 0, last_scan_at_ms INTEGER
            )
            """.trimIndent()
        )
        db.execSQL("CREATE TABLE collections (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at_ms INTEGER NOT NULL DEFAULT 0)")
        db.execSQL("CREATE TABLE collection_assets (collection_id TEXT NOT NULL, asset_id TEXT NOT NULL, PRIMARY KEY(collection_id, asset_id))")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // 0.1.0 起步阶段：结构变化直接重建索引（素材文件本身不受影响）
        db.execSQL("DROP TABLE IF EXISTS collection_assets")
        db.execSQL("DROP TABLE IF EXISTS collections")
        db.execSQL("DROP TABLE IF EXISTS libraries")
        db.execSQL("DROP TABLE IF EXISTS assets")
        onCreate(db)
    }

    // ------------------------------------------------------------------ 素材

    fun loadAllAssets(): List<MediaAsset> {
        val result = ArrayList<MediaAsset>(4096)
        readableDatabase.rawQuery("SELECT * FROM assets", null).use { c ->
            while (c.moveToNext()) result += readAsset(c)
        }
        return result
    }

    fun upsertAll(assets: List<MediaAsset>) {
        if (assets.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (a in assets) db.insertWithOnConflict("assets", null, toValues(a), SQLiteDatabase.CONFLICT_REPLACE)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    fun applyPatches(patches: Map<MediaAsset, AssetPatch>) {
        if (patches.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            for ((asset, patch) in patches) {
                patch.rating?.let { asset.rating = it }
                patch.colorLabel?.let { asset.colorLabel = it }
                patch.isPick?.let { asset.isPick = it }
                patch.country?.let { asset.country = it }
                patch.province?.let { asset.province = it }
                patch.city?.let { asset.city = it; asset.citySource = "manual" }
                patch.themes?.let { asset.themes = it.toMutableList() }
                patch.tone?.let { asset.tone = it }
                patch.keywords?.let { asset.keywords = it.toMutableList() }
                patch.title?.let { asset.title = it }
                patch.postProcess?.let { asset.postProcess = it }
                db.insertWithOnConflict("assets", null, toValues(asset), SQLiteDatabase.CONFLICT_REPLACE)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    fun updateAsset(asset: MediaAsset) {
        writableDatabase.insertWithOnConflict("assets", null, toValues(asset), SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun deleteAssets(ids: kotlin.collections.Collection<String>) {
        if (ids.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            ids.chunked(400).forEach { chunk ->
                db.delete("assets", "id IN (${chunk.joinToString(",") { "?" }})", chunk.toTypedArray())
                db.delete("collection_assets", "asset_id IN (${chunk.joinToString(",") { "?" }})", chunk.toTypedArray())
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    fun clearAll() {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete("assets", null, null)
            db.delete("collection_assets", null, null)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    // ------------------------------------------------------------------ 媒体库

    fun loadLibraries(): List<LibraryRoot> {
        val result = ArrayList<LibraryRoot>()
        readableDatabase.rawQuery("SELECT * FROM libraries ORDER BY created_at_ms", null).use { c ->
            while (c.moveToNext()) {
                result += LibraryRoot(
                    id = c.getString(c.getColumnIndexOrThrow("id")),
                    name = c.getString(c.getColumnIndexOrThrow("name")),
                    path = c.getString(c.getColumnIndexOrThrow("path")),
                    uri = c.getStringOrNull("uri"),
                    createdAt = c.getLong(c.getColumnIndexOrThrow("created_at_ms")),
                    lastScanAt = c.getLongOrNull("last_scan_at_ms"),
                )
            }
        }
        return result
    }

    fun saveLibrary(root: LibraryRoot) {
        val values = ContentValues().apply {
            put("id", root.id)
            put("name", root.name)
            put("path", root.path)
            put("uri", root.uri)
            put("created_at_ms", root.createdAt)
            put("last_scan_at_ms", root.lastScanAt)
        }
        writableDatabase.insertWithOnConflict("libraries", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun deleteLibrary(id: String) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete("libraries", "id=?", arrayOf(id))
            db.delete("assets", "library_id=?", arrayOf(id))
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    // ------------------------------------------------------------------ 合集

    fun loadCollections(): List<MediaCollection> {
        val collections = LinkedHashMap<String, MediaCollection>()
        readableDatabase.rawQuery("SELECT * FROM collections ORDER BY created_at_ms", null).use { c ->
            while (c.moveToNext()) {
                val col = MediaCollection(
                    id = c.getString(c.getColumnIndexOrThrow("id")),
                    name = c.getString(c.getColumnIndexOrThrow("name")),
                    createdAt = c.getLong(c.getColumnIndexOrThrow("created_at_ms")),
                )
                collections[col.id] = col
            }
        }
        readableDatabase.rawQuery("SELECT collection_id, asset_id FROM collection_assets", null).use { c ->
            while (c.moveToNext()) {
                collections[c.getString(0)]?.assetIds?.add(c.getString(1))
            }
        }
        return collections.values.toList()
    }

    fun ensureCollection(name: String): MediaCollection {
        val trimmed = name.trim()
        loadCollections().firstOrNull { it.name.equals(trimmed, ignoreCase = true) }?.let { return it }
        val col = MediaCollection(id = "c" + System.currentTimeMillis(), name = trimmed)
        writableDatabase.insertWithOnConflict("collections", null, ContentValues().apply {
            put("id", col.id); put("name", col.name); put("created_at_ms", col.createdAt)
        }, SQLiteDatabase.CONFLICT_REPLACE)
        return col
    }

    fun addToCollection(collectionId: String, assetIds: List<String>): Int {
        if (assetIds.isEmpty()) return 0
        val db = writableDatabase
        var added = 0
        db.beginTransaction()
        try {
            for (id in assetIds) {
                val values = ContentValues().apply {
                    put("collection_id", collectionId); put("asset_id", id)
                }
                if (db.insertWithOnConflict("collection_assets", null, values, SQLiteDatabase.CONFLICT_IGNORE) != -1L) added++
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
        return added
    }

    // ------------------------------------------------------------------ 映射

    private fun readAsset(c: android.database.Cursor): MediaAsset = MediaAsset(
        id = c.getString(c.getColumnIndexOrThrow("id")),
        libraryId = c.getString(c.getColumnIndexOrThrow("library_id")),
        fullPath = c.getString(c.getColumnIndexOrThrow("full_path")),
        contentUri = c.getStringOrNull("content_uri"),
        fileName = c.getString(c.getColumnIndexOrThrow("file_name")),
        relativePath = c.getStringOrNull("relative_path"),
        sizeBytes = c.getLong(c.getColumnIndexOrThrow("size_bytes")),
        fileModified = c.getLong(c.getColumnIndexOrThrow("file_modified_ms")),
        kind = MediaKind.of(c.getInt(c.getColumnIndexOrThrow("kind"))),
        formatCode = c.getString(c.getColumnIndexOrThrow("format_code")),
        rawFormat = RawFormat.of(c.getStringOrNull("raw_format")),
        width = c.getIntOrNull("width"),
        height = c.getIntOrNull("height"),
        durationMs = c.getLongOrNull("duration_ms"),
        capturedAt = c.getLongOrNull("captured_at_ms"),
        capturedAtSource = c.getStringOrNull("captured_at_source"),
        cameraMake = c.getStringOrNull("camera_make"),
        cameraModel = c.getStringOrNull("camera_model"),
        deviceKey = c.getStringOrNull("device_key"),
        latitude = c.getDoubleOrNull("latitude"),
        longitude = c.getDoubleOrNull("longitude"),
        country = c.getStringOrNull("country"),
        province = c.getStringOrNull("province"),
        city = c.getStringOrNull("city"),
        citySource = c.getStringOrNull("city_source"),
        title = c.getStringOrNull("title"),
        caption = c.getStringOrNull("caption"),
        keywords = readStringList(c.getStringOrNull("keywords_json")).toMutableList(),
        themes = readStringList(c.getStringOrNull("themes_json")).toMutableList(),
        tone = c.getStringOrNull("tone"),
        rating = c.getInt(c.getColumnIndexOrThrow("rating")),
        colorLabel = c.getStringOrNull("color_label"),
        isPick = c.getIntOrNull("is_pick")?.let { it != 0 },
        postProcess = PostProcessState.of(c.getInt(c.getColumnIndexOrThrow("post_process"))),
        isLog = c.getIntOrNull("is_log")?.let { it != 0 },
        origin = c.getStringOrNull("origin"),
        lastError = c.getStringOrNull("last_error"),
    )

    private fun toValues(a: MediaAsset): ContentValues = ContentValues().apply {
        put("id", a.id)
        put("library_id", a.libraryId)
        put("full_path", a.fullPath)
        put("content_uri", a.contentUri)
        put("file_name", a.fileName)
        put("relative_path", a.relativePath)
        put("size_bytes", a.sizeBytes)
        put("file_modified_ms", a.fileModified)
        put("indexed_at_ms", System.currentTimeMillis())
        put("kind", a.kind.code)
        put("format_code", a.formatCode)
        put("raw_format", a.rawFormat.code)
        put("width", a.width)
        put("height", a.height)
        put("duration_ms", a.durationMs)
        put("captured_at_ms", a.capturedAt)
        put("captured_at_source", a.capturedAtSource)
        put("camera_make", a.cameraMake)
        put("camera_model", a.cameraModel)
        put("device_key", a.deviceKey)
        put("latitude", a.latitude)
        put("longitude", a.longitude)
        put("country", a.country)
        put("province", a.province)
        put("city", a.city)
        put("city_source", a.citySource)
        put("title", a.title)
        put("caption", a.caption)
        put("keywords_json", JSONArray(a.keywords).toString())
        put("themes_json", JSONArray(a.themes).toString())
        put("theme", a.primaryTheme)
        put("tone", a.tone)
        put("rating", a.rating)
        put("color_label", a.colorLabel)
        put("is_pick", a.isPick?.let { if (it) 1 else 0 })
        put("post_process", a.postProcess.code)
        put("is_log", a.isLog?.let { if (it) 1 else 0 })
        put("origin", a.origin)
        put("last_error", a.lastError)
    }

    private fun readStringList(json: String?): List<String> = try {
        if (json.isNullOrBlank()) emptyList() else {
            val arr = JSONArray(json)
            (0 until arr.length()).map { arr.optString(it) }.filter { it.isNotBlank() }
        }
    } catch (_: Exception) {
        emptyList()
    }

    companion object {
        private const val DB_VERSION = 1
    }
}

private fun android.database.Cursor.getColumnIndexOrThrowNullable(name: String): Int {
    val index = getColumnIndex(name)
    return index
}

private fun android.database.Cursor.getStringOrNull(name: String): String? {
    val i = getColumnIndex(name)
    return if (i < 0 || isNull(i)) null else getString(i)
}

private fun android.database.Cursor.getLongOrNull(name: String): Long? {
    val i = getColumnIndex(name)
    return if (i < 0 || isNull(i)) null else getLong(i)
}

private fun android.database.Cursor.getIntOrNull(name: String): Int? {
    val i = getColumnIndex(name)
    return if (i < 0 || isNull(i)) null else getInt(i)
}

private fun android.database.Cursor.getDoubleOrNull(name: String): Double? {
    val i = getColumnIndex(name)
    return if (i < 0 || isNull(i)) null else getDouble(i)
}