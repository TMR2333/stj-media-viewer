package com.stjarna.mediaviewer.core.meta

import com.stjarna.mediaviewer.core.meta.XmpCodec
import com.stjarna.mediaviewer.core.meta.XmpData
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class XmpTest {
    @Test
    fun buildThenParseRoundTrip() {
        val data = XmpData(
            rating = 4,
            label = "蓝",
            keywords = listOf("外滩", "夜景"),
            city = "上海",
            title = "外滩夜色",
            theme = "城市夜景",
            tone = "冷调",
        )
        val xml = XmpCodec.build(data)
        assertTrue(xml.contains("xmp:Rating=\"4\""))
        assertTrue(xml.contains("photoshop:City"))
        val parsed = XmpCodec.parse(xml)
        assertEquals(4, parsed.rating)
        assertEquals("蓝", parsed.label)
        assertEquals(listOf("外滩", "夜景"), parsed.keywords)
        assertEquals("上海", parsed.city)
        assertEquals("城市夜景", parsed.theme)
        assertEquals("冷调", parsed.tone)
    }

    @Test
    fun parsesAttributeStyleAndEmptyFields() {
        val xml = """<rdf:Description xmp:Rating="5" photoshop:City="平遥" xmp:Label="红"/>"""
        val parsed = XmpCodec.parse(xml)
        assertEquals(5, parsed.rating)
        assertEquals("平遥", parsed.city)
        assertEquals("红", parsed.label)
        assertTrue(parsed.keywords.isEmpty())
        assertNull(parsed.theme)
    }

    @Test
    fun labelToInternal() {
        assertEquals("Red", XmpCodec.labelToInternal("红"))
        assertEquals("Purple", XmpCodec.labelToInternal("紫"))
        assertEquals(null, XmpCodec.labelToInternal(null))
    }
}