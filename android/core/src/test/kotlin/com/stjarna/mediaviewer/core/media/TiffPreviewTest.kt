package com.stjarna.mediaviewer.core.media

import com.stjarna.mediaviewer.core.media.ByteArraySource
import com.stjarna.mediaviewer.core.media.TiffPreview
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TiffPreviewTest {
    private fun jpeg(size: Int = 600): ByteArray {
        val bytes = ByteArray(size)
        bytes[0] = 0xFF.toByte(); bytes[1] = 0xD8.toByte()
        for (i in 3 until size - 2) bytes[i] = (i % 251).toByte()
        bytes[2] = 0xFF.toByte()
        bytes[size - 2] = 0xFF.toByte(); bytes[size - 1] = 0xD9.toByte()
        return bytes
    }

    private fun leU32(value: Int): ByteArray =
        byteArrayOf((value and 0xFF).toByte(), ((value shr 8) and 0xFF).toByte(), ((value shr 16) and 0xFF).toByte(), ((value shr 24) and 0xFF).toByte())

    private fun leU16(value: Int): ByteArray =
        byteArrayOf((value and 0xFF).toByte(), ((value shr 8) and 0xFF).toByte())

    private fun tiffWithIfdPreview(preview: ByteArray): ByteArray {
        val ifdOffset = 8
        val entriesStart = ifdOffset + 2
        val dataStart = entriesStart + 3 * 12 + 4
        val out = ArrayList<Byte>()
        out.addAll(byteArrayOf('I'.code.toByte(), 'I'.code.toByte()).toList())
        out.addAll(leU16(42).toList())
        out.addAll(leU32(ifdOffset).toList())
        out.addAll(leU16(3).toList())
        fun entry(tag: Int, value: Int) {
            out.addAll(leU16(tag).toList())
            out.addAll(leU16(4).toList())
            out.addAll(leU32(1).toList())
            out.addAll(leU32(value).toList())
        }
        entry(0x0201, dataStart)
        entry(0x0202, preview.size)
        entry(0x0100, 100) // 普通 IFD 条目
        out.addAll(leU32(0).toList()) // next IFD
        out.addAll(preview.toList())
        return out.toByteArray()
    }

    @Test
    fun extractsPreviewFromIfd() {
        val preview = jpeg()
        val tiff = tiffWithIfdPreview(preview)
        val result = TiffPreview.extract(ByteArraySource(tiff))
        assertNotNull(result)
        assertTrue(result!!.fromIfd)
        assertTrue(TiffPreview.looksLikeJpeg(result.bytes))
        assertEquals(600, result.bytes.size)
    }

    @Test
    fun fallsBackToLargestJpegInFile() {
        val preview = jpeg(2048)
        val header = byteArrayOf('I'.code.toByte(), 'I'.code.toByte(), 42, 0, 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte())
        val padding = ByteArray(1024) { (it % 127).toByte() }
        val bytes = header + padding + preview
        val result = TiffPreview.extract(ByteArraySource(bytes))

        assertNotNull(result)
        assertTrue(!result!!.fromIfd, "应走流式扫描回退")
        assertTrue(TiffPreview.looksLikeJpeg(result.bytes))
        assertEquals(2048, result.bytes.size)
    }

    @Test
    fun nonTiffReturnsNull() {
        val png = byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0)
        assertNull(TiffPreview.extract(ByteArraySource(png)))
    }
}