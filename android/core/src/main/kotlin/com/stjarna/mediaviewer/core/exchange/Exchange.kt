package com.stjarna.mediaviewer.core.exchange

import com.stjarna.mediaviewer.core.ColorLabels
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.PostProcessState
import com.stjarna.mediaviewer.core.SortKey
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * 跨平台交换文件 v1（与 Windows 版 `StjMediaViewer.Core.Exchange`、macOS 版 `SettingsExchange.swift` 同一契约）。
 * 只含界面偏好 + 用户编辑成果 + 合集结构，**绝不含任何密钥**。
 */
@Serializable
data class ExchangeDocument(
    var format: String = FORMAT_ID,
    var formatVersion: Int = CURRENT_VERSION,
    var app: ExchangeAppInfo = ExchangeAppInfo(),
    var settings: ExchangeSettings = ExchangeSettings(),
    var assets: MutableList<ExchangeAsset> = mutableListOf(),
    var collections: MutableList<ExchangeCollection> = mutableListOf(),
) {
    companion object {
        const val FORMAT_ID = "stj-media-viewer/exchange"
        const val CURRENT_VERSION = 1
    }
}

@Serializable
data class ExchangeAppInfo(
    var name: String = "STJ Media Viewer",
    var version: String = "",
    var platform: String = "android",
    var exportedAt: String = "",
)

@Serializable
data class ExchangeSettings(
    var themeMode: String? = null,
    var density: String? = null,
    var sort: ExchangeSort? = null,
    var panels: ExchangePanels? = null,
    var foldRawPairs: Boolean? = null,
)

@Serializable
data class ExchangeSort(var key: String? = null, var ascending: Boolean = false)

@Serializable
data class ExchangePanels(var sidebar: Boolean = true, var inspector: Boolean = true)

@Serializable
data class ExchangeAsset(
    var fileName: String = "",
    var sizeBytes: Long = 0,
    var relativePath: String? = null,
    var rating: Int? = null,
    var colorLabel: String? = null,
    var isPick: Int? = null,
    var country: String? = null,
    var province: String? = null,
    var city: String? = null,
    var themes: List<String>? = null,
    var tone: String? = null,
    var keywords: List<String>? = null,
    var title: String? = null,
    var postProcess: Int? = null,
) {
    val hasAnyEdit: Boolean
        get() = rating != null || colorLabel != null || isPick != null ||
            !country.isNullOrBlank() || !province.isNullOrBlank() || !city.isNullOrBlank() ||
            !themes.isNullOrEmpty() || !tone.isNullOrBlank() ||
            !keywords.isNullOrEmpty() || !title.isNullOrBlank() || postProcess != null
}

@Serializable
data class ExchangeCollection(
    var name: String = "",
    var assets: MutableList<ExchangeAssetKey> = mutableListOf(),
)

@Serializable
data class ExchangeAssetKey(
    var fileName: String = "",
    var sizeBytes: Long = 0,
    var relativePath: String? = null,
)

enum class MergeMode { FillEmpty, Overwrite }

class ExchangeFormatException(message: String) : Exception(message)

object ExchangeCodec {
    val json: Json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
        prettyPrint = true
        prettyPrintIndent = "  "
    }

    fun encode(doc: ExchangeDocument): String = json.encodeToString(ExchangeDocument.serializer(), doc)

    fun decode(text: String): ExchangeDocument {
        val doc = try {
            json.decodeFromString(ExchangeDocument.serializer(), text)
        } catch (ex: Exception) {
            throw ExchangeFormatException("文件不是有效的交换文件：" + (ex.message ?: "格式错误"))
        }
        validate(doc)
        return doc
    }

    fun validate(doc: ExchangeDocument) {
        if (doc.format != ExchangeDocument.FORMAT_ID) {
            throw ExchangeFormatException("不是本软件的交换文件（format = ${doc.format}，应为 ${ExchangeDocument.FORMAT_ID}）")
        }
        if (doc.formatVersion < 1) {
            throw ExchangeFormatException("交换文件版本无效（formatVersion = ${doc.formatVersion}）")
        }
        if (doc.formatVersion > ExchangeDocument.CURRENT_VERSION) {
            throw ExchangeFormatException(
                "交换文件来自更新的版本（formatVersion = ${doc.formatVersion}，当前支持 ${ExchangeDocument.CURRENT_VERSION}），请先更新应用。"
            )
        }
    }
}

// =====================================================================
//  导出
// =====================================================================

data class ExportSettings(
    var themeMode: String? = null,
    var density: String? = null,
    var sort: SortKey = SortKey.CapturedAt,
    var descending: Boolean = true,
)

object ExchangeExporter {
    /** 只导出"有编辑"的素材 + 全部合集成员；按（文件名+大小）去重。 */
    fun build(
        assets: List<MediaAsset>,
        collections: List<Triple<String, List<MediaAsset>, Long>>,
        settings: ExportSettings,
        appVersion: String,
        exportedAt: String,
    ): ExchangeDocument {
        val memberIds = collections.flatMap { it.second }.map { it.id }.toSet()
        val seen = HashSet<String>()
        val exported = mutableListOf<ExchangeAsset>()

        for (a in assets) {
            val e = toExchangeAsset(a)
            val inCollection = a.id in memberIds
            if (!e.hasAnyEdit && !inCollection) continue
            if (!seen.add(keyOf(a.fileName, a.sizeBytes))) continue
            exported += e
        }

        return ExchangeDocument(
            app = ExchangeAppInfo(version = appVersion, platform = "android", exportedAt = exportedAt),
            settings = ExchangeSettings(
                themeMode = settings.themeMode,
                density = toExchangeDensity(settings.density),
                sort = ExchangeSort(key = toExchangeSortKey(settings.sort), ascending = !settings.descending),
            ),
            assets = exported,
            collections = collections.map { (name, members, _) ->
                ExchangeCollection(
                    name = name,
                    assets = members.map {
                        ExchangeAssetKey(
                            fileName = it.fileName,
                            sizeBytes = it.sizeBytes,
                            relativePath = it.relativePath?.replace('\\', '/'),
                        )
                    }.toMutableList(),
                )
            }.toMutableList(),
        )
    }

    fun toExchangeAsset(a: MediaAsset): ExchangeAsset = ExchangeAsset(
        fileName = a.fileName,
        sizeBytes = a.sizeBytes,
        relativePath = a.relativePath?.replace('\\', '/'),
        rating = a.rating.takeIf { it > 0 },
        colorLabel = ColorLabels.toChinese(a.colorLabel),
        isPick = a.isPick?.let { if (it) 1 else 0 },
        country = a.country?.takeIf { it.isNotBlank() },
        province = a.province?.takeIf { it.isNotBlank() },
        city = a.city?.takeIf { it.isNotBlank() },
        themes = a.themes.takeIf { it.isNotEmpty() }?.toList(),
        tone = a.tone?.takeIf { it.isNotBlank() },
        keywords = a.keywords.takeIf { it.isNotEmpty() }?.toList(),
        title = a.title?.takeIf { it.isNotBlank() },
        postProcess = if (a.postProcess == PostProcessState.Processed) 2 else null,
    )

    fun toExchangeDensity(density: String?): String? = when (density?.lowercase()) {
        "small", "compact" -> "small"
        "medium", "comfortable" -> "medium"
        "large", "spacious" -> "large"
        "list" -> null
        else -> null
    }

    fun toExchangeSortKey(sort: SortKey): String = when (sort) {
        SortKey.CapturedAt -> "captured"
        SortKey.FileName -> "fileName"
        SortKey.FileModified -> "modified"
        SortKey.Size -> "size"
        SortKey.Rating -> "rating"
        SortKey.IndexedAt -> "indexed"
    }

    private fun keyOf(fileName: String, size: Long) = fileName.trim().lowercase() + "|" + size
}

// =====================================================================
//  导入：匹配 + 合并
// =====================================================================

data class MatchedAsset(
    val source: ExchangeAsset,
    val target: MediaAsset,
    val ambiguous: Boolean,
)

data class CollectionPlan(
    val name: String,
    val assetIds: List<String>,
    val missing: List<String>,
)

data class ImportPlan(
    val document: ExchangeDocument,
    val matches: List<MatchedAsset>,
    val unmatched: List<String>,
    val ambiguous: List<String>,
    val collections: List<CollectionPlan>,
) {
    val hasSettings: Boolean get() = document.settings.let {
        !it.themeMode.isNullOrBlank() || !it.density.isNullOrBlank() || it.sort?.key != null
    }
}

object ExchangeImporter {
    fun buildPlan(doc: ExchangeDocument, assets: List<MediaAsset>): ImportPlan {
        ExchangeCodec.validate(doc)
        val byKey = HashMap<String, MutableList<MediaAsset>>()
        for (a in assets) byKey.getOrPut(keyOf(a.fileName, a.sizeBytes)) { mutableListOf() }.add(a)

        val matches = mutableListOf<MatchedAsset>()
        val unmatched = mutableListOf<String>()
        val ambiguous = mutableListOf<String>()

        for (item in doc.assets) {
            val candidates = byKey[keyOf(item.fileName, item.sizeBytes)]
            if (candidates.isNullOrEmpty()) {
                unmatched += item.fileName
                continue
            }
            if (candidates.size == 1) {
                matches += MatchedAsset(item, candidates[0], false)
            } else {
                val wanted = item.relativePath?.replace('\\', '/')?.trimStart('/')
                val narrowed = candidates.filter { it.relativePath?.replace('\\', '/')?.trimStart('/') == wanted }
                if (narrowed.size == 1) {
                    matches += MatchedAsset(item, narrowed[0], false)
                } else {
                    ambiguous += item.fileName
                    matches += MatchedAsset(item, candidates[0], true)
                }
            }
        }

        val collections = doc.collections.map { col ->
            val ids = mutableListOf<String>()
            val missing = mutableListOf<String>()
            for (m in col.assets) {
                val candidates = byKey[keyOf(m.fileName, m.sizeBytes)]
                if (candidates.isNullOrEmpty()) {
                    missing += m.fileName
                    continue
                }
                val wanted = m.relativePath?.replace('\\', '/')?.trimStart('/')
                val chosen = if (candidates.size == 1) candidates[0]
                else candidates.firstOrNull { it.relativePath?.replace('\\', '/')?.trimStart('/') == wanted }
                    ?: candidates[0]
                if (chosen.id.isNotEmpty()) ids += chosen.id
            }
            CollectionPlan(col.name, ids.distinct(), missing)
        }

        return ImportPlan(doc, matches, unmatched, ambiguous, collections)
    }

    /** 计算"只改需要改的字段"的补丁；null = 无需写入。 */
    fun buildPatch(src: ExchangeAsset, current: MediaAsset, mode: MergeMode): AssetPatch? {
        val fill = mode == MergeMode.FillEmpty
        val patch = AssetPatch()
        var changed = false

        if (src.rating != null && src.rating!! > 0 && (!fill || current.rating <= 0)) {
            patch.rating = src.rating!!.coerceIn(0, 5); changed = true
        }
        if (!src.colorLabel.isNullOrBlank()) {
            val color = ColorLabels.fromChinese(src.colorLabel!!)
            if (color != null && (!fill || current.colorLabel.isNullOrBlank())) {
                patch.colorLabel = color; changed = true
            }
        }
        if (src.isPick != null && (!fill || current.isPick == null)) {
            patch.isPick = src.isPick != 0; changed = true
        }
        if (!src.country.isNullOrBlank() && (!fill || current.country.isNullOrBlank())) {
            patch.country = src.country!!.trim(); changed = true
        }
        if (!src.province.isNullOrBlank() && (!fill || current.province.isNullOrBlank())) {
            patch.province = src.province!!.trim(); changed = true
        }
        if (!src.city.isNullOrBlank() && (!fill || current.city.isNullOrBlank())) {
            patch.city = src.city!!.trim(); changed = true
        }

        val fileThemes = src.themes.orEmpty().map { it.trim() }.filter { it.isNotEmpty() }
        if (fileThemes.isNotEmpty()) {
            val merged = current.themes.toMutableList()
            for (t in fileThemes) if (merged.none { it.equals(t, true) }) merged += t
            if (!fill || current.themes.isEmpty() || merged.size != current.themes.size) {
                patch.themes = merged; changed = true
            }
        }
        if (!src.tone.isNullOrBlank() && (!fill || current.tone.isNullOrBlank())) {
            patch.tone = src.tone!!.trim(); changed = true
        }
        if (!src.keywords.isNullOrEmpty()) {
            val merged = current.keywords.toMutableList()
            for (k in src.keywords!!.map { it.trim() }.filter { it.isNotEmpty() }) {
                if (merged.none { it.equals(k, true) }) merged += k
            }
            if (merged.size != current.keywords.size) {
                patch.keywords = merged; changed = true
            }
        }
        if (!src.title.isNullOrBlank() && (!fill || current.title.isNullOrBlank())) {
            patch.title = src.title!!.trim(); changed = true
        }
        if (src.postProcess != null && src.postProcess!! > 1 &&
            (!fill || current.postProcess == PostProcessState.Unknown || current.postProcess == PostProcessState.Unprocessed)
        ) {
            patch.postProcess = PostProcessState.Processed; changed = true
        }
        return if (changed) patch else null
    }

    private fun keyOf(fileName: String, size: Long) = fileName.trim().lowercase() + "|" + size
}

/** 只描述"要写入哪些字段"；null = 不改动。 */
data class AssetPatch(
    var rating: Int? = null,
    var colorLabel: String? = null,
    var isPick: Boolean? = null,
    var country: String? = null,
    var province: String? = null,
    var city: String? = null,
    var themes: List<String>? = null,
    var tone: String? = null,
    var keywords: List<String>? = null,
    var title: String? = null,
    var postProcess: PostProcessState? = null,
)

// =====================================================================
//  设置导入映射
// =====================================================================

data class SettingsApply(
    val themeMode: String? = null,
    val density: String? = null,
    val sortKey: SortKey? = null,
    val ascending: Boolean = false,
    val notes: List<String> = emptyList(),
)

object ExchangeSettingsApply {
    /** Windows/macOS 的 density 枚举 → Android 网格密度（small/medium/large）。 */
    fun densityFromExchange(value: String?): String? = when (value?.lowercase()) {
        "small", "compact" -> "small"
        "medium", "comfortable" -> "medium"
        "large", "spacious" -> "large"
        else -> null
    }

    fun sortFromExchange(key: String?): SortKey? = when (key) {
        "captured" -> SortKey.CapturedAt
        "fileName" -> SortKey.FileName
        "modified" -> SortKey.FileModified
        "size" -> SortKey.Size
        "rating" -> SortKey.Rating
        "indexed" -> SortKey.IndexedAt
        else -> null
    }

    fun apply(doc: ExchangeDocument): SettingsApply {
        val s = doc.settings
        val notes = mutableListOf<String>()
        val theme = s.themeMode?.lowercase()?.takeIf { it in setOf("system", "light", "dark") }
        if (theme != null) notes += "明暗模式 → $theme"
        val density = densityFromExchange(s.density)
        if (density != null) notes += "网格密度 → $density"
        val sort = sortFromExchange(s.sort?.key)
        if (sort != null) notes += "排序 → ${sort.label}${if (s.sort?.ascending == true) " ↑" else " ↓"}"
        return SettingsApply(theme, density, sort, s.sort?.ascending ?: false, notes)
    }
}