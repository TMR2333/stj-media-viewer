package com.stjarna.mediaviewer.data

import android.content.Context
import com.stjarna.mediaviewer.core.LibraryRoot
import org.json.JSONArray
import org.json.JSONObject

/** 应用设置（SharedPreferences）：外观、排序、媒体库列表、root 模式。 */
class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("stj-settings", Context.MODE_PRIVATE)

    var themeMode: String
        get() = prefs.getString("themeMode", "system") ?: "system"
        set(value) = prefs.edit().putString("themeMode", value).apply()

    var dynamicColor: Boolean
        get() = prefs.getBoolean("dynamicColor", true)
        set(value) = prefs.edit().putBoolean("dynamicColor", value).apply()

    var density: String
        get() = prefs.getString("density", "medium") ?: "medium"
        set(value) = prefs.edit().putString("density", value).apply()

    var sortId: String
        get() = prefs.getString("sortId", "capturedAt") ?: "capturedAt"
        set(value) = prefs.edit().putString("sortId", value).apply()

    var descending: Boolean
        get() = prefs.getBoolean("descending", true)
        set(value) = prefs.edit().putBoolean("descending", value).apply()

    var rootMode: Boolean
        get() = prefs.getBoolean("rootMode", false)
        set(value) = prefs.edit().putBoolean("rootMode", value).apply()

    var lastViewedAssetId: String?
        get() = prefs.getString("lastViewedAssetId", null)
        set(value) = prefs.edit().putString("lastViewedAssetId", value).apply()

    // ---------------- 云相册（WebDAV） ----------------

    var cloudUrl: String
        get() = prefs.getString("cloudUrl", "") ?: ""
        set(value) = prefs.edit().putString("cloudUrl", value.trim()).apply()

    var cloudUser: String
        get() = prefs.getString("cloudUser", "") ?: ""
        set(value) = prefs.edit().putString("cloudUser", value.trim()).apply()

    var cloudPassword: String
        get() = prefs.getString("cloudPassword", "") ?: ""
        set(value) = prefs.edit().putString("cloudPassword", value).apply()

    var cloudRoot: String
        get() = prefs.getString("cloudRoot", "") ?: ""
        set(value) = prefs.edit().putString("cloudRoot", value.trim().trim('/')).apply()

    val cloudConfigured: Boolean
        get() = cloudUrl.isNotBlank() && cloudUser.isNotBlank()

    fun libraries(): List<LibraryRoot> {
        val raw = prefs.getString("libraries", null) ?: return emptyList()
        return try {
            val arr = JSONArray(raw)
            (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                LibraryRoot(
                    id = o.optString("id"),
                    name = o.optString("name"),
                    path = o.optString("path"),
                    uri = o.optString("uri").takeIf { it.isNotBlank() },
                    createdAt = o.optLong("createdAt"),
                    lastScanAt = o.optLong("lastScanAt").takeIf { it > 0 },
                    assetCount = o.optInt("assetCount"),
                )
            }
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun saveLibraries(roots: List<LibraryRoot>) {
        val arr = JSONArray()
        roots.forEach { r ->
            arr.put(
                JSONObject().apply {
                    put("id", r.id)
                    put("name", r.name)
                    put("path", r.path)
                    put("uri", r.uri ?: "")
                    put("createdAt", r.createdAt)
                    put("lastScanAt", r.lastScanAt ?: 0)
                    put("assetCount", r.assetCount)
                }
            )
        }
        prefs.edit().putString("libraries", arr.toString()).apply()
    }
}

object LibraryRootFactory {
    const val MEDIASTORE_PATH = "@mediastore"

    fun create(name: String, path: String, uri: String? = null): LibraryRoot = LibraryRoot(
        id = "lib" + System.currentTimeMillis().toString(36),
        name = name,
        path = path,
        uri = uri,
        createdAt = System.currentTimeMillis(),
    )
}