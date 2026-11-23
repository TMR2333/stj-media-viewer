package com.stjarna.mediaviewer.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.outlined.Cloud
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LargeTopAppBar
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.unit.dp
import com.stjarna.mediaviewer.StjApp
import com.stjarna.mediaviewer.core.PickState
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.vm.LibraryViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LibraryScreen(
    vm: LibraryViewModel,
    onOpenSettings: () -> Unit,
    onOpenCloud: () -> Unit = {},
) {
    val state = vm.state
    val app = StjApp.instance
    val snackbar = remember { SnackbarHostState() }
    var searchOpen by remember { mutableStateOf(false) }
    var filterOpen by remember { mutableStateOf(false) }
    var detailAsset by remember { mutableStateOf<MediaAsset?>(null) }
    var batchOpen by remember { mutableStateOf(false) }
    val scrollBehavior = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()

    LaunchedEffect(state.message) {
        state.message?.let {
            snackbar.showSnackbar(it)
            vm.clearMessage()
        }
    }

    Scaffold(
        modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        topBar = {
            if (searchOpen) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    TextField(
                        value = state.filter.search,
                        onValueChange = vm::setSearch,
                        modifier = Modifier.weight(1f),
                        placeholder = { Text("搜索文件名 / 城市 / 设备 / 关键词") },
                        singleLine = true,
                        colors = TextFieldDefaults.colors(
                            focusedIndicatorColor = Color.Transparent,
                            unfocusedIndicatorColor = Color.Transparent,
                        ),
                    )
                    IconButton(onClick = {
                        searchOpen = false
                        vm.setSearch("")
                    }) { Icon(Icons.Filled.Close, "关闭搜索") }
                }
            } else {
                LargeTopAppBar(
                    title = {
                        Text(
                            if (state.selection.isEmpty()) {
                                "素材库 · ${state.filtered.size} / ${state.assets.size}"
                            } else {
                                "已选 ${state.selection.size} 项"
                            }
                        )
                    },
                    actions = {
                        IconButton(onClick = {
                            vm.setSort(state.filter.sort)
                        }) { Icon(Icons.Filled.Refresh, "切换排序") }
                        IconButton(onClick = { searchOpen = true }) { Icon(Icons.Filled.Search, "搜索") }
                        BadgedBox(badge = {
                            if (state.filter.hasAnyFacet) Badge { Text("${filterCount(state)}") }
                        }) {
                            IconButton(onClick = { filterOpen = true }) { Icon(Icons.Filled.FilterList, "筛选") }
                        }
                        IconButton(onClick = onOpenCloud) { Icon(Icons.Outlined.Cloud, "云相册") }
                        IconButton(onClick = onOpenSettings) { Icon(Icons.Filled.Settings, "设置") }
                    },
                    scrollBehavior = scrollBehavior,
                )
            }
        },
        snackbarHost = { SnackbarHost(snackbar) },
        floatingActionButton = {
            if (state.selection.isEmpty()) {
                ExtendedFloatingActionButton(
                    onClick = { vm.scan() },
                    icon = { Icon(Icons.Filled.Refresh, null) },
                    text = { Text(if (state.scanning) "扫描中…" else "扫描") },
                )
            }
        },
        bottomBar = {
            if (state.selection.isNotEmpty()) SelectionBar(vm)
        },
    ) { padding ->
        Box(Modifier.padding(padding).fillMaxSize()) {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(cardWidthFor(state.density) + 8.dp),
                contentPadding = PaddingValues(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                    QuickFilterRow(vm)
                }
                if (state.scanning) {
                    item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                        LinearProgressIndicator(
                            progress = { state.scanPercent / 100f },
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp),
                        )
                    }
                }
                items(state.filtered, key = { it.id }) { asset ->
                    AssetCard(
                        asset = asset,
                        selected = asset.id in state.selection,
                        density = state.density,
                        loader = app.thumbnails,
                        onClick = {
                            if (state.selection.isEmpty()) detailAsset = asset
                            else vm.toggleSelect(asset.id)
                        },
                        onLongClick = { vm.toggleSelect(asset.id) },
                    )
                }
                if (state.filtered.isEmpty() && !state.loading) {
                    item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                        EmptyState(
                            title = if (state.assets.isEmpty()) "还没有素材" else "没有符合筛选的素材",
                            description = if (state.assets.isEmpty()) {
                                "点右下角「扫描」索引系统相册，或到设置里添加相机/素材文件夹。"
                            } else {
                                "试试右侧筛选里的「重置」，或换个关键词。"
                            },
                        )
                    }
                }
            }
        }
    }

    if (filterOpen) {
        FilterSheet(
            vm = vm,
            onDismiss = { filterOpen = false },
        )
    }

    detailAsset?.let { asset ->
        DetailSheet(
            asset = asset,
            vm = vm,
            onDismiss = {
                detailAsset = null
                vm.refresh()
            },
        )
    }

    if (batchOpen) {
        BatchSheet(vm = vm, onDismiss = { batchOpen = false })
    }
}

private fun filterCount(state: com.stjarna.mediaviewer.vm.LibraryUiState): Int {
    val f = state.filter
    var n = 0
    n += f.kinds.size + f.formats.size + f.devices.size + f.cities.size + f.years.size
    n += f.themes.size + f.tones.size + f.colorLabels.size
    if (f.minRating != null) n++
    if (f.pickState != PickState.All) n++
    if (f.onlyRaw) n++
    if (f.onlyLog) n++
    if (f.onlyUncolored) n++
    if (f.onlyColored) n++
    return n
}

@Composable
private fun QuickFilterRow(vm: LibraryViewModel) {
    val filter = vm.state.filter
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 6.dp, vertical = 2.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        FilterChip(
            selected = filter.onlyRaw,
            onClick = { vm.updateFilter { it.copy(onlyRaw = !it.onlyRaw) } },
            label = { Text("只看 RAW") },
        )
        FilterChip(
            selected = filter.onlyLog,
            onClick = { vm.updateFilter { it.copy(onlyLog = !it.onlyLog) } },
            label = { Text("只看 Log") },
        )
        FilterChip(
            selected = filter.pickState == PickState.Picked,
            onClick = {
                vm.updateFilter { it.copy(pickState = if (it.pickState == PickState.Picked) PickState.All else PickState.Picked) }
            },
            label = { Text("选用") },
        )
        FilterChip(
            selected = filter.pickState == PickState.Rejected,
            onClick = {
                vm.updateFilter { it.copy(pickState = if (it.pickState == PickState.Rejected) PickState.All else PickState.Rejected) }
            },
            label = { Text("废片") },
        )
    }
}

@Composable
private fun SelectionBar(vm: LibraryViewModel) {
    Surface(tonalElevation = 3.dp) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text("已选 ${vm.state.selection.size}", style = MaterialTheme.typography.labelLarge)
            Spacer(Modifier.weight(1f))
            TextButton(onClick = { vm.selectAllFiltered() }) { Text("全选") }
            TextButton(onClick = { vm.clearSelection() }) { Text("取消") }
        }
    }
}