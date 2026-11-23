package com.stjarna.mediaviewer.cloud

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.CloudUpload
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.stjarna.mediaviewer.vm.LibraryViewModel

/** 云相册：浏览 WebDAV 目录、缩略图预览、导入本机、上传所选素材。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CloudAlbumScreen(
    vm: CloudAlbumViewModel,
    libraryVm: LibraryViewModel,
    onBack: () -> Unit,
) {
    val state by vm.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    var showAccount by remember { mutableStateOf(false) }
    var url by remember { mutableStateOf(state.url) }
    var user by remember { mutableStateOf(state.user) }
    var password by remember { mutableStateOf("") }
    BackHandler(onBack = onBack)

    LaunchedEffect(Unit) {
        vm.bindLibrary(libraryVm)
        if (state.configured) vm.connect()
    }
    LaunchedEffect(Unit) {
        vm.messageFlow.collect { snackbar.showSnackbar(it) }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("云相册") },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "返回") }
                },
                actions = {
                    IconButton(onClick = { showAccount = true }) { Icon(Icons.Filled.Cloud, "账号") }
                    IconButton(onClick = { vm.refresh(state.path) }) { Icon(Icons.Filled.Refresh, "刷新") }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            Surface(tonalElevation = 3.dp) {
                Column {
                    if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text("已选 ${state.selection.size}", style = MaterialTheme.typography.labelLarge)
                        Spacer(Modifier.weight(1f))
                        FilledTonalButton(onClick = { vm.importSelection() }) {
                            Icon(Icons.Filled.Download, null, Modifier.size(18.dp))
                            Text(" 下载")
                        }
                        FilledTonalButton(onClick = { vm.uploadSelection() }) {
                            Icon(Icons.Filled.CloudUpload, null, Modifier.size(18.dp))
                            Text(" 上传")
                        }
                    }
                }
            }
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    "/" + state.path,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { vm.up() }) { Text("上一级") }
                TextButton(onClick = { vm.selectAll() }) { Text("全选") }
            }
            Text(
                state.status,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 12.dp),
            )
            LazyVerticalGrid(
                columns = GridCells.Adaptive(104.dp),
                contentPadding = PaddingValues(6.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(state.entries, key = { it.path }) { entry ->
                    val selected = entry.path in state.selection
                    val bmp = state.thumbs[entry.path]
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .combinedClickable(
                                onClick = {
                                    if (entry.isDirectory) vm.enter(entry)
                                    else vm.openPreview(entry)
                                },
                                onLongClick = { vm.toggle(entry.path) },
                            )
                            .padding(2.dp),
                    ) {
                        Box(
                            Modifier
                                .fillMaxWidth()
                                .height(96.dp),
                        ) {
                            Surface(
                                modifier = Modifier.fillMaxSize(),
                                shape = MaterialTheme.shapes.medium,
                                color = MaterialTheme.colorScheme.surfaceVariant,
                            ) {
                                if (bmp != null) {
                                    Image(
                                        bitmap = bmp.asImageBitmap(),
                                        contentDescription = entry.name,
                                        modifier = Modifier.fillMaxSize(),
                                        contentScale = ContentScale.Crop,
                                    )
                                } else {
                                    Icon(
                                        if (entry.isDirectory) Icons.Filled.Folder else Icons.Filled.Cloud,
                                        contentDescription = null,
                                        modifier = Modifier.align(Alignment.Center).size(32.dp),
                                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                            }
                            Checkbox(
                                checked = selected,
                                onCheckedChange = { vm.toggle(entry.path) },
                                modifier = Modifier.align(Alignment.TopEnd),
                            )
                        }
                        Text(
                            entry.name,
                            style = MaterialTheme.typography.labelSmall,
                            maxLines = 2,
                            modifier = Modifier.padding(top = 2.dp),
                        )
                        if (!entry.isDirectory) {
                            Text(
                                formatSize(entry.sizeBytes),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
                if (state.entries.isEmpty() && !state.busy) {
                    item(span = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }) {
                        Text(
                            if (state.configured) "这个目录是空的，或还没连接成功。"
                            else "先在右上角「账号」里填 WebDAV 地址与账号。",
                            modifier = Modifier.padding(24.dp),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }
            }
        }
    }

    state.preview?.let { file ->
        val bitmap = remember(file) { android.graphics.BitmapFactory.decodeFile(file.absolutePath)?.asImageBitmap() }
        androidx.compose.ui.window.Dialog(onDismissRequest = { vm.dismissPreview() }) {
            Surface(shape = MaterialTheme.shapes.large) {
                Column(Modifier.padding(12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(state.previewName, style = MaterialTheme.typography.titleSmall)
                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap,
                            contentDescription = null,
                            modifier = Modifier.fillMaxWidth().height(360.dp),
                            contentScale = ContentScale.Fit,
                        )
                    } else {
                        Text("这个格式暂不支持应用内预览", modifier = Modifier.padding(24.dp))
                    }
                    TextButton(onClick = { vm.dismissPreview() }) { Text("关闭") }
                }
            }
        }
    }

    if (showAccount) {
        AlertDialog(
            onDismissRequest = { showAccount = false },
            title = { Text("云相册 · WebDAV") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(
                        value = url,
                        onValueChange = { url = it },
                        label = { Text("WebDAV 地址，如 https://webdav.123pan.cn/webdav/") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    OutlinedTextField(
                        value = user,
                        onValueChange = { user = it },
                        label = { Text("账号") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text("密码（仅存本机，不写交换文件）") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Text(
                        "支持 123pan / 坚果云 / Nextcloud 等标准 WebDAV；Basic 认证。预览走 Range，不上传原图。",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            },
            confirmButton = {
                Button(onClick = {
                    vm.saveAccount(url, user, password)
                    showAccount = false
                }) { Text("连接") }
            },
            dismissButton = { TextButton(onClick = { showAccount = false }) { Text("取消") } },
        )
    }
}

private fun formatSize(bytes: Long): String = when {
    bytes >= 1L shl 30 -> "%.1f GB".format(bytes / 1073741824.0)
    bytes >= 1L shl 20 -> "%.1f MB".format(bytes / 1048576.0)
    bytes >= 1L shl 10 -> "%.0f KB".format(bytes / 1024.0)
    else -> "$bytes B"
}
