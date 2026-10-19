package com.wirelessscanner.core.report

import java.lang.reflect.Modifier
import java.math.BigDecimal
import java.time.Duration
import java.time.Instant
import java.time.format.DateTimeFormatter
import java.time.ZoneOffset

/**
 * Minimal JSON writer equivalent to System.Text.Json with the Windows export options (indented, camelCase, string enums,
 * named floating-point literals). Data classes are serialized by their primary-constructor order (core is compiled with
 * -java-parameters), so field names and order follow the record definitions shared with C#.
 * Mapping: Instant → ISO-8601 UTC "…Z"; Duration → .NET TimeSpan text ("hh:mm:ss[.fffffff]", "d.hh:mm:ss"); enums → name;
 * maps → objects (enum keys by name); null → null.
 */
object Json {
    /** Derived properties C# serializes (public get-only members) that Kotlin keeps outside the constructor. */
    private val extras: Map<Class<*>, (Any) -> List<Pair<String, Any?>>> = mapOf(
        com.wirelessscanner.core.domain.GeoTag::class.java to { g ->
            g as com.wirelessscanner.core.domain.GeoTag
            listOf("hasCoordinates" to g.hasCoordinates, "coordinatesText" to g.coordinatesText, "mapUrl" to g.mapUrl)
        },
        com.wirelessscanner.core.domain.SaturationTestConfig::class.java to { c ->
            c as com.wirelessscanner.core.domain.SaturationTestConfig
            listOf("freshnessLimit" to c.freshnessLimit)
        },
        com.wirelessscanner.core.domain.ResponderSettings::class.java to { r ->
            r as com.wirelessscanner.core.domain.ResponderSettings
            listOf("isConfigured" to r.isConfigured)
        },
    )

    /** Never written: secrets (the responder key) stay out of every export. */
    private val redacted: Set<Pair<Class<*>, String>> = setOf(com.wirelessscanner.core.domain.ResponderSettings::class.java to "key")

    fun write(value: Any?): String = StringBuilder().also { write(it, value, 0) }.toString()

    private fun indent(sb: StringBuilder, n: Int) { sb.append('\n'); repeat(n) { sb.append("  ") } }

    fun write(sb: StringBuilder, v: Any?, level: Int) {
        when (v) {
            null -> sb.append("null")
            is String -> str(sb, v)
            is Boolean -> sb.append(if (v) "true" else "false")
            is Int, is Long, is Short, is Byte -> sb.append(v.toString())
            is Double -> num(sb, v)
            is Float -> num(sb, v.toDouble())
            is Enum<*> -> str(sb, v.name)
            is Instant -> str(sb, isoUtc(v))
            is Duration -> str(sb, ReportText.timeSpan(v))
            is Map<*, *> -> obj(sb, v.entries.map { (k, x) -> (if (k is Enum<*>) k.name else k.toString()) to x }, level)
            is Iterable<*> -> arr(sb, v.toList(), level)
            is Array<*> -> arr(sb, v.toList(), level)
            is JsonObject -> obj(sb, v.fields, level)
            else -> obj(sb, properties(v), level)
        }
    }

    class JsonObject(val fields: List<Pair<String, Any?>>)
    fun obj(vararg f: Pair<String, Any?>) = JsonObject(f.toList())

    private fun obj(sb: StringBuilder, fields: List<Pair<String, Any?>>, level: Int) {
        if (fields.isEmpty()) { sb.append("{}"); return }
        sb.append('{')
        fields.forEachIndexed { i, (k, x) ->
            if (i > 0) sb.append(',')
            indent(sb, level + 1)
            str(sb, k); sb.append(": ")
            write(sb, x, level + 1)
        }
        indent(sb, level); sb.append('}')
    }

    private fun arr(sb: StringBuilder, items: List<Any?>, level: Int) {
        if (items.isEmpty()) { sb.append("[]"); return }
        sb.append('[')
        items.forEachIndexed { i, x -> if (i > 0) sb.append(','); indent(sb, level + 1); write(sb, x, level + 1) }
        indent(sb, level); sb.append(']')
    }

    private fun num(sb: StringBuilder, d: Double) {
        when {
            d.isNaN() -> str(sb, "NaN")
            d == Double.POSITIVE_INFINITY -> str(sb, "Infinity")
            d == Double.NEGATIVE_INFINITY -> str(sb, "-Infinity")
            d == Math.rint(d) && kotlin.math.abs(d) < 1e15 -> sb.append(d.toLong().toString())
            else -> sb.append(BigDecimal(d.toString()).stripTrailingZeros().toPlainString())
        }
    }

    private fun str(sb: StringBuilder, s: String) {
        sb.append('"')
        for (ch in s) when {
            ch == '"' -> sb.append("\\\"")
            ch == '\\' -> sb.append("\\\\")
            ch == '\n' -> sb.append("\\n")
            ch == '\r' -> sb.append("\\r")
            ch == '\t' -> sb.append("\\t")
            ch < ' ' || ch == '<' || ch == '>' || ch == '&' || ch == '\'' || ch == '+' || ch == '`' -> sb.append(String.format("\\u%04X", ch.code))
            ch.code > 126 -> sb.append(String.format("\\u%04X", ch.code))
            else -> sb.append(ch)
        }
        sb.append('"')
    }

    private val propertyCache = HashMap<Class<*>, List<Pair<String, java.lang.reflect.Method>>>()

    private fun properties(v: Any): List<Pair<String, Any?>> {
        val cls = v.javaClass
        val props = synchronized(propertyCache) {
            propertyCache.getOrPut(cls) {
                val ctor = cls.declaredConstructors.filter { !it.isSynthetic && Modifier.isPublic(it.modifiers) }.maxByOrNull { it.parameterCount }
                    ?: cls.declaredConstructors.maxBy { it.parameterCount }
                ctor.parameters.mapNotNull { p ->
                    val name = p.name
                    if (!p.isNamePresent || Pair<Class<*>, String>(cls, name) in redacted) return@mapNotNull null
                    val getter = listOf(name, "get" + name.replaceFirstChar { it.uppercase() })
                        .firstNotNullOfOrNull { n -> runCatching { cls.getMethod(n) }.getOrNull() } ?: return@mapNotNull null
                    name to getter
                }
            }
        }
        return props.map { (n, m) -> n to m.invoke(v) } + (extras[cls]?.invoke(v) ?: emptyList())
    }

    private val isoFormat = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSS'Z'").withZone(ZoneOffset.UTC)

    /** Like System.Text.Json for UTC DateTime: up to 7 fractional digits, trailing zeros trimmed. */
    fun isoUtc(t: Instant): String {
        val s = isoFormat.format(t)
        val dot = s.indexOf('.')
        val frac = s.substring(dot + 1, s.length - 1).trimEnd('0')
        return s.substring(0, dot) + (if (frac.isEmpty()) "" else ".$frac") + "Z"
    }
}
