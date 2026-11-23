package com.stjarna.mediaviewer.core.geo

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.zip.GZIPOutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class GeoTest {
    private fun tsvGzip(text: String): ByteArray {
        val out = ByteArrayOutputStream()
        GZIPOutputStream(out).use { it.write(text.toByteArray(Charsets.UTF_8)) }
        return out.toByteArray()
    }

    @Test
    fun nearestCityFromSyntheticData() {
        val tsv = buildString {
            appendLine("31.2317\t121.4737\tShanghai\tShanghai\tCN\tSH\t24870895\t上海\t8")
            appendLine("37.2014\t112.1746\tPingyao\tPingyao\tCN\tSX\t50000\t平遥\t9")
            appendLine("48.8566\t2.3522\tParis\tParis\tFR\tIDF\t2148000\t巴黎\t7")
        }
        val geocoder = OfflineGeocoder.load(ByteArrayInputStream(tsvGzip(tsv)))
        assertEquals(3, geocoder.size)

        val shanghai = geocoder.nearest(31.24, 121.49)
        assertNotNull(shanghai)
        assertEquals("上海", shanghai!!.display)

        val pingyao = geocoder.nearest(37.20, 112.18, maxKm = 60.0)
        assertEquals("平遥", pingyao?.display)

        // 太平洋中间：附近没有城市
        assertNull(geocoder.nearest(0.0, -160.0, maxKm = 80.0))
    }

    @Test
    fun haversineDistance() {
        val km = OfflineGeocoder.haversineKm(31.2317, 121.4737, 48.8566, 2.3522)
        assertTrue(km in 9000.0..9600.0, "上海到巴黎应约 9300km，实际 $km")
    }
}