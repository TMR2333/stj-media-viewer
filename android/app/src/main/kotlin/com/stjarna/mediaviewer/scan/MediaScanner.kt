package com.stjarna.mediaviewer.scan

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import androidx.documentfile.provider.DocumentFile
import com.stjarna.mediaviewer.core.FormatCatalog
import com.stjarna.mediaviewer.core.LibraryRoot
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.data.CatalogDb
import com.stjarna.mediaviewer.data.LibraryRootFactory
import com.stjarna.mediaviewer.data.SettingsStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.coroutines.coroutineContext

data class ScanProgress(val phase: String, val done: Int, val total: Int) {
    val percent: Float get() = if (total <= 0) 0f else (done.toFloat() / total * 100f)
}

data class ScanResult(
    val discovered: Int,
    val indexed: Int,
    val skipped: Int,
    val errors: Int,
    val elapsedMs: Long,
    val source: String,
)

/** 扫描引擎：root 目录 / SAF 目录树 / 系统相册（MediaStore）三种来源。 */
class MediaScanner(
    private val context: Context,
    private val db: CatalogDb,
    private val metadata: MetadataReader,
) {
    suspend fun scan(
        root: LibraryRoot,
        settings: SettingsStore,
        onProgress: (ScanProgress) -> Unit = {},
    ): ScanResult = withContext(Dispatchers.IO) {
        val started = System.currentTimeMillis()
        onProgress(ScanProgress("发现文件…", 0, 0))
        coroutineContext.ensureActive()

        val files: List<DiscoveredFile>
        val source: String
        when {
            root.path == LibraryRootFactory.MEDIASTORE_PATH -> {
                files = discoverMediaStore(); source = "系统相册"
            }
            root.uri != null -> {
                files = discoverSaf(root); source = "SAF 目录"
            }
            settings.rootMode && RootManager.isAvailable() && !isReadableDirectory(root.path) -> {
                files = discoverRoot(root); source = "root 目录"
            }
            else -> {
                files = discoverFileTree(root); source = "本地目录"
            }
        }

        val pending = ArrayList<MediaAsset>(256)
        var skipped = 0
        var errors = 0
        var indexed = 0
        files.forEachIndexed { index, file ->
            if (index % 16 == 0) {
                coroutineContext.ensureActive()
                onProgress(ScanProgress("解析元数据 $index/${files.size}", index, files.size))
            }
            if (FormatCatalog.isJunkName(file.displayName) || FormatCatalog.isSidecar(file.displayName)) {
                skipped++
                return@forEachIndexed
            }
            val asset = try {
                metadata.read(file, root.id)
            } catch (ex: Exception) {
                errors++
                null
            }
            if (asset != null) {
                pending += asset
                indexed++
                if (pending.size >= 200) {
                    db.upsertAll(pending)
                    pending.clear()
                }
            } else {
                skipped++
            }
        }
        if (pending.isNotEmpty()) db.upsertAll(pending)

        // 清理"索引里有、这次扫描没遇到"的失效记录（仅当发现阶段没有明显失败时）
        if (files.isNotEmpty()) {
            val seen = files.map { MediaAsset.makeId(it.path) }.toHashSet()
            val stale = db.loadAllAssets().filter { it.libraryId == root.id && it.id !in seen }.map { it.id }
            if (stale.isNotEmpty()) db.deleteAssets(stale)
        }

        val elapsed = System.currentTimeMillis() - started
        val total = db.loadAllAssets().count { it.libraryId == root.id }
        onProgress(ScanProgress("完成", files.size, files.size))
        ScanResult(files.size, total, skipped, errors, elapsed, source)
    }

    // ------------------------------------------------------------------ 发现

    private fun discoverFileTree(root: LibraryRoot): List<DiscoveredFile> {
        val base = File(root.path)
        if (!base.isDirectory) return emptyList()
        val result = ArrayList<DiscoveredFile>(2048)
        val queue = ArrayDeque<Pair<File, String>>()
        queue.add(base to "")
        var visited = 0
        while (queue.isNotEmpty() && visited < 200_000) {
            val (dir, rel) = queue.removeFirst()
            val children = dir.listFiles() ?: continue
            for (child in children) {
                visited++
                val childRel = if (rel.isEmpty()) child.name else "$rel/${child.name}"
                if (child.isDirectory) {
                    if (!FormatCatalog.isJunkName(child.name)) queue.add(child to childRel)
                } else {
                    result += DiscoveredFile(
                        path = child.absolutePath,
                        displayName = child.name,
                        sizeBytes = child.length(),
                        modifiedMs = child.lastModified(),
                        relativePath = childRel,
                    )
                }
            }
        }
        return result
    }

    private fun discoverRoot(root: LibraryRoot): List<DiscoveredFile> {
        val base = root.path.trimEnd('/')
        return RootManager.listFiles(base).map { f ->
            DiscoveredFile(
                path = f.path,
                displayName = f.path.substringAfterLast('/'),
                sizeBytes = f.size,
                modifiedMs = f.modified,
                relativePath = f.path.removePrefix("$base/"),
            )
        }
    }

    private fun discoverSaf(root: LibraryRoot): List<DiscoveredFile> {
        val uri = root.uri?.let { Uri.parse(it) } ?: return emptyList()
        val tree = DocumentFile.fromTreeUri(context, uri) ?: return emptyList()
        val result = ArrayList<DiscoveredFile>(1024)
        fun walk(dir: DocumentFile, rel: String, depth: Int) {
            if (depth > 32) return
            dir.listFiles().forEach { child ->
                val childRel = if (rel.isEmpty()) child.name ?: "" else "$rel/${child.name}"
                if (child.isDirectory) {
                    walk(child, childRel, depth + 1)
                } else {
                    result += DiscoveredFile(
                        path = child.uri.toString(),
                        displayName = child.name ?: "",
                        sizeBytes = child.length(),
                        modifiedMs = child.lastModified(),
                        contentUri = child.uri.toString(),
                        relativePath = childRel,
                    )
                }
            }
        }
        walk(tree, "", 0)
        return result
    }

    private fun discoverMediaStore(): List<DiscoveredFile> {
        val result = ArrayList<DiscoveredFile>(4096)
        val collections = listOf(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI,
        )
        for (collection in collections) {
            val projection = arrayOf(
                MediaStore.MediaColumns._ID,
                MediaStore.MediaColumns.DISPLAY_NAME,
                MediaStore.MediaColumns.SIZE,
                MediaStore.MediaColumns.DATE_MODIFIED,
                MediaStore.MediaColumns.DATA,
                MediaStore.MediaColumns.BUCKET_DISPLAY_NAME,
            )
            runCatching {
                context.contentResolver.query(collection, projection, null, null, null)?.use { c ->
                    while (c.moveToNext()) {
                        val id = c.getLong(0)
                        val name = c.getString(1) ?: continue
                        val size = c.getLong(2)
                        val modified = c.getLong(3) * 1000
                        val realPath = c.getString(4) ?: name
                        val bucket = c.getString(5)
                        val contentUri = ContentUris.withAppendedId(collection, id).toString()
                        result += DiscoveredFile(
                            path = realPath,
                            displayName = name,
                            sizeBytes = size,
                            modifiedMs = modified,
                            contentUri = contentUri,
                            relativePath = if (bucket.isNullOrBlank()) name else "$bucket/$name",
                        )
                    }
                }
            }
        }
        return result
    }

    private fun isReadableDirectory(path: String): Boolean = try {
        File(path).let { it.isDirectory && it.canRead() && it.list() != null }
    } catch (_: Exception) {
        false
    }
}