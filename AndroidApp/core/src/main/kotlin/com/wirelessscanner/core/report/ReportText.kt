package com.wirelessscanner.core.report

import com.wirelessscanner.core.domain.ClientConnectionInfo
import java.math.RoundingMode
import java.text.DecimalFormat
import java.text.DecimalFormatSymbols
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Who generated a report: app version and platform, plus the time zone used for local times. */
data class ReportContext(
    val appVersion: String,
    val platform: String = "Android",
    val zone: ZoneId = ZoneId.systemDefault(),
    val now: () -> Instant = { Instant.now() },
)

/** Formatting shared by every export: invariant culture, .NET custom-format semantics, UTC/local timestamps, CSV cells. */
object ReportText {
    private val symbols = DecimalFormatSymbols(Locale.ROOT)
    private val formats = HashMap<String, DecimalFormat>()

    /** .NET custom numeric format ("0.##", "0.000", "0", "0.000000" …), half away from zero. */
    fun fmt(v: Double?, pattern: String): String {
        if (v == null) return ""
        val df = synchronized(formats) { formats.getOrPut(pattern) { DecimalFormat(pattern, symbols).also { it.roundingMode = RoundingMode.HALF_UP; it.isGroupingUsed = false } } }
        val s = synchronized(df) { df.format(v) }
        return if (s == "-0") "0" else s
    }
    fun fmt(v: Int?): String = v?.toString() ?: ""
    fun fmt(v: Long?): String = v?.toString() ?: ""

    /** .NET "N0" (thousands separators). */
    fun n0(v: Int): String = String.format(Locale.ROOT, "%,d", v)

    /** "+0.##;-0.##;0" for UTC offsets in hours. */
    fun signedHours(minutes: Int): String {
        val h = minutes / 60.0
        return when { h > 0 -> "+" + fmt(h, "0.##"); h < 0 -> "-" + fmt(-h, "0.##"); else -> "0" }
    }

    private val isoMs = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(java.time.ZoneOffset.UTC)
    fun iso(t: Instant?): String = t?.let { isoMs.format(it) } ?: ""

    fun localIso(t: Instant?, zone: ZoneId): String =
        t?.let { DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSXXX").format(it.atZone(zone)) } ?: ""

    fun local(t: Instant, zone: ZoneId, pattern: String): String = DateTimeFormatter.ofPattern(pattern, Locale.ROOT).format(t.atZone(zone))
    fun utc(t: Instant, pattern: String): String = DateTimeFormatter.ofPattern(pattern, Locale.ROOT).format(t.atZone(java.time.ZoneOffset.UTC))

    /** "2026-10-16 11:31:26 (UTC-05:00)". */
    fun localWithOffset(t: Instant, zone: ZoneId, pattern: String = "yyyy-MM-dd HH:mm:ss"): String {
        val z = t.atZone(zone)
        val off = z.offset.totalSeconds
        val a = kotlin.math.abs(off)
        return "${local(t, zone, pattern)} (UTC${if (off < 0) "-" else "+"}${String.format(Locale.ROOT, "%02d:%02d", a / 3600, (a % 3600) / 60)})"
    }

    /** .NET TimeSpan default text: [-][d.]hh:mm:ss[.fffffff]. */
    fun timeSpan(d: Duration): String {
        val neg = d.isNegative
        val a = d.abs()
        val days = a.toDays()
        val h = a.toHours() % 24; val m = a.toMinutes() % 60; val s = a.seconds % 60
        val ticks = a.nano / 100
        return (if (neg) "-" else "") + (if (days > 0) "$days." else "") + String.format(Locale.ROOT, "%02d:%02d:%02d", h, m, s) +
            (if (ticks > 0) String.format(Locale.ROOT, ".%07d", ticks) else "")
    }

    /** hh:mm:ss, or d.hh:mm:ss from one day (the PDF duration format). */
    fun dur(seconds: Double): String {
        val total = seconds.toLong()
        val d = total / 86400; val h = (total % 86400) / 3600; val m = (total % 3600) / 60; val s = total % 60
        return if (seconds >= 86400) String.format(Locale.ROOT, "%d.%02d:%02d:%02d", d, h, m, s) else String.format(Locale.ROOT, "%02d:%02d:%02d", d * 24 + h, m, s)
    }

    /**
     * CSV cell, RFC 4180 quoting as on Windows. Android also neutralises spreadsheet formulas (A-LRN-034): a text cell that starts
     * with = + - @ or a control character and is not a number gets a leading apostrophe, because SSIDs are chosen by strangers.
     */
    fun csv(field: String?): String {
        var f = field ?: ""
        if (f.isNotEmpty() && f[0] in "=+-@\t\r" && f.toDoubleOrNull() == null) f = "'$f"
        return if (f.any { it == ',' || it == '"' || it == '\n' || it == '\r' }) "\"" + f.replace("\"", "\"\"") + "\"" else f
    }

    fun row(vararg cells: String?): String = cells.joinToString(",") { csv(it) }

    fun connectionText(c: ClientConnectionInfo?): String = when {
        c == null -> "not recorded"
        !c.connected -> "not connected (${c.state})"
        else -> "connected to ${c.ssid ?: "<hidden>"} (${c.bssid ?: ""}), ch ${c.channel?.toString() ?: "?"} ${c.band ?: ""}, ${c.phyType ?: "PHY ?"}, " +
            "rx ${c.rxRateMbps?.let { fmt(it, "0.#") } ?: "?"} / tx ${c.txRateMbps?.let { fmt(it, "0.#") } ?: "?"} Mbps, signal ${c.signalQualityPct?.toString() ?: "?"} %"
    }

    /** UTF-8 with BOM, as the Windows CSV files (Excel opens them with the right encoding). */
    fun utf8Bom(s: String): ByteArray = byteArrayOf(0xEF.toByte(), 0xBB.toByte(), 0xBF.toByte()) + s.toByteArray(Charsets.UTF_8)
}

/** One output file of an export: name relative to the export folder and its bytes. */
data class ExportFile(val name: String, val mimeType: String, val bytes: ByteArray)
