package com.stjarna.mediaviewer.core

/** 扩展名 → 大类/格式/RAW 判定（与 Windows 版 `MediaTypeCatalog` 的判定口径一致）。 */
object FormatCatalog {
    private val rawByExtension: Map<String, RawFormat> = buildMap {
        fun add(raw: RawFormat, vararg exts: String) = exts.forEach { put(it, raw) }
        add(RawFormat.Arw, "arw", "srf", "sr2")
        add(RawFormat.Cr2, "cr2")
        add(RawFormat.Cr3, "cr3")
        add(RawFormat.Nef, "nef")
        add(RawFormat.Nrw, "nrw")
        add(RawFormat.Raf, "raf")
        add(RawFormat.Orf, "orf")
        add(RawFormat.Rw2, "rw2", "raw")
        add(RawFormat.Pef, "pef")
        add(RawFormat.Srw, "srw")
        add(RawFormat.X3f, "x3f")
        add(RawFormat.Dng, "dng")
        add(RawFormat.Iiq, "iiq")
        add(RawFormat.Mos, "mos")
        add(RawFormat.Gpr, "gpr")
        add(RawFormat.ThreeFr, "3fr")
    }

    private val imageFormats: Map<String, String> = mapOf(
        "jpg" to "JPG", "jpeg" to "JPG", "jpe" to "JPG",
        "png" to "PNG", "tif" to "TIFF", "tiff" to "TIFF",
        "heic" to "HEIC", "heif" to "HEIF", "avif" to "AVIF",
        "webp" to "WEBP", "bmp" to "BMP", "gif" to "GIF", "psd" to "PSD",
    )

    private val videoFormats: Map<String, String> = mapOf(
        "mp4" to "MP4", "m4v" to "M4V", "mov" to "MOV", "mkv" to "MKV",
        "webm" to "WEBM", "avi" to "AVI", "mxf" to "MXF", "3gp" to "3GP",
    )

    private val audioFormats: Map<String, String> = mapOf(
        "wav" to "WAV", "mp3" to "MP3", "flac" to "FLAC", "ogg" to "OGG",
        "opus" to "OPUS", "aiff" to "AIFF", "aif" to "AIFF", "m4a" to "M4A",
    )

    private val sidecarExtensions = setOf(
        "xmp", "acr", "dop", "pp3", "xmp.bak", "cos", "on1", "arp",
    )

    /** 不要入库/展示的垃圾文件（AppleDouble、系统缓存等）。 */
    fun isJunkName(name: String): Boolean {
        val n = name.trim()
        if (n.isEmpty()) return true
        if (n.startsWith(".")) return true
        val lower = n.lowercase()
        return lower in setOf(
            "thumbs.db", "ehthumbs.db", "desktop.ini", "icon\r", ".ds_store",
            ".directory", ".localized", "node_modules",
        ) || lower.startsWith("._")
    }

    fun isSidecar(name: String): Boolean {
        val lower = name.lowercase()
        return sidecarExtensions.any { lower.endsWith(".$it") }
    }

    fun detect(name: String): FileKind {
        val lower = name.lowercase()
        val ext = lower.substringAfterLast('.', "")
        if (ext.isEmpty()) return FileKind(MediaKind.Unknown, "", RawFormat.None)
        rawByExtension[ext]?.let { return FileKind(MediaKind.Image, it.code, it) }
        imageFormats[ext]?.let { return FileKind(MediaKind.Image, it, RawFormat.None) }
        videoFormats[ext]?.let { return FileKind(MediaKind.Video, it, RawFormat.None) }
        audioFormats[ext]?.let { return FileKind(MediaKind.Audio, it, RawFormat.None) }
        return FileKind(MediaKind.Unknown, "", RawFormat.None)
    }

    data class FileKind(val kind: MediaKind, val formatCode: String, val rawFormat: RawFormat)

    /** 可识别的媒体扩展名（扫描时用，避免逐文件调 detect 也可以）。 */
    val allMediaExtensions: Set<String> = buildSet {
        addAll(rawByExtension.keys)
        addAll(imageFormats.keys)
        addAll(videoFormats.keys)
        addAll(audioFormats.keys)
    }

    /** 视频扩展名，用于 MediaStore 与缩略图分支。 */
    val videoExtensions: Set<String> = videoFormats.keys
}