package com.stjarna.mediaviewer.vm

import android.app.Application
import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.stjarna.mediaviewer.StjApp
import com.stjarna.mediaviewer.core.AssetFilter
import com.stjarna.mediaviewer.core.Facets
import com.stjarna.mediaviewer.core.LibraryRoot
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.PathUtil
import com.stjarna.mediaviewer.core.SortKey
import com.stjarna.mediaviewer.core.computeFacets
import com.stjarna.mediaviewer.core.exchange.MergeMode
import com.stjarna.mediaviewer.core.meta.XmpCodec
import com.stjarna.mediaviewer.core.meta.XmpData
import com.stjarna.mediaviewer.data.LibraryRootFactory
import com.stjarna.mediaviewer.exchange.ExchangeIo
import com.stjarna.mediaviewer.scan.RootManager
import com.stjarna.mediaviewer.scan.ScanProgress
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

data class LibraryUiState(
    val assets: List<MediaAsset> = emptyList(),
    val filtered: List<MediaAsset> = emptyList(),
    val facets: Facets = Facets(),
    val filter: AssetFilter = AssetFilter(),
    val selection: Set<String> = emptySet(),
    val roots: List<LibraryRoot> = emptyList(),
    val scanning: Boolean = false,
    val scanText: String = "",
    val scanPercent: Float = 0f,
    val message: String? = null,
    val loading: Boolean = false,
    val rootAvailable: Boolean = false,
    val geocoderReady: Boolean = false,
    val pendingImport: ExchangeIo.Preview? = null,
    val importBusy: Boolean = false,
    val rootMode: Boolean = false,
    val themeMode: String = "system",
    val dynamicColor: Boolean = true,
    val density: String = "medium",
)

/** 素材库/筛选/编辑/扫描/交换的单一状态源。 */
class LibraryViewModel(app: Application) : AndroidViewModel(app) {
    private val graph = app as StjApp
    private var allAssets: List<MediaAsset> = emptyList()

    var state by mutableStateOf(LibraryUiState())
        private set

    init {
        state = state.copy(
            roots = graph.settings.libraries(),
            filter = AssetFilter(sort = SortKey.of(graph.settings.sortId), descending = graph.settings.descending),
            rootMode = graph.settings.rootMode,
            themeMode = graph.settings.themeMode,
            dynamicColor = graph.settings.dynamicColor,
            density = graph.settings.density,
        )
        viewModelScope.launch {
            refresh()
            state = state.copy(
                geocoderReady = graph.geocoder != null,
                rootAvailable = graph.settings.rootMode && RootManager.isAvailable(),
            )
        }
    }

    fun refresh() {
        viewModelScope.launch {
            state = state.copy(loading = true)
            val assets = withContext(Dispatchers.IO) { graph.db.loadAllAssets() }
            allAssets = assets
            recompute()
            state = state.copy(loading = false)
        }
    }

    private fun recompute() {
        val filter = state.filter
        val filtered = filter.sorted(allAssets.filter { filter.matches(it) })
        state = state.copy(
            assets = allAssets,
            filtered = filtered,
            facets = computeFacets(allAssets),
            selection = state.selection.filter { id -> allAssets.any { it.id == id } }.toSet(),
        )
    }

    fun updateFilter(transform: (AssetFilter) -> AssetFilter) {
        state = state.copy(filter = transform(state.filter))
        graph.settings.apply {
            sortId = state.filter.sort.id
            descending = state.filter.descending
        }
        recompute()
    }

    fun setSort(sort: SortKey) {
        updateFilter { it.copy(sort = sort, descending = if (it.sort == sort) !it.descending else true) }
    }

    fun setSearch(text: String) = updateFilter { it.copy(search = text) }

    fun resetFilter() {
        val sort = state.filter.sort
        updateFilter { AssetFilter(sort = sort, descending = state.filter.descending) }
    }

    // ------------------------------------------------------------------ 选择

    fun toggleSelect(id: String) {
        val selection = state.selection.toMutableSet()
        if (!selection.add(id)) selection.remove(id)
        state = state.copy(selection = selection)
    }

    fun setSelected(id: String, selected: Boolean) {
        val selection = state.selection.toMutableSet()
        if (selected) selection.add(id) else selection.remove(id)
        state = state.copy(selection = selection)
    }

    fun selectAllFiltered() {
        state = state.copy(selection = state.filtered.map { it.id }.toSet())
    }

    fun clearSelection() {
        state = state.copy(selection = emptySet())
    }

    // ------------------------------------------------------------------ 编辑

    /** 更新单个素材（含写 XMP 边车；写失败只提示，不影响索引库）。 */
    fun saveAsset(asset: MediaAsset) {
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                graph.db.updateAsset(asset)
                writeSidecar(asset)
            }
            allAssets = allAssets.map { if (it.id == asset.id) asset else it }
            recompute()
            state = state.copy(message = "已保存")
        }
    }

    /** 批量编辑：把同一组字段写到选中素材。 */
    fun applyBatch(
        rating: Int? = null,
        colorLabel: String? = null,
        isPick: Boolean? = null,
        postProcess: Int? = null,
        tone: String? = null,
    ) {
        val targets = allAssets.filter { it.id in state.selection }
        if (targets.isEmpty()) return
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                targets.forEach { a ->
                    rating?.let { a.rating = it }
                    colorLabel?.let { a.colorLabel = if (it == "None") null else it }
                    isPick?.let { a.isPick = it }
                    postProcess?.let { a.postProcess = com.stjarna.mediaviewer.core.PostProcessState.of(it) }
                    tone?.let { a.tone = it }
                    graph.db.updateAsset(a)
                    writeSidecar(a)
                }
            }
            allAssets = allAssets.map { a -> targets.firstOrNull { it.id == a.id } ?: a }
            recompute()
            state = state.copy(message = "已应用到 ${targets.size} 张素材")
        }
    }

    private fun writeSidecar(asset: MediaAsset) {
        if (asset.contentUri != null) return
        val xmp = XmpCodec.build(
            XmpData(
                rating = asset.rating,
                label = com.stjarna.mediaviewer.core.ColorLabels.toChinese(asset.colorLabel),
                keywords = asset.keywords,
                city = asset.city,
                title = asset.title,
                theme = asset.primaryTheme,
                tone = asset.tone,
            )
        ).toByteArray(Charsets.UTF_8)
        val target = asset.fullPath + ".xmp"
        val written = if (graph.settings.rootMode && RootManager.isAvailable()) {
            RootManager.writeFile(target, xmp)
        } else {
            runCatching { File(target).writeBytes(xmp) }.isSuccess
        }
        if (!written) {
            asset.lastError = "XMP 写入失败"
        }
    }

    /** 当前选中的素材（云相册上传等批量操作用）。 */
    fun selectedAssets(): List<MediaAsset> = allAssets.filter { it.id in state.selection }

    /** 已有同路径媒体库就返回，否则新建（云相册下载目录自动入库）。 */
    fun ensurePathRoot(path: String, name: String): LibraryRoot? {
        graph.settings.libraries().firstOrNull { it.path == path }?.let { return it }
        addPathRoot(path, name)
        return graph.settings.libraries().lastOrNull()
    }

    // ------------------------------------------------------------------ 扫描

    fun addPathRoot(path: String, name: String? = null) {
        val root = LibraryRootFactory.create(name ?: path.substringAfterLast('/'), path.trim())
        val roots = graph.settings.libraries() + root
        graph.settings.saveLibraries(roots)
        state = state.copy(roots = roots, message = "已添加媒体库：${root.name}")
    }

    fun addSafRoot(uri: Uri, name: String?) {
        val label = name ?: uri.lastPathSegment ?: "SAF 目录"
        val root = LibraryRootFactory.create(label, uri.toString(), uri.toString())
        val roots = graph.settings.libraries() + root
        graph.settings.saveLibraries(roots)
        state = state.copy(roots = roots, message = "已添加媒体库：$label")
    }

    fun addMediaStoreRoot() {
        val root = LibraryRootFactory.create("系统相册", LibraryRootFactory.MEDIASTORE_PATH)
        val roots = graph.settings.libraries() + root
        graph.settings.saveLibraries(roots)
        state = state.copy(roots = roots, message = "已添加「系统相册」，点扫描开始索引")
    }

    fun removeRoot(root: LibraryRoot) {
        val roots = graph.settings.libraries().filterNot { it.id == root.id }
        graph.settings.saveLibraries(roots)
        viewModelScope.launch {
            withContext(Dispatchers.IO) { graph.db.deleteLibrary(root.id) }
            refresh()
            state = state.copy(roots = roots, message = "已移除媒体库：${root.name}")
        }
    }

    fun scan(root: LibraryRoot? = null) {
        if (state.scanning) return
        val targets = if (root != null) listOf(root) else state.roots
        if (targets.isEmpty()) {
            state = state.copy(message = "先添加一个媒体库（设置 → 媒体库）")
            return
        }
        viewModelScope.launch {
            state = state.copy(scanning = true, scanText = "准备扫描…", scanPercent = 0f)
            var total = 0
            var errors = 0
            for (target in targets) {
                runCatching {
                    graph.scanner.scan(target, graph.settings) { p: ScanProgress ->
                        state = state.copy(scanText = "${target.name} · ${p.phase}", scanPercent = p.percent)
                    }
                }.onFailure {
                    errors++
                }
                total++
                val roots = graph.settings.libraries().map {
                    if (it.id == target.id) it.copy(lastScanAt = System.currentTimeMillis()) else it
                }
                graph.settings.saveLibraries(roots)
            }
            val assets = withContext(Dispatchers.IO) { graph.db.loadAllAssets() }
            allAssets = assets
            recompute()
            val roots = graph.settings.libraries().map { r ->
                r.copy(assetCount = assets.count { it.libraryId == r.id })
            }
            graph.settings.saveLibraries(roots)
            state = state.copy(
                scanning = false,
                scanText = "",
                scanPercent = 0f,
                roots = roots,
                message = "扫描完成：${assets.size} 张素材" + if (errors > 0) "（$errors 个库出错）" else "",
                geocoderReady = graph.geocoder != null,
                rootAvailable = graph.settings.rootMode && RootManager.isAvailable(),
            )
        }
    }

    // ------------------------------------------------------------------ 交换文件

    fun exportExchange(uri: Uri) {
        viewModelScope.launch {
            state = state.copy(importBusy = true)
            val message = runCatching { graph.exchange.exportTo(uri) }
                .getOrElse { "导出失败：${it.message}" }
            state = state.copy(importBusy = false, message = message)
        }
    }

    fun prepareImport(uri: Uri) {
        viewModelScope.launch {
            state = state.copy(importBusy = true)
            val preview = runCatching { graph.exchange.prepareImport(uri) }.getOrNull()
            state = state.copy(
                importBusy = false,
                pendingImport = preview,
                message = if (preview == null) "读取交换文件失败" else null,
            )
        }
    }

    fun applyImport(mode: MergeMode) {
        val preview = state.pendingImport ?: return
        viewModelScope.launch {
            state = state.copy(importBusy = true)
            val message = runCatching { graph.exchange.apply(preview, mode) }
                .getOrElse { "导入失败：${it.message}" }
            state = state.copy(importBusy = false, pendingImport = null, message = message)
            refresh()
        }
    }

    fun dismissImport() {
        state = state.copy(pendingImport = null)
    }

    fun clearMessage() {
        state = state.copy(message = null)
    }

    fun setRootMode(enabled: Boolean) {
        graph.settings.rootMode = enabled
        state = state.copy(rootMode = enabled)
        viewModelScope.launch {
            val available = withContext(Dispatchers.IO) { RootManager.isAvailable(force = true) }
            state = state.copy(rootAvailable = available)
        }
    }

    fun setTheme(mode: String) {
        graph.settings.themeMode = mode
        state = state.copy(themeMode = mode)
    }

    fun setDynamicColor(enabled: Boolean) {
        graph.settings.dynamicColor = enabled
        state = state.copy(dynamicColor = enabled)
    }

    fun setDensity(density: String) {
        graph.settings.density = density
        state = state.copy(density = density)
    }

    fun assetById(id: String): MediaAsset? = allAssets.firstOrNull { it.id == id }

    fun rootDisplay(root: LibraryRoot): String =
        if (root.path == LibraryRootFactory.MEDIASTORE_PATH) "系统相册（MediaStore）" else root.path

    fun relativeLabel(asset: MediaAsset): String =
        asset.city ?: asset.relativePath ?: asset.fileName
}