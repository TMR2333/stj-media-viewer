package com.stjarna.mediaviewer.cloud

import android.app.Application
import android.graphics.Bitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.stjarna.mediaviewer.StjApp
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.vm.LibraryViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.File

data class CloudAlbumUiState(
    val configured: Boolean = false,
    val url: String = "",
    val user: String = "",
    val path: String = "",
    val entries: List<DavEntry> = emptyList(),
    val thumbs: Map<String, Bitmap> = emptyMap(),
    val selection: Set<String> = emptySet(),
    val busy: Boolean = false,
    val status: String = "填入 WebDAV 地址与账号后点「连接」。",
    val preview: File? = null,
    val previewName: String = "",
)

/**
 * 云相册：WebDAV 浏览 + Range 缩略图 + 导入到本机 + 上传本地选中素材。
 * 账号存在 SettingsStore（与桌面版一样，不上传、不写进交换文件）。
 */
class CloudAlbumViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = StjApp.instance.settings
    private val repo = CloudAlbumRepository(app)
    private val _state = MutableStateFlow(
        CloudAlbumUiState(
            configured = settings.cloudConfigured,
            url = settings.cloudUrl,
            user = settings.cloudUser,
            path = settings.cloudRoot,
        )
    )
    val state: StateFlow<CloudAlbumUiState> = _state.asStateFlow()

    private val messages = Channel<String>(Channel.BUFFERED)
    val messageFlow = messages.receiveAsFlow()

    private var client: WebDavClient? = null
    private var libraryVm: LibraryViewModel? = null

    fun bindLibrary(vm: LibraryViewModel) {
        libraryVm = vm
    }

    private fun clientOrNull(): WebDavClient? {
        client?.let { return it }
        if (!settings.cloudConfigured) return null
        return repo.client(settings.cloudUrl, settings.cloudUser, settings.cloudPassword).also { client = it }
    }

    fun saveAccount(url: String, user: String, password: String) {
        if (url.isBlank() || user.isBlank()) {
            messages.trySend("地址和账号不能为空")
            return
        }
        settings.cloudUrl = url
        settings.cloudUser = user
        if (password.isNotEmpty()) settings.cloudPassword = password
        client = null
        _state.value = _state.value.copy(configured = true, url = settings.cloudUrl, user = settings.cloudUser)
        connect()
    }

    fun connect() {
        val c = clientOrNull() ?: run {
            _state.value = _state.value.copy(status = "先填 WebDAV 地址和账号")
            return
        }
        viewModelScope.launch(Dispatchers.IO) {
            _state.value = _state.value.copy(busy = true, status = "正在连接…")
            val error = c.test()
            _state.value = _state.value.copy(
                busy = false,
                status = if (error == null) "连接成功" else "连接失败：$error",
            )
            if (error == null) refresh(settings.cloudRoot)
        }
    }

    fun refresh(path: String = _state.value.path) {
        val c = clientOrNull() ?: return
        viewModelScope.launch(Dispatchers.IO) {
            _state.value = _state.value.copy(busy = true, path = path.trim('/'), status = "正在列出云端目录…")
            runCatching { repo.list(c, path) }
                .onSuccess { entries ->
                    _state.value = _state.value.copy(
                        busy = false,
                        entries = entries,
                        selection = emptySet(),
                        status = if (entries.isEmpty()) "这个目录是空的"
                        else "共 ${entries.size} 项（${entries.count { it.isDirectory }} 个文件夹 / ${entries.count { !it.isDirectory }} 个文件）",
                    )
                    loadThumbs(c, entries)
                }
                .onFailure { err ->
                    _state.value = _state.value.copy(busy = false, status = "列目录失败：${err.message}")
                }
        }
    }

    private fun loadThumbs(c: WebDavClient, entries: List<DavEntry>) {
        val targets = entries.filter { !it.isDirectory }
        viewModelScope.launch(Dispatchers.IO) {
            val gate = Semaphore(3)
            for (entry in targets) {
                launch {
                    gate.withPermit {
                        val bmp = repo.thumbnail(c, entry) ?: return@withPermit
                        _state.value = _state.value.copy(thumbs = _state.value.thumbs + (entry.path to bmp))
                    }
                }
            }
        }
    }

    fun up() {
        val path = _state.value.path
        refresh(if ('/' in path) path.substringBeforeLast('/') else "")
    }

    fun enter(entry: DavEntry) {
        if (entry.isDirectory) refresh(entry.path) else openPreview(entry)
    }

    fun toggle(path: String) {
        val sel = _state.value.selection
        _state.value = _state.value.copy(
            selection = if (path in sel) sel - path else sel + path
        )
        _state.value = _state.value.copy(status = "已选 ${_state.value.selection.size} 项")
    }

    fun selectAll() {
        _state.value = _state.value.copy(
            selection = _state.value.entries.filter { !it.isDirectory }.map { it.path }.toSet()
        )
        _state.value = _state.value.copy(status = "已选 ${_state.value.selection.size} 项")
    }

    fun openPreview(entry: DavEntry) {
        val c = clientOrNull() ?: return
        viewModelScope.launch(Dispatchers.IO) {
            _state.value = _state.value.copy(busy = true, status = "正在下载预览：${entry.name}")
            runCatching { repo.downloadPreview(c, entry) }
                .onSuccess { file ->
                    _state.value = _state.value.copy(busy = false, preview = file, previewName = entry.name, status = "预览已就绪")
                }
                .onFailure { _state.value = _state.value.copy(busy = false, status = "预览失败：${it.message}") }
        }
    }

    fun dismissPreview() {
        _state.value = _state.value.copy(preview = null, previewName = "")
    }

    /** 下载选中项到本机图片目录，并交给素材库扫描。 */
    fun importSelection() {
        val c = clientOrNull() ?: return
        val selected = _state.value.entries.filter { it.path in _state.value.selection && !it.isDirectory }
        if (selected.isEmpty()) {
            messages.trySend("先勾选要下载的文件")
            return
        }
        val app = getApplication<Application>()
        val targetDir = File(app.getExternalFilesDir(android.os.Environment.DIRECTORY_PICTURES), "STJ云相册")
        viewModelScope.launch(Dispatchers.IO) {
            _state.value = _state.value.copy(busy = true, status = "正在下载 0/${selected.size}…")
            var ok = 0
            var failed = 0
            for ((index, entry) in selected.withIndex()) {
                _state.value = _state.value.copy(status = "正在下载 ${index + 1}/${selected.size}：${entry.name}")
                runCatching { repo.importToDevice(c, entry, targetDir) }
                    .onSuccess { file ->
                        ok++
                        android.media.MediaScannerConnection.scanFile(app, arrayOf(file.absolutePath), null, null)
                    }
                    .onFailure { failed++ }
            }
            _state.value = _state.value.copy(busy = false, status = "已下载 $ok 个文件" + if (failed > 0) "（失败 $failed）" else "")
            // 添加/刷新本地素材库根目录并扫描
            val lib = libraryVm
            if (lib != null) {
                val root = lib.ensurePathRoot(targetDir.absolutePath, "STJ云相册")
                if (root != null) lib.scan(root)
            }
        }
    }

    /** 上传素材库里当前选中的素材（含 XMP 边车）到云相册当前目录。 */
    fun uploadSelection() {
        val c = clientOrNull() ?: return
        val lib = libraryVm ?: return
        val assets = lib.selectedAssets()
        if (assets.isEmpty()) {
            messages.trySend("先在素材库选中要上传的素材")
            return
        }
        val dir = _state.value.path
        viewModelScope.launch(Dispatchers.IO) {
            _state.value = _state.value.copy(busy = true, status = "正在上传 0/${assets.size}…")
            var files = 0
            var failed = 0
            assets.forEachIndexed { index, asset ->
                _state.value = _state.value.copy(status = "正在上传 ${index + 1}/${assets.size}：${asset.fileName}")
                val list = buildList {
                    if (asset.fullPath.isNotBlank()) add(File(asset.fullPath))
                    sidecarCandidates(asset).forEach { add(it) }
                }
                runCatching { repo.upload(c, list, dir) }
                    .onSuccess { files += it }
                    .onFailure { failed++ }
            }
            _state.value = _state.value.copy(busy = false, status = "已上传 $files 个文件" + if (failed > 0) "（失败 $failed 项）" else "")
            if (failed == 0) refresh(dir)
        }
    }

    private fun sidecarCandidates(asset: MediaAsset): List<File> {
        if (asset.fullPath.isBlank()) return emptyList()
        val path = File(asset.fullPath)
        val name = path.name
        val stem = name.substringBeforeLast('.')
        return listOf(
            File(path.parentFile, "$name.xmp"),
            File(path.parentFile, "$stem.xmp"),
        ).filter { it.exists() }
    }
}