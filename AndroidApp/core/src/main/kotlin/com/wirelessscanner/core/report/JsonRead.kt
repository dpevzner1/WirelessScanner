package com.wirelessscanner.core.report

import java.lang.reflect.ParameterizedType
import java.lang.reflect.Type
import java.lang.reflect.WildcardType
import java.time.Duration
import java.time.Instant

/**
 * Reads what [Json] writes, back into the core data classes (used for on-device storage: configs, cycles, probes).
 * Constructor parameters are matched by name; missing values become null/defaults of the type.
 */
object JsonRead {
    fun parse(text: String): Any? = Parser(text).run { ws(); value() }

    @Suppress("UNCHECKED_CAST")
    fun <T> read(text: String, cls: Class<T>): T = convert(parse(text), cls) as T

    fun <T> readList(text: String, cls: Class<T>): List<T> = (parse(text) as List<*>).map { convert(it, cls) as T }

    fun convert(v: Any?, type: Type): Any? {
        if (v == null) return null
        val raw: Class<*> = when (type) {
            is Class<*> -> type
            is ParameterizedType -> type.rawType as Class<*>
            is WildcardType -> return convert(v, type.upperBounds[0])
            else -> error("Unsupported type $type")
        }
        return when {
            raw == String::class.java -> v as String
            raw == Int::class.javaPrimitiveType || raw == Int::class.javaObjectType -> (v as Number).toInt()
            raw == Long::class.javaPrimitiveType || raw == Long::class.javaObjectType -> (v as Number).toLong()
            raw == Double::class.javaPrimitiveType || raw == Double::class.javaObjectType -> when (v) { is Number -> v.toDouble(); is String -> v.toDouble(); else -> error("number") }
            raw == Float::class.javaPrimitiveType || raw == Float::class.javaObjectType -> (v as Number).toFloat()
            raw == Boolean::class.javaPrimitiveType || raw == Boolean::class.javaObjectType -> v as Boolean
            raw == Byte::class.javaPrimitiveType || raw == Byte::class.javaObjectType -> (v as Number).toByte()
            raw == Instant::class.java -> Instant.parse(v as String)
            raw == Duration::class.java -> timeSpan(v as String)
            raw.isEnum -> raw.enumConstants.first { (it as Enum<*>).name == v }
            List::class.java.isAssignableFrom(raw) || Collection::class.java.isAssignableFrom(raw) -> {
                val el = (type as? ParameterizedType)?.actualTypeArguments?.get(0) ?: Any::class.java
                (v as List<*>).map { convert(it, el) }
            }
            Map::class.java.isAssignableFrom(raw) -> {
                val args = (type as? ParameterizedType)?.actualTypeArguments
                val kt = args?.get(0) ?: String::class.java; val vt = args?.get(1) ?: Any::class.java
                val m = LinkedHashMap<Any?, Any?>()
                (v as Map<*, *>).forEach { (k, x) -> m[convert(k, kt)] = convert(x, vt) }
                if (MutableMap::class.java.isAssignableFrom(raw) || raw.name.contains("Mutable")) m else m
            }
            raw == Any::class.java -> v
            else -> obj(v as Map<*, *>, raw)
        }
    }

    private fun obj(m: Map<*, *>, cls: Class<*>): Any {
        val ctor = cls.declaredConstructors.filter { !it.isSynthetic }.maxBy { it.parameterCount }
        ctor.isAccessible = true
        val args = ctor.parameters.mapIndexed { i, p ->
            val gt = ctor.genericParameterTypes[i]
            // An explicit null stays null (nullable property); only an absent key falls back to a type default.
            if (m.containsKey(p.name)) convert(m[p.name], gt) ?: if (p.type.isPrimitive) defaultFor(p.type) else null else defaultFor(p.type)
        }
        return ctor.newInstance(*args.toTypedArray())
    }

    private fun defaultFor(t: Class<*>): Any? = when (t) {
        Int::class.javaPrimitiveType -> 0
        Long::class.javaPrimitiveType -> 0L
        Double::class.javaPrimitiveType -> 0.0
        Float::class.javaPrimitiveType -> 0f
        Boolean::class.javaPrimitiveType -> false
        Byte::class.javaPrimitiveType -> 0.toByte()
        String::class.java -> ""
        List::class.java -> emptyList<Any>()
        Map::class.java -> emptyMap<Any, Any>()
        else -> null
    }

    /** Parses .NET TimeSpan text: [-][d.]hh:mm:ss[.fffffff]. */
    fun timeSpan(s: String): Duration {
        var t = s.trim(); var neg = false
        if (t.startsWith("-")) { neg = true; t = t.substring(1) }
        var days = 0L
        val firstColon = t.indexOf(':')
        val dot = t.indexOf('.')
        if (dot in 0 until firstColon) { days = t.substring(0, dot).toLong(); t = t.substring(dot + 1) }
        val parts = t.split(':')
        val h = parts[0].toLong(); val m = parts[1].toLong()
        val secParts = parts[2].split('.')
        val sec = secParts[0].toLong()
        val nanos = if (secParts.size > 1) (secParts[1] + "000000000").substring(0, 9).toLong() else 0L
        val d = Duration.ofDays(days).plusHours(h).plusMinutes(m).plusSeconds(sec).plusNanos(nanos)
        return if (neg) d.negated() else d
    }

    private class Parser(val s: String) {
        var i = 0
        fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
        fun value(): Any? {
            ws()
            return when (val c = s[i]) {
                '{' -> { val m = LinkedHashMap<String, Any?>(); i++; ws(); if (s[i] == '}') { i++; m } else { while (true) { ws(); val k = str(); ws(); i++; m[k] = value(); ws(); if (s[i++] == '}') break }; m } }
                '[' -> { val l = ArrayList<Any?>(); i++; ws(); if (s[i] == ']') { i++; l } else { while (true) { l.add(value()); ws(); if (s[i++] == ']') break }; l } }
                '"' -> str()
                't' -> { i += 4; true }
                'f' -> { i += 5; false }
                'n' -> { i += 4; null }
                else -> if (c == '-' || c.isDigit()) num() else error("Unexpected '$c' at $i")
            }
        }
        fun str(): String {
            val sb = StringBuilder(); i++
            while (true) {
                when (val c = s[i++]) {
                    '"' -> return sb.toString()
                    '\\' -> when (val e = s[i++]) {
                        'n' -> sb.append('\n'); 'r' -> sb.append('\r'); 't' -> sb.append('\t'); 'b' -> sb.append('\b'); 'f' -> sb.append('\u000C')
                        'u' -> { sb.append(s.substring(i, i + 4).toInt(16).toChar()); i += 4 }
                        else -> sb.append(e)
                    }
                    else -> sb.append(c)
                }
            }
        }
        fun num(): Number {
            val st = i
            while (i < s.length && (s[i].isDigit() || s[i] in "+-.eE")) i++
            val t = s.substring(st, i)
            return if (t.any { it in ".eE" }) t.toDouble() else t.toLong()
        }
    }
}
