package com.stjarna.mediaviewer.scan

import java.io.File
import java.util.concurrent.TimeUnit

/**
 * root 支持：只做三件移动端真正需要的事——
 * 1) 探测 `su` 是否可用；2) 用 su 列目录（普通权限看不到的路径）；3) 用 su 写 XMP / 移动文件。
 * 所有命令都带超时，失败一律返回 null/false，绝不阻塞主线程。
 */
object RootManager {
    @Volatile
    private var cachedAvailable: Boolean? = null

    data class RootFile(val path: String, val size: Long, val modified: Long)

    data class RootResult(val code: Int, val stdout: String, val stderr: String) {
        val ok: Boolean get() = code == 0
    }

    fun isAvailable(force: Boolean = false): Boolean {
        cachedAvailable?.let { if (!force) return it }
        val result = exec("id", timeoutSeconds = 6)
        val available = result != null && result.ok && result.stdout.contains("uid=0")
        cachedAvailable = available
        return available
    }

    fun exec(command: String, timeoutSeconds: Int = 30): RootResult? = try {
        val process = ProcessBuilder("su", "-c", command).redirectErrorStream(false).start()
        val finished = process.waitFor(timeoutSeconds.toLong(), TimeUnit.SECONDS)
        if (!finished) {
            process.destroyForcibly()
            null
        } else {
            RootResult(
                code = process.exitValue(),
                stdout = process.inputStream.bufferedReader().readText(),
                stderr = process.errorStream.bufferedReader().readText(),
            )
        }
    } catch (_: Exception) {
        null
    }

    /** 用 su 列一个目录（递归）里的所有文件；toybox find 不支持 -printf，所以用 stat 兜底。 */
    fun listFiles(directory: String): List<RootFile> {
        val quoted = shellQuote(directory)
        val find = exec("find $quoted -type f 2>/dev/null", timeoutSeconds = 120) ?: return emptyList()
        if (!find.ok || find.stdout.isBlank()) return emptyList()
        val paths = find.stdout.lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
        val result = ArrayList<RootFile>(paths.size)
        // 分批 stat，避免一次命令过长
        paths.chunked(200).forEach { chunk ->
            val stat = exec(
                "stat -c '%n\t%s\t%Y' " + chunk.joinToString(" ") { shellQuote(it) } + " 2>/dev/null",
                timeoutSeconds = 60,
            ) ?: return@forEach
            stat.stdout.lineSequence().forEach { line ->
                val parts = line.trim().split('\t')
                if (parts.size >= 3) {
                    val path = parts[0]
                    result += RootFile(
                        path = path,
                        size = parts[1].toLongOrNull() ?: 0L,
                        modified = (parts[2].toLongOrNull() ?: 0L) * 1000,
                    )
                }
            }
        }
        return result
    }

    /** 只列一层目录（用于快速判断路径是否存在）。 */
    fun listDirectories(directory: String): List<String> {
        val quoted = shellQuote(directory)
        val result = exec("ls -1 $quoted 2>/dev/null", timeoutSeconds = 20) ?: return emptyList()
        return result.stdout.lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
    }

    fun writeFile(path: String, content: ByteArray): Boolean {
        val temp = File.createTempFile("stj-xmp-", ".tmp")
        return try {
            temp.writeBytes(content)
            val result = exec("cp ${shellQuote(temp.absolutePath)} ${shellQuote(path)} && chmod 664 ${shellQuote(path)}")
            result?.ok == true
        } catch (_: Exception) {
            false
        } finally {
            temp.delete()
        }
    }

    fun move(from: String, to: String): Boolean {
        val result = exec("mkdir -p ${shellQuote(File(to).parent ?: "/")} && mv -f ${shellQuote(from)} ${shellQuote(to)}")
        return result?.ok == true
    }

    private fun shellQuote(value: String): String = "'" + value.replace("'", "'\\''") + "'"
}