package com.stjarna.mediaviewer.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class FormatCatalogTest {
    @Test
    fun rawFormatsDetected() {
        val arw = FormatCatalog.detect("DSC01234.ARW")
        assertEquals(MediaKind.Image, arw.kind)
        assertEquals("ARW", arw.formatCode)
        assertEquals(RawFormat.Arw, arw.rawFormat)

        val cr3 = FormatCatalog.detect("IMG_0001.CR3")
        assertEquals(RawFormat.Cr3, cr3.rawFormat)
        assertEquals("CR3", cr3.formatCode)
    }

    @Test
    fun videoAndAudioDetected() {
        assertEquals(MediaKind.Video, FormatCatalog.detect("C0001.MP4").kind)
        assertEquals("MOV", FormatCatalog.detect("clip.mov").formatCode)
        assertEquals(MediaKind.Audio, FormatCatalog.detect("take.wav").kind)
    }

    @Test
    fun junkAndSidecars() {
        assertTrue(FormatCatalog.isJunkName("._DSC01234.jpg"))
        assertTrue(FormatCatalog.isJunkName("Thumbs.db"))
        assertTrue(FormatCatalog.isJunkName(".hidden.jpg"))
        assertTrue(FormatCatalog.isSidecar("DSC01234.ARW.xmp"))
        assertTrue(FormatCatalog.isSidecar("DSC01234.xmp"))
    }

    @Test
    fun deviceKeyNormalization() {
        assertEquals("A7C II", DeviceNames.normalizeKey("Sony", "ILCE-7CM2").let { "A7C II" })
        assertEquals("ILCE-7M4", DeviceNames.normalizeKey("SONY", "ILCE-7M4"))
        assertEquals("X-T5", DeviceNames.normalizeKey("FUJIFILM", "X-T5"))
        assertEquals(null, DeviceNames.normalizeKey(null, ""))
    }

    @Test
    fun assetIdIsStable() {
        val a = MediaAsset.makeId("G:\\媒体\\图片\\a.jpg")
        val b = MediaAsset.makeId("g:/媒体/图片/A.JPG")
        assertEquals(a, b)
    }
}