package com.stjarna.mediaviewer.cloud

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import androidx.exifinterface.media.ExifInterface
import com.stjarna.mediaviewer.core.media.ByteSource
import com.stjarna.mediaviewer.core.media.TiffPreview
import java.io.ByteArrayInputStream
import java.io.File
import java.security.MessageDigest

/** 通过 WebDAV Range 读取任意片段的字节源（给 TiffPreview 用）。 */
private class RemoteByteSource(
    private val client: WebDavClient,
    private val path: String,
    override val size: Long,
) : ByteSource {
    override fun read(offset: Long, length: Int): ByteArray =
        client.range(path, offset, length) ?: ByteArray(0)
}

/**
 * 云相册仓库：列表（直接转发 WebDavClient）、缩略图（Range + 内嵌预览）、下载、上传。
 * 缩略图落盘缓存，key 由 路径+大小+修改时间 决定。
 */
class CloudAlbumRepository(private val context: Context) {
    private val cacheDir = File(context.cacheDir, "cloud-album").apply { mkdirs() }
    private val previewDir = File(context.cacheDir, "cloud-album-preview").apply { mkdirs() }

    fun client(url: String, user: String, password: String) = WebDavClient(url, user, password)

    fun list(client: WebDavClient, dir: String): List<DavEntry> = client.list(dir)

    /** 生成/读取缩略图；失败返回 null（UI 显示占位）。 */
    fun thumbnail(client: WebDavClient, entry: DavEntry, maxPx: Int = 320): Bitmap? {
        if (entry.isDirectory) return null
        val cache = File(cacheDir, cacheKey(entry) + "-$maxPx.jpg")
        if (cache.exists() && cache.length() > 0) {
            BitmapFactory.decodeFile(cache.absolutePath)?.let { return it }
        }

        val bitmap = runCatching { buildThumbnail(client, entry, maxPx) }.getOrNull() ?: return null
        runCatching {
            cache.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 85, it) }
        }
        return bitmap
    }

    private fun buildThumbnail(client: WebDavClient, entry: DavEntry, maxPx: Int): Bitmap? {
        val ext = entry.name.substringAfterLast('.', "").lowercase()
        val isRaw = ext in setOf("arw", "cr2", "cr3", "nef", "raf", "orf", "rw2", "dng", "pef", "srw")
        val isVideo = ext in setOf("mp4", "mov", "mkv", "avi", "mxf")

        if (isVideo) {
            // 视频不做首帧（要整文件 + MediaMetadataRetriever），列表里先用占位
            return null
        }

        if (isRaw && entry.sizeBytes > 12L * 1024 * 1024) {
            val preview = TiffPreview.extract(
                RemoteByteSource(client, entry.path, entry.sizeBytes),
                maxScanBytes = 24L * 1024 * 1024,
            )?.bytes
            if (preview != null) return decodeScaled(preview, maxPx)
        }

        val headLen = minOf(entry.sizeBytes, 1024L * 1024).toInt()
        val head = if (headLen > 0) client.range(entry.path, 0, headLen) else null
        if (head != null && head.size >= 4 && head[0] == 0xFF.toByte() && head[1] == 0xD8.toByte()) {
            // JPEG：优先用 EXIF 内嵌缩略图（不用下载整张）
            runCatching {
                val exif = ExifInterface(ByteArrayInputStream(head))
                val thumb = exif.thumbnailBytes ?: return@runCatching
                decodeScaled(thumb, maxPx)?.let { bmp -> return orient(exif, bmp) }
            }
        }
        // HEIC/PNG/小 JPG：直接解头部字节（失败会走整文件）
        if (head != null) {
            decodeScaled(head, maxPx)?.let { return it }
        }

        if (entry.sizeBytes in 1..(12L * 1024 * 1024)) {
            val temp = File(previewDir, "t-" + System.nanoTime() + "." + ext)
            return try {
                client.download(entry.path, temp)
                decodeScaled(temp.readBytes(), maxPx)
            } finally {
                temp.delete()
            }
        }
        return null
    }

    /** 下载整文件到缓存（预览用），返回本地文件。 */
    fun downloadPreview(client: WebDavClient, entry: DavEntry): File {
        val file = File(previewDir, "p-" + cacheKey(entry) + "." + entry.name.substringAfterLast('.', "bin"))
        if (!file.exists() || file.length() != entry.sizeBytes) client.download(entry.path, file)
        return file
    }

    /** 把云端文件下载到应用图片目录（系统相册可见），返回本地路径。 */
    fun importToDevice(client: WebDavClient, entry: DavEntry, targetDir: File): File {
        val safeName = entry.name.replace(Regex("[\\\\/:*?\"<>|]"), "_")
        val dest = File(targetDir, safeName)
        dest.parentFile?.mkdirs()
        client.download(entry.path, dest)
        return dest
    }

    /** 上传本地文件（含同名/同 stem 的 xmp 边车）到云端目录。 */
    fun upload(client: WebDavClient, files: List<File>, remoteDir: String): Int {
        var count = 0
        for (file in files) {
            if (!file.exists()) continue
            val remote = if (remoteDir.isEmpty()) file.name else "$remoteDir/${file.name}"
            client.upload(file, remote)
            count++
        }
        return count
    }

    fun cacheKey(entry: DavEntry): String {
        val raw = "${entry.path}|${entry.sizeBytes}|${entry.modifiedEpochMs}"
        val digest = MessageDigest.getInstance("SHA-1").digest(raw.toByteArray())
        return digest.joinToString("") { "%02x".format(it) }.take(20)
    }

    private fun decodeScaled(bytes: ByteArray, maxPx: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0) return null
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= maxPx || bounds.outHeight / (sample * 2) >= maxPx) sample *= 2
        val opts = BitmapFactory.Options().apply { inSampleSize = sample }
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts) ?: return null
        val scale = maxPx.toFloat() / maxOf(bitmap.width, bitmap.height)
        return if (scale >= 1f) bitmap else Bitmap.createScaledBitmap(
            bitmap,
            (bitmap.width * scale).toInt().coerceAtLeast(1),
            (bitmap.height * scale).toInt().coerceAtLeast(1),
            true,
        )
    }

    private fun orient(exif: ExifInterface, bitmap: Bitmap): Bitmap {
        val degrees = when (exif.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)) {
            ExifInterface.ORIENTATION_ROTATE_90 -> 90f
            ExifInterface.ORIENTATION_ROTATE_180 -> 180f
            ExifInterface.ORIENTATION_ROTATE_270 -> 270f
            else -> 0f
        }
        if (degrees == 0f) return bitmap
        val matrix = Matrix().apply { postRotate(degrees) }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
    }
}