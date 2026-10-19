package com.wirelessscanner.core.parity

import com.wirelessscanner.core.domain.AccessPoint
import com.wirelessscanner.core.report.Json
import org.junit.Assert.fail
import java.io.File
import java.time.Instant
import kotlin.math.abs
import kotlin.math.max

/** Reads the parity fixtures written by WirelessScanner.Tests/AndroidParityFixtures.cs and compares JSON trees. */
object Fixture {
    private val dir = File(System.getProperty("parity.dir") ?: "src/test/resources/parity")

    fun load(name: String): Any? = MiniJson.parse(File(dir, name).readText(Charsets.UTF_8))

    @Suppress("UNCHECKED_CAST")
    fun Any?.obj(): Map<String, Any?> = this as Map<String, Any?>
    @Suppress("UNCHECKED_CAST")
    fun Any?.arr(): List<Any?> = this as List<Any?>
    fun Any?.num(): Double = (this as Number).toDouble()
    fun Any?.int(): Int = (this as Number).toInt()
    fun Any?.long(): Long = (this as Number).toLong()
    fun Any?.str(): String = this as String
    fun Any?.optInt(): Int? = (this as Number?)?.toInt()
    fun Any?.optNum(): Double? = (this as Number?)?.toDouble()
    fun Any?.optStr(): String? = this as String?
    fun Any?.instant(): Instant = Instant.parse(this as String)

    fun ap(m: Map<String, Any?>, t0: Instant): AccessPoint = AccessPoint(
        bssid = m["bssid"].str(), ssid = m["ssid"].str(), band = m["band"].str(), channel = m["channel"].int(), channelWidth = m["channelWidth"].optInt(),
        rssi = m["rssi"].int(), snr = m["snr"].int(), quality = m["quality"].int(), securityType = m["securityType"].str(), vendorOui = m["vendorOui"].optStr(),
        lastSeen = t0, beaconAgeMs = m["beaconAgeMs"].optNum(), phyType = m["phyType"].optStr(), beaconIntervalTu = m["beaconIntervalTu"].optInt(),
        centerFrequencyMhz = m["centerFrequencyMhz"].optNum(), stationCount = m["stationCount"].optInt(), channelUtilizationPct = m["channelUtilizationPct"].optNum(),
        tsfOffsetMs = m["tsfOffsetMs"].optNum(), wpsManufacturer = m["wpsManufacturer"].optStr(), wpsModel = m["wpsModel"].optStr(),
        wpsModelNumber = m["wpsModelNumber"].optStr(), wpsDeviceName = m["wpsDeviceName"].optStr(), wpsUuid = m["wpsUuid"].optStr(),
    )

    /** Serializes [actual] with the Android JSON writer and requires the same tree as the C# output (numbers within 1e-9 relative). */
    fun assertSameJson(what: String, expected: Any?, actual: Any?) {
        val a = MiniJson.parse(Json.write(actual))
        val diffs = ArrayList<String>()
        compare(what, expected, a, diffs)
        if (diffs.isNotEmpty()) fail("$what: ${diffs.size} difference(s)\n" + diffs.take(25).joinToString("\n"))
    }

    fun compare(path: String, e: Any?, a: Any?, out: MutableList<String>) {
        when {
            e is Number && a is Number -> {
                val x = e.toDouble(); val y = a.toDouble()
                if (abs(x - y) > 1e-9 * max(1.0, max(abs(x), abs(y)))) out.add("$path: expected $x, actual $y")
            }
            e is Map<*, *> && a is Map<*, *> -> {
                for (k in e.keys + a.keys) {
                    if (k !in a) { out.add("$path.$k: missing in Android output"); continue }
                    if (k !in e) { out.add("$path.$k: extra in Android output"); continue }
                    compare("$path.$k", e[k], a[k], out)
                }
            }
            e is List<*> && a is List<*> -> {
                if (e.size != a.size) out.add("$path: expected ${e.size} items, actual ${a.size}")
                for (i in 0 until minOf(e.size, a.size)) compare("$path[$i]", e[i], a[i], out)
            }
            e != a -> out.add("$path: expected <$e>, actual <$a>")
        }
    }
}

/** Small JSON parser for the tests (objects keep key order). */
object MiniJson {
    fun parse(s: String): Any? = P(s).run { ws(); val v = value(); ws(); v }

    private class P(val s: String) {
        var i = 0
        fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
        fun value(): Any? {
            ws()
            return when (val c = s[i]) {
                '{' -> obj()
                '[' -> arr()
                '"' -> str()
                't' -> { i += 4; true }
                'f' -> { i += 5; false }
                'n' -> { i += 4; null }
                else -> if (c == '-' || c.isDigit()) num() else error("Unexpected '$c' at $i")
            }
        }
        fun obj(): Map<String, Any?> {
            val m = LinkedHashMap<String, Any?>(); i++; ws()
            if (s[i] == '}') { i++; return m }
            while (true) {
                ws(); val k = str(); ws(); i++ /* : */
                m[k] = value(); ws()
                if (s[i] == ',') { i++; continue }
                i++; return m
            }
        }
        fun arr(): List<Any?> {
            val l = ArrayList<Any?>(); i++; ws()
            if (s[i] == ']') { i++; return l }
            while (true) {
                l.add(value()); ws()
                if (s[i] == ',') { i++; continue }
                i++; return l
            }
        }
        fun str(): String {
            val sb = StringBuilder(); i++
            while (true) {
                val c = s[i++]
                when (c) {
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
