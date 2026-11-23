package com.stjarna.mediaviewer.cloud

import okhttp3.Credentials
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.w3c.dom.Element
import java.io.File
import java.io.IOException
import java.net.URLDecoder
import java.util.concurrent.TimeUnit
import javax.xml.parsers.DocumentBuilderFactory

/** 云端一个条目（path 是相对 WebDAV 根的路径，'/' 分隔，根为 ""）。 */
data class DavEntry(
    val path: String,
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedEpochMs: Long,
)

/**
 * 极简 WebDAV 客户端（云相册用）：
 * - PROPFIND 一层目录；Basic 认证；
 * - GET 支持 Range（含 302 → CDN 的手动重定向，Range 头必须每次重发）；
 * - PUT 支持，失败时用 `Expect: 100-continue` 重试（123pan 这类服务器需要）。
 */
class WebDavClient(
    baseUrl: String,
    private val userName: String,
    private val password: String,
) {
    private val base = baseUrl.trim().trimEnd('/')

    private val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS)
        .readTimeout(120, TimeUnit.SECONDS)
        .writeTimeout(300, TimeUnit.SECONDS)
        .followRedirects(false) // 手动重定向，保证 Range 头不丢
        .build()

    private fun auth(req: Request.Builder): Request.Builder =
        req.header("Authorization", Credentials.basic(userName, password))

    /** 把相对路径拼成绝对 URL（逐段编码，保留中文）。 */
    fun url(relative: String, isDirectory: Boolean): String {
        val segments = relative.replace('\\', '/').split('/').filter { it.isNotEmpty() }
        val sb = StringBuilder(base)
        for (segment in segments) {
            if (segment == "." || segment == "..") throw IOException("非法路径")
            sb.append('/').append(java.net.URLEncoder.encode(segment, "UTF-8").replace("+", "%20"))
        }
        if (isDirectory && !sb.endsWith('/')) sb.append('/')
        return sb.toString()
    }

    /** 列出目录一层。 */
    fun list(relativeDir: String): List<DavEntry> {
        val body = """<?xml version="1.0" encoding="utf-8"?>
<D:propfind xmlns:D="DAV:"><D:prop>
<D:displayname/><D:getcontentlength/><D:getlastmodified/><D:resourcetype/>
</D:prop></D:propfind>""".toRequestBody("application/xml; charset=utf-8".toMediaType())

        val request = auth(Request.Builder().url(url(relativeDir, true)))
            .header("Depth", "1")
            .method("PROPFIND", body)
            .build()

        val xml = client.newCall(request).execute().use { resp ->
            if (resp.code !in 200..299) throw IOException("PROPFIND 失败：HTTP ${resp.code}")
            resp.body?.string() ?: throw IOException("PROPFIND 返回空")
        }

        val doc = DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true }
            .newDocumentBuilder().parse(xml.byteInputStream(Charsets.UTF_8))
        val ns = "DAV:"
        val responses = doc.getElementsByTagNameNS(ns, "response")
        val result = mutableListOf<DavEntry>()
        val dirPath = relativeDir.replace('\\', '/').trim('/')
        for (i in 0 until responses.length) {
            val resp = responses.item(i) as? Element ?: continue
            val href = resp.getElementsByTagNameNS(ns, "href").item(0)?.textContent
                ?: (resp.getElementsByTagName("D:href").item(0)?.textContent)
                ?: continue
            val decoded = URLDecoder.decode(href.replace("+", "%2B"), "UTF-8")
            val rel = toRelative(decoded) ?: continue
            if (rel.isEmpty()) continue
            val parent = if ('/' in rel) rel.substringBeforeLast('/') else ""
            if (!parent.equals(dirPath, ignoreCase = true)) continue

            val isDir = resp.getElementsByTagNameNS(ns, "collection").length > 0
            val sizeText = resp.getElementsByTagNameNS(ns, "getcontentlength").item(0)?.textContent
            val modifiedText = resp.getElementsByTagNameNS(ns, "getlastmodified").item(0)?.textContent
            val name = rel.substringAfterLast('/')
            result.add(
                DavEntry(
                    path = rel,
                    name = name,
                    isDirectory = isDir,
                    sizeBytes = sizeText?.trim()?.toLongOrNull() ?: 0L,
                    modifiedEpochMs = parseHttpDate(modifiedText) ?: 0L,
                )
            )
        }
        return result.sortedWith(compareBy({ !it.isDirectory }, { it.name.lowercase() }))
    }

    /** 读取一段字节；服务器忽略 Range（返回 200）时本地切片。 */
    fun range(relative: String, offset: Long, length: Int): ByteArray? {
        if (offset < 0 || length <= 0) return null
        val request = auth(Request.Builder().url(url(relative, false)))
            .header("Range", "bytes=$offset-${offset + length - 1}")
            .get()
            .build()
        return executeWithRedirects(request) { resp ->
            if (!resp.isSuccessful) return@executeWithRedirects null
            val bytes = resp.body?.bytes() ?: return@executeWithRedirects null
            if (resp.code == 206) bytes else {
                if (offset >= bytes.size) ByteArray(0)
                else bytes.copyOfRange(offset.toInt(), minOf(bytes.size.toLong(), offset + length).toInt())
            }
        }
    }

    /** 下载整文件。 */
    fun download(relative: String, destination: File) {
        destination.parentFile?.mkdirs()
        val request = auth(Request.Builder().url(url(relative, false))).get().build()
        executeWithRedirects(request) { resp ->
            if (!resp.isSuccessful) throw IOException("下载失败：HTTP ${resp.code}")
            resp.body!!.byteStream().use { input ->
                destination.outputStream().use { output -> input.copyTo(output) }
            }
            true
        } ?: throw IOException("下载失败（重定向过多）")
    }

    /** 上传文件；123pan 这类服务器要求 Expect: 100-continue，失败自动重试一次。 */
    fun upload(local: File, remoteRelative: String, expectContinue: Boolean = false) {
        val type = guessMime(local.name).toMediaType()
        val body = local.asRequestBody(type)
        val builder = auth(Request.Builder().url(url(remoteRelative, false))).put(body)
        if (expectContinue) builder.header("Expect", "100-continue")
        val request = builder.build()
        try {
            client.newCall(request).execute().use { resp ->
                if (resp.code == 417 || resp.code == 400 || resp.code == 100 || resp.code == 500) {
                    if (!expectContinue) {
                        upload(local, remoteRelative, expectContinue = true)
                        return
                    }
                }
                if (!resp.isSuccessful) throw IOException("上传失败：HTTP ${resp.code}")
            }
        } catch (ex: IOException) {
            if (!expectContinue) upload(local, remoteRelative, expectContinue = true) else throw ex
        }
    }

    /** 连通性检查：对根目录做 Depth 0 PROPFIND。 */
    fun test(): String? {
        return try {
            val body = """<?xml version="1.0"?><D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/></D:prop></D:propfind>"""
                .toRequestBody("application/xml".toMediaType())
            val request = auth(Request.Builder().url(url("", true)))
                .header("Depth", "0")
                .method("PROPFIND", body)
                .build()
            client.newCall(request).execute().use { resp ->
                if (resp.code in 200..299) null else "HTTP ${resp.code}"
            }
        } catch (ex: Exception) {
            ex.message ?: ex.javaClass.simpleName
        }
    }

    private fun <T> executeWithRedirects(request: Request, parse: (Response) -> T?): T? {
        var current = request
        repeat(6) {
            val response = try {
                client.newCall(current).execute()
            } catch (ex: IOException) {
                throw IOException("网络请求失败：${ex.message}", ex)
            }
            response.use { resp ->
                val code = resp.code
                if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) {
                    return parse(resp)
                }
                val location = resp.header("Location") ?: return null
                val target = current.url.resolve(location) ?: return null
                current = current.newBuilder().url(target).build()
            }
        }
        return null
    }

    private fun toRelative(decodedPath: String): String? {
        val basePath = base.toHttpUrl().encodedPath.trimEnd('/')
        var path = decodedPath
        if (!path.startsWith('/')) path = "/$path"
        if (path == basePath || path == "$basePath/") return ""
        if (!path.startsWith("$basePath/")) return null
        return path.removePrefix("$basePath/").trim('/')
    }

    private fun parseHttpDate(text: String?): Long? {
        if (text.isNullOrBlank()) return null
        return runCatching {
            val fmt = java.text.SimpleDateFormat("EEE, dd MMM yyyy HH:mm:ss zzz", java.util.Locale.US)
            fmt.parse(text.trim())?.time
        }.getOrNull()
    }

    private fun guessMime(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "webp" -> "image/webp"
        "heic" -> "image/heic"
        "xmp" -> "application/rdf+xml"
        "mp4" -> "video/mp4"
        "mov" -> "video/quicktime"
        "json" -> "application/json"
        else -> "application/octet-stream"
    }
}