package com.stjarna.mediaviewer.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.stjarna.mediaviewer.core.ColorLabels
import com.stjarna.mediaviewer.core.capturedText
import com.stjarna.mediaviewer.core.DeviceNames
import com.stjarna.mediaviewer.core.UNSET
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.PickState
import com.stjarna.mediaviewer.core.PostProcessState
import com.stjarna.mediaviewer.util.AssetActions
import com.stjarna.mediaviewer.vm.LibraryViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilterSheet(vm: LibraryViewModel, onDismiss: () -> Unit) {
    val state = vm.state
    val filter = state.filter
    val facets = state.facets
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheet) {
        LazyColumn(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                Row(
                    Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("筛选", style = MaterialTheme.typography.titleLarge)
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = { vm.resetFilter() }) { Text("重置") }
                }
            }

            item {
                ChipSection(
                    title = "类型",
                    items = facets.kinds.entries.sortedByDescending { it.value }
                        .map { it.key.label to "${it.key.label} ${it.value}" },
                    selected = filter.kinds.map { it.label }.toSet(),
                    onToggle = { label ->
                        val kind = com.stjarna.mediaviewer.core.MediaKind.entries.first { it.label == label.substringBefore(' ') }
                        vm.updateFilter { f ->
                            f.copy(kinds = if (kind in f.kinds) f.kinds - kind else f.kinds + kind)
                        }
                    },
                )
            }

            if (facets.formats.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "文件格式",
                        items = facets.formats.entries.sortedByDescending { it.value }
                            .map { (k, v) -> k to "$k $v" },
                        selected = filter.formats,
                        onToggle = { key ->
                            vm.updateFilter { f ->
                                f.copy(formats = if (key in f.formats) f.formats - key else f.formats + key)
                            }
                        },
                    )
                }
            }

            if (facets.devices.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "拍摄设备",
                        items = facets.devices.entries.sortedByDescending { it.value }.take(24)
                            .map { (k, v) -> k to "${DeviceNames.display(k)} $v" },
                        selected = filter.devices,
                        onToggle = { key ->
                            vm.updateFilter { f ->
                                f.copy(devices = if (key in f.devices) f.devices - key else f.devices + key)
                            }
                        },
                    )
                }
            }

            if (facets.cities.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "拍摄地点",
                        items = facets.cities.entries.sortedByDescending { it.value }.take(24)
                            .map { (k, v) -> k to "${if (k == UNSET) "未分类" else k} $v" },
                        selected = filter.cities,
                        onToggle = { key ->
                            vm.updateFilter { f ->
                                f.copy(cities = if (key in f.cities) f.cities - key else f.cities + key)
                            }
                        },
                    )
                }
            }

            if (facets.years.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "拍摄年份",
                        items = facets.years.entries.sortedByDescending { it.key }
                            .map { (k, v) -> k.toString() to "$k $v" },
                        selected = filter.years.map { it.toString() }.toSet(),
                        onToggle = { key ->
                            val year = key.toIntOrNull() ?: return@ChipSection
                            vm.updateFilter { f ->
                                f.copy(years = if (year in f.years) f.years - year else f.years + year)
                            }
                        },
                    )
                }
            }

            if (facets.themes.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "拍摄主题",
                        items = facets.themes.entries.sortedByDescending { it.value }.take(24)
                            .map { (k, v) -> k to "${if (k == UNSET) "未填写" else k} $v" },
                        selected = filter.themes,
                        onToggle = { key ->
                            vm.updateFilter { f ->
                                f.copy(themes = if (key in f.themes) f.themes - key else f.themes + key)
                            }
                        },
                    )
                }
            }

            if (facets.tones.isNotEmpty()) {
                item {
                    ChipSection(
                        title = "拍摄影调",
                        items = facets.tones.entries.sortedByDescending { it.value }.take(24)
                            .map { (k, v) -> k to "${if (k == UNSET) "未填写" else k} $v" },
                        selected = filter.tones,
                        onToggle = { key ->
                            vm.updateFilter { f ->
                                f.copy(tones = if (key in f.tones) f.tones - key else f.tones + key)
                            }
                        },
                    )
                }
            }

            item {
                Text("色标", style = MaterialTheme.typography.labelLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ColorLabels.order.forEach { label ->
                        FilterChip(
                            selected = label in filter.colorLabels,
                            onClick = {
                                vm.updateFilter { f ->
                                    f.copy(colorLabels = if (label in f.colorLabels) f.colorLabels - label else f.colorLabels + label)
                                }
                            },
                            label = { Text(ColorLabels.display(label)) },
                        )
                    }
                }
            }

            item {
                Text("评分", style = MaterialTheme.typography.labelLarge)
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items((1..5).toList()) { r ->
                        FilterChip(
                            selected = filter.minRating == r,
                            onClick = { vm.updateFilter { it.copy(minRating = if (it.minRating == r) null else r) } },
                            label = { Text("$r 星+") },
                        )
                    }
                }
            }

            item {
                Text("选用状态", style = MaterialTheme.typography.labelLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    PickState.entries.forEach { pick ->
                        FilterChip(
                            selected = filter.pickState == pick,
                            onClick = { vm.updateFilter { it.copy(pickState = pick) } },
                            label = { Text(pick.label) },
                        )
                    }
                }
            }

            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = filter.onlyUncolored,
                        onClick = { vm.updateFilter { it.copy(onlyUncolored = !it.onlyUncolored, onlyColored = false) } },
                        label = { Text("只看未调色") },
                    )
                    FilterChip(
                        selected = filter.onlyColored,
                        onClick = { vm.updateFilter { it.copy(onlyColored = !it.onlyColored, onlyUncolored = false) } },
                        label = { Text("只看已调色") },
                    )
                }
            }

            item {
                Column(Modifier.fillMaxWidth().padding(bottom = 24.dp)) {
                    Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) {
                        Text("完成 · ${state.filtered.size} 张")
                    }
                }
            }
        }
    }
}

@Composable
private fun ChipSection(
    title: String,
    items: List<Pair<String, String>>,
    selected: Set<String>,
    onToggle: (String) -> Unit,
) {
    Column {
        Text(title, style = MaterialTheme.typography.labelLarge)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(items) { (key, label) ->
                FilterChip(
                    selected = key in selected,
                    onClick = { onToggle(key) },
                    label = { Text(label) },
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DetailSheet(asset: MediaAsset, vm: LibraryViewModel, onDismiss: () -> Unit) {
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var rating by remember { mutableStateOf(asset.rating) }
    var colorLabel by remember { mutableStateOf(asset.colorLabel) }
    var isPick by remember { mutableStateOf(asset.isPick) }
    var postProcess by remember { mutableStateOf(asset.postProcess.code) }
    var isLog by remember { mutableStateOf(asset.isLog) }
    var city by remember { mutableStateOf(asset.city ?: "") }
    var title by remember { mutableStateOf(asset.title ?: "") }
    var themes by remember { mutableStateOf(asset.themes.joinToString(",")) }
    var tone by remember { mutableStateOf(asset.tone ?: "") }
    var keywords by remember { mutableStateOf(asset.keywords.joinToString(",")) }
    var fullscreen by remember { mutableStateOf(false) }
    var deviceKey by remember { mutableStateOf(asset.deviceKey ?: "") }
    var cameraModel by remember { mutableStateOf(asset.cameraModel ?: "") }
    var capturedText by remember {
        mutableStateOf(
            asset.capturedAt?.let {
                java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", java.util.Locale.getDefault()).format(java.util.Date(it))
            } ?: ""
        )
    }
    var latitude by remember { mutableStateOf(asset.latitude?.toString() ?: "") }
    var longitude by remember { mutableStateOf(asset.longitude?.toString() ?: "") }

    if (fullscreen) {
        FullscreenViewer(
            asset = asset,
            loader = com.stjarna.mediaviewer.StjApp.instance.thumbnails,
            onClose = { fullscreen = false },
        )
    }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheet) {
        LazyColumn(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                androidx.compose.foundation.layout.Box {
                    AssetThumbnail(
                        asset = asset,
                        loader = com.stjarna.mediaviewer.StjApp.instance.thumbnails,
                        targetPx = 1400,
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(220.dp)
                            .clickable { fullscreen = true },
                    )
                    androidx.compose.material3.Surface(
                        modifier = Modifier.align(Alignment.BottomEnd).padding(8.dp),
                        color = androidx.compose.ui.graphics.Color(0x99000000),
                        shape = MaterialTheme.shapes.small,
                    ) {
                        Text(
                            "点击全屏",
                            color = androidx.compose.ui.graphics.Color.White,
                            style = MaterialTheme.typography.labelSmall,
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp),
                        )
                    }
                }
            }
            item {
                Text(asset.fileName, style = MaterialTheme.typography.titleMedium)
                Text(
                    buildString {
                        append(asset.formatCode)
                        if (asset.isRaw) append(" · RAW")
                        asset.width?.let { append(" · ${it}") }
                        asset.height?.let { append("×$it") }
                        asset.capturedText?.let { append(" · $it") }
                        asset.deviceKey?.let { append(" · $it") }
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            item {
                val context = LocalContext.current
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { AssetActions.share(context, asset) },
                        modifier = Modifier.weight(1f),
                    ) {
                        Icon(Icons.Filled.Share, null, Modifier.size(18.dp))
                        Text(" 分享")
                    }
                    OutlinedButton(
                        onClick = { AssetActions.openWith(context, asset) },
                        modifier = Modifier.weight(1f),
                    ) {
                        Icon(Icons.Filled.OpenInNew, null, Modifier.size(18.dp))
                        Text(" 用其他方式打开")
                    }
                }
            }

            item {
                Text("评分", style = MaterialTheme.typography.labelLarge)
                RatingStars(rating, enabled = true, onRating = { rating = it }, size = 30)
            }
            item {
                Text("色标", style = MaterialTheme.typography.labelLarge)
                ColorDotRow(colorLabel, onSelect = { colorLabel = it })
            }
            item {
                Text("选用状态", style = MaterialTheme.typography.labelLarge)
                SingleChoiceSegmentedButtonRow {
                    SegmentedButton(
                        selected = isPick == true,
                        onClick = { isPick = true },
                        shape = SegmentedButtonDefaults.itemShape(0, 3),
                    ) { Text("选用") }
                    SegmentedButton(
                        selected = isPick == null,
                        onClick = { isPick = null },
                        shape = SegmentedButtonDefaults.itemShape(1, 3),
                    ) { Text("未标记") }
                    SegmentedButton(
                        selected = isPick == false,
                        onClick = { isPick = false },
                        shape = SegmentedButtonDefaults.itemShape(2, 3),
                    ) { Text("废片") }
                }
            }
            item {
                Text("调色状态", style = MaterialTheme.typography.labelLarge)
                SingleChoiceSegmentedButtonRow {
                    SegmentedButton(
                        selected = postProcess == PostProcessState.Unprocessed.code,
                        onClick = { postProcess = PostProcessState.Unprocessed.code },
                        shape = SegmentedButtonDefaults.itemShape(0, 2),
                    ) { Text("未调色") }
                    SegmentedButton(
                        selected = postProcess == PostProcessState.Processed.code,
                        onClick = { postProcess = PostProcessState.Processed.code },
                        shape = SegmentedButtonDefaults.itemShape(1, 2),
                    ) { Text("已调色") }
                }
            }
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Log 素材", style = MaterialTheme.typography.labelLarge)
                    Spacer(Modifier.weight(1f))
                    Switch(checked = isLog == true, onCheckedChange = { isLog = it })
                }
            }
            item {
                TextField(
                    value = deviceKey,
                    onValueChange = { deviceKey = it },
                    label = { Text("设备标识（如 ILCE-7M4 / X-T5，会参与设备筛选）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = cameraModel,
                    onValueChange = { cameraModel = it },
                    label = { Text("相机型号（EXIF Model，可修正）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = capturedText,
                    onValueChange = { capturedText = it },
                    label = { Text("拍摄时间（yyyy-MM-dd HH:mm，留空=未知）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    TextField(
                        value = latitude,
                        onValueChange = { latitude = it },
                        label = { Text("纬度") },
                        modifier = Modifier.weight(1f),
                        singleLine = true,
                    )
                    TextField(
                        value = longitude,
                        onValueChange = { longitude = it },
                        label = { Text("经度") },
                        modifier = Modifier.weight(1f),
                        singleLine = true,
                    )
                }
            }
            item {
                TextField(
                    value = city,
                    onValueChange = { city = it },
                    label = { Text("拍摄地点（自由输入，如 平遥 / 上海·外滩）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = title,
                    onValueChange = { title = it },
                    label = { Text("标题") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = themes,
                    onValueChange = { themes = it },
                    label = { Text("主题（逗号分隔，第一个为主主题）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = tone,
                    onValueChange = { tone = it },
                    label = { Text("影调（高调 / 低调 / 冷调 …）") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
            item {
                TextField(
                    value = keywords,
                    onValueChange = { keywords = it },
                    label = { Text("关键词（逗号分隔）") },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                Column(Modifier.fillMaxWidth().padding(bottom = 28.dp)) {
                    Button(
                        onClick = {
                            asset.rating = rating
                            asset.colorLabel = colorLabel
                            asset.isPick = isPick
                            asset.postProcess = PostProcessState.of(postProcess)
                            asset.isLog = isLog
                            asset.city = city.trim().ifEmpty { null }
                            asset.title = title.trim().ifEmpty { null }
                            asset.themes = themes.split(',', '，').map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()
                            asset.tone = tone.trim().ifEmpty { null }
                            asset.keywords = keywords.split(',', '，').map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()
                            asset.deviceKey = deviceKey.trim().ifEmpty { null }
                            asset.cameraModel = cameraModel.trim().ifEmpty { null }
                            asset.capturedAt = parseDateTime(capturedText) ?: asset.capturedAt
                            asset.latitude = latitude.trim().toDoubleOrNull()?.takeIf { it in -90.0..90.0 }
                            asset.longitude = longitude.trim().toDoubleOrNull()?.takeIf { it in -180.0..180.0 }
                            vm.saveAsset(asset)
                            onDismiss()
                        },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text("保存（并写回 XMP 边车）") }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BatchSheet(vm: LibraryViewModel, onDismiss: () -> Unit) {
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var rating by remember { mutableStateOf<Int?>(null) }
    var colorLabel by remember { mutableStateOf<String?>(null) }
    var isPick by remember { mutableStateOf<Boolean?>(null) }
    var postProcess by remember { mutableStateOf<Int?>(null) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheet) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("批量编辑 · 已选 ${vm.state.selection.size} 项", style = MaterialTheme.typography.titleMedium)
            Text("评分（不选 = 不改）", style = MaterialTheme.typography.labelLarge)
            RatingStars(rating ?: 0, enabled = true, onRating = { rating = it }, size = 28)
            Text("色标", style = MaterialTheme.typography.labelLarge)
            ColorDotRow(colorLabel, onSelect = { colorLabel = it })
            Text("选用状态", style = MaterialTheme.typography.labelLarge)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(selected = isPick == true, onClick = { isPick = true }, label = { Text("选用") })
                FilterChip(selected = isPick == null, onClick = { isPick = null }, label = { Text("不改") })
                FilterChip(selected = isPick == false, onClick = { isPick = false }, label = { Text("废片") })
            }
            Text("调色状态", style = MaterialTheme.typography.labelLarge)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(
                    selected = postProcess == PostProcessState.Unprocessed.code,
                    onClick = { postProcess = PostProcessState.Unprocessed.code },
                    label = { Text("未调色") },
                )
                FilterChip(
                    selected = postProcess == PostProcessState.Processed.code,
                    onClick = { postProcess = PostProcessState.Processed.code },
                    label = { Text("已调色") },
                )
            }
            Button(
                onClick = {
                    vm.applyBatch(rating = rating, colorLabel = colorLabel, isPick = isPick, postProcess = postProcess)
                    onDismiss()
                },
                modifier = Modifier.fillMaxWidth(),
            ) { Text("应用到 ${vm.state.selection.size} 项") }
            Spacer(Modifier.height(12.dp))
        }
    }
}

private fun parseDateTime(text: String): Long? {
    val trimmed = text.trim()
    if (trimmed.isEmpty()) return null
    val patterns = listOf("yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd")
    for (pattern in patterns) {
        runCatching {
            val fmt = java.text.SimpleDateFormat(pattern, java.util.Locale.getDefault())
            fmt.isLenient = false
            return fmt.parse(trimmed)?.time
        }
    }
    return null
}
