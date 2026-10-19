package com.wirelessscanner.core.report

import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.report.ReportText.fmt
import com.wirelessscanner.core.report.ReportText.iso
import com.wirelessscanner.core.report.ReportText.row

/**
 * Port of SaturationExportService.cs (SPEC-SAT-001 §8): CSV bundle, JSON (schema v3) and the PDF document model.
 * Texts are identical to Windows except where they name the platform (measurement boundary, unit inference).
 */
class SaturationReports(private val ctx: ReportContext) {
    companion object {
        const val JSON_SCHEMA = "wirelessscanner.saturation_report.v3"
        const val PDF_APPENDIX_ROW_LIMIT = 20000

        const val UNIT_DEFINITION =
            "Access points (physical units) are INFERRED, not reported by Android: BSSIDs whose radio timers (TSF) agree within 5 ms in the same band share one radio " +
                "(TSF is not available on Android, so this rule never applies on the tablet); radios join one unit by a shared WPS device UUID, a shared Wi-Fi 7 " +
                "multi-link (MLD) address or a related MAC-address family; the user's corrections win. Membership was fixed at test start. " +
                "Confidence: High = radio clock, WPS or MLD evidence; Medium = MAC family only; Single = one BSSID; Manual = corrected by the user."

        const val MEASUREMENT_BOUNDARY =
            "Client-side receive view from the Android Wi-Fi scan results (WifiManager) on this device's Wi-Fi radio. RSSI is reported by the Wi-Fi " +
                "firmware/driver (resolution and averaging vary by device; not calibrated against an RF instrument); link quality is derived from RSSI. " +
                "Channel/band are derived from centre frequency; channel width from the scan result (blank = unknown). Noise floor is UNAVAILABLE (not exposed " +
                "by the API). Android limits apps to 4 scans per 2 minutes unless Wi-Fi scan throttling is turned off in Developer options; throttled cycles are " +
                "recorded as stale. 'RSSI variation' is the scan-to-scan change in RSSI (dB), not packet latency jitter. Throughput, latency and packet loss are not measured."

        const val SCORE_FORMULA =
            "Stability score = 100 - 4*sigma - 0.5*(100 - availability%) - 3*drops/h - 5*dropouts/h - 5*channel changes/h, clamped 0-100; " +
                "rates use max(active hours, 0.25). Grades: A>=90, B>=75, C>=60, D>=40, F<40. Derived heuristic, not an industry standard."

        const val PRECISION_STATEMENT =
            "RSSI is reported by the driver in whole dBm; min/max/percentiles are therefore whole dBm (nearest-rank percentiles). " +
                "Means, standard deviation (population), variation and slopes are computed in double precision and shown to 1 decimal in the PDF, " +
                "2 decimals in CSV, and unrounded in JSON. Time shares are weighted by the time each cycle represents (until the next cycle, " +
                "max 2 intervals; the last cycle's window ends at the end of the test). Durations are seconds of active (unpaused) time. " +
                "Times are UTC in data files and local (with UTC offset) in the PDF."

        const val NEIGHBOUR_DEFINITION =
            "Neighbours are other BSSIDs heard FRESH in the same cycle (beacon within the freshness limit). Co-channel = same primary channel. " +
                "Overlapping = different primary channel whose occupied block (incl. 40/80/160 MHz bonding, all bands) shares spectrum. " +
                "Interference margin = target RSSI minus the strongest co-channel or overlapping neighbour (dB; higher is better, aim >= 20 dB). " +
                "BSS Load (stations, channel busy %) is what the AP itself advertises (802.11 element 11); blank when not advertised."

        fun tier(rssi: Double) = SaturationAnalyzer.tier(rssi)
    }

    private val generator get() = "WirelessScanner ${ctx.appVersion} (${ctx.platform})"
    private fun f(v: Double?, p: String = "0.##") = fmt(v, p)
    private fun f(v: Int?) = fmt(v)
    private fun localIso(t: java.time.Instant?) = ReportText.localIso(t, ctx.zone)

    fun csvBundle(prefix: String, report: SaturationReport): List<ExportFile> {
        val id = report.config.testId
        val geo = report.config.geo
        val geoCells = arrayOf(f(geo.latitude, "0.000000"), f(geo.longitude, "0.000000"), f(geo.accuracyMeters, "0"), geo.source, geo.building, geo.floor, geo.zone, report.config.location)
        val geoHeads = arrayOf("Latitude", "Longitude", "GeoAccuracy_m", "GeoSource", "Building", "Floor", "Zone", "Location")

        val samples = StringBuilder()
        samples.append("# WirelessScanner saturation test samples | test $id | analytics ${report.analyticsVersion} | one row per cycle per target, including missing/stale/unknown rows\r\n")
        samples.append(row("TestId", "CycleSeq", "WallUtc", "WallLocal", "ElapsedSec", "CycleStatus", "BSSID", "SSID", "ObservationStatus", "RSSI_dBm", "RSSI_Tier",
            "LinkQuality_pct", "Channel", "Band", "ChannelWidth_MHz", "CenterFreq_MHz", "PhyType", "Security", "Vendor", "BeaconInterval_TU", "BeaconAge_ms",
            "CoChannel_Count", "Overlap_Count", "StrongestNeighbour_dBm", "InterferenceMargin_dB", "BssLoad_Stations", "BssLoad_ChannelBusy_pct").trimEnd() + "," + row(*geoHeads) + "\r\n")
        for (c in report.cycles) for (o in c.observations) {
            samples.append(row(id, c.sequence.toString(), iso(c.wallUtc), localIso(c.wallUtc), f(c.elapsedMs / 1000.0, "0.000"), c.status.name, o.bssid, o.ssid,
                o.status.name, f(o.rssi), if (o.status == ObservationStatus.Observed && o.rssi != null) tier(o.rssi.toDouble()) else "", f(o.linkQuality), f(o.channel), o.band,
                f(o.channelWidth), f(o.centerFrequencyMhz, "0.###"), o.phyType, o.security, o.vendor, f(o.beaconIntervalTu), f(o.beaconAgeMs, "0"),
                f(o.coChannelCount), f(o.overlapCount), f(o.strongestOverlapRssi),
                if (o.rssi != null && o.strongestOverlapRssi != null) f(o.rssi - o.strongestOverlapRssi) else "", f(o.stationCount), f(o.channelUtilizationPct, "0.0")) + "," + row(*geoCells) + "\r\n")
        }

        val events = StringBuilder(row("Type", "Subject", "SSID", "StartUtc", "EndUtc", "StartElapsedSec", "EndElapsedSec", "Duration_s", "Cycles",
            "Baseline_dBm", "Extreme_dBm", "Magnitude_dB", "Recovery_s", "Detail") + "\r\n")
        for (e in report.events) events.append(row(e.type.name, e.subject, e.ssid, iso(e.startUtc), iso(e.endUtc), f(e.startElapsedMs / 1000.0, "0.000"),
            e.endElapsedMs?.let { f(it / 1000.0, "0.000") } ?: "", f(e.durationSeconds, "0.0"), f(e.cycles),
            f(e.baselineDbm, "0.0"), f(e.extremeRssi), f(e.magnitudeDb, "0.0"), f(e.recoverySeconds, "0.0"), e.detail) + "\r\n")

        val summary = StringBuilder(row("BSSID", "SSID", "Bands", "Channels", "PrimaryChannel", "ChannelWidth_MHz", "CyclesTracked", "Observed", "Missing", "Stale", "Unknown",
            "Availability_pct", "Min_dBm", "Max_dBm", "Mean_dBm", "Median_dBm", "StdDev_dB", "P5", "P25", "P75", "P95", "IQR_dB", "Range_dB",
            "Variation_Mean_dB", "Variation_P95_dB", "Variation_Max_dB", "Trend_dB_per_h", "PctBelowGood", "PctBelowFair", "PctBelowUnusable",
            "LongestBelowFair_s", "LongestBelowUnusable_s", "Drops", "Peaks", "Dropouts", "ChannelChanges", "WorstDrop_dB", "LongestDropout_s",
            "CoChannel_Mean", "CoChannel_Max", "Overlap_Mean", "Overlap_Max", "StabilityScore", "Grade", "Tier", "Findings",
            "Vendor", "Security", "PhyType", "BeaconInterval_TU", "CenterFreq_MHz", "DFS", "LinkQuality_Mean_pct", "LinkQuality_Min_pct", "SNR_Estimated_Mean_dB",
            "ObservedSpan_s", "BssLoad_Advertised_pct", "BssLoad_Stations_Mean", "BssLoad_Stations_Max", "BssLoad_ChannelBusy_Mean_pct", "BssLoad_ChannelBusy_Max_pct",
            "InterferenceMargin_Median_dB", "InterferenceMargin_Min_dB", "StrongestNeighbour_Max_dBm",
            "TestId", "TestStartUtc", "ActiveSec", "AnalyticsVersion", "AppVersion") + "," + row(*geoHeads) + "\r\n")
        for (t in report.targets) summary.append(row(t.bssid, t.ssid, t.bands, t.channels, f(t.primaryChannel), f(t.channelWidth), f(t.cyclesTracked), f(t.observed), f(t.missing),
            f(t.stale), f(t.unknown), f(t.availabilityPct), f(t.min), f(t.max), f(t.mean), f(t.median), f(t.stdDev), f(t.p5), f(t.p25), f(t.p75), f(t.p95),
            f(t.iqr), f(t.range), f(t.variationMean), f(t.variationP95), f(t.variationMax), f(t.trendDbPerHour), f(t.pctBelowGood), f(t.pctBelowFair),
            f(t.pctBelowUnusable), f(t.longestBelowFairSeconds, "0.0"), f(t.longestBelowUnusableSeconds, "0.0"), f(t.dropCount), f(t.peakCount),
            f(t.dropoutCount), f(t.channelChangeCount), f(t.worstDropDb), f(t.longestDropoutSeconds, "0.0"), f(t.coChannelMean), f(t.coChannelMax),
            f(t.overlapMean), f(t.overlapMax), f(t.stabilityScore), t.grade, t.tier, t.findings.joinToString(" | "),
            t.vendor, t.security, t.phyType, f(t.beaconIntervalTu), f(t.centerFrequencyMhz, "0.###"), t.isDfs?.let { if (it) "yes" else "no" } ?: "",
            f(t.linkQualityMean), f(t.linkQualityMin), f(t.snrEstimatedMeanDb), f(t.observedSpanSeconds, "0.0"), f(t.loadAdvertisedPct), f(t.stationCountMean),
            f(t.stationCountMax), f(t.channelUtilizationMeanPct), f(t.channelUtilizationMaxPct), f(t.interferenceMarginMedianDb), f(t.interferenceMarginMinDb),
            f(t.strongestOverlapRssiMax), id, iso(report.startUtc), f(report.activeSeconds, "0.000"), report.analyticsVersion, ctx.appVersion) + "," + row(*geoCells) + "\r\n")

        val cycles = StringBuilder(row("CycleSeq", "WallUtc", "WallLocal", "ElapsedSec", "Status", "ScanDuration_ms", "BssCount", "ErrorCode", "ErrorMessage") + "\r\n")
        for (c in report.cycles) cycles.append(row(c.sequence.toString(), iso(c.wallUtc), localIso(c.wallUtc), f(c.elapsedMs / 1000.0, "0.000"), c.status.name, f(c.durationMs, "0"),
            f(c.bssCount), c.errorCode, c.errorMessage) + "\r\n")

        val markers = StringBuilder(row("WallUtc", "ElapsedSec", "Text") + "\r\n")
        for (m in report.markers) markers.append(row(iso(m.wallUtc), f(m.elapsedMs / 1000.0, "0.000"), m.text) + "\r\n")

        val info = StringBuilder(row("Key", "Value") + "\r\n")
        for ((k, v) in infoRows(report)) info.append(row(k, v) + "\r\n")

        val files = arrayListOf("info" to info, "samples" to samples, "events" to events, "summary" to summary, "cycles" to cycles, "markers" to markers)
        if (report.unitSummaries.isNotEmpty()) {
            val units = StringBuilder("# $UNIT_DEFINITION\r\n")
            units.append(row("UnitId", "UnitName", "Confidence", "UnitBssids", "UnitSsids", "UnitAvailability_pct", "UnitBestMean_dBm", "RadioKey", "Band",
                "BSSID", "SSID", "RadioSsidConsistency_Mean_dB", "RadioSsidConsistency_Max_dB", "SsidOnlyOutage_pct", "Evidence", "Findings",
                "TestId", "TestStartUtc", "AnalyticsVersion") + "," + row(*geoHeads) + "\r\n")
            for (u in report.unitSummaries) {
                val target = report.config.targetUnits.firstOrNull { it.id == u.id }
                for (r in u.radios) for (b in r.bssids) {
                    val ssid = target?.members?.firstOrNull { it.bssid.equals(b, true) }?.ssid ?: ""
                    units.append(row(u.id, u.name, u.confidence.name, f(u.bssidCount), f(u.ssidCount), f(u.unitAvailabilityPct), f(u.bestMeanDbm),
                        r.radioKey, r.band, b, ssid, f(r.consistencyMeanDeltaDb), f(r.consistencyMaxDeltaDb),
                        r.ssidOnlyOutagePct.entries.firstOrNull { it.key.equals(b, true) }?.let { f(it.value) } ?: "", u.evidence.joinToString(" | "), u.findings.joinToString(" | "),
                        id, iso(report.startUtc), report.analyticsVersion) + "," + row(*geoCells) + "\r\n")
                }
            }
            files.add("units" to units)
        }
        return files.map { (n, sb) -> ExportFile("${prefix}_$n.csv", "text/csv", ReportText.utf8Bom(sb.toString())) }
    }

    fun infoRows(r: SaturationReport): List<Pair<String, String>> {
        val c = r.config; val e = c.environment; val th = c.thresholds
        val out = arrayListOf(
            "ReportSchema" to JSON_SCHEMA, "AnalyticsVersion" to r.analyticsVersion, "GeneratedBy" to generator, "GeneratedUtc" to iso(ctx.now()),
            "TestId" to c.testId, "TestName" to c.name, "Status" to r.status.name, "Scope" to c.scope.name,
            "TargetSsids" to c.targetSsids.joinToString(" | "), "TargetBssids" to c.targetBssids.joinToString(" | "),
            "TargetUnits" to c.targetUnits.joinToString(" | ") { "${it.name} [${it.id}, ${it.confidence}, ${it.members.size} BSSIDs]" },
        )
        if (c.targetUnits.isNotEmpty()) out.add("UnitInference" to UNIT_DEFINITION)
        out += listOf(
            "StartUtc" to iso(r.startUtc), "StartLocal" to localIso(r.startUtc), "EndUtc" to iso(r.endUtc), "EndLocal" to localIso(r.endUtc),
            "ActiveSeconds" to f(r.activeSeconds, "0.000"),
            "PlannedSeconds" to (c.duration?.let { f(it.toMillis() / 1000.0, "0") } ?: "until stopped"),
            "Interval_s" to f(c.interval.toMillis() / 1000.0, "0.###"), "ScanTimeout_s" to f(c.scanTimeout.toMillis() / 1000.0, "0.###"),
            "FreshnessLimit_s" to f(c.freshnessLimit.toMillis() / 1000.0, "0.###"),
            "Thresholds" to "drop ${num(th.dropDb)} dB, peak ${num(th.peakDb)} dB, baseline window ${th.baselineWindow} (min ${th.minBaselineSamples}), dropout ${th.dropoutMissedCycles} missed, strongest-change hysteresis ${num(th.strongestChangeHysteresisDb)} dB, good ${th.goodDbm}, fair ${th.fairDbm}, unusable ${th.unusableDbm} dBm",
            "Location" to c.location, "Facility" to c.facility, "Operator" to c.operator, "Client" to c.client, "Notes" to c.notes,
            "Latitude" to f(c.geo.latitude, "0.000000"), "Longitude" to f(c.geo.longitude, "0.000000"), "GeoAccuracy_m" to f(c.geo.accuracyMeters, "0"),
            "GeoSource" to c.geo.source, "Building/Floor/Zone" to "${c.geo.building} / ${c.geo.floor} / ${c.geo.zone}",
            "Adapter" to (e?.adapterDescription ?: c.interfaceName), "AdapterMac" to (e?.adapterMac ?: ""),
            "DriverVersion" to (e?.driverVersion ?: "not recorded"), "DriverDate" to (e?.driverDate ?: ""), "DriverProvider" to (e?.driverProvider ?: ""),
            "PcConnectionAtStart" to ReportText.connectionText(e?.connectionAtStart),
            "RecordedWithApp" to (e?.appVersion ?: "before 1.3 (not recorded)"), "OS" to (e?.osDescription ?: ""), "Machine" to (e?.machineName ?: ""),
            "TimeZone" to (if (e == null) "" else "${e.timeZoneId} (UTC offset at start ${ReportText.signedHours(e.utcOffsetMinutes)} h)"),
            "MeasurementBoundary" to MEASUREMENT_BOUNDARY, "Precision" to PRECISION_STATEMENT, "Neighbours" to NEIGHBOUR_DEFINITION, "ScoreFormula" to SCORE_FORMULA,
        )
        return out
    }

    /** C# default double formatting in interpolation (shortest round-trip, invariant). */
    private fun num(d: Double) = if (d == Math.rint(d)) d.toLong().toString() else d.toString()

    fun json(report: SaturationReport): ExportFile {
        val g = report.config.geo
        val payload = Json.obj(
            "schema" to JSON_SCHEMA, "analyticsVersion" to report.analyticsVersion, "generatedBy" to generator, "generatedUtc" to ctx.now(),
            "measurementBoundary" to MEASUREMENT_BOUNDARY, "precision" to PRECISION_STATEMENT, "neighbours" to NEIGHBOUR_DEFINITION, "scoreFormula" to SCORE_FORMULA,
            "environment" to report.config.environment, "startLocal" to localIso(report.startUtc), "endLocal" to localIso(report.endUtc),
            "test" to report.config,
            "geo" to Json.obj("latitude" to g.latitude, "longitude" to g.longitude, "accuracyMeters" to g.accuracyMeters, "source" to g.source,
                "capturedUtc" to g.capturedUtc, "building" to g.building, "floor" to g.floor, "zone" to g.zone, "location" to report.config.location, "mapUrl" to g.mapUrl),
            "startUtc" to report.startUtc, "endUtc" to report.endUtc, "activeSeconds" to report.activeSeconds, "status" to report.status,
            "health" to report.health, "targets" to report.targets, "ssids" to report.ssids,
            "unitDefinition" to (if (report.unitSummaries.isNotEmpty()) UNIT_DEFINITION else null), "units" to report.unitSummaries,
            "events" to report.events, "markers" to report.markers, "cycles" to report.cycles,
        )
        return ExportFile("report.json", "application/json", Json.write(payload).toByteArray(Charsets.UTF_8))
    }

    // ---------------------------------------------------------------- PDF document model

    private fun n(v: Double?, p: String = "0.0") = if (v == null) "—" else fmt(v, p)
    private fun n(v: Int?) = v?.toString() ?: "—"
    private fun localT(t: java.time.Instant, p: String = "HH:mm:ss") = ReportText.local(t, ctx.zone, p)
    private fun lwo(t: java.time.Instant, p: String = "yyyy-MM-dd HH:mm:ss") = ReportText.localWithOffset(t, ctx.zone, p)
    private fun label(t: SaturationTargetSummary) = "${t.ssid.ifEmpty { "<hidden>" }} (${t.bssid})"

    fun document(r: SaturationReport): ReportDocument {
        val cfg = r.config; val th = cfg.thresholds
        val b = ArrayList<Block>()
        fun h1(s: String) = b.add(Block.Heading(s))
        fun kv(vararg rows: Pair<String, String>) = b.add(Block.KeyValues(rows.toList()))

        h1("1. Test details")
        kv("Test ID" to cfg.testId, "Test name" to cfg.name, "Location / test area" to cfg.location, "Facility" to cfg.facility,
            "Geolocation (WGS84)" to (if (cfg.geo.hasCoordinates) "${cfg.geo.coordinatesText} — source: ${cfg.geo.source}${cfg.geo.capturedUtc?.let { ", captured ${localT(it, "yyyy-MM-dd HH:mm")}" } ?: ""}" else "not recorded"),
            "Map link" to cfg.geo.mapUrl,
            "Building / floor / zone" to listOf(cfg.geo.building, cfg.geo.floor, cfg.geo.zone).filter { it.isNotBlank() }.joinToString(" / "),
            "Operator" to cfg.operator, "Client" to cfg.client, "Adapter" to cfg.interfaceName, "Scope" to cfg.scope.name,
            "Target SSIDs" to cfg.targetSsids.joinToString(", "), "Target BSSIDs" to cfg.targetBssids.joinToString(", "),
            "Started" to "${lwo(r.startUtc)}  =  ${ReportText.utc(r.startUtc, "yyyy-MM-dd HH:mm:ss")} UTC",
            "Ended" to (r.endUtc?.let { "${lwo(it)}  =  ${ReportText.utc(it, "yyyy-MM-dd HH:mm:ss")} UTC" } ?: "running"),
            "Active duration" to "${ReportText.dur(r.activeSeconds)} (${fmt(r.activeSeconds, "0.0")} s, paused time excluded)" + (cfg.duration?.let { " of planned ${ReportText.dur(it.toMillis() / 1000.0)}" } ?: " (until stopped)"),
            "Scan interval / timeout" to "${fmt(cfg.interval.toMillis() / 1000.0, "0.#")} s / ${fmt(cfg.scanTimeout.toMillis() / 1000.0, "0.#")} s",
            "Final status" to r.status.name, "Notes" to cfg.notes)

        val env = cfg.environment
        h1("Measurement environment")
        kv("Recorded with" to (if (env == null) "WirelessScanner before 1.3 (environment not recorded)" else "WirelessScanner ${env.appVersion}; report generated by $generator; analytics ${r.analyticsVersion}"),
            "Device / OS" to (if (env == null) "" else "${env.machineName}; ${env.osDescription}"),
            "Time zone" to (if (env == null) ctx.zone.id else "${env.timeZoneId} (UTC offset at start ${ReportText.signedHours(env.utcOffsetMinutes)} h)"),
            "Adapter" to "${env?.adapterDescription ?: cfg.interfaceName}${if (env?.adapterMac.isNullOrEmpty()) "" else " (MAC ${env!!.adapterMac})"}",
            "Adapter driver" to (if (env?.driverVersion == null) "not recorded" else "${env.driverVersion} (${env.driverDate}, ${env.driverProvider}) — RSSI is comparable only between tests with the same device and firmware"),
            "This device's connection at start" to ReportText.connectionText(env?.connectionAtStart) + (if (env?.connectionAtStart?.connected == true) " — Android throttles scans to 4 per 2 minutes unless throttling is off (see stale cycles)" else ""))

        h1("2. Executive summary")
        b.add(Block.Grid(listOf("Target", "Grade", "Score", "Tier", "Avail. %", "Mean dBm", "σ dB", "Min/Max", "Margin dB", "Drops", "Dropouts", "Ch. chg"),
            r.targets.map { t -> listOf(label(t), t.grade, n(t.stabilityScore), t.tier, n(t.availabilityPct), n(t.mean), n(t.stdDev), "${n(t.min)} / ${n(t.max)}",
                n(t.interferenceMarginMinDb, "0"), n(t.dropCount), n(t.dropoutCount), n(t.channelChangeCount)) },
            listOf(3.0f, 0.6f, 0.7f, 0.9f, 0.8f, 0.9f, 0.6f, 1.0f, 0.8f, 0.6f, 0.8f, 0.7f)))
        b.add(Block.Paragraph("Grade = stability, not strength (read it with the tier). Margin = lowest interference margin observed (own RSSI − strongest overlapping neighbour).", 7f, italic = true))
        for (t in r.targets) {
            b.add(Block.Paragraph(label(t), bold = true))
            t.findings.forEach { b.add(Block.Paragraph("• $it", indent = 10f)) }
        }
        if (r.targets.isEmpty()) b.add(Block.Paragraph("No targets were observed during this test."))

        h1("3. Methodology and measurement limits")
        kv("Analytics version" to r.analyticsVersion,
            "Sampling" to "Each cycle requests a fresh Android scan and waits up to ${fmt(cfg.scanTimeout.toMillis() / 1000.0, "0")} s for completion. Entries older than ${fmt(cfg.freshnessLimit.toMillis() / 1000.0, "0")} s are 'stale' and excluded from statistics.",
            "Drop / peak" to "RSSI ≤ baseline − ${fmt(th.dropDb, "0")} dB (drop) or ≥ baseline + ${fmt(th.peakDb, "0")} dB (peak); baseline = rolling median of last ${th.baselineWindow} observed samples (min ${th.minBaselineSamples}); event ends within half the threshold (hysteresis).",
            "Dropout" to "Target missing from ≥ ${th.dropoutMissedCycles} consecutive fresh scans.",
            "Thresholds" to "Good ≥ ${th.goodDbm} dBm, Fair ≥ ${th.fairDbm} dBm, Unusable < ${th.unusableDbm} dBm. Exposure is time-weighted by cycle spacing; the last cycle's window ends at the end of the test.",
            "Trend" to "Least-squares slope of observed RSSI, reported only when the observed span is ≥ 15 min (shorter spans extrapolate noise).",
            "Neighbours / BSS Load" to NEIGHBOUR_DEFINITION, "Precision" to PRECISION_STATEMENT,
            "Strongest-BSSID change" to "SSID scope: best BSSID changes when previous is not seen or new is ≥ ${fmt(th.strongestChangeHysteresisDb, "0")} dB stronger.",
            "Stability score" to SCORE_FORMULA, "Measurement boundary" to MEASUREMENT_BOUNDARY)

        h1("4. Scan health")
        val h = r.health
        kv("Cycles" to "${h.total} total: ${h.fresh} fresh, ${h.stale} stale, ${h.failed} failed, ${h.skipped} skipped",
            "Scan duration" to "mean ${n(h.meanScanMs, "0")} ms, max ${n(h.maxScanMs, "0")} ms",
            "Largest gap between cycles" to "${fmt(h.maxGapSeconds, "0.0")} s",
            "Errors" to (if (h.errorCounts.isEmpty()) "none" else h.errorCounts.entries.joinToString("; ") { "${it.key} ×${it.value}" }))

        var first = true
        for (t in r.targets) {
            if (first) h1("5. Target detail") else b.add(Block.Rule())
            first = false
            b.add(Block.SubHeading(label(t)))
            kv("Grade / score / tier" to "${t.grade} / ${n(t.stabilityScore)} / ${t.tier}",
                "Access point" to "${t.vendor ?: "vendor not in table"}; ${t.security ?: "security not reported"}; ${t.phyType ?: "standard not reported"}; beacon ${t.beaconIntervalTu?.let { "$it TU (${fmt(it * 1.024, "0.#")} ms)" } ?: "not reported"}",
                "Band(s), channel(s), width" to "${t.bands}; ch ${t.channels}; ${t.channelWidth?.let { "$it MHz" } ?: "width not advertised"}" +
                    (t.centerFrequencyMhz?.let { "; primary ${fmt(it, "0")} MHz" } ?: "") + (if (t.isDfs == true) "; DFS (radar may force channel changes)" else ""),
                "Cycles tracked" to "${t.cyclesTracked}: ${t.observed} observed, ${t.missing} missing, ${t.stale} stale, ${t.unknown} unknown; availability ${n(t.availabilityPct)} %",
                "RSSI (dBm)" to "min ${n(t.min)}, P5 ${n(t.p5)}, P25 ${n(t.p25)}, median ${n(t.median)}, mean ${n(t.mean)}, P75 ${n(t.p75)}, P95 ${n(t.p95)}, max ${n(t.max)}",
                "Spread" to "σ ${n(t.stdDev)} dB, IQR ${n(t.iqr)} dB, range ${n(t.range)} dB, trend " +
                    (t.trendDbPerHour?.let { "${fmt(it, "0.0")} dB/h" } ?: "n/a (observed span ${fmt(t.observedSpanSeconds / 60, "0.#")} min < 15 min)"),
                "Link quality / SNR" to "link quality mean ${n(t.linkQualityMean)} %, min ${n(t.linkQualityMin)} %; SNR estimated ${n(t.snrEstimatedMeanDb)} dB (assumed −95 dBm floor; not measured)",
                "RSSI variation (dB)" to "mean ${n(t.variationMean)}, P95 ${n(t.variationP95)}, max ${n(t.variationMax)}",
                "Threshold exposure" to "below ${th.goodDbm}: ${n(t.pctBelowGood)} %; below ${th.fairDbm}: ${n(t.pctBelowFair)} % (longest ${fmt(t.longestBelowFairSeconds, "0")} s); below ${th.unusableDbm}: ${n(t.pctBelowUnusable)} % (longest ${fmt(t.longestBelowUnusableSeconds, "0")} s)",
                "Events" to "${t.dropCount} drops (worst ${n(t.worstDropDb, "0")} dB), ${t.peakCount} peaks, ${t.dropoutCount} dropouts (longest ${fmt(t.longestDropoutSeconds, "0")} s), ${t.channelChangeCount} channel changes",
                "Neighbours (fresh)" to "co-channel: mean ${n(t.coChannelMean)}, max ${n(t.coChannelMax)}; overlapping: mean ${n(t.overlapMean)}, max ${n(t.overlapMax)}" +
                    (t.strongestOverlapRssiMax?.let { "; strongest neighbour up to $it dBm" } ?: ""),
                "Interference margin" to (if (t.interferenceMarginMedianDb != null) "median ${n(t.interferenceMarginMedianDb, "0")} dB, minimum ${n(t.interferenceMarginMinDb, "0")} dB (aim ≥ 20 dB)" else "no co-channel or overlapping neighbour heard fresh"),
                "BSS Load (AP-reported)" to (t.loadAdvertisedPct?.takeIf { it > 0 }?.let { "advertised in ${fmt(it, "0")} % of samples; stations mean ${n(t.stationCountMean)}, max ${n(t.stationCountMax)}; channel busy mean ${n(t.channelUtilizationMeanPct)} %, max ${n(t.channelUtilizationMaxPct)} %" } ?: "not advertised by this AP"))

            b.add(Block.Chart("RSSI timeline — y: RSSI (dBm), x: elapsed active time (hh:mm:ss). Shaded: below −67 / −70 / −80 dBm; red ticks = missing; orange spans = drops; dashed = markers", 190f, timeline(r, t.bssid)))
            b.add(Block.Chart("RSSI distribution — x: RSSI (dBm, 1 dB bins), y: number of observed samples", 110f, histogram(r, t.bssid)))
            val ev = r.events.filter { it.subject.equals(t.bssid, true) }
            b.add(Block.Paragraph("Events (${ev.size})", bold = true))
            if (ev.isEmpty()) b.add(Block.Paragraph("No drops, peaks, dropouts or channel changes detected (drop/peak threshold ${fmt(th.dropDb, "0")} dB, dropout ≥ ${th.dropoutMissedCycles} missed scans)."))
            else b.add(Block.Grid(listOf("Type", "Start", "End", "Dur. s", "Magn. dB", "Detail"),
                ev.map { listOf(it.type.name, localT(it.startUtc), it.endUtc?.let { e -> localT(e) } ?: "open", n(it.durationSeconds, "0"), n(it.magnitudeDb, "0"), it.detail) },
                listOf(1f, 0.8f, 0.8f, 0.6f, 0.6f, 4f)))
        }

        if (r.ssids.isNotEmpty()) {
            h1("6. SSID aggregates (best BSSID per cycle)")
            b.add(Block.Grid(listOf("SSID", "BSSIDs", "Avail. %", "Best mean", "Best min", "Best max", "Strongest changes"),
                r.ssids.map { listOf(it.ssid, n(it.bssidCount), n(it.availabilityPct), n(it.bestMean), n(it.bestMin), n(it.bestMax), n(it.strongestBssidChanges)) }))
            val sc = r.events.filter { it.type == SaturationEventType.StrongestBssidChange }
            if (sc.isNotEmpty()) b.add(Block.Grid(listOf("Time", "SSID", "Detail"), sc.map { listOf(localT(it.startUtc), it.ssid ?: "", it.detail) }, listOf(0.8f, 1.2f, 4f)))
        }

        if (r.unitSummaries.isNotEmpty()) {
            h1("6b. Access points (physical units)")
            b.add(Block.Paragraph(UNIT_DEFINITION, 7.5f, italic = true))
            b.add(Block.Grid(listOf("Access point", "Confidence", "BSSIDs", "SSIDs", "On air %", "Best mean dBm"),
                r.unitSummaries.map { listOf(it.name, it.confidence.name, n(it.bssidCount), n(it.ssidCount), n(it.unitAvailabilityPct), n(it.bestMeanDbm)) },
                listOf(2.4f, 0.9f, 0.6f, 0.6f, 0.7f, 0.9f)))
            for (u in r.unitSummaries) {
                b.add(Block.Paragraph("${u.name} (${u.id})", bold = true))
                if (u.evidence.isNotEmpty()) b.add(Block.Paragraph("Evidence: " + u.evidence.joinToString(" "), 8f))
                b.add(Block.Grid(listOf("Radio", "SSIDs", "BSSIDs", "SSID Δ mean dB", "SSID Δ max dB", "SSID-only outage %"),
                    u.radios.map { rr -> listOf(rr.band, rr.ssids.joinToString(", "), rr.bssids.joinToString(", "), n(rr.consistencyMeanDeltaDb, "0.0"), n(rr.consistencyMaxDeltaDb, "0.0"),
                        if (rr.ssidOnlyOutagePct.isEmpty()) "—" else rr.ssidOnlyOutagePct.entries.joinToString(", ") { "${it.key.takeLast(5)} ${fmt(it.value, "0.0")}" }) },
                    listOf(0.6f, 1.6f, 2.2f, 0.7f, 0.7f, 1.3f)))
                if (u.findings.isEmpty()) b.add(Block.Paragraph("No unit-level findings: SSIDs of each radio tracked together and no SSID dropped out on its own.", 8f))
                else u.findings.forEach { b.add(Block.Paragraph("• $it", 8f)) }
            }
        }

        h1("7. Tester markers")
        if (r.markers.isEmpty()) b.add(Block.Paragraph("No markers were added during this test."))
        else b.add(Block.Grid(listOf("Time", "Elapsed", "Marker"), r.markers.map { listOf(localT(it.wallUtc), ReportText.dur(it.elapsedMs / 1000.0), it.text) }, listOf(0.8f, 0.8f, 4f)))

        b.add(Block.PageBreak)
        h1("Appendix A. Per-cycle observation log")
        val rows = r.cycles.flatMap { c -> c.observations.map { c to it } }
        if (rows.size > PDF_APPENDIX_ROW_LIMIT) b.add(Block.Paragraph("Showing the first ${ReportText.n0(PDF_APPENDIX_ROW_LIMIT)} of ${ReportText.n0(rows.size)} rows. The complete log is in the CSV bundle (_samples.csv) and JSON export.", bold = true))
        b.add(Block.Paragraph("Times are local (${lwo(r.startUtc, "yyyy-MM-dd")}). Co/Ovl = fresh co-channel / overlapping neighbours; Nbr = strongest neighbour dBm; Sta/Busy = AP-advertised BSS Load.", 7f, italic = true))
        b.add(Block.Grid(listOf("#", "Time", "Cycle", "BSSID", "Status", "RSSI", "Ch", "Band", "MHz", "Age ms", "Co", "Ovl", "Nbr", "Sta", "Busy %"),
            rows.take(PDF_APPENDIX_ROW_LIMIT).map { (c, o) -> listOf(c.sequence.toString(), localT(c.wallUtc), c.status.name, o.bssid, o.status.name, n(o.rssi), n(o.channel), o.band ?: "—",
                n(o.channelWidth), n(o.beaconAgeMs, "0"), n(o.coChannelCount), n(o.overlapCount), n(o.strongestOverlapRssi), n(o.stationCount), n(o.channelUtilizationPct, "0")) },
            listOf(0.45f, 0.75f, 0.6f, 1.55f, 0.8f, 0.5f, 0.4f, 0.65f, 0.45f, 0.6f, 0.35f, 0.4f, 0.45f, 0.4f, 0.5f)))

        return ReportDocument(
            title = "SIGNAL SATURATION TEST REPORT",
            subtitle = "${cfg.name}  |  ${cfg.location}  |  ${lwo(r.startUtc, "yyyy-MM-dd HH:mm")}",
            metadataTitle = "Signal Saturation Test - ${cfg.name}",
            author = cfg.operator.ifBlank { "WirelessScanner" },
            subject = "Wi-Fi signal saturation (stability) test report",
            footer = "$generator | ${r.analyticsVersion} | Test ${cfg.testId}",
            accent = 0xFFB45309.toInt(), blocks = b,
        )
    }

    fun timeline(r: SaturationReport, bssid: String): ChartSpec.RssiTimeline {
        val th = r.config.thresholds
        val points = r.cycles.flatMap { c -> c.observations.filter { it.bssid.equals(bssid, true) }.map { c to it } }
        val observed = points.filter { it.second.status == ObservationStatus.Observed && it.second.rssi != null }
        val maxT = maxOf(1.0, maxOf(r.activeSeconds, r.cycles.lastOrNull()?.elapsedMs?.div(1000.0) ?: 1.0))
        val yMax = minOf(-20, (observed.maxOfOrNull { it.second.rssi!! } ?: -40) + 5)
        var yMin = maxOf(-100, (observed.minOfOrNull { it.second.rssi!! } ?: -90) - 5)
        yMin = minOf(yMin, th.unusableDbm - 5)
        return ChartSpec.RssiTimeline(maxT, yMin, yMax, th.goodDbm, th.fairDbm, th.unusableDbm,
            points.map { (c, o) -> c.elapsedMs / 1000.0 to (if (o.status == ObservationStatus.Observed) o.rssi else null) },
            points.filter { it.second.status == ObservationStatus.Missing }.map { it.first.elapsedMs / 1000.0 },
            r.events.filter { it.type == SaturationEventType.Drop && it.subject.equals(bssid, true) }.map { it.startElapsedMs / 1000.0 to (it.endElapsedMs ?: it.startElapsedMs) / 1000.0 },
            r.markers.map { it.elapsedMs / 1000.0 to it.text })
    }

    fun histogram(r: SaturationReport, bssid: String): ChartSpec.Histogram {
        val values = r.cycles.flatMap { it.observations }.filter { it.bssid.equals(bssid, true) && it.status == ObservationStatus.Observed && it.rssi != null }.map { it.rssi!! }
        if (values.isEmpty()) return ChartSpec.Histogram(emptyList(), 0)
        val lo = values.min(); val hi = values.max()
        return ChartSpec.Histogram((lo..hi).map { v -> v to values.count { it == v } }, values.size)
    }
}
