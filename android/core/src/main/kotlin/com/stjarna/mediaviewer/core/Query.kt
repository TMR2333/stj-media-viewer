package com.stjarna.mediaviewer.core

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** 筛选伪值：表示"未填写/未分类"（与 Windows 版 `__unset__` 一致）。 */
const val UNSET = "__unset__"

/** 排序键（与 Windows 版 `AssetSortKeys` 同义）。 */
enum class SortKey(val id: String, val label: String) {
    CapturedAt("capturedAt", "拍摄时间"),
    FileName("fileName", "文件名"),
    FileModified("fileModified", "修改时间"),
    Size("size", "大小"),
    Rating("rating", "评分"),
    IndexedAt("indexedAt", "入库时间");

    companion object {
        fun of(id: String?): SortKey = entries.firstOrNull { it.id == id } ?: CapturedAt
    }
}

/** 色标：内部值与 Windows 一致用英文；界面显示中文。 */
object ColorLabels {
    val order = listOf("Red", "Yellow", "Green", "Blue", "Purple")

    fun display(value: String?): String = when (value) {
        "Red" -> "红"
        "Orange" -> "橙"
        "Yellow" -> "黄"
        "Green" -> "绿"
        "Blue" -> "蓝"
        "Purple" -> "紫"
        else -> "无"
    }

    /** 交换文件里的中文色标 → 内部英文值。 */
    fun fromChinese(value: String): String? = when (value.trim()) {
        "红" -> "Red"
        "橙", "橙色" -> "Orange"
        "黄" -> "Yellow"
        "绿" -> "Green"
        "蓝" -> "Blue"
        "紫" -> "Purple"
        else -> value.trim().takeIf { it.isNotEmpty() }
    }

    fun toChinese(value: String?): String? = when (value) {
        "Red" -> "红"
        "Orange" -> "橙"
        "Yellow" -> "黄"
        "Green" -> "绿"
        "Blue" -> "蓝"
        "Purple" -> "紫"
        else -> null
    }
}

/**
 * 素材库筛选条件。多选 = 同组 OR，跨组 AND；`UNSET` 表示"该字段为空"。
 * 与 Windows 版 `AssetQuery` 的语义一致（只是移动端少了合集/合并组/云来源等维度）。
 */
data class AssetFilter(
    var search: String = "",
    var kinds: Set<MediaKind> = emptySet(),
    var formats: Set<String> = emptySet(),
    var devices: Set<String> = emptySet(),
    var cities: Set<String> = emptySet(),
    var years: Set<Int> = emptySet(),
    var themes: Set<String> = emptySet(),
    var tones: Set<String> = emptySet(),
    var colorLabels: Set<String> = emptySet(),
    var minRating: Int? = null,
    var pickState: PickState = PickState.All,
    var onlyRaw: Boolean = false,
    var onlyLog: Boolean = false,
    var onlyUncolored: Boolean = false,
    var onlyColored: Boolean = false,
    var sort: SortKey = SortKey.CapturedAt,
    var descending: Boolean = true,
) {
    val hasAnyFacet: Boolean
        get() = kinds.isNotEmpty() || formats.isNotEmpty() || devices.isNotEmpty() ||
            cities.isNotEmpty() || years.isNotEmpty() || themes.isNotEmpty() ||
            tones.isNotEmpty() || colorLabels.isNotEmpty() || minRating != null ||
            pickState != PickState.All || onlyRaw || onlyLog || onlyUncolored || onlyColored

    fun matches(a: MediaAsset): Boolean {
        if (search.isNotBlank()) {
            val q = search.trim().lowercase()
            val haystack = buildString {
                append(a.fileName).append(' ').append(a.title ?: "").append(' ')
                append(a.city ?: "").append(' ').append(a.deviceKey ?: "").append(' ')
                append(a.keywords.joinToString(" ")).append(' ').append(a.themes.joinToString(" "))
            }.lowercase()
            if (!haystack.contains(q)) return false
        }
        if (kinds.isNotEmpty() && a.kind !in kinds) return false
        if (formats.isNotEmpty() && !formats.any { it.equals(a.formatCode, ignoreCase = true) }) return false
        if (devices.isNotEmpty() && !devices.any { matchesKey(it, a.deviceKey) }) return false
        if (cities.isNotEmpty() && !cities.any { matchesKey(it, a.city) }) return false
        if (years.isNotEmpty()) {
            val year = a.capturedYear() ?: return false
            if (year !in years) return false
        }
        if (themes.isNotEmpty() && !themes.any { t ->
                if (t == UNSET) a.themes.isEmpty() else a.hasTheme(t)
            }) return false
        if (tones.isNotEmpty() && !tones.any { matchesKey(it, a.tone) }) return false
        if (colorLabels.isNotEmpty() && !colorLabels.any { matchesKey(it, a.colorLabel) }) return false
        if (minRating != null && a.rating < minRating!!) return false
        when (pickState) {
            PickState.Picked -> if (a.isPick != true) return false
            PickState.Rejected -> if (a.isPick != false) return false
            PickState.Unmarked -> if (a.isPick != null) return false
            PickState.All -> Unit
        }
        if (onlyRaw && !a.isRaw) return false
        if (onlyLog && a.isLog != true) return false
        val colored = a.postProcess == PostProcessState.Processed
        if (onlyUncolored && colored) return false
        if (onlyColored && !colored) return false
        return true
    }

    fun sorted(list: List<MediaAsset>): List<MediaAsset> {
        val comparator: Comparator<MediaAsset> = when (sort) {
            SortKey.CapturedAt -> compareBy<MediaAsset, Long?>(nullsLast()) { it.capturedAt }
            SortKey.FileName -> compareBy(String.CASE_INSENSITIVE_ORDER) { it.fileName }
            SortKey.FileModified -> compareBy { it.fileModified }
            SortKey.Size -> compareBy { it.sizeBytes }
            SortKey.Rating -> compareBy { it.rating }
            SortKey.IndexedAt -> compareBy { it.id }
        }
        return if (descending) list.sortedWith(comparator.reversed()) else list.sortedWith(comparator)
    }

    private fun matchesKey(filter: String, value: String?): Boolean =
        if (filter == UNSET) value.isNullOrBlank() else value != null && value.equals(filter, ignoreCase = true)
}

enum class PickState(val label: String) {
    All("全部"),
    Picked("选用"),
    Rejected("废片"),
    Unmarked("未标记"),
}

/** 分面计数（筛选面板里的数字）。 */
data class Facets(
    val kinds: Map<MediaKind, Int> = emptyMap(),
    val formats: Map<String, Int> = emptyMap(),
    val devices: Map<String, Int> = emptyMap(),
    val cities: Map<String, Int> = emptyMap(),
    val years: Map<Int, Int> = emptyMap(),
    val themes: Map<String, Int> = emptyMap(),
    val tones: Map<String, Int> = emptyMap(),
    val colorLabels: Map<String, Int> = emptyMap(),
    val ratings: Map<Int, Int> = emptyMap(),
    val total: Int = 0,
    val rawCount: Int = 0,
    val logCount: Int = 0,
    val pickedCount: Int = 0,
    val rejectedCount: Int = 0,
)

fun computeFacets(assets: List<MediaAsset>): Facets {
    fun <K> bump(map: MutableMap<K, Int>, key: K) = map.merge(key, 1, Int::plus)
    val kinds = mutableMapOf<MediaKind, Int>()
    val formats = mutableMapOf<String, Int>()
    val devices = mutableMapOf<String, Int>()
    val cities = mutableMapOf<String, Int>()
    val years = mutableMapOf<Int, Int>()
    val themes = mutableMapOf<String, Int>()
    val tones = mutableMapOf<String, Int>()
    val colors = mutableMapOf<String, Int>()
    val ratings = mutableMapOf<Int, Int>()
    var raw = 0; var log = 0; var picked = 0; var rejected = 0
    for (a in assets) {
        bump(kinds, a.kind)
        if (a.formatCode.isNotBlank()) bump(formats, a.formatCode.uppercase())
        (a.deviceKey ?: UNSET).let { bump(devices, it) }
        (a.city?.takeIf { it.isNotBlank() } ?: UNSET).let { bump(cities, it) }
        a.capturedYear()?.let { bump(years, it) }
        if (a.themes.isEmpty()) bump(themes, UNSET) else a.themes.forEach { bump(themes, it) }
        (a.tone?.takeIf { it.isNotBlank() } ?: UNSET).let { bump(tones, it) }
        if (!a.colorLabel.isNullOrBlank()) bump(colors, a.colorLabel!!)
        if (a.rating > 0) bump(ratings, a.rating)
        if (a.isRaw) raw++
        if (a.isLog == true) log++
        if (a.isPick == true) picked++
        if (a.isPick == false) rejected++
    }
    return Facets(
        kinds = kinds, formats = formats, devices = devices, cities = cities, years = years,
        themes = themes, tones = tones, colorLabels = colors, ratings = ratings,
        total = assets.size, rawCount = raw, logCount = log, pickedCount = picked, rejectedCount = rejected,
    )
}

private fun MediaAsset.capturedYear(): Int? {
    val ms = capturedAt ?: return null
    return Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()).year
}

val MediaAsset.capturedDay: String?
    get() = capturedAt?.let {
        Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault()).toLocalDate().toString()
    }

val MediaAsset.capturedText: String?
    get() = capturedAt?.let {
        DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")
            .format(Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault()))
    }

/** 设备显示名（"Sony ILCE-7M4" 之类）；与 Windows 版 `DeviceKey` 口径一致。 */
object DeviceNames {
    private val brandPrefix = Regex("^(SONY|CANON|NIKON|FUJIFILM|PANASONIC|OLYMPUS|OM DIGITAL SOLUTIONS|LEICA|HASSELBLAD|DJI|APPLE|SIGMA|PENTAX|RICOH)\\s+", RegexOption.IGNORE_CASE)

    fun normalizeKey(make: String?, model: String?): String? {
        val m = model?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        var key = m.replace(Regex("\\s+"), " ")
        val brand = make?.trim()?.takeIf { it.isNotEmpty() }
        if (brand != null && !key.startsWith(brand, ignoreCase = true)) {
            val short = brand.split(' ').first()
            if (!key.contains(short, ignoreCase = true)) key = "$short $key"
        }
        key = brandPrefix.replace(key, "").trim()
        return if (key.isEmpty()) null else key
    }

    fun display(key: String?): String = key?.takeIf { it.isNotBlank() } ?: "未知设备"
}