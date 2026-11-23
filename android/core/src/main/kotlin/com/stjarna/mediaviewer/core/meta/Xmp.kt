package com.stjarna.mediaviewer.core.meta

import com.stjarna.mediaviewer.core.ColorLabels

/** XMP 边车里与素材库相关的字段（与 Windows 版 `XmpWriter`/`XmpReader` 的属性名保持一致）。 */
data class XmpData(
    var rating: Int? = null,
    var label: String? = null,
    var keywords: List<String> = emptyList(),
    var city: String? = null,
    var title: String? = null,
    var theme: String? = null,
    var tone: String? = null,
    var creator: String? = null,
    var creatorTool: String? = null,
)

object XmpCodec {
    private const val NS_STJ = "https://stj.local/ns/1.0/"

    private val ratingRegex = Regex("""xmp:Rating\s*=\s*"(-?\d+)"|<xmp:Rating>\s*(-?\d+)\s*</xmp:Rating>""")
    private val labelRegex = Regex("""xmp:Label\s*=\s*"([^"]*)"|<xmp:Label>\s*([^<]*)\s*</xmp:Label>""")
    private val cityRegex = Regex("""photoshop:City\s*=\s*"([^"]*)"|<photoshop:City>\s*([^<]*)\s*</photoshop:City>""")
    private val legacyCityIptc = Regex("""iptc:City\s*=\s*"([^"]*)"""")
    private val themeRegex = Regex("""<stj:Theme[^>]*stj:id\s*=\s*"([^"]*)"""")
    private val toneRegex = Regex("""<stj:Tone[^>]*stj:id\s*=\s*"([^"]*)"""")
    private val creatorRegex = Regex("""<dc:creator>[\s\S]*?<rdf:li[^>]*>([^<]*)</rdf:li>""")
    private val titleAltRegex = Regex("""<dc:title>[\s\S]*?<rdf:li[^>]*>([^<]*)</rdf:li>""")
    private val subjectBagRegex = Regex("""<dc:subject>([\s\S]*?)</dc:subject>""")
    private val liRegex = Regex("""<rdf:li[^>]*>([^<]*)</rdf:li>""")

    /** 从边车 XML 文本里提取字段；缺失的留 null。 */
    fun parse(xml: String): XmpData {
        if (xml.isBlank()) return XmpData()
        fun pick(regex: Regex): String? = regex.find(xml)?.let { m ->
            (m.groupValues.drop(1).firstOrNull { it.isNotEmpty() })?.trim()?.takeIf { it.isNotEmpty() }
        }

        val rating = pick(ratingRegex)?.toIntOrNull()?.coerceIn(0, 5)
        val subject = subjectBagRegex.find(xml)?.groupValues?.get(1)
        val keywords = subject?.let { bag ->
            liRegex.findAll(bag).map { it.groupValues[1].trim() }.filter { it.isNotEmpty() }.toList()
        } ?: emptyList()

        return XmpData(
            rating = rating,
            label = pick(labelRegex),
            keywords = keywords,
            city = pick(cityRegex) ?: pick(legacyCityIptc),
            title = pick(titleAltRegex),
            theme = pick(themeRegex),
            tone = pick(toneRegex),
            creator = pick(creatorRegex),
        )
    }

    /** 生成标准 XMP 边车（覆盖写，保留最小包结构）。 */
    fun build(data: XmpData): String {
        val sb = StringBuilder()
        sb.append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n")
        sb.append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"STJ Media Viewer Android\">\n")
        sb.append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n")
        sb.append("  <rdf:Description rdf:about=\"\"\n")
        sb.append("    xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"\n")
        sb.append("    xmlns:dc=\"http://purl.org/dc/elements/1.1/\"\n")
        sb.append("    xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\"\n")
        sb.append("    xmlns:stj=\"$NS_STJ\"\n")

        data.rating?.takeIf { it > 0 }?.let { sb.append("    xmp:Rating=\"$it\"\n") }
        data.label?.takeIf { it.isNotBlank() }?.let { sb.append("    xmp:Label=\"${escape(it)}\"\n") }
        data.creatorTool?.takeIf { it.isNotBlank() }?.let { sb.append("    xmp:CreatorTool=\"${escape(it)}\"\n") }
        sb.append("    stj:App=\"android\">\n")

        data.city?.takeIf { it.isNotBlank() }?.let {
            sb.append("   <photoshop:City>${escape(it)}</photoshop:City>\n")
        }
        data.title?.takeIf { it.isNotBlank() }?.let {
            sb.append("   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">${escape(it)}</rdf:li></rdf:Alt></dc:title>\n")
        }
        data.keywords.takeIf { it.isNotEmpty() }?.let { kws ->
            sb.append("   <dc:subject><rdf:Bag>\n")
            kws.forEach { sb.append("    <rdf:li>${escape(it)}</rdf:li>\n") }
            sb.append("   </rdf:Bag></dc:subject>\n")
        }
        data.creator?.takeIf { it.isNotBlank() }?.let {
            sb.append("   <dc:creator><rdf:Seq><rdf:li>${escape(it)}</rdf:li></rdf:Seq></dc:creator>\n")
        }
        data.theme?.takeIf { it.isNotBlank() }?.let {
            sb.append("   <stj:Theme stj:id=\"${escape(it)}\" />\n")
        }
        data.tone?.takeIf { it.isNotBlank() }?.let {
            sb.append("   <stj:Tone stj:id=\"${escape(it)}\" />\n")
        }

        sb.append("  </rdf:Description>\n")
        sb.append(" </rdf:RDF>\n")
        sb.append("</x:xmpmeta>\n")
        sb.append("<?xpacket end=\"w\"?>")
        return sb.toString()
    }

    fun labelToInternal(label: String?): String? = label?.let { ColorLabels.fromChinese(it) ?: it.takeIf { v -> v in ColorLabels.order } }

    private fun escape(value: String): String = buildString {
        for (ch in value) when (ch) {
            '&' -> append("&amp;")
            '<' -> append("&lt;")
            '>' -> append("&gt;")
            '"' -> append("&quot;")
            else -> append(ch)
        }
    }
}