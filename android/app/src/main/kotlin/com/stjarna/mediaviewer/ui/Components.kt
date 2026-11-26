package com.stjarna.mediaviewer.ui

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.BrokenImage
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarBorder
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.stjarna.mediaviewer.core.ColorLabels
import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import com.stjarna.mediaviewer.scan.ThumbnailLoader

/** 网格密度 → 卡片目标宽度。 */
fun cardWidthFor(density: String) = when (density) {
    "small" -> 86.dp
    "large" -> 180.dp
    else -> 128.dp
}

fun thumbTargetFor(density: String) = when (density) {
    "small" -> 200
    "large" -> 560
    else -> 380
}

@Composable
fun AssetThumbnail(
    asset: MediaAsset,
    loader: ThumbnailLoader,
    modifier: Modifier = Modifier,
    targetPx: Int = 380,
) {
    val bitmap by produceState<Bitmap?>(initialValue = null, asset.id, asset.fileModified, targetPx) {
        value = loader.load(asset, targetPx)
    }
    Box(
        modifier = modifier.background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val bmp = bitmap
        if (bmp != null) {
            Image(
                bitmap = bmp.asImageBitmap(),
                contentDescription = asset.fileName,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Icon(
                imageVector = if (asset.kind == MediaKind.Video) Icons.Filled.Movie else Icons.Filled.BrokenImage,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(28.dp),
            )
        }
        if (asset.isRaw) {
            BadgeChip("RAW", Modifier.align(Alignment.TopStart).padding(6.dp))
        }
        if (asset.isLog == true) {
            BadgeChip("LOG", Modifier.align(Alignment.TopEnd).padding(6.dp), color = StjColors.blue)
        }
        if (asset.kind == MediaKind.Video) {
            BadgeChip("视频", Modifier.align(Alignment.BottomEnd).padding(6.dp), color = Color(0xFF3E6B8F))
        }
    }
}

@Composable
fun BadgeChip(text: String, modifier: Modifier = Modifier, color: Color = Color(0xCC14141A)) {
    Surface(
        modifier = modifier,
        color = color,
        contentColor = Color.White,
        shape = RoundedCornerShape(6.dp),
    ) {
        Text(
            text = text,
            style = MaterialTheme.typography.labelSmall,
            modifier = Modifier.padding(horizontal = 5.dp, vertical = 1.dp),
        )
    }
}

@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun AssetCard(
    asset: MediaAsset,
    selected: Boolean,
    density: String,
    loader: ThumbnailLoader,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
) {
    val scheme = MaterialTheme.colorScheme
    Card(
        modifier = Modifier
            .width(cardWidthFor(density))
            .padding(3.dp)
            .combinedClickable(onClick = onClick, onLongClick = onLongClick),
        shape = MaterialTheme.shapes.extraLarge,
        colors = CardDefaults.cardColors(
            containerColor = if (selected) scheme.primaryContainer else scheme.surfaceVariant.copy(alpha = 0.45f),
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
    ) {
        Box {
            Column {
                AssetThumbnail(
                    asset = asset,
                    loader = loader,
                    targetPx = thumbTargetFor(density),
                    modifier = Modifier
                        .fillMaxWidth()
                        .aspectRatio(1f)
                        .clip(RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp)),
                )
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 8.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = asset.fileName,
                        style = MaterialTheme.typography.labelMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    if (asset.isPick == true) {
                        Icon(Icons.Filled.Favorite, null, tint = StjColors.amber, modifier = Modifier.size(12.dp))
                    } else if (asset.isPick == false) {
                        Icon(Icons.Filled.Favorite, null, tint = scheme.outline, modifier = Modifier.size(12.dp))
                    }
                }
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 8.dp, end = 8.dp, bottom = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RatingStars(asset.rating, enabled = false, onRating = {}, size = 11)
                    Spacer(Modifier.weight(1f))
                    ColorLabels.toChinese(asset.colorLabel)?.let {
                        Box(
                            Modifier
                                .size(9.dp)
                                .clip(CircleShape)
                                .background(StjColors.colorLabel(asset.colorLabel))
                                .border(0.5.dp, scheme.outline.copy(alpha = 0.5f), CircleShape),
                        )
                    }
                }
            }
            if (selected) {
                Box(
                    Modifier
                        .align(Alignment.TopEnd)
                        .padding(6.dp)
                        .size(20.dp)
                        .clip(CircleShape)
                        .background(scheme.primary),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.Filled.Check, null, tint = scheme.onPrimary, modifier = Modifier.size(14.dp))
                }
            }
        }
    }
}

@Composable
fun RatingStars(rating: Int, enabled: Boolean, onRating: (Int) -> Unit, size: Int = 18) {
    Row {
        for (i in 1..5) {
            Icon(
                imageVector = if (i <= rating) Icons.Filled.Star else Icons.Outlined.StarBorder,
                contentDescription = "$i 星",
                tint = if (i <= rating) StjColors.amber else MaterialTheme.colorScheme.outline,
                modifier = Modifier
                    .size(size.dp)
                    .then(if (enabled) Modifier.clickable { onRating(if (rating == i) 0 else i) } else Modifier),
            )
        }
    }
}

@Composable
fun ColorDotRow(
    value: String?,
    onSelect: (String?) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        ColorLabels.order.forEach { label ->
            val color = StjColors.colorLabel(label)
            Box(
                Modifier
                    .size(26.dp)
                    .clip(CircleShape)
                    .background(if (value == label) color else color.copy(alpha = 0.28f))
                    .border(
                        width = if (value == label) 2.dp else 1.dp,
                        color = if (value == label) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.outline.copy(alpha = 0.6f),
                        shape = CircleShape,
                    )
                    .clickable { onSelect(if (value == label) null else label) },
            )
        }
        Box(
            Modifier
                .size(26.dp)
                .clip(CircleShape)
                .border(1.dp, MaterialTheme.colorScheme.outline, CircleShape)
                .clickable { onSelect(null) },
            contentAlignment = Alignment.Center,
        ) {
            Text("×", style = MaterialTheme.typography.labelSmall)
        }
    }
}

@Composable
fun EmptyState(
    title: String,
    description: String,
    modifier: Modifier = Modifier,
    actionText: String? = null,
    onAction: (() -> Unit)? = null,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            Icons.Filled.BrokenImage,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.outline,
            modifier = Modifier.size(48.dp),
        )
        Spacer(Modifier.height(12.dp))
        Text(title, style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(4.dp))
        Text(
            description,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (actionText != null && onAction != null) {
            androidx.compose.foundation.layout.Spacer(Modifier.height(10.dp))
            androidx.compose.material3.TextButton(onClick = onAction) { Text(actionText) }
        }
    }
}