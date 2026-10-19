package com.wirelessscanner.core.report

import com.wirelessscanner.core.domain.CaptureSession
import com.wirelessscanner.core.domain.CaptureSessionMode
import com.wirelessscanner.core.domain.TelemetrySample
import com.wirelessscanner.core.report.ReportText.csv
import com.wirelessscanner.core.runtime.StatisticsCalculator
import java.time.Instant
import java.util.Locale

data class ExportMetadata(val clientName: String = "", val surveyorName: String = "", val notes: String = "")

data class ExportFields(
    val time: Boolean = true, val ssid: Boolean = true, val bssid: Boolean = true, val channel: Boolean = true, val band: Boolean = true,
    val rssi: Boolean = true, val snr: Boolean = true, val quality: Boolean = true, val jitter: Boolean = true, val zone: Boolean = true,
    val includedBssids: Set<String>? = null,
)

/** Port of ExportService.cs (capture sessions): CSV, JSON and the "WIRELESS SITE SURVEY REPORT" PDF model. */
class SessionReports(private val ctx: ReportContext) {
    private fun filter(samples: List<TelemetrySample>, f: ExportFields) = f.includedBssids?.let { allowed -> samples.filter { it.bssid in allowed } } ?: samples

    private fun scopeAndNotes(s: CaptureSession): Pair<String, String> =
        if (s.scope.contains(" | Notes: ")) s.scope.split(" | Notes: ", limit = 2).let { it[0] to it[1] } else s.scope to ""

    private fun mode(s: CaptureSession) = if (s.mode == CaptureSessionMode.Stationary) "Stationary Capture" else "Roaming Survey"
    private fun utc(t: Instant, p: String) = ReportText.utc(t, p)

    fun csv(session: CaptureSession, all: List<TelemetrySample>, fields: ExportFields = ExportFields()): ExportFile {
        val samples = filter(all, fields)
        val (scope, notes) = scopeAndNotes(session)
        val sb = StringBuilder()
        fun line(s: String) { sb.append(s).append("\r\n") }
        line("# Session Name: ${csv(session.name)}")
        line("# Facility: ${csv(session.facilityName)}")
        line("# Scope: ${csv(scope)}")
        line("# Notes: ${csv(notes)}")
        line("# Capture Mode: ${mode(session)}")
        line("# Interface: ${csv(session.interfaceName)}")
        line("# Start Time (UTC): ${utc(session.startTime, "yyyy-MM-dd HH:mm:ss")} | local ${ReportText.localWithOffset(session.startTime, ctx.zone)}")
        line("# End Time (UTC): ${session.endTime?.let { utc(it, "yyyy-MM-dd HH:mm:ss") + " | local " + ReportText.localWithOffset(it, ctx.zone) } ?: "Active"}")
        line("# Generated: ${utc(ctx.now(), "yyyy-MM-dd HH:mm:ss")} UTC by WirelessScanner ${ctx.appVersion} (${ctx.platform})")
        line("# Timestamps are UTC. SNR is ESTIMATED (RSSI - assumed -95 dBm floor). RSSI_Variation_dB = |change from the previous sample| in dB.")
        line("#")
        val f = fields
        line(listOfNotNull(if (f.time) "Timestamp_UTC" else null, if (f.zone) "SurveyPoint" else null, if (f.bssid) "BSSID" else null, if (f.ssid) "SSID" else null,
            if (f.band) "Band" else null, if (f.channel) "Channel" else null, if (f.rssi) "RSSI" else null, if (f.snr) "SNR_Estimated_dB" else null,
            if (f.quality) "Quality" else null, if (f.jitter) "RSSI_Variation_dB" else null).joinToString(","))
        for (s in samples) line(listOfNotNull(if (f.time) utc(s.timestamp, "yyyy-MM-dd HH:mm:ss.SSS") else null, if (f.zone) csv(s.surveyPoint) else null,
            if (f.bssid) csv(s.bssid) else null, if (f.ssid) csv(s.ssid) else null, if (f.band) csv(s.band) else null, if (f.channel) s.channel.toString() else null,
            if (f.rssi) s.rssi.toString() else null, if (f.snr) s.snr.toString() else null, if (f.quality) s.quality.toString() else null,
            if (f.jitter) String.format(Locale.ROOT, "%.3f", s.jitter) else null).joinToString(","))
        return ExportFile("${safe(session.name)}.csv", "text/csv", ReportText.utf8Bom(sb.toString()))
    }

    fun json(session: CaptureSession, all: List<TelemetrySample>, fields: ExportFields = ExportFields()): ExportFile {
        val samples = filter(all, fields)
        val (scope, notes) = scopeAndNotes(session)
        val duration = if (samples.isEmpty()) 0.0 else ((session.endTime ?: samples.last().timestamp).toEpochMilli() - session.startTime.toEpochMilli()) / 1000.0
        val f = fields
        val rows = samples.map { s ->
            Json.JsonObject(listOfNotNull(if (f.time) "Timestamp" to s.timestamp else null, if (f.zone) "SurveyPoint" to s.surveyPoint else null,
                if (f.bssid) "BSSID" to s.bssid else null, if (f.ssid) "SSID" to s.ssid else null, if (f.band) "Band" to s.band else null,
                if (f.channel) "Channel" to s.channel else null, if (f.rssi) "RSSI" to s.rssi else null, if (f.snr) "SnrEstimatedDb" to s.snr else null,
                if (f.quality) "Quality" to s.quality else null, if (f.jitter) "RssiVariationDb" to s.jitter else null))
        }
        // ExportService.cs serializes this payload with default (PascalCase) naming.
        val payload = Json.obj(
            "Session" to Json.obj("SessionId" to session.sessionId, "Name" to session.name, "Scope" to scope, "Notes" to notes, "FacilityName" to session.facilityName,
                "InterfaceName" to session.interfaceName, "StartTime" to session.startTime, "EndTime" to session.endTime, "Mode" to mode(session), "Status" to session.status.name),
            "Samples" to rows,
            "Summary" to Json.obj("DurationSeconds" to duration, "TotalSamples" to samples.size, "UniqueAPs" to samples.map { it.bssid }.distinct().size,
                "UniqueSSIDs" to samples.map { it.ssid }.distinct().size, "AvgRSSI" to (if (samples.isEmpty()) 0.0 else samples.map { it.rssi }.average()),
                "WeakestRSSI" to (samples.minOfOrNull { it.rssi } ?: 0), "StrongestRSSI" to (samples.maxOfOrNull { it.rssi } ?: 0)),
        )
        return ExportFile("${safe(session.name)}.json", "application/json", Json.write(payload).toByteArray(Charsets.UTF_8))
    }

    fun document(session: CaptureSession, all: List<TelemetrySample>, meta: ExportMetadata, fields: ExportFields = ExportFields()): ReportDocument {
        val samples = filter(all, fields)
        val (scope, notes) = scopeAndNotes(session)
        val b = ArrayList<Block>()
        b.add(Block.KeyValues(listOfNotNull("Client name" to meta.clientName, "Surveyor name" to meta.surveyorName, "Notes (export)" to meta.notes,
            "Facility name" to session.facilityName, "Scope" to scope, "Capture mode" to mode(session),
            if (notes.isNotBlank()) "Session notes" to notes else null,
            "Time" to "${ReportText.local(session.startTime, ctx.zone, "yyyy-MM-dd HH:mm:ss")} to ${session.endTime?.let { ReportText.local(it, ctx.zone, "yyyy-MM-dd HH:mm:ss") } ?: "Active"}")))
        b.add(Block.Heading("Executive Summary"))
        val minutes = ((session.endTime ?: ctx.now()).toEpochMilli() - session.startTime.toEpochMilli()) / 60000.0
        b.add(Block.Grid(listOf("Total Samples", "Unique SSIDs", "Unique APs", "Duration"),
            listOf(listOf(samples.size.toString(), samples.map { it.ssid }.distinct().size.toString(), samples.map { it.bssid }.distinct().size.toString(), ReportText.fmt(minutes, "0.0") + " min"))))
        b.add(Block.Heading("Access Point Telemetry Analysis"))
        val grouped = LinkedHashMap<String, MutableList<TelemetrySample>>()
        samples.forEach { grouped.getOrPut(it.bssid) { ArrayList() }.add(it) }
        b.add(Block.Grid(listOf("SSID", "BSSID", "Band", "Ch", "Min", "Max", "Mean", "StdDev", "Var dB"),
            grouped.values.map { g -> val st = StatisticsCalculator.calculate(g.map { it.rssi }, g.map { it.jitter }); val first = g.first()
                listOf(first.ssid, first.bssid, first.band, first.channel.toString(), st.minRssi.toString(), st.maxRssi.toString(),
                    ReportText.fmt(st.meanRssi, "0.0"), ReportText.fmt(st.stdDevRssi, "0.0"), ReportText.fmt(st.avgJitter, "0.0")) },
            listOf(3f, 2.5f, 1.2f, 1f, 1f, 1f, 1f, 1.2f, 1f)))
        if (samples.isNotEmpty()) {
            val top = grouped.values.sortedByDescending { it.size }.take(5)
            b.add(Block.Heading("Signal Strength Timeline (Top 5 APs)"))
            b.add(Block.Chart("", 250f, ChartSpec.SessionTimeline(session.startTime.toEpochMilli(), (session.endTime ?: samples.maxOf { it.timestamp }).toEpochMilli(),
                top.map { g -> val avg = g.map { it.rssi }.average(); Triple("${g.first().ssid.ifEmpty { "<Hidden SSID>" }} (${g.first().bssid})",
                    if (avg >= -55) 0xFF00FF66.toInt() else if (avg >= -70) 0xFFFFBB33.toInt() else 0xFFFF3300.toInt(),
                    g.sortedBy { it.timestamp }.map { it.timestamp.toEpochMilli() to it.rssi }) })))
            b.add(Block.PageBreak)
            b.add(Block.Heading("Detailed Telemetry Log"))
            val f = fields
            val heads = listOfNotNull(if (f.time) "Timestamp" else null, if (f.ssid) "SSID" else null, if (f.bssid) "BSSID" else null, if (f.band) "Band" else null,
                if (f.channel) "Ch" else null, if (f.rssi) "RSSI" else null, if (f.snr) "SNR est." else null, if (f.quality) "Qual" else null,
                if (f.jitter) "Var dB" else null, if (f.zone) "Zone" else null)
            val widths = listOfNotNull(if (f.time) 2.2f else null, if (f.ssid) 2.8f else null, if (f.bssid) 2.2f else null, if (f.band) 1f else null,
                if (f.channel) 0.8f else null, if (f.rssi) 1f else null, if (f.snr) 0.8f else null, if (f.quality) 0.8f else null, if (f.jitter) 1f else null, if (f.zone) 1.8f else null)
            b.add(Block.Grid(heads, samples.sortedBy { it.timestamp }.map { s -> listOfNotNull(
                if (f.time) ReportText.local(s.timestamp, ctx.zone, "yyyy-MM-dd HH:mm:ss") else null, if (f.ssid) s.ssid.ifEmpty { "<Hidden SSID>" } else null,
                if (f.bssid) s.bssid else null, if (f.band) s.band else null, if (f.channel) s.channel.toString() else null, if (f.rssi) "${s.rssi} dBm" else null,
                if (f.snr) "~${s.snr} dB" else null, if (f.quality) "${s.quality}%" else null, if (f.jitter) "${ReportText.fmt(s.jitter, "0.0")} dB" else null,
                if (f.zone) s.surveyPoint else null) }, widths))
        }
        return ReportDocument("WIRELESS SITE SURVEY REPORT", "Session: ${session.name}", "Wireless site survey - ${session.name}",
            meta.surveyorName.ifBlank { "WirelessScanner" }, "Wi-Fi capture session report",
            "WirelessScanner ${ctx.appVersion} (${ctx.platform}) | Session ${session.sessionId}", 0xFF0891B2.toInt(), b)
    }

    companion object {
        fun safe(name: String) = name.replace(Regex("[^A-Za-z0-9._-]+"), "_").take(80).ifEmpty { "export" }
    }
}
