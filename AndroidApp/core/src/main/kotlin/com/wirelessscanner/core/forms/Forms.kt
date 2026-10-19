package com.wirelessscanner.core.forms

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.report.Json
import com.wirelessscanner.core.report.ReportText
import com.wirelessscanner.core.selection.NetworkSelection
import java.time.Duration
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/** Duration presets (same list as Windows). duration = null and custom = false means "until stopped". */
data class DurationOption(val label: String, val duration: Duration?, val custom: Boolean = false)

val DURATION_OPTIONS = listOf(
    DurationOption("5 minutes", Duration.ofMinutes(5)), DurationOption("15 minutes", Duration.ofMinutes(15)), DurationOption("30 minutes", Duration.ofMinutes(30)),
    DurationOption("1 hour", Duration.ofHours(1)), DurationOption("4 hours", Duration.ofHours(4)), DurationOption("8 hours", Duration.ofHours(8)),
    DurationOption("24 hours", Duration.ofHours(24)), DurationOption("Custom (minutes)", null, custom = true), DurationOption("Until stopped", null),
)
val INTERVAL_OPTIONS = listOf(5, 10, 15, 30, 60)
val SCHEDULE_OPTIONS = listOf(0, 15, 30, 60)

/** "hh:mm:ss", or "Nd hh:mm:ss" from 24 h (SaturationTestViewModel.FormatSpan). */
fun formatSpan(seconds: Double): String {
    val total = seconds.toLong()
    val hms = String.format(java.util.Locale.ROOT, "%02d:%02d:%02d", (total % 86400) / 3600, (total % 3600) / 60, total % 60)
    return if (seconds >= 86400) "${total / 86400}d $hms" else String.format(java.util.Locale.ROOT, "%02d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
}

private fun parseDouble(s: String) = s.trim().replace(',', '.').toDoubleOrNull()
private fun tryInt(s: String, min: Int, max: Int) = s.trim().toIntOrNull()?.takeIf { it in min..max }

/** Where the test was performed (shared by both test forms). */
data class PlaceFields(
    val location: String = "", val facility: String = "", val operator: String = "", val client: String = "", val notes: String = "",
    val latitude: String = "", val longitude: String = "", val accuracy: String = "", val building: String = "", val floor: String = "", val zone: String = "",
    val geoSource: String = "manual", val geoCapturedUtc: java.time.Instant? = null,
) {
    fun problems(decimalHint: Boolean): List<String> {
        val p = ArrayList<String>()
        if (location.isBlank()) p.add("Enter the test area / location (it is printed on the report).")
        val latText = latitude.isNotBlank(); val lonText = longitude.isNotBlank()
        if ((latText && parseDouble(latitude) == null) || (lonText && parseDouble(longitude) == null))
            p.add(if (decimalHint) "Latitude/longitude must be decimal degrees, e.g. 41.878113 and -87.629799." else "Latitude/longitude must be decimal degrees.")
        else GeoTag.validate(if (latText) parseDouble(latitude) else null, if (lonText) parseDouble(longitude) else null)?.let { p.add(it) }
        return p
    }

    fun geo() = GeoTag(parseDouble(latitude), parseDouble(longitude), parseDouble(accuracy), geoSource, geoCapturedUtc, building.trim(), floor.trim(), zone.trim())
}

/** Port of SaturationTestViewModel setup (validation texts, summary line, BuildConfig with units resolved at start). */
data class SaturationForm(
    val name: String = "",
    val durationIndex: Int = 1,
    val customMinutes: String = "60",
    val intervalSeconds: Int = 5,
    val place: PlaceFields = PlaceFields(),
    val keepAwake: Boolean = true,
    val dropDb: String = "8", val peakDb: String = "8", val dropoutCycles: String = "2", val baselineWindow: String = "12",
) {
    data class Validation(val message: String, val canStart: Boolean, val summary: String)

    val durationOption get() = DURATION_OPTIONS[durationIndex]

    fun duration(): Duration? = if (durationOption.custom) tryInt(customMinutes, 1, 1440)?.let { Duration.ofMinutes(it.toLong()) } else durationOption.duration

    fun validate(adapterReady: Boolean, selection: NetworkSelection, running: Boolean): Validation {
        val problems = ArrayList<String>()
        if (!adapterReady) problems.add("Turn on Wi-Fi and allow location access so the tablet can scan.")
        val scope = selection.deriveScope()
        val n = selection.bssids.size; val ssids = selection.ssids.size
        val units = selection.units.size; val unitBssids = if (units == 0) 0 else selection.unitBssids().size
        if (units > 0 && unitBssids == 0) problems.add("The selected access point has no BSSIDs in range right now; wait for a scan or select it again.")
        if (durationOption.custom && tryInt(customMinutes, 1, 1440) == null) problems.add("Custom duration must be 1–1440 minutes.")
        problems.addAll(place.problems(true))
        if (tryInt(dropDb, 3, 30) == null || tryInt(peakDb, 3, 30) == null) problems.add("Drop/peak thresholds must be 3–30 dB.")
        if (tryInt(dropoutCycles, 1, 20) == null) problems.add("Dropout cycles must be 1–20.")
        if (tryInt(baselineWindow, 3, 120) == null) problems.add("Baseline window must be 3–120 samples.")
        fun s(k: Int) = if (k == 1) "" else "s"
        val what = when (scope) {
            SaturationScope.SingleAp, SaturationScope.MultipleAps -> "$n BSSID${s(n)}"
            SaturationScope.SingleSsid, SaturationScope.MultipleSsids -> "$ssids SSID${s(ssids)} (all BSSIDs)"
            SaturationScope.SingleUnit, SaturationScope.MultipleUnits -> "$units access point${s(units)} ($unitBssids BSSID${s(unitBssids)}, every SSID and radio)"
            SaturationScope.Mixed -> if (units > 0) "$units access point${s(units)} ($unitBssids BSSIDs)" + (if (ssids > 0) " + $ssids SSID${s(ssids)}" else "") + (if (n > 0) " + $n fixed BSSID${s(n)}" else "")
                else "$ssids SSID${s(ssids)} (all BSSIDs) + $n fixed BSSID${s(n)}"
            else -> "all visible networks"
        }
        val d = duration()
        val howLong = if (d != null) "for ${formatSpan(d.seconds.toDouble())} (≈${ReportText.n0(maxOf(1, (d.seconds / intervalSeconds).toInt()))} cycles)" else "until you stop it"
        return Validation(problems.firstOrNull() ?: "", problems.isEmpty() && !running, "Test $what every $intervalSeconds s $howLong.")
    }

    fun buildConfig(selection: NetworkSelection, interfaceId: String, interfaceName: String, now: LocalDateTime = LocalDateTime.now()): SaturationTestConfig {
        val units = resolveUnits(selection)
        val bssids = (selection.bssids + units.flatMap { u -> u.members.map { it.bssid } }).map { it.uppercase() }.distinct().sorted()
        return SaturationTestConfig(
            name = name.ifBlank { "Saturation " + now.format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")) }.trim(),
            scope = selection.deriveScope(), targetBssids = bssids, targetSsids = selection.ssids.sorted(), targetUnits = units,
            duration = duration(), interval = Duration.ofSeconds(intervalSeconds.toLong()),
            location = place.location.trim(), facility = place.facility.trim(), operator = place.operator.trim(), client = place.client.trim(), notes = place.notes.trim(),
            geo = place.geo(), interfaceId = interfaceId, interfaceName = interfaceName, keepAwake = keepAwake,
            thresholds = SaturationThresholds(dropDb = dropDb.trim().toDouble(), peakDb = peakDb.trim().toDouble(), dropoutMissedCycles = dropoutCycles.trim().toInt(), baselineWindow = baselineWindow.trim().toInt()),
        )
    }

    companion object {
        fun resolveUnits(selection: NetworkSelection): List<SaturationUnitTarget> {
            val resolver = selection.unitResolver ?: return emptyList()
            return selection.units.sorted().mapNotNull { id ->
                val unit = resolver.get(id) ?: return@mapNotNull null
                SaturationUnitTarget(unit.id, unit.name, unit.confidence, unit.evidence,
                    unit.radios.flatMap { r -> r.members.map { m -> SaturationUnitMember(m.bssid.uppercase(), m.ssid, m.band, r.key) } })
            }
        }
    }
}

/** Live per-target tile (port of SaturationTile). The status always has a word as well as a colour. */
data class SaturationTile(val bssid: String, val title: String, val current: String, val label: String, val color: Long, val stats: String, val events: String)

fun saturationTiles(report: SaturationReport, last: SaturationCycle?): List<SaturationTile> = report.targets.map { t ->
    val o = last?.observations?.firstOrNull { it.bssid.equals(t.bssid, true) }
    val (cur, label, color) = if (o?.status == ObservationStatus.Observed && o.rssi != null) {
        val r = o.rssi
        Triple("$r dBm", SaturationAnalyzer.tier(r.toDouble()).uppercase(), if (r >= -67) 0xFF00FF66 else if (r >= -70) 0xFFFFBB33 else if (r >= -80) 0xFFFF7700 else 0xFFFF3300)
    } else Triple("—", when (o?.status) { ObservationStatus.Missing -> "NOT SEEN"; ObservationStatus.Stale -> "STALE"; ObservationStatus.Unknown -> "SCAN FAILED"; else -> "WAITING" },
        if (o?.status == ObservationStatus.Missing) 0xFFFF3300 else 0xFFAA4400)
    val f1 = { v: Double? -> v?.let { ReportText.fmt(it, "0.0") } ?: "—" }
    SaturationTile(t.bssid, "${t.ssid.ifEmpty { "<hidden>" }}  ·  ${t.bssid}  ·  ch ${t.primaryChannel?.toString() ?: "?"}", cur, label, color,
        "min ${t.min?.toString() ?: "—"} / avg ${f1(t.mean)} / max ${t.max?.toString() ?: "—"} dBm  ·  σ ${f1(t.stdDev)} dB  ·  avail ${t.availabilityPct?.let { ReportText.fmt(it, "0.0") + "%" } ?: "—"}",
        "drops ${t.dropCount} · peaks ${t.peakCount} · dropouts ${t.dropoutCount} · ch changes ${t.channelChangeCount} · grade ${t.grade}")
}

fun saturationWarnings(report: SaturationReport, last: SaturationCycle, onBattery: Boolean?, throttled: Boolean): String {
    val w = ArrayList<String>()
    val h = report.health
    if (last.status == ScanCycleStatus.Failed) w.add("Adapter problem — retrying: ${last.errorMessage}")
    if (h.total >= 5 && h.stale > h.total / 3) w.add(if (throttled) "Many stale cycles: Android is throttling Wi-Fi scans (4 per 2 minutes). Turn off Wi-Fi scan throttling in Developer options for one scan per interval. Stale data is excluded."
        else "Many stale cycles: the Wi-Fi scan did not complete in time. Results remain valid; stale data is excluded.")
    if (onBattery == true) w.add("On battery power.")
    return w.joinToString("  ")
}

/** Port of ConnectionTestViewModel setup. */
data class ConnectionForm(
    val name: String = "",
    val durationIndex: Int = 1,
    val customMinutes: String = "60",
    val intensity: ConnectionIntensity = ConnectionIntensity.Light,
    val probeWifiLink: Boolean = true, val probeGateway: Boolean = true, val probeIsp: Boolean = true,
    val probeInternet: Boolean = true, val probeDns: Boolean = true, val probeWeb: Boolean = true,
    val internetTargets: String = ConnectionTestConfig.DEFAULT_INTERNET_TARGETS.joinToString(", "),
    val place: PlaceFields = PlaceFields(),
    val alsoSaturation: Boolean = false, val stopOnNetworkChange: Boolean = false, val keepAwake: Boolean = true,
    val speedAtStart: Boolean = false, val speedEveryMinutes: Int = 0, val twoWayAtStart: Boolean = false, val twoWayEveryMinutes: Int = 0,
) {
    data class Validation(val message: String, val canStart: Boolean, val summary: String)

    val durationOption get() = DURATION_OPTIONS[durationIndex]
    fun duration(): Duration? = if (durationOption.custom) tryInt(customMinutes, 1, 1440)?.let { Duration.ofMinutes(it.toLong()) } else durationOption.duration
    fun targets() = internetTargets.split(',', ';', ' ', '\n', '\r', '\t').map { it.trim() }.filter { it.isNotEmpty() }.distinct()

    fun validate(context: ConnectionContext?, reading: Boolean, hasSpeedServer: Boolean, hasResponder: Boolean, running: Boolean): Validation {
        val p = ArrayList<String>()
        if (context == null) p.add(if (reading) "Reading your connection…" else "Turn on Wi-Fi and connect to a network.")
        else if (!context.wifiConnected) p.add((context.problem ?: "Not connected over Wi-Fi.") + " Connect to a network first; this test never connects for you.")
        if (!(probeWifiLink || probeGateway || probeIsp || probeInternet || probeDns || probeWeb)) p.add("Choose at least one part of the path to test.")
        val t = targets()
        if (probeInternet && t.isEmpty()) p.add("Enter at least one internet target (e.g. 1.1.1.1).")
        if (probeInternet && t.any { !ConnectionTestConfig.isIPv4(it) }) p.add("Internet targets must be IPv4 addresses (e.g. 1.1.1.1, 8.8.8.8, 9.9.9.9).")
        if (durationOption.custom && tryInt(customMinutes, 1, 1440) == null) p.add("Custom duration must be 1–1440 minutes.")
        p.addAll(place.problems(false))
        if ((speedAtStart || speedEveryMinutes > 0) && !hasSpeedServer) p.add("Speed tests need a test server: SETTINGS → Test servers.")
        if ((twoWayAtStart || twoWayEveryMinutes > 0) && !hasResponder) p.add("Two-way tests need a paired LAN responder: SETTINGS → Test servers.")
        val d = duration()
        val probe = buildConfig(context, "", "").copy(duration = d ?: Duration.ofMinutes(15))
        val howLong = if (d != null) "for ${formatSpan(d.seconds.toDouble())}" else "until you stop it"
        return Validation(p.firstOrNull() ?: "", p.isEmpty() && !running,
            "Test your connection $howLong: about ${ReportText.fmt(probe.estimatedKbps(), "0.#")} kbit/s of probes" +
                (if (speedAtStart || speedEveryMinutes > 0) ", plus speed tests" else "") + (if (twoWayAtStart || twoWayEveryMinutes > 0) ", plus two-way tests" else "") + ".")
    }

    fun buildConfig(context: ConnectionContext?, interfaceId: String, interfaceName: String, now: LocalDateTime = LocalDateTime.now()) = ConnectionTestConfig(
        name = name.ifBlank { "Connection " + now.format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")) }.trim(),
        duration = duration(), intensity = intensity, tickMilliseconds = if (intensity == ConnectionIntensity.Detailed) 500 else 1000,
        interfaceId = interfaceId, interfaceName = interfaceName,
        probeWifiLink = probeWifiLink, probeGateway = probeGateway, probeIsp = probeIsp, probeInternet = probeInternet, probeDns = probeDns, probeWeb = probeWeb,
        internetTargets = targets(), location = place.location.trim(), facility = place.facility.trim(), operator = place.operator.trim(), client = place.client.trim(),
        notes = place.notes.trim(), geo = place.geo(), keepAwake = keepAwake, stopOnNetworkChange = stopOnNetworkChange,
        speedTestAtStart = speedAtStart, speedTestEveryMinutes = speedEveryMinutes, twoWayAtStart = twoWayAtStart, twoWayEveryMinutes = twoWayEveryMinutes,
        contextAtStart = context,
    )

    companion object {
        fun contextText(c: ConnectionContext?, reading: Boolean, unitName: String?): String = when {
            c == null -> if (reading) "Reading your connection…" else "Turn on Wi-Fi and connect to a network."
            !c.wifiConnected -> c.problem ?: "Not connected over Wi-Fi."
            else -> {
                val radio = ArrayList<String>()
                if (c.band != null || c.channel != null) radio.add("${c.band ?: ""} ch ${c.channel ?: ""}".trim())
                c.phyType?.let { radio.add(it) }
                c.rxRateMbps?.let { radio.add("${ReportText.fmt(it, "0")} Mbps") }
                radio.add(c.rssi?.let { "$it dBm" } ?: "signal n/a")
                "${c.ssid} · access point ${unitName ?: c.bssid}\n${radio.joinToString(" · ")}\n" +
                    "This device ${c.localIPv4} · router ${if (c.gateways.isEmpty()) "none" else c.gateways.joinToString(", ")} · DNS ${if (c.dnsServers.isEmpty()) "none" else c.dnsServers.joinToString(", ")}"
            }
        }

        fun contextWarning(c: ConnectionContext?) = if ((c?.otherActiveInterfaces?.size ?: 0) > 0) "Also connected: ${c!!.otherActiveInterfaces.joinToString(", ")}. Probes are forced through Wi-Fi." else ""
    }
}

/** One part of the path on the live strip (port of ConnectionSegmentTile). */
data class SegmentTile(val segment: ConnectionSegment, val title: String, val status: String, val color: Long, val line1: String, val line2: String, val details: String)

fun linkTile(l: ConnectionLinkSample?, enabled: Boolean): SegmentTile {
    val t = ConnectionAnalyzer.name(ConnectionSegment.WifiLink)
    if (!enabled) return SegmentTile(ConnectionSegment.WifiLink, t, "NOT TESTED", 0xFF7A6A55, "off", "", "Wi-Fi link sampling switched off.")
    if (l == null) return SegmentTile(ConnectionSegment.WifiLink, t, "WAITING", 0xFF7A6A55, "—", "", "No sample yet.")
    if (!l.connected) return SegmentTile(ConnectionSegment.WifiLink, t, "DOWN", 0xFFD03B3B, "disconnected", "", "The Wi-Fi association is down.")
    val tier = l.rssi?.let { if (it >= -67) "GOOD" else if (it >= -75) "DEGRADED" else "WEAK" } ?: "GOOD"
    return SegmentTile(ConnectionSegment.WifiLink, t, tier, if (tier == "GOOD") 0xFF0CA30C else if (tier == "DEGRADED") 0xFFFAB219 else 0xFFEC835A,
        l.rssi?.let { "$it dBm" } ?: "signal n/a", "${ReportText.fmt(l.rxRateMbps, "0")} Mbps", "${l.ssid} · ${l.bssid} · ch ${l.channel}")
}

fun segmentTile(segment: ConnectionSegment, r: ConnectionReport?, cfg: ConnectionTestConfig): SegmentTile {
    val title = ConnectionAnalyzer.name(segment)
    val enabled = when (segment) {
        ConnectionSegment.Gateway -> cfg.probeGateway; ConnectionSegment.IspHop -> cfg.probeIsp && cfg.contextAtStart?.ispHop != null
        ConnectionSegment.Internet -> cfg.probeInternet; ConnectionSegment.Dns -> cfg.probeDns; ConnectionSegment.Web -> cfg.probeWeb; else -> true
    }
    if (!enabled) return SegmentTile(segment, title, "NOT TESTED", 0xFF7A6A55, if (segment == ConnectionSegment.IspHop && cfg.probeIsp) "hop not found" else "off", "", "This part of the path is not measured.")
    val s = r?.segments?.firstOrNull { it.segment == segment } ?: return SegmentTile(segment, title, "WAITING", 0xFF7A6A55, "—", "", "No probe has finished yet.")
    val byRound = LinkedHashMap<Long, MutableList<ConnectionProbeResult>>()
    r.probes.filter { it.segment == segment && it.method == s.method }.forEach { byRound.getOrPut(it.sequence) { ArrayList() }.add(it) }
    val recent = byRound.entries.sortedByDescending { it.key }.take(3)
    val down = recent.size == 3 && recent.all { g -> g.value.all { it.outcome != ProbeOutcome.Ok } }
    val degraded = (s.lossPct ?: 0.0) > 2 || r.events.any { it.type == ConnectionEventType.LatencySpike && it.segment == segment && it.startElapsedMs > r.activeSeconds * 1000 - 30_000 }
    val status = if (down) "DOWN" else if (degraded) "DEGRADED" else "GOOD"
    return SegmentTile(segment, title, status, if (down) 0xFFD03B3B else if (degraded) 0xFFFAB219 else 0xFF0CA30C,
        s.rttP50?.let { "${ReportText.fmt(it, "0")} ms" } ?: "no reply", "loss ${ReportText.fmt(s.lossPct, "0.#")} %",
        "$title: ${s.target} (${s.method}) · p95 ${ReportText.fmt(s.rttP95, "0")} ms · jitter ${ReportText.fmt(s.jitterMs, "0.0")} ms · ${s.sent} probes · outages ${s.outages}")
}

/** Compact live state for GET /api/connection (same fields as Windows ConnectionTestViewModel.ApiStatus). */
fun connectionApiStatus(running: Boolean, cfg: ConnectionTestConfig?, live: ConnectionReport?, last: ConnectionReport?, elapsed: String): Json.JsonObject =
    if (!running || cfg == null) Json.obj("running" to false, "lastTest" to last?.let { Json.obj("testId" to it.config.testId, "name" to it.config.name, "status" to it.status.name) })
    else Json.obj("running" to true, "testId" to cfg.testId, "name" to cfg.name, "ssid" to cfg.contextAtStart?.ssid, "bssid" to cfg.contextAtStart?.bssid, "elapsed" to elapsed,
        "analyticsVersion" to live?.analyticsVersion,
        "segments" to live?.segments?.map { Json.obj("segment" to it.segment.name, "sent" to it.sent, "lossPct" to it.lossPct, "availabilityPct" to it.availabilityPct, "rttP50" to it.rttP50, "rttP95" to it.rttP95, "jitterMs" to it.jitterMs, "outages" to it.outages) },
        "problemsBySegment" to live?.problemsBySegment, "verdicts" to live?.verdicts, "events" to live?.events?.size, "speedTests" to live?.speedTests?.size, "twoWayTests" to live?.twoWayTests?.size)
