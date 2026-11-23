package com.stjarna.mediaviewer.exchange

import android.content.Context
import android.net.Uri
import com.stjarna.mediaviewer.BuildConfig
import com.stjarna.mediaviewer.core.exchange.ExchangeCodec
import com.stjarna.mediaviewer.core.exchange.ExchangeDocument
import com.stjarna.mediaviewer.core.exchange.ExchangeExporter
import com.stjarna.mediaviewer.core.exchange.ExchangeImporter
import com.stjarna.mediaviewer.core.exchange.ExchangeSettingsApply
import com.stjarna.mediaviewer.core.exchange.ExportSettings
import com.stjarna.mediaviewer.core.exchange.ImportPlan
import com.stjarna.mediaviewer.core.exchange.MergeMode
import com.stjarna.mediaviewer.core.exchange.SettingsApply
import com.stjarna.mediaviewer.data.CatalogDb
import com.stjarna.mediaviewer.data.SettingsStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.OutputStreamWriter
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/** 交换文件导入/导出（SAF Uri ↔ `stj-media-viewer/exchange` v1 JSON）。 */
class ExchangeIo(
    private val context: Context,
    private val db: CatalogDb,
    private val settings: SettingsStore,
) {
    data class Preview(
        val document: ExchangeDocument,
        val plan: ImportPlan,
        val text: String,
    )

    suspend fun exportTo(uri: Uri): String = withContext(Dispatchers.IO) {
        val assets = db.loadAllAssets()
        val byId = assets.associateBy { it.id }
        val collections = db.loadCollections().map { col ->
            Triple(
                col.name,
                col.assetIds.mapNotNull { byId[it] },
                col.createdAt,
            )
        }
        val doc = ExchangeExporter.build(
            assets = assets,
            collections = collections,
            settings = ExportSettings(
                themeMode = settings.themeMode,
                density = settings.density,
                sort = com.stjarna.mediaviewer.core.SortKey.of(settings.sortId),
                descending = settings.descending,
            ),
            appVersion = BuildConfig.VERSION_NAME,
            exportedAt = DateTimeFormatter.ISO_INSTANT.format(Instant.now().atZone(ZoneOffset.UTC)),
        )
        val json = ExchangeCodec.encode(doc)
        context.contentResolver.openOutputStream(uri)?.use { out ->
            OutputStreamWriter(out, Charsets.UTF_8).use { it.write(json) }
        } ?: throw IllegalStateException("无法写入所选文件")
        "已导出 ${doc.assets.size} 条素材编辑、${doc.collections.size} 个合集（${json.length} 字符，不含密钥）"
    }

    suspend fun prepareImport(uri: Uri): Preview = withContext(Dispatchers.IO) {
        val text = context.contentResolver.openInputStream(uri)?.use { it.readBytes().toString(Charsets.UTF_8) }
            ?: throw IllegalStateException("无法读取所选文件")
        val doc = ExchangeCodec.decode(text)
        val plan = ExchangeImporter.buildPlan(doc, db.loadAllAssets())
        val app = doc.app
        val lines = buildList {
            add("来自 ${app.version} · ${app.platform} · ${app.exportedAt}")
            add("素材：匹配 ${plan.matches.size} 条，未匹配 ${plan.unmatched.size} 条")
            if (plan.ambiguous.isNotEmpty()) add("其中 ${plan.ambiguous.size} 条同名同大小有歧义（按第一条处理）")
            add(if (plan.hasSettings) "包含界面设置" else "不含界面设置")
            add(if (doc.collections.isNotEmpty()) "包含合集 ${doc.collections.size} 个" else "不含合集")
            add("不含密钥")
            if (plan.unmatched.isNotEmpty()) add("未匹配示例：" + plan.unmatched.take(3).joinToString("、"))
        }
        Preview(doc, plan, lines.joinToString("\n"))
    }

    suspend fun apply(preview: Preview, mode: MergeMode): String = withContext(Dispatchers.IO) {
        var updated = 0
        var skipped = 0
        var failed = 0
        val patches = LinkedHashMap<com.stjarna.mediaviewer.core.MediaAsset, com.stjarna.mediaviewer.core.exchange.AssetPatch>()
        for (match in preview.plan.matches) {
            val patch = ExchangeImporter.buildPatch(match.source, match.target, mode)
            if (patch == null) skipped++ else patches[match.target] = patch
        }
        runCatching { db.applyPatches(patches) }
            .onSuccess { updated = patches.size }
            .onFailure { failed = patches.size }

        var created = 0
        var added = 0
        for (plan in preview.plan.collections) {
            if (plan.name.isBlank()) continue
            val existed = db.loadCollections().any { it.name.equals(plan.name, ignoreCase = true) }
            val collection = db.ensureCollection(plan.name)
            if (!existed) created++
            if (plan.assetIds.isNotEmpty()) added += db.addToCollection(collection.id, plan.assetIds)
        }

        val settingsResult: SettingsApply = ExchangeSettingsApply.apply(preview.document)
        settingsResult.themeMode?.let { settings.themeMode = it }
        settingsResult.density?.let { settings.density = it }
        settingsResult.sortKey?.let {
            settings.sortId = it.id
            settings.descending = !settingsResult.ascending
        }

        buildString {
            append("匹配 ${preview.plan.matches.size} · 更新 $updated · 跳过 $skipped")
            if (failed > 0) append(" · 失败 $failed")
            if (preview.plan.unmatched.isNotEmpty()) append(" · 未匹配 ${preview.plan.unmatched.size}")
            if (created > 0) append(" · 新建合集 $created")
            if (added > 0) append(" · 合集新增 $added")
            if (settingsResult.notes.isNotEmpty()) append("\n设置：" + settingsResult.notes.joinToString("、"))
        }
    }
}