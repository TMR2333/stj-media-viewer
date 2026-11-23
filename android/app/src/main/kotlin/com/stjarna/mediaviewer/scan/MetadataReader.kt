package com.stjarna.mediaviewer.scan

import android.content.Context
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import androidx.exifinterface.media.ExifInterface
import com.stjarna.mediaviewer.core.DeviceNames
import com.stjarna.mediaviewer.core.FormatCatalog
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import com.stjarna.mediaviewer.core.PathUtil
import com.stjarna.mediaviewer.core.geo.OfflineGeocoder
import com.stjarna.mediaviewer.core.meta.XmpCodec
import java.io.File
import java.text.SimpleDateFormat
import java.util.Locale

/** 扫描阶段发现的一个文件（路径或 SAF/MediaStore URI）。 */
data class DiscoveredFile(
    val path: String,
    val displayName: String,
    val sizeBytes: Long,
    val modifiedMs: Long,
    val contentUri: String? = null,
    val relativePath: String? = null,
)

/** 元数据读取：EXIF / 视频容器 / XMP 边车 + 离线城市反查。 */
class MetadataReader(
    private val context: Context,
    private val geocoderProvider: () -> OfflineGeocoder?,
) {
    private val exifDateFormat = SimpleDateFormat("yyyy:MM:dd HH:mm:ss", Locale.US)

    fun read(file: DiscoveredFile, libraryId: String): MediaAsset? {
        val kindInfo = FormatCatalog.detect(file.displayName)
        if (kindInfo.kind == MediaKind.Unknown) return null

        val asset = MediaAsset(
            id = MediaAsset.makeId(file.path),
            libraryId = libraryId,
            fullPath = file.path,
            contentUri = file.contentUri,
            fileName = file.displayName,
            relativePath = file.relativePath,
            sizeBytes = file.sizeBytes,
            fileModified = file.modifiedMs,
            kind = kindInfo.kind,
            formatCode = kindInfo.formatCode,
            rawFormat = kindInfo.rawFormat,
        )

        try {
            when (kindInfo.kind) {
                MediaKind.Image -> readImage(asset)
                MediaKind.Video -> readVideo(asset)
                MediaKind.Audio -> readAudio(asset)
                else -> Unit
            }
            applySidecar(asset)
            applyGeocoder(asset)
        } catch (ex: Exception) {
            asset.lastError = ex.message
        }
        return asset
    }

    private fun readImage(asset: MediaAsset) {
        val exif = openExif(asset) ?: return
        asset.width = exif.getAttributeInt(ExifInterface.TAG_IMAGE_WIDTH, 0).takeIf { it > 0 }
            ?: exif.getAttributeInt(ExifInterface.TAG_PIXEL_X_DIMENSION, 0).takeIf { it > 0 }
        asset.height = exif.getAttributeInt(ExifInterface.TAG_IMAGE_LENGTH, 0).takeIf { it > 0 }
            ?: exif.getAttributeInt(ExifInterface.TAG_PIXEL_Y_DIMENSION, 0).takeIf { it > 0 }

        val date = exif.getAttribute(ExifInterface.TAG_DATETIME_ORIGINAL)
            ?: exif.getAttribute(ExifInterface.TAG_DATETIME)
        if (date != null) {
            runCatching { exifDateFormat.parse(date)?.time }.getOrNull()?.let {
                asset.capturedAt = it
                asset.capturedAtSource = "exif"
            }
        }
        asset.cameraMake = exif.getAttribute(ExifInterface.TAG_MAKE)?.trim()?.takeIf { it.isNotEmpty() }
        asset.cameraModel = exif.getAttribute(ExifInterface.TAG_MODEL)?.trim()?.takeIf { it.isNotEmpty() }
        asset.deviceKey = DeviceNames.normalizeKey(asset.cameraMake, asset.cameraModel)

        val latLong = FloatArray(2)
        if (exif.getLatLong(latLong)) {
            asset.latitude = latLong[0].toDouble()
            asset.longitude = latLong[1].toDouble()
        }
        asset.title = null
    }

    private fun readVideo(asset: MediaAsset) {
        val retriever = MediaMetadataRetriever()
        try {
            if (asset.contentUri != null) retriever.setDataSource(context, Uri.parse(asset.contentUri))
            else retriever.setDataSource(asset.fullPath)
            asset.width = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull()
            asset.height = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull()
            asset.durationMs = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull()
            val date = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DATE)
            if (!date.isNullOrBlank()) {
                runCatching {
                    SimpleDateFormat("yyyyMMdd'T'HHmmss", Locale.US).parse(date.take(15))?.time
                }.getOrNull()?.let {
                    asset.capturedAt = it
                    asset.capturedAtSource = "quicktime"
                }
            }
        } catch (_: Exception) {
            asset.lastError = "视频元数据读取失败"
        } finally {
            runCatching { retriever.release() }
        }
    }

    private fun readAudio(asset: MediaAsset) {
        val retriever = MediaMetadataRetriever()
        try {
            if (asset.contentUri != null) retriever.setDataSource(context, Uri.parse(asset.contentUri))
            else retriever.setDataSource(asset.fullPath)
            asset.durationMs = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull()
        } catch (_: Exception) {
            // 音频元数据失败不影响入库
        } finally {
            runCatching { retriever.release() }
        }
    }

    private fun openExif(asset: MediaAsset): ExifInterface? = try {
        if (asset.contentUri != null) {
            context.contentResolver.openInputStream(Uri.parse(asset.contentUri))?.use { ExifInterface(it) }
        } else {
            ExifInterface(asset.fullPath)
        }
    } catch (_: Exception) {
        null
    }

    /** 边车 XMP：`xxx.jpg.xmp` 或 `xxx.xmp`（仅本地文件路径；SAF 暂不读兄弟文档）。 */
    private fun applySidecar(asset: MediaAsset) {
        if (asset.contentUri != null) return
        val candidates = listOf(File(asset.fullPath + ".xmp"), File(stripExtension(asset.fullPath) + ".xmp"))
        val sidecar = candidates.firstOrNull { it.isFile && it.length() > 0 } ?: return
        val xmp = runCatching { XmpCodec.parse(sidecar.readText()) }.getOrNull() ?: return
        xmp.rating?.takeIf { it > 0 }?.let { asset.rating = it }
        xmp.label?.let { asset.colorLabel = XmpCodec.labelToInternal(it) }
        if (xmp.keywords.isNotEmpty()) asset.keywords = xmp.keywords.toMutableList()
        xmp.city?.takeIf { it.isNotBlank() }?.let {
            asset.city = it
            asset.citySource = "xmp"
        }
        xmp.title?.takeIf { it.isNotBlank() }?.let { asset.title = it }
        xmp.theme?.takeIf { it.isNotBlank() }?.let { asset.themes = mutableListOf(it) }
        xmp.tone?.takeIf { it.isNotBlank() }?.let { asset.tone = it }
    }

    private fun applyGeocoder(asset: MediaAsset) {
        val lat = asset.latitude ?: return
        val lon = asset.longitude ?: return
        if (!asset.city.isNullOrBlank()) return
        val geocoder = geocoderProvider() ?: return
        val city = runCatching { geocoder.nearest(lat, lon) }.getOrNull() ?: return
        asset.city = city.display
        asset.citySource = "offline"
    }

    private fun stripExtension(path: String): String {
        val idx = path.lastIndexOf('.')
        return if (idx > path.lastIndexOf('/')) path.substring(0, idx) else path
    }
}