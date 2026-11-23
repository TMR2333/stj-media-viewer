package com.stjarna.mediaviewer.util

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.core.content.FileProvider
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import java.io.File

/**
 * 素材的对外动作：分享 / 用其他应用打开。
 * - 优先用 MediaStore/SAF 的 content:// 地址；
 * - 本地文件经 FileProvider 暴露（只给接收方临时读权限）；
 * - RAW 等冷门格式用 application/octet-stream，让系统列出所有能打开的应用。
 */
object AssetActions {

    private const val AUTHORITY_SUFFIX = ".fileprovider"

    fun share(context: Context, asset: MediaAsset) {
        val uri = uriFor(context, asset) ?: return
        val intent = Intent(Intent.ACTION_SEND).apply {
            type = mimeOf(asset)
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_TITLE, asset.fileName)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        launchChooser(context, intent, "分享到…")
    }

    /** 多选分享：只把能解析出 URI 的素材一起发出去。 */
    fun shareMany(context: Context, assets: List<MediaAsset>) {
        if (assets.isEmpty()) return
        if (assets.size == 1) {
            share(context, assets.first())
            return
        }
        val uris = assets.mapNotNull { uriFor(context, it) }
        if (uris.isEmpty()) return
        val intent = Intent(Intent.ACTION_SEND_MULTIPLE).apply {
            type = "*/*"
            putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        launchChooser(context, intent, "分享 ${uris.size} 个素材到…")
    }

    fun openWith(context: Context, asset: MediaAsset) {
        val uri = uriFor(context, asset) ?: return
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mimeOf(asset))
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        launchChooser(context, intent, "用其他应用打开…")
    }

    private fun launchChooser(context: Context, intent: Intent, title: String) {
        val chooser = Intent.createChooser(intent, title)
        chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try {
            context.startActivity(chooser)
        } catch (_: ActivityNotFoundException) {
            Toast.makeText(context, "没有找到可以处理它的应用", Toast.LENGTH_SHORT).show()
        } catch (_: Exception) {
            Toast.makeText(context, "打开失败：文件可能已被移动或删除", Toast.LENGTH_SHORT).show()
        }
    }

    /** 能分享/能打开的真实 URI；都没有就提示并返回 null。 */
    fun uriFor(context: Context, asset: MediaAsset): Uri? {
        asset.contentUri?.takeIf { it.isNotBlank() }?.let { raw ->
            runCatching {
                val uri = Uri.parse(raw)
                context.contentResolver.openInputStream(uri)?.use { }
                return uri
            }
        }
        val path = asset.fullPath
        val file = if (path.isNotBlank()) File(path) else null
        if (file == null || !file.exists()) {
            Toast.makeText(context, "文件不存在，可能已被移动", Toast.LENGTH_SHORT).show()
            return null
        }
        return runCatching {
            FileProvider.getUriForFile(context, context.packageName + AUTHORITY_SUFFIX, file)
        }.onFailure {
            Toast.makeText(context, "无法共享这个路径：${it.message}", Toast.LENGTH_SHORT).show()
        }.getOrNull()
    }

    fun mimeOf(asset: MediaAsset): String {
        asset.mimeType?.takeIf { it.isNotBlank() }?.let { return it }
        val ext = asset.fileName.substringAfterLast('.', "").lowercase()
        return when {
            ext in setOf("jpg", "jpeg") -> "image/jpeg"
            ext == "png" -> "image/png"
            ext == "webp" -> "image/webp"
            ext == "gif" -> "image/gif"
            ext == "heic" || ext == "heif" -> "image/heic"
            ext == "bmp" -> "image/bmp"
            ext in setOf("tif", "tiff") -> "image/tiff"
            ext == "mp4" -> "video/mp4"
            ext == "mov" -> "video/quicktime"
            ext == "mkv" -> "video/x-matroska"
            ext == "avi" -> "video/x-msvideo"
            ext in setOf("wav", "flac", "mp3", "m4a") -> "audio/*"
            asset.kind == MediaKind.Image -> "image/*"
            asset.kind == MediaKind.Video -> "video/*"
            else -> "application/octet-stream"
        }
    }
}