package com.stjarna.mediaviewer.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.stjarna.mediaviewer.BuildConfig
import com.stjarna.mediaviewer.data.LibraryRootFactory
import com.stjarna.mediaviewer.vm.LibraryViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    vm: LibraryViewModel,
    onBack: () -> Unit,
    onPickLibraryFolder: () -> Unit,
    onPickRootPath: () -> Unit,
    onExportExchange: () -> Unit,
    onImportExchange: () -> Unit,
) {
    val state = vm.state
    var pathDialog by remember { mutableStateOf(false) }
    var pathInput by remember { mutableStateOf("/sdcard/DCIM") }
    BackHandler(onBack = onBack)

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("设置") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, "返回")
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxWidth()) {

            item { SectionHeader("媒体库") }
            items(state.roots) { root ->
                ListItem(
                    headlineContent = { Text(root.name) },
                    supportingContent = {
                        Column {
                            Text(vm.rootDisplay(root), style = MaterialTheme.typography.bodySmall)
                            Text(
                                "${root.assetCount} 张" +
                                    (root.lastScanAt?.let { " · 已扫描" } ?: " · 未扫描"),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    },
                    trailingContent = {
                        Row {
                            IconButton(onClick = { vm.scan(root) }) {
                                Icon(Icons.Filled.Refresh, "扫描这个库")
                            }
                            IconButton(onClick = { vm.removeRoot(root) }) {
                                Icon(Icons.Filled.Delete, "移除")
                            }
                        }
                    },
                )
            }
            item {
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton(onClick = onPickLibraryFolder) {
                            Icon(Icons.Filled.Add, null, Modifier.width(18.dp))
                            Text(" 选择文件夹")
                        }
                        FilledTonalButton(onClick = { pathDialog = true }) { Text("输入路径") }
                        TextButton(onClick = { vm.addMediaStoreRoot() }) { Text("系统相册") }
                    }
                    Text(
                        "系统相册只需一次媒体权限；相机卡/SD 卡/www 目录可用「选择文件夹」或 root 模式输入路径。",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { SectionHeader("Root 模式（可选）") }
            item {
                ListItem(
                    headlineContent = { Text("使用 root 获取更多权限") },
                    supportingContent = {
                        Text(
                            when {
                                state.rootMode && state.rootAvailable -> "已启用 · su 可用：可扫描宿主任意目录、写 XMP、后续整理文件"
                                state.rootMode -> "已启用，但当前没有 su 权限（未 root 或未授权）"
                                else -> "关闭时只用系统授权访问相册/文件夹；开启后可用 root 扫描与写回"
                            },
                            style = MaterialTheme.typography.bodySmall,
                        )
                    },
                    trailingContent = {
                        Switch(
                            checked = state.rootMode,
                            onCheckedChange = { vm.setRootMode(it) },
                        )
                    },
                )
            }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { SectionHeader("设置与图片数据迁移") }
            item {
                Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        "与 Windows / macOS 共用 stj-media-viewer/exchange v1 交换文件：界面偏好 + 图片编辑成果 + 合集，不含任何密钥。",
                        style = MaterialTheme.typography.bodySmall,
                    )
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton(onClick = onExportExchange) { Text("导出…") }
                        FilledTonalButton(onClick = onImportExchange) { Text("导入…") }
                    }
                }
            }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { SectionHeader("外观") }
            item {
                Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("明暗模式", style = MaterialTheme.typography.labelLarge)
                    SingleChoiceSegmentedButtonRow {
                        listOf("system" to "系统", "light" to "浅色", "dark" to "深色").forEachIndexed { i, (mode, label) ->
                            SegmentedButton(
                                selected = state.themeMode == mode,
                                onClick = { vm.setTheme(mode) },
                                shape = SegmentedButtonDefaults.itemShape(i, 3),
                                modifier = Modifier.weight(1f),
                            ) { Text(label, maxLines = 1) }
                        }
                    }
                    ListItem(
                        headlineContent = { Text("动态取色（Material You）") },
                        supportingContent = { Text("Android 12+ 用壁纸取色；关闭则用 STJ 品牌色") },
                        trailingContent = {
                            Switch(checked = state.dynamicColor, onCheckedChange = { vm.setDynamicColor(it) })
                        },
                    )
                    Text("网格密度", style = MaterialTheme.typography.labelLarge)
                    SingleChoiceSegmentedButtonRow {
                        listOf("small" to "小", "medium" to "中", "large" to "大").forEachIndexed { i, (d, label) ->
                            SegmentedButton(
                                selected = state.density == d,
                                onClick = { vm.setDensity(d) },
                                shape = SegmentedButtonDefaults.itemShape(i, 3),
                                modifier = Modifier.weight(1f),
                            ) { Text(label, maxLines = 1) }
                        }
                    }
                }
            }

            item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }
            item { SectionHeader("关于") }
            item {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("STJ 素材库 Android ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.titleSmall)
                    Text(
                        "Stjarna PERSPECTIVE · 与 Windows 版同步更新\n交换格式：stj-media-viewer/exchange v1\n素材不上传，AI/云同步在移动端暂不提供。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            item { Spacer(Modifier.padding(bottom = 24.dp)) }
        }
    }

    if (pathDialog) {
        AlertDialog(
            onDismissRequest = { pathDialog = false },
            title = { Text("添加素材目录") },
            text = {
                Column {
                    OutlinedTextField(
                        value = pathInput,
                        onValueChange = { pathInput = it },
                        label = { Text("绝对路径") },
                        singleLine = true,
                    )
                    Text(
                        "例：/sdcard/DCIM、/storage/emulated/0/Pictures；root 模式下可填普通应用读不到的路径。",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                }
            },
            confirmButton = {
                Button(onClick = {
                    val path = pathInput.trim()
                    if (path.isNotEmpty()) vm.addPathRoot(path)
                    pathDialog = false
                }) { Text("添加") }
            },
            dismissButton = { TextButton(onClick = { pathDialog = false }) { Text("取消") } },
        )
    }

    state.pendingImport?.let { preview ->
        AlertDialog(
            onDismissRequest = { vm.dismissImport() },
            title = { Text("导入交换文件") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(preview.text, style = MaterialTheme.typography.bodySmall)
                    Text(
                        "「只填空」保留本机已有编辑；「覆盖」用文件里的值覆盖。",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            },
            confirmButton = {
                Button(onClick = { vm.applyImport(com.stjarna.mediaviewer.core.exchange.MergeMode.FillEmpty) }) {
                    Text("只填空字段")
                }
            },
            dismissButton = {
                Row {
                    TextButton(onClick = { vm.applyImport(com.stjarna.mediaviewer.core.exchange.MergeMode.Overwrite) }) {
                        Text("覆盖已有值")
                    }
                    TextButton(onClick = { vm.dismissImport() }) { Text("取消") }
                }
            },
        )
    }

    if (state.importBusy) {
        AlertDialog(
            onDismissRequest = {},
            title = { Text("处理中…") },
            text = { Text("正在读写交换文件，请稍候。") },
            confirmButton = {},
        )
    }
}

@Composable
private fun SectionHeader(title: String) {
    Text(
        title,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp),
    )
}