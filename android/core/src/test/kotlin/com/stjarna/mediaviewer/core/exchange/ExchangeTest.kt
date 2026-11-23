package com.stjarna.mediaviewer.core.exchange

import com.stjarna.mediaviewer.core.MediaAsset
import com.stjarna.mediaviewer.core.MediaKind
import com.stjarna.mediaviewer.core.PostProcessState
import com.stjarna.mediaviewer.core.SortKey
import com.stjarna.mediaviewer.core.exchange.ExchangeCodec
import com.stjarna.mediaviewer.core.exchange.ExchangeExporter
import com.stjarna.mediaviewer.core.exchange.ExchangeImporter
import com.stjarna.mediaviewer.core.exchange.ExportSettings
import com.stjarna.mediaviewer.core.exchange.MergeMode
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ExchangeTest {
    private fun asset(name: String, size: Long = 100, rating: Int = 0): MediaAsset = MediaAsset(
        id = name,
        fileName = name,
        sizeBytes = size,
        kind = MediaKind.Image,
        formatCode = "JPG",
        rating = rating,
    )

    @Test
    fun exportOmitsDefaultsAndSecrets() {
        val edited = asset("a.jpg", 100, rating = 4)
        edited.city = "平遥"
        edited.colorLabel = "Blue"
        edited.isPick = true
        edited.themes = mutableListOf("风光")
        edited.keywords = mutableListOf("日落")
        edited.postProcess = PostProcessState.Processed

        val clean = asset("b.jpg")
        val doc = ExchangeExporter.build(
            assets = listOf(edited, clean),
            collections = emptyList(),
            settings = ExportSettings(themeMode = "dark", density = "comfortable", sort = SortKey.CapturedAt, descending = true),
            appVersion = "0.1.0",
            exportedAt = "2026-11-23T00:00:00Z",
        )
        assertEquals(1, doc.assets.size, "只有编辑过的素材才导出")
        val item = doc.assets[0]
        assertEquals(4, item.rating)
        assertEquals("蓝", item.colorLabel)
        assertEquals(1, item.isPick)
        assertEquals(2, item.postProcess)
        assertEquals("medium", doc.settings.density)
        assertEquals("captured", doc.settings.sort?.key)
        assertTrue(!doc.settings.sort!!.ascending)

        val json = ExchangeCodec.encode(doc)
        assertTrue(json.contains("stj-media-viewer/exchange"))
        assertTrue(!json.contains("apiKey", ignoreCase = true))
        assertTrue(!json.contains("password", ignoreCase = true))
    }

    @Test
    fun codecValidatesFormatAndVersion() {
        assertFailsWith<ExchangeFormatException> {
            ExchangeCodec.decode("""{"format":"other/app","formatVersion":1}""")
        }
        assertFailsWith<ExchangeFormatException> {
            ExchangeCodec.decode("""{"format":"stj-media-viewer/exchange","formatVersion":99}""")
        }
        assertFailsWith<ExchangeFormatException> {
            ExchangeCodec.decode("""{"format":"stj-media-viewer/exchange","formatVersion":0}""")
        }
        assertFailsWith<ExchangeFormatException> { ExchangeCodec.decode("{ broken") }
    }

    @Test
    fun matchingByFileNameAndSize() {
        val localA = asset("a.jpg", 100)
        val localB = asset("b.jpg", 200)
        val doc = ExchangeDocument(
            assets = mutableListOf(
                ExchangeAsset(fileName = "A.JPG", sizeBytes = 100, rating = 5),
                ExchangeAsset(fileName = "missing.jpg", sizeBytes = 1),
            )
        )
        val plan = ExchangeImporter.buildPlan(doc, listOf(localA, localB))
        assertEquals(1, plan.matches.size)
        assertEquals(localA, plan.matches[0].target)
        assertEquals(listOf("missing.jpg"), plan.unmatched)
    }

    @Test
    fun fillEmptyKeepsLocalAndOverwriteWins() {
        val local = asset("a.jpg", 100, rating = 1)
        local.city = "上海"
        val src = ExchangeAsset(fileName = "a.jpg", sizeBytes = 100, rating = 5, city = "平遥")

        val fillPatch = ExchangeImporter.buildPatch(src, local, MergeMode.FillEmpty)
        assertNull(fillPatch?.rating, "fillEmpty 不应覆盖本机评分")
        assertNull(fillPatch?.city, "fillEmpty 不应覆盖本机城市")

        val overwritePatch = ExchangeImporter.buildPatch(src, local, MergeMode.Overwrite)
        assertEquals(5, overwritePatch?.rating)
        assertEquals("平遥", overwritePatch?.city)
    }

    @Test
    fun keywordsAndThemesUnion() {
        val local = asset("a.jpg")
        local.keywords = mutableListOf("本机")
        local.themes = mutableListOf("主主题")
        val src = ExchangeAsset(fileName = "a.jpg", sizeBytes = 100, keywords = listOf("文件", "本机"), themes = listOf("次主题"))
        val patch = ExchangeImporter.buildPatch(src, local, MergeMode.FillEmpty)
        assertNotNull(patch)
        assertEquals(listOf("本机", "文件"), patch!!.keywords)
        assertEquals(listOf("主主题", "次主题"), patch.themes)
    }

    @Test
    fun settingsMapping() {
        val doc = ExchangeDocument(
            settings = ExchangeSettings(themeMode = "dark", density = "large", sort = ExchangeSort("rating", ascending = true))
        )
        val applied = ExchangeSettingsApply.apply(doc)
        assertEquals("dark", applied.themeMode)
        assertEquals("large", applied.density)
        assertEquals(SortKey.Rating, applied.sortKey)
        assertTrue(applied.ascending)
    }
}