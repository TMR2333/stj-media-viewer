package com.stjarna.mediaviewer.core

import kotlinx.serialization.Serializable

/** 素材大类。数值与 Windows 版 `MediaKind` 一致（Unknown=0 / Image=1 / Video=2 / Audio=3）。 */
enum class MediaKind(val code: Int, val label: String) {
    Unknown(0, "未知"),
    Image(1, "图片"),
    Video(2, "视频"),
    Audio(3, "音频");

    companion object {
        fun of(code: Int): MediaKind = entries.firstOrNull { it.code == code } ?: Unknown
    }
}

/** RAW 格式（与 Windows 版 `RawFormat` 同名，便于交换文件/索引库互通）。 */
enum class RawFormat(val code: String) {
    None(""),
    Arw("ARW"), Srf("SRF"), Cr2("CR2"), Cr3("CR3"), Nef("NEF"), Nrw("NRW"),
    Raf("RAF"), Orf("ORF"), Rw2("RW2"), Pef("PEF"), Srw("SRW"), X3f("X3F"),
    Dng("DNG"), Iiq("IIQ"), Mos("MOS"), Gpr("GPR"), ThreeFr("3FR");

    companion object {
        private val byCode = entries.associateBy { it.code }
        fun of(code: String?): RawFormat = byCode[code?.uppercase() ?: ""] ?: None
    }
}

/** 后期状态。数值与 Windows 版 `PostProcessState` 一致。 */
enum class PostProcessState(val code: Int) {
    Unknown(0), Unprocessed(1), Processed(2);

    companion object {
        fun of(code: Int): PostProcessState = entries.firstOrNull { it.code == code } ?: Unknown
    }
}

/** 一条素材。字段与 Windows `catalog.db` 的 assets 表子集同名同义。 */
data class MediaAsset(
    var id: String = "",
    var libraryId: String = "",
    var fullPath: String = "",
    /** Android 专用：SAF / MediaStore 的 content:// 地址（桌面端为 null）。 */
    var contentUri: String? = null,
    var fileName: String = "",
    var relativePath: String? = null,
    var sizeBytes: Long = 0,
    var fileModified: Long = 0,
    var kind: MediaKind = MediaKind.Unknown,
    var formatCode: String = "",
    var mimeType: String? = null,
    var rawFormat: RawFormat = RawFormat.None,
    var width: Int? = null,
    var height: Int? = null,
    var durationMs: Long? = null,
    var capturedAt: Long? = null,
    var capturedAtSource: String? = null,
    var cameraMake: String? = null,
    var cameraModel: String? = null,
    var deviceKey: String? = null,
    var latitude: Double? = null,
    var longitude: Double? = null,
    var country: String? = null,
    var province: String? = null,
    var city: String? = null,
    var citySource: String? = null,
    var title: String? = null,
    var caption: String? = null,
    var keywords: MutableList<String> = mutableListOf(),
    var themes: MutableList<String> = mutableListOf(),
    var tone: String? = null,
    var rating: Int = 0,
    var colorLabel: String? = null,
    var isPick: Boolean? = null,
    var postProcess: PostProcessState = PostProcessState.Unknown,
    var isLog: Boolean? = null,
    var origin: String? = null,
    var lastError: String? = null,
) {
    val isRaw: Boolean get() = rawFormat != RawFormat.None

    /** 主主题（列表第一项），与 Windows 版 `PrimaryTheme` 一致。 */
    val primaryTheme: String? get() = themes.firstOrNull()

    fun hasTheme(theme: String): Boolean =
        themes.any { it.equals(theme, ignoreCase = true) }

    companion object {
        /** 与 Windows 版一致：`a` + 规范化路径的小写稳定哈希（32 位十六进制）。 */
        fun makeId(fullPath: String): String {
            val normalized = PathUtil.normalize(fullPath).lowercase()
            var hash = -0x340d631b // FNV-1a 32 offset basis
            for (ch in normalized) {
                hash = hash xor ch.code
                hash *= 0x01000193
            }
            return "a" + (hash.toUInt().toString(16).padStart(8, '0')) +
                "_" + normalized.hashCode().toUInt().toString(16)
        }
    }
}

/** 媒体库根（Android 版通常一个或多个用户选中的目录）。 */
data class LibraryRoot(
    var id: String = "",
    var name: String = "",
    var path: String = "",
    var uri: String? = null,
    var createdAt: Long = System.currentTimeMillis(),
    var lastScanAt: Long? = null,
    var assetCount: Int = 0,
)

/** 影像合集（与 Windows 版同构；移动端不做筛选 UI，但交换文件要能往返）。 */
data class Collection(
    var id: String = "",
    var name: String = "",
    var createdAt: Long = System.currentTimeMillis(),
    var assetIds: MutableSet<String> = mutableSetOf(),
)

object PathUtil {
    fun normalize(path: String): String =
        path.replace('\\', '/').replace(Regex("/+"), "/").trimEnd('/')

    fun fileName(path: String): String = normalize(path).substringAfterLast('/')

    fun relativeTo(path: String, root: String): String? {
        val p = normalize(path)
        val r = normalize(root)
        if (!p.startsWith("$r/")) return null
        return p.removePrefix("$r/")
    }

    fun isUnder(path: String, root: String): Boolean {
        val p = normalize(path)
        val r = normalize(root)
        return p.equals(r, ignoreCase = true) || p.startsWith("$r/", ignoreCase = true)
    }
}