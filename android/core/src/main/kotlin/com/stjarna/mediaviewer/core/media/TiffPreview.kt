package com.stjarna.mediaviewer.core.media

import kotlin.math.max
import kotlin.math.min

/**
 * 只读的随机访问字节源（Android 上由 File / RandomAccessFile / ContentResolver 实现）。
 * 让"从大 RAW 里取内嵌 JPEG 预览"的逻辑可以脱离平台单测。
 */
interface ByteSource {
    val size: Long
    fun read(offset: Long, length: Int): ByteArray
}

/** 内存字节源（单测用）。 */
class ByteArraySource(private val bytes: ByteArray) : ByteSource {
    override val size: Long get() = bytes.size.toLong()
    override fun read(offset: Long, length: Int): ByteArray {
        if (offset < 0 || offset >= bytes.size) return ByteArray(0)
        val end = min(bytes.size.toLong(), offset + length).toInt()
        return bytes.copyOfRange(offset.toInt(), end)
    }
}

/**
 * RAW/TIFF 内嵌预览提取（与 Windows 版 `RawPreviewExtractor` 同一策略）：
 * 1) 读 TIFF 头，走 IFD0/IFD1 + SubIFDs，取 JPEGInterchangeFormat(513)/Length(514) 快路径；
 * 2) 快路径失败则全文件流式扫描 SOI/EOI，取"最大的一段 JPEG"（很多相机 RAW 预览不在标准 IFD 里）。
 */
object TiffPreview {
    private const val TAG_JPEG_OFFSET = 0x0201
    private const val TAG_JPEG_LENGTH = 0x0202
    private const val TAG_SUB_IFDS = 0x014A

    data class Result(val bytes: ByteArray, val fromIfd: Boolean)

    fun extract(source: ByteSource, maxScanBytes: Long = 96L * 1024 * 1024): Result? {
        if (source.size < 16) return null
        val header = source.read(0, 8)
        if (!isTiffHeader(header)) return null
        val little = header[0] == 'I'.code.toByte()

        runCatching { extractFromIfds(source, little) }?.getOrNull()?.let { return Result(it, true) }
        runCatching { scanLargestJpeg(source, maxScanBytes) }?.getOrNull()?.let { return Result(it, false) }
        return null
    }

    fun isTiffHeader(header: ByteArray): Boolean {
        if (header.size < 4) return false
        val ii = header[0] == 'I'.code.toByte() && header[1] == 'I'.code.toByte()
        val mm = header[0] == 'M'.code.toByte() && header[1] == 'M'.code.toByte()
        if (!ii && !mm) return false
        val magic = if (ii) u16(header, 2, true) else u16(header, 2, false)
        return magic == 42
    }

    private fun extractFromIfds(source: ByteSource, little: Boolean): ByteArray? {
        val ifdOffsets = ArrayDeque<Long>()
        val first = readU32(source, 4, little) ?: return null
        if (first <= 0 || first >= source.size) return null
        ifdOffsets += first
        val visited = HashSet<Long>()

        while (ifdOffsets.isNotEmpty() && visited.size < 8) {
            val offset = ifdOffsets.removeFirst()
            if (offset <= 0 || offset + 2 > source.size || !visited.add(offset)) continue
            val countBytes = source.read(offset, 2)
            if (countBytes.size < 2) continue
            val count = u16(countBytes, 0, little)
            if (count <= 0 || count > 1024) continue
            val entries = source.read(offset + 2, count * 12)
            if (entries.size < count * 12) continue

            var jpegOffset = -1L
            var jpegLength = -1L
            for (i in 0 until count) {
                val base = i * 12
                val tag = u16(entries, base, little)
                val type = u16(entries, base + 2, little)
                val value = u32At(entries, base + 8, little) ?: continue
                when (tag) {
                    TAG_JPEG_OFFSET -> if (type == 4 || type == 3) jpegOffset = value
                    TAG_JPEG_LENGTH -> if (type == 4 || type == 3) jpegLength = value
                    TAG_SUB_IFDS -> {
                        if (type == 4 || type == 3) {
                            // 只有一个 SubIFD 时 value 直接是偏移；多个时 value 指向偏移数组
                            if (value in 8 until source.size) ifdOffsets += value
                        }
                    }
                }
            }

            if (jpegOffset in 8 until source.size && jpegLength > 512) {
                val length = min(jpegLength, source.size - jpegOffset).toInt()
                if (length > 4) {
                    val bytes = source.read(jpegOffset, min(length, 32 * 1024 * 1024))
                    if (looksLikeJpeg(bytes)) return bytes
                }
            }

            // IFD1（缩略图）跟随 next IFD 指针
            val nextOffset = readU32(source, offset + 2 + count * 12L, little) ?: 0
            if (nextOffset in 8 until source.size) ifdOffsets += nextOffset
        }
        return null
    }

    /** 流式扫描全文件，取包含 SOI...EOI 的最大一段。 */
    private fun scanLargestJpeg(source: ByteSource, maxBytes: Long): ByteArray? {
        val limit = min(source.size, maxBytes)
        val chunk = 4 * 1024 * 1024
        var pos = 0L
        var bestStart = -1L
        var bestEnd = -1L
        var window = ByteArray(0)
        var windowStart = 0L

        while (pos < limit) {
            val readLen = min(chunk.toLong(), limit - pos).toInt()
            val bytes = source.read(pos, readLen)
            if (bytes.isEmpty()) break
            val combined = if (window.isEmpty()) bytes else window + bytes
            val combinedStart = if (window.isEmpty()) pos else windowStart

            var i = 0
            while (i + 2 < combined.size) {
                if (combined[i] == 0xFF.toByte() && combined[i + 1] == 0xD8.toByte() &&
                    combined[i + 2] == 0xFF.toByte()
                ) {
                    val end = findEoi(combined, i + 3)
                    if (end > 0) {
                        val absStart = combinedStart + i
                        val absEnd = combinedStart + end
                        if (absEnd - absStart > bestEnd - bestStart) {
                            bestStart = absStart
                            bestEnd = absEnd
                        }
                    }
                    i = end.coerceAtLeast(i + 3)
                } else i++
            }

            // 保留末尾 3 字节，避免 SOI 跨块
            val keep = 3
            window = combined.copyOfRange(max(0, combined.size - keep), combined.size)
            windowStart = combinedStart + combined.size - window.size
            pos += readLen
        }

        if (bestStart < 0 || bestEnd <= bestStart) return null
        val len = min(bestEnd - bestStart, 48L * 1024 * 1024).toInt()
        val result = source.read(bestStart, len)
        return result.takeIf { looksLikeJpeg(it) }
    }

    private fun findEoi(bytes: ByteArray, from: Int): Int {
        var i = from
        while (i + 1 < bytes.size) {
            if (bytes[i] == 0xFF.toByte() && bytes[i + 1] == 0xD9.toByte()) return i + 2
            i++
        }
        return -1
    }

    fun looksLikeJpeg(bytes: ByteArray): Boolean =
        bytes.size > 4 && bytes[0] == 0xFF.toByte() && bytes[1] == 0xD8.toByte()

    private fun u32At(bytes: ByteArray, offset: Int, little: Boolean): Long? {
        if (offset + 4 > bytes.size) return null
        return if (little) {
            (bytes[offset].toLong() and 0xFF) or ((bytes[offset + 1].toLong() and 0xFF) shl 8) or
                ((bytes[offset + 2].toLong() and 0xFF) shl 16) or ((bytes[offset + 3].toLong() and 0xFF) shl 24)
        } else {
            (bytes[offset + 3].toLong() and 0xFF) or ((bytes[offset + 2].toLong() and 0xFF) shl 8) or
                ((bytes[offset + 1].toLong() and 0xFF) shl 16) or ((bytes[offset].toLong() and 0xFF) shl 24)
        }
    }

    private fun readU32(source: ByteSource, offset: Long, little: Boolean): Long? {
        val b = source.read(offset, 4)
        if (b.size < 4) return null
        return if (little) {
            (b[0].toLong() and 0xFF) or ((b[1].toLong() and 0xFF) shl 8) or
                ((b[2].toLong() and 0xFF) shl 16) or ((b[3].toLong() and 0xFF) shl 24)
        } else {
            (b[3].toLong() and 0xFF) or ((b[2].toLong() and 0xFF) shl 8) or
                ((b[1].toLong() and 0xFF) shl 16) or ((b[0].toLong() and 0xFF) shl 24)
        }
    }

    private fun u16(bytes: ByteArray, offset: Int, little: Boolean): Int {
        if (offset + 1 >= bytes.size) return 0
        val a = bytes[offset].toInt() and 0xFF
        val b = bytes[offset + 1].toInt() and 0xFF
        return if (little) a or (b shl 8) else (a shl 8) or b
    }
}