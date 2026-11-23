package com.stjarna.mediaviewer.scan

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.util.LruCache
import androidx.exifinterface.media.ExifInterface
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import com.stjarna.mediaviewer.core.media.ByteArraySource
import com.stjarna.mediaviewer.core.media.TiffPreview
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/**
 * 缩略图加载：
 * - 普通图片/HEIC：BitmapFactory 采样解码 + EXIF 方向校正；
 * - RAW：TiffPreview 取内嵌 JPEG（快路径/流式扫描），解不出来就返回占位；
 * - 视频：MediaMetadataRetriever 首帧；
 * - 内存 LRU（按像素数计），避免长列表 OOM。
 */
class ThumbnailLoader(private val context: Context) {
    private val maxBytes = (Runtime.getRuntime().maxMemory() / 8).coerceAtMost(160L * 1024 * 1024).toInt()
    private val cache = object : LruCache<String, Bitmap>(maxBytes) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }

    suspend fun load(asset: MediaAsset, targetPx: Int = 360): Bitmap? = withContext(Dispatchers.IO) {
        val key = asset.id + "@" + targetPx
        cache.get(key)?.let { return@withContext it }
        val bitmap = runCatching { decode(asset, targetPx) }.getOrNull() ?: return@withContext null
        cache.put(key, bitmap)
        bitmap
    }

    private fun decode(asset: MediaAsset, targetPx: Int): Bitmap? = when (asset.kind) {
        MediaKind.Video -> decodeVideo(asset, targetPx)
        MediaKind.Image -> if (asset.isRaw) decodeRaw(asset, targetPx) else decodeImage(asset, targetPx)
        else -> null
    }

    private fun openStream(asset: MediaAsset) = if (asset.contentUri != null) {
        context.contentResolver.openInputStream(Uri.parse(asset.contentUri))
    } else {
        File(asset.fullPath).inputStream()
    }

    private fun decodeImage(asset: MediaAsset, targetPx: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        openStream(asset)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        val sample = sampleSize(bounds.outWidth, bounds.outHeight, targetPx)
        val options = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        }
        val bitmap = openStream(asset)?.use { BitmapFactory.decodeStream(it, null, options) } ?: return null
        return applyOrientation(bitmap, asset)
    }

    private fun decodeRaw(asset: MediaAsset, targetPx: Int): Bitmap? {
        val bytes = if (asset.contentUri != null) {
            openStream(asset)?.use { it.readBytes() } ?: return null
        } else {
            val file = File(asset.fullPath)
            if (!file.isFile) return null
            file.readBytes()
        }
        val preview = TiffPreview.extract(ByteArraySource(bytes))?.bytes ?: return null
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(preview, 0, preview.size, bounds)
        val sample = sampleSize(bounds.outWidth, bounds.outHeight, targetPx)
        val options = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        }
        return BitmapFactory.decodeByteArray(preview, 0, preview.size, options)
    }

    private fun decodeVideo(asset: MediaAsset, targetPx: Int): Bitmap? {
        val retriever = MediaMetadataRetriever()
        return try {
            if (asset.contentUri != null) retriever.setDataSource(context, Uri.parse(asset.contentUri))
            else retriever.setDataSource(asset.fullPath)
            retriever.getFrameAtTime(0, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
                ?.let { scaleDown(it, targetPx) }
        } catch (_: Exception) {
            null
        } finally {
            runCatching { retriever.release() }
        }
    }

    private fun applyOrientation(bitmap: Bitmap, asset: MediaAsset): Bitmap {
        val orientation = runCatching {
            val exif = if (asset.contentUri != null) {
                context.contentResolver.openInputStream(Uri.parse(asset.contentUri))?.use { ExifInterface(it) }
            } else ExifInterface(asset.fullPath)
            exif?.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
        }.getOrNull() ?: ExifInterface.ORIENTATION_NORMAL
        val degrees = when (orientation) {
            ExifInterface.ORIENTATION_ROTATE_90 -> 90f
            ExifInterface.ORIENTATION_ROTATE_180 -> 180f
            ExifInterface.ORIENTATION_ROTATE_270 -> 270f
            else -> 0f
        }
        if (degrees == 0f) return bitmap
        return runCatching {
            val matrix = Matrix().apply { postRotate(degrees) }
            Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
        }.getOrDefault(bitmap)
    }

    private fun scaleDown(bitmap: Bitmap, targetPx: Int): Bitmap {
        val maxSide = maxOf(bitmap.width, bitmap.height)
        if (maxSide <= targetPx) return bitmap
        val scale = targetPx.toFloat() / maxSide
        return Bitmap.createScaledBitmap(bitmap, (bitmap.width * scale).toInt(), (bitmap.height * scale).toInt(), true)
    }

    private fun sampleSize(width: Int, height: Int, targetPx: Int): Int {
        var sample = 1
        var maxSide = maxOf(width, height)
        while (maxSide / (sample * 2) >= targetPx) sample *= 2
        return sample
    }
}