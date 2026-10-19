package com.wirelessscanner.core.report

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.report.ReportText.fmt
import com.wirelessscanner.core.report.ReportText.iso
import com.wirelessscanner.core.report.ReportText.row

/** Port of ConnectionExportService.cs (DR-0014): CSV bundle, JSON (wirelessscanner.connection_report.v1) and the PDF document model. */
class ConnectionReports(private val ctx: ReportContext) {
    companion object {
        const val JSON_SCHEMA = "wirelessscanner.connection_report.v1"
        const val PDF_APPENDIX_ROW_LIMIT = 20_000

        const val METHOD =
            "Probes are sent from this device over its Wi-Fi network only (sockets bound to the Wi-Fi network) to the router (ICMP echo), the first ISP hop " +
                "(ICMP echo, or a TTL-limited probe when the hop does not answer echo), public anycast targets (TCP connect to port 443 = primary, plus ICMP), " +
                "the configured DNS resolvers (one A query) and one HTTPS address (cold request: name resolution + TCP + TLS + first byte). Light intensity: " +
                "router every 1 s, ISP and internet every 2 s, DNS every 10 s, web every 30 s; Detailed halves the intervals. The Wi-Fi connection itself is " +
                "never changed. Round-trip times are measured with the device's monotonic clock around each probe; ICMP uses the system ping utility."

        const val DEFINITIONS =
            "Loss % = failed ÷ sent. Availability % = share of probe rounds of a segment with at least one successful probe. Percentiles: nearest rank over " +
                "successful probes. Jitter = mean |RTT(n) − RTT(n−1)| over consecutive successful probes of one target; delay variation P95 = 95th percentile of " +
                "the same differences (round-trip; one-way per direction only with a LAN responder). Outage = ≥ 3 consecutive failed rounds, attributed to the first " +
                "failing segment upstream (Wi-Fi link → router → ISP hop → internet; DNS and web after those). Latency spike = RTT > max(3 × baseline, baseline + 100 ms), " +
                "baseline = median of the previous 30 successful RTTs of that target. A TCP reset (port closed) is recorded as Refused without a round trip. ISP-hop failures while " +
                "the internet stays reachable are treated as reply rate-limiting, not a fault."

        const val LIMITS =
            "Round-trip measurements only (one-way delay needs synchronised clocks). Routers may deprioritise ICMP; TCP connect is therefore the primary internet " +
                "metric. The voice score is an ESTIMATE from a simplified ITU-T G.107 E-model (one-way delay ≈ mean RTT ÷ 2), not a measured call. Results describe " +
                "this device at this place on this connection; other devices may differ. Android does not expose 802.11 frame counters (retries, failures), so they are " +
                "reported as not available. Speed tests depend on the server and its path, and saturate the link while they run; regular probes pause during speed and two-way tests."
    }

    private val generator get() = "WirelessScanner ${ctx.appVersion} (${ctx.platform})"
    private fun f(v: Double?, p: String = "0.###") = fmt(v, p)
    private fun f(v: Int?) = fmt(v)
    private fun localIso(t: java.time.Instant?) = ReportText.localIso(t, ctx.zone)
    private fun num(d: Double) = if (d == Math.rint(d)) d.toLong().toString() else d.toString()

    fun infoRows(r: ConnectionReport): List<Pair<String, String>> {
        val c = r.config; val x = c.contextAtStart; val e = c.environment
        return listOf(
            "ReportSchema" to JSON_SCHEMA, "AnalyticsVersion" to r.analyticsVersion, "GeneratedBy" to generator, "TestId" to c.testId, "TestName" to c.name,
            "Status" to r.status.name, "StartUtc" to iso(r.startUtc), "StartLocal" to localIso(r.startUtc), "EndUtc" to iso(r.endUtc), "EndLocal" to localIso(r.endUtc),
            "ActiveSeconds" to f(r.activeSeconds, "0.000"), "PlannedSeconds" to (c.duration?.let { f(it.toMillis() / 1000.0, "0") } ?: "until stopped"),
            "Intensity" to "${c.intensity} (tick ${c.tickMilliseconds} ms, about ${f(c.estimatedKbps(), "0.0")} kbit/s)",
            "Segments" to listOfNotNull(if (c.probeWifiLink) "Wi-Fi link" else null, if (c.probeGateway) "router" else null, if (c.probeIsp) "ISP hop" else null,
                if (c.probeInternet) "internet" else null, if (c.probeDns) "DNS" else null, if (c.probeWeb) "web" else null).joinToString(" | "),
            "InternetTargets" to c.internetTargets.joinToString(" | ") + " (TCP ${c.internetTcpPort} + ICMP)", "DnsQueryName" to c.dnsQueryName, "WebUrl" to c.webUrl,
            "Ssid" to (x?.ssid ?: ""), "Bssid" to (x?.bssid ?: ""), "ChannelBand" to "${x?.channel ?: ""} ${x?.band ?: ""}".trim(), "Phy" to (x?.phyType ?: ""),
            "RssiAtStart_dBm" to f(x?.rssi), "RxTxRate_Mbps" to "${f(x?.rxRateMbps, "0.#")} / ${f(x?.txRateMbps, "0.#")}",
            "LocalIPv4" to (x?.localIPv4 ?: ""), "Gateways" to (x?.gateways ?: emptyList()).joinToString(" | "), "DnsServers" to (x?.dnsServers ?: emptyList()).joinToString(" | "),
            "PathHops" to (x?.pathHops ?: emptyList()).joinToString(" → "),
            "IspHop" to (if (x?.ispHop == null) "not found" else "${x.ispHop} (TTL ${x.ispHopTtl}, ${if (x.ispHopAnswersEcho) "echo" else "TTL-limited probe"})"),
            "OtherActiveInterfaces" to (if ((x?.otherActiveInterfaces?.size ?: 0) > 0) x!!.otherActiveInterfaces.joinToString(" | ") + " (probes bound to Wi-Fi)" else "none"),
            "LinkedSaturationTest" to (c.linkedSaturationTestId ?: ""), "Location" to c.location, "Facility" to c.facility, "Operator" to c.operator, "Client" to c.client, "Notes" to c.notes,
            "Latitude" to f(c.geo.latitude, "0.000000"), "Longitude" to f(c.geo.longitude, "0.000000"), "Building/Floor/Zone" to "${c.geo.building} / ${c.geo.floor} / ${c.geo.zone}",
            "Adapter" to (e?.adapterDescription ?: c.interfaceName), "DriverVersion" to (e?.driverVersion ?: "not recorded"), "RecordedWithApp" to (e?.appVersion ?: ""),
            "OS" to (e?.osDescription ?: ""), "Machine" to (e?.machineName ?: ""),
            "TimeZone" to (if (e == null) "" else "${e.timeZoneId} (UTC offset at start ${ReportText.signedHours(e.utcOffsetMinutes)} h)"),
            "Totals" to "${r.totals.probesSent} probes sent = ${r.totals.probesOk} ok + ${r.totals.probesFailed} failed; ${r.totals.linkSamples} link samples; ${r.totals.sleepGaps} gap(s), ${f(r.totals.unmeasuredSeconds, "0.0")} s unmeasured",
            "VoiceEstimate" to (if (r.voiceMos != null) "R ${f(r.voiceRFactor, "0.0")}, MOS ${f(r.voiceMos, "0.00")} (estimate)" else "not available"),
            "Method" to METHOD, "Definitions" to DEFINITIONS, "Limits" to LIMITS,
            "VerdictThresholds" to ConnectionAnalyzer.VERDICT_THRESHOLDS.joinToString(" | ") { t ->
                "${t.use}: GOOD p95 ≤ ${num(t.goodP95)} ms, jitter ≤ ${num(t.goodJitter)} ms, loss ≤ ${num(t.goodLoss)} %; FAIR p95 ≤ ${num(t.fairP95)}, jitter ≤ ${num(t.fairJitter)}, loss ≤ ${num(t.fairLoss)}" },
        )
    }

    fun csvBundle(prefix: String, r: ConnectionReport): List<ExportFile> {
        val id = r.config.testId
        val geo = r.config.geo
        val geoCells = arrayOf(f(geo.latitude, "0.000000"), f(geo.longitude, "0.000000"), geo.building, geo.floor, geo.zone, r.config.location)
        val geoHead = row("Latitude", "Longitude", "Building", "Floor", "Zone", "Location")
        val nl = "\r\n"

        val info = StringBuilder(row("Key", "Value") + "\n")
        for ((k, v) in infoRows(r)) info.append(row(k, v) + nl)

        val probes = StringBuilder("# WirelessScanner connection test probes | test $id | ${r.analyticsVersion} | one row per probe, failures included$nl")
        probes.append(row("TestId", "Round", "WallUtc", "WallLocal", "ElapsedSec", "Segment", "Target", "Method", "Outcome", "RTT_ms", "Detail") + "," + geoHead + nl)
        for (p in r.probes) probes.append(row(id, p.sequence.toString(), iso(p.wallUtc), localIso(p.wallUtc), f(p.elapsedMs / 1000.0, "0.000"),
            p.segment.name, p.target, p.method.name, p.outcome.name, f(p.rttMs, "0.000"), p.detail) + "," + row(*geoCells) + nl)

        val link = StringBuilder(row("TestId", "Tick", "WallUtc", "WallLocal", "ElapsedSec", "Connected", "SSID", "BSSID", "Channel", "RSSI_dBm", "SignalQuality_pct",
            "RxRate_Mbps", "TxRate_Mbps", "LocalIp", "Gateway", "IfBytesIn", "IfBytesOut", "TxFrames", "TxRetries", "TxFailed", "AckFailures", "RxFrames", "FcsErrors", "GapMs") + "\n")
        for (l in r.linkSamples) link.append(row(id, l.sequence.toString(), iso(l.wallUtc), localIso(l.wallUtc), f(l.elapsedMs / 1000.0, "0.000"), if (l.connected) "yes" else "no",
            l.ssid, l.bssid, f(l.channel), f(l.rssi), f(l.signalQualityPct), f(l.rxRateMbps, "0.#"), f(l.txRateMbps, "0.#"), l.localIp, l.gateway,
            l.ifBytesIn?.toString(), l.ifBytesOut?.toString(), l.counters?.txFrames?.toString(), l.counters?.txRetries?.toString(), l.counters?.txFailed?.toString(),
            l.counters?.ackFailures?.toString(), l.counters?.rxFrames?.toString(), l.counters?.fcsErrors?.toString(), l.gapMs?.toString()) + nl)

        val segs = StringBuilder(row("Level", "Segment", "Target", "Method", "Sent", "Ok", "Failed", "Loss_pct", "Availability_pct", "RTT_min_ms", "RTT_p50_ms",
            "RTT_p95_ms", "RTT_p99_ms", "RTT_max_ms", "RTT_mean_ms", "Jitter_ms", "DelayVariation_p95_ms", "Spikes", "Outages", "LongestOutage_s", "FailureReasons",
            "TestId", "AnalyticsVersion") + "," + geoHead + "\n")
        for ((level, list) in listOf("segment" to r.segments, "target" to r.targets)) for (s in list)
            segs.append(row(level, s.segment.name, s.target, s.method.name, f(s.sent), f(s.ok), f(s.failed), f(s.lossPct), f(s.availabilityPct),
                f(s.rttMin), f(s.rttP50), f(s.rttP95), f(s.rttP99), f(s.rttMax), f(s.rttMean), f(s.jitterMs), f(s.delayVariationP95Ms), f(s.spikes), f(s.outages),
                f(s.longestOutageSeconds, "0.0"), s.failureReasons.entries.joinToString(" | ") { "${it.key} ${it.value}" }, id, r.analyticsVersion) + "," + row(*geoCells) + nl)

        val events = StringBuilder(row("Type", "Segment", "AttributedTo", "StartUtc", "EndUtc", "StartElapsedSec", "EndElapsedSec", "Duration_s", "Probes", "PeakRTT_ms", "Detail") + "\n")
        for (e in r.events) events.append(row(e.type.name, e.segment?.name, e.attributedTo?.name, iso(e.startUtc), iso(e.endUtc), f(e.startElapsedMs / 1000.0, "0.000"),
            e.endElapsedMs?.let { f(it / 1000.0, "0.000") } ?: "", f(e.durationSeconds, "0.0"), f(e.probes), f(e.peakRttMs, "0.0"), e.detail) + nl)

        val files = arrayListOf("info" to info, "probes" to probes, "link" to link, "segments" to segs, "events" to events)
        if (r.speedTests.isNotEmpty()) {
            val sp = StringBuilder(row("StartUtc", "Provider", "Server", "Streams", "SecondsPerDirection", "Download_Mbps", "Upload_Mbps", "BytesDown", "BytesUp", "IdleLatency_ms",
                "LoadedLatencyDown_ms", "LoadedLatencyUp_ms", "LoadedLatency_p95_ms", "LatencyIncrease_ms", "BufferbloatGrade", "Responsiveness_RPM_approx", "LatencyTarget", "Error") + "\n")
            for (s in r.speedTests) sp.append(row(iso(s.startUtc), s.provider, s.server, f(s.streams), f(s.secondsPerDirection), f(s.downloadMbps, "0.00"), f(s.uploadMbps, "0.00"),
                s.bytesDown.toString(), s.bytesUp.toString(), f(s.idleLatencyMs), f(s.loadedLatencyDownMs), f(s.loadedLatencyUpMs), f(s.loadedLatencyP95Ms),
                f(s.latencyIncreaseMs), s.bufferbloatGrade, f(s.responsivenessRpm, "0"), s.latencyTarget, s.error) + nl)
            files.add("speed" to sp)
        }
        if (r.twoWayTests.isNotEmpty()) {
            val tw = StringBuilder(row("StartUtc", "Responder", "Seconds", "PacketsPerSecond", "PayloadBytes", "Sent", "ReceivedByResponder", "RepliesSent", "ReceivedBack",
                "UpLoss_pct", "DownLoss_pct", "RTT_p50_ms", "RTT_p95_ms", "UpJitter_ms", "DownJitter_ms", "UpReordered", "DownReordered", "Duplicates", "MOS_up_est", "MOS_down_est",
                "LanDown_Mbps", "LanUp_Mbps", "Error") + "\n")
            for (t in r.twoWayTests) tw.append(row(iso(t.startUtc), t.responder, f(t.seconds), f(t.packetsPerSecond), f(t.payloadBytes), f(t.sent), f(t.receivedByResponder), f(t.repliesSent),
                f(t.receivedBack), f(t.upLossPct), f(t.downLossPct), f(t.rttP50Ms), f(t.rttP95Ms), f(t.upJitterMs), f(t.downJitterMs), f(t.upReordered), f(t.downReordered),
                f(t.duplicates), f(t.mosUp, "0.00"), f(t.mosDown, "0.00"), f(t.lanDownMbps, "0.00"), f(t.lanUpMbps, "0.00"), t.error) + nl)
            files.add("twoway" to tw)
        }
        val markers = StringBuilder(row("WallUtc", "ElapsedSec", "Text") + "\n")
        for (m in r.markers) markers.append(row(iso(m.wallUtc), f(m.elapsedMs / 1000.0, "0.000"), m.text) + nl)
        files.add("markers" to markers)
        return files.map { (n, sb) -> ExportFile("${prefix}_$n.csv", "text/csv", ReportText.utf8Bom(sb.toString())) }
    }

    fun json(r: ConnectionReport): ExportFile {
        val payload = Json.obj(
            "schema" to JSON_SCHEMA, "analyticsVersion" to r.analyticsVersion, "generatedBy" to generator, "generatedUtc" to ctx.now(),
            "method" to METHOD, "definitions" to DEFINITIONS, "limits" to LIMITS,
            "verdictThresholds" to ConnectionAnalyzer.VERDICT_THRESHOLDS.map {
                Json.obj("use" to it.use, "goodP95" to it.goodP95, "goodJitter" to it.goodJitter, "goodLoss" to it.goodLoss, "fairP95" to it.fairP95, "fairJitter" to it.fairJitter, "fairLoss" to it.fairLoss)
            },
            "test" to r.config, "environment" to r.config.environment, "connection" to r.config.contextAtStart,
            "startLocal" to localIso(r.startUtc), "endLocal" to localIso(r.endUtc), "startUtc" to r.startUtc, "endUtc" to r.endUtc,
            "activeSeconds" to r.activeSeconds, "status" to r.status, "totals" to r.totals, "verdicts" to r.verdicts,
            "voice" to Json.obj("rFactor" to r.voiceRFactor, "mos" to r.voiceMos, "note" to "estimate (simplified ITU-T G.107 E-model)"),
            "problemsBySegment" to r.problemsBySegment, "segments" to r.segments, "targets" to r.targets, "link" to r.link,
            "speedTests" to r.speedTests, "twoWayTests" to r.twoWayTests, "events" to r.events, "markers" to r.markers, "probes" to r.probes, "linkSamples" to r.linkSamples,
        )
        return ExportFile("report.json", "application/json", Json.write(payload).toByteArray(Charsets.UTF_8))
    }

    private fun n(v: Double?, p: String = "0.0") = if (v == null) "—" else fmt(v, p)
    private fun n(v: Int?) = v?.toString() ?: "—"
    private fun localT(t: java.time.Instant) = ReportText.local(t, ctx.zone, "HH:mm:ss")
    private fun lwo(t: java.time.Instant, p: String = "yyyy-MM-dd HH:mm:ss") = ReportText.localWithOffset(t, ctx.zone, p)

    fun document(r: ConnectionReport): ReportDocument {
        val cfg = r.config; val x = cfg.contextAtStart
        val b = ArrayList<Block>()
        fun h1(s: String) = b.add(Block.Heading(s))
        fun kv(vararg rows: Pair<String, String>) = b.add(Block.KeyValues(rows.toList(), 150f))

        h1("1. Test details")
        kv("Test" to "${cfg.name} (${r.status})", "Location" to "${cfg.location}  ${cfg.geo.coordinatesText}",
            "Building / floor / zone" to "${cfg.geo.building} / ${cfg.geo.floor} / ${cfg.geo.zone}",
            "Start" to "${lwo(r.startUtc)}  (${ReportText.utc(r.startUtc, "yyyy-MM-dd HH:mm:ss")} UTC)",
            "End" to (r.endUtc?.let { "${lwo(it)}  (${ReportText.utc(it, "yyyy-MM-dd HH:mm:ss")} UTC)" } ?: "running"),
            "Active / planned" to "${ReportText.dur(r.activeSeconds)} / ${cfg.duration?.let { ReportText.dur(it.toMillis() / 1000.0) } ?: "until stopped"}",
            "Intensity" to "${cfg.intensity}: about ${fmt(cfg.estimatedKbps(), "0.0")} kbit/s of probe traffic",
            "Operator / client / facility" to "${cfg.operator} / ${cfg.client} / ${cfg.facility}", "Notes" to cfg.notes,
            "Linked saturation test" to (cfg.linkedSaturationTestId ?: "none"))

        h1("2. What was tested (your connection)")
        kv("Network / access point" to "${x?.ssid ?: ""} / ${x?.bssid ?: ""}", "Channel / band / standard" to "${x?.channel ?: ""} / ${x?.band ?: ""} / ${x?.phyType ?: ""}",
            "Signal / PHY rate at start" to "${n(x?.rssi)} dBm / rx ${n(x?.rxRateMbps)} Mbps, tx ${n(x?.txRateMbps)} Mbps",
            "This device" to "${x?.localIPv4 ?: ""} on ${cfg.environment?.adapterDescription ?: cfg.interfaceName} (firmware ${cfg.environment?.driverVersion ?: "not recorded"})",
            "Router / DNS" to "${(x?.gateways ?: emptyList()).joinToString(", ")} / ${(x?.dnsServers ?: emptyList()).joinToString(", ")}",
            "Path at start" to (x?.pathHops ?: emptyList()).joinToString(" → "),
            "ISP hop" to (if (x?.ispHop == null) "not found (ISP segment not measured)" else "${x.ispHop} at TTL ${x.ispHopTtl} (${if (x.ispHopAnswersEcho) "echo" else "TTL-limited probe"})"),
            "Internet targets" to "${cfg.internetTargets.joinToString(", ")} (TCP ${cfg.internetTcpPort} primary + ICMP)", "DNS name / web address" to "${cfg.dnsQueryName} / ${cfg.webUrl}",
            "Other active interfaces" to (if ((x?.otherActiveInterfaces?.size ?: 0) > 0) x!!.otherActiveInterfaces.joinToString(", ") + " — every probe was bound to Wi-Fi" else "none"))

        h1("3. Summary")
        b.add(Block.Grid(listOf("Use", "Rating", "Why"), r.verdicts.map { listOf(it.use, it.rating, it.reason) }, listOf(1.1f, 0.7f, 4f)))
        b.add(Block.Paragraph("Voice estimate: ${if (r.voiceMos != null) "R ${n(r.voiceRFactor)}, MOS ${n(r.voiceMos, "0.00")} (simplified E-model; an estimate, not a measured call)" else "not available"}."))
        val problems = r.problemsBySegment.entries.sortedBy { it.key.ordinal }.map { "${ConnectionAnalyzer.name(it.key)} ${it.value}" }
        b.add(Block.Paragraph("Where problems happened: ${if (problems.isEmpty()) "no outages or latency spikes were attributed to any segment" else problems.joinToString(" · ")}.", bold = true))
        b.add(Block.Paragraph("Totals: ${ReportText.n0(r.totals.probesSent)} probes = ${ReportText.n0(r.totals.probesOk)} ok + ${ReportText.n0(r.totals.probesFailed)} failed; ${ReportText.n0(r.totals.linkSamples)} link samples; " +
            "${r.totals.sleepGaps} unmeasured gap(s), ${fmt(r.totals.unmeasuredSeconds, "0.0")} s.", 7.5f))

        h1("4. Segments (pooled over targets of the primary method)")
        b.add(Block.Grid(listOf("Segment", "Method", "Sent", "Loss %", "Avail. %", "p50", "p95", "p99", "Max", "Jitter", "DV p95", "Spikes", "Outages", "Longest s"),
            r.segments.map { s -> listOf(ConnectionAnalyzer.name(s.segment), s.method.name, n(s.sent), n(s.lossPct, "0.##"), n(s.availabilityPct, "0.##"), n(s.rttP50), n(s.rttP95),
                n(s.rttP99), n(s.rttMax), n(s.jitterMs), n(s.delayVariationP95Ms), n(s.spikes), n(s.outages), n(s.longestOutageSeconds)) },
            listOf(1.3f, 0.6f, 0.5f, 0.5f, 0.55f, 0.45f, 0.45f, 0.45f, 0.5f, 0.5f, 0.5f, 0.5f, 0.55f, 0.6f)))
        b.add(Block.Paragraph("Round-trip times in ms. DV = delay variation. Failure reasons per target in section 5.", 7f, italic = true))

        h1("5. Targets")
        b.add(Block.Grid(listOf("Segment", "Target", "Method", "Sent", "Loss %", "p50", "p95", "Max", "Jitter", "Failures"),
            r.targets.map { s -> listOf(ConnectionAnalyzer.name(s.segment), s.target, s.method.name, n(s.sent), n(s.lossPct, "0.##"), n(s.rttP50), n(s.rttP95), n(s.rttMax), n(s.jitterMs),
                if (s.failureReasons.isEmpty()) "—" else s.failureReasons.entries.joinToString(", ") { "${it.key} ${it.value}" }) },
            listOf(1.1f, 1.8f, 0.6f, 0.5f, 0.5f, 0.45f, 0.45f, 0.5f, 0.5f, 1.6f)))

        h1("6. Timelines")
        b.add(Block.Paragraph("Round-trip time per segment (ms; red ticks = failed probes) and Wi-Fi signal (dBm), on one time axis (elapsed active time).", 7.5f, italic = true))
        for (seg in listOf(ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns)) {
            val pm = ConnectionAnalyzer.primaryMethod(seg, r.probes)
            val pts = r.probes.filter { it.segment == seg && it.method == pm }
            if (pts.isEmpty()) continue
            b.add(Block.Chart(ConnectionAnalyzer.name(seg), 80f, probeChart(r, pts)))
        }
        if (r.linkSamples.any { it.rssi != null }) b.add(Block.Chart("Wi-Fi signal (dBm) — vertical lines = roams", 70f, signalChart(r)))

        h1("7. Wi-Fi link")
        val l = r.link
        kv("Samples / roams / disconnects" to "${ReportText.n0(l.samples)} / ${l.roams} / ${l.disconnects}", "Access points used" to l.bssids.joinToString(", "),
            "Signal mean / minimum" to "${n(l.rssiMean)} / ${n(l.rssiMin)} dBm", "PHY rate mean rx / tx" to "${n(l.rxRateMean)} / ${n(l.txRateMean)} Mbps",
            "Frame retries / failed / FCS errors" to (if (l.countersAvailable) "${n(l.retryPct, "0.##")} % / ${n(l.txFailedPct, "0.##")} % / ${n(l.fcsErrorPct, "0.##")} %" else "not reported on Android (no frame counters)"),
            "This device's own traffic" to "mean ${n(l.pcTrafficMeanKbps)} kbit/s, max ${n(l.pcTrafficMaxKbps)} kbit/s (context for spikes)")

        if (r.speedTests.isNotEmpty()) {
            h1("8. Speed and responsiveness")
            b.add(Block.Grid(listOf("Time", "Server", "Down Mbps", "Up Mbps", "Idle ms", "Loaded ms (down/up)", "+ms", "Grade", "RPM≈", "Note"),
                r.speedTests.map { s -> listOf(localT(s.startUtc), "${s.provider} ${s.server}", n(s.downloadMbps, "0.0"), n(s.uploadMbps, "0.0"), n(s.idleLatencyMs),
                    "${n(s.loadedLatencyDownMs)} / ${n(s.loadedLatencyUpMs)}", n(s.latencyIncreaseMs), s.bufferbloatGrade, n(s.responsivenessRpm, "0"), s.error ?: s.latencyTarget) },
                listOf(0.6f, 1.6f, 0.6f, 0.6f, 0.5f, 0.9f, 0.45f, 0.45f, 0.5f, 1.2f)))
            b.add(Block.Paragraph("Grade from the median latency increase under load: A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F ≥ 400. RPM≈ = 60 000 ÷ loaded median round trip (approximation, not the IETF algorithm).", 7f, italic = true))
        }
        if (r.twoWayTests.isNotEmpty()) {
            h1("9. Two-way Wi-Fi test (LAN responder)")
            b.add(Block.Grid(listOf("Time", "Responder", "Loss up/down %", "RTT p50/p95", "Jitter up/down", "Reorder up/down", "MOS up/down (est.)", "LAN Mbps down/up", "Note"),
                r.twoWayTests.map { t -> listOf(localT(t.startUtc), t.responder, "${n(t.upLossPct, "0.##")} / ${n(t.downLossPct, "0.##")}", "${n(t.rttP50Ms)} / ${n(t.rttP95Ms)}",
                    "${n(t.upJitterMs, "0.00")} / ${n(t.downJitterMs, "0.00")}", "${t.upReordered} / ${t.downReordered}", "${n(t.mosUp, "0.00")} / ${n(t.mosDown, "0.00")}",
                    "${n(t.lanDownMbps)} / ${n(t.lanUpMbps)}", t.error ?: "${t.sent} pkts, ${t.packetsPerSecond}/s, ${t.payloadBytes} B") },
                listOf(0.55f, 1.0f, 0.75f, 0.7f, 0.75f, 0.65f, 0.75f, 0.8f, 1.0f)))
        }

        h1("10. Events")
        if (r.events.isEmpty()) b.add(Block.Paragraph("No outages, spikes, roams, disconnects or gaps."))
        else b.add(Block.Grid(listOf("Time", "Type", "Where (attributed)", "Dur. s", "Detail"),
            r.events.map { e -> listOf(localT(e.startUtc), e.type.name, e.attributedTo?.let { ConnectionAnalyzer.name(it) } ?: "—", n(e.durationSeconds), e.detail) },
            listOf(0.55f, 0.8f, 1.0f, 0.45f, 4f)))

        h1("11. Method, definitions and limits")
        b.add(Block.Paragraph(METHOD, 7.5f)); b.add(Block.Paragraph(DEFINITIONS, 7.5f)); b.add(Block.Paragraph(LIMITS, 7.5f))
        b.add(Block.Paragraph("Verdict thresholds (internet round trip, pooled TCP): " + ConnectionAnalyzer.VERDICT_THRESHOLDS.joinToString("; ") { t ->
            "${t.use} GOOD p95 ≤ ${num(t.goodP95)} ms, jitter ≤ ${num(t.goodJitter)}, loss ≤ ${num(t.goodLoss)} % / FAIR ≤ ${num(t.fairP95)}, ${num(t.fairJitter)}, ${num(t.fairLoss)} %" }, 7.5f))

        h1("12. Tester markers")
        if (r.markers.isEmpty()) b.add(Block.Paragraph("No markers."))
        else b.add(Block.Grid(listOf("Time", "Marker"), r.markers.map { listOf(localT(it.wallUtc), it.text) }, listOf(0.6f, 4f)))

        b.add(Block.PageBreak)
        h1("Appendix A. Probe log")
        if (r.probes.size > PDF_APPENDIX_ROW_LIMIT) b.add(Block.Paragraph("Showing the first ${ReportText.n0(PDF_APPENDIX_ROW_LIMIT)} of ${ReportText.n0(r.probes.size)} probes. The complete log is in the CSV bundle (_probes.csv) and the JSON export.", bold = true))
        b.add(Block.Grid(listOf("Round", "Time", "Segment", "Target", "Method", "Outcome", "RTT ms", "Detail"),
            r.probes.take(PDF_APPENDIX_ROW_LIMIT).map { p -> listOf(p.sequence.toString(), localT(p.wallUtc), p.segment.name, p.target, p.method.name, p.outcome.name, n(p.rttMs, "0.00"), p.detail ?: "") },
            listOf(0.5f, 0.6f, 0.7f, 1.6f, 0.55f, 0.65f, 0.55f, 1.4f)))

        return ReportDocument("CONNECTION TEST REPORT", "${cfg.name}  |  ${cfg.location}  |  ${lwo(r.startUtc, "yyyy-MM-dd HH:mm")}",
            "Connection Test - ${cfg.name}", cfg.operator.ifBlank { "WirelessScanner" }, "Wi-Fi connection and internet quality report",
            "$generator | ${r.analyticsVersion} | Test ${cfg.testId}", 0xFFB45309.toInt(), b)
    }

    fun probeChart(r: ConnectionReport, pts: List<ConnectionProbeResult>): ChartSpec.ProbeTimeline {
        val xMax = maxOf(1.0, r.activeSeconds)
        val ok = pts.filter { it.outcome == ProbeOutcome.Ok && it.rttMs != null }
        val yMax = if (ok.isEmpty()) 10.0 else maxOf(10.0, ConnectionAnalyzer.percentile(ok.map { it.rttMs!! }.sorted(), 99.0) ?: 10.0) * 1.15
        val byTarget = LinkedHashMap<String, MutableList<Pair<Double, Double>>>()
        ok.forEach { byTarget.getOrPut(it.target) { ArrayList() }.add(it.elapsedMs / 1000.0 to minOf(it.rttMs!!, yMax)) }
        return ChartSpec.ProbeTimeline(xMax, yMax, byTarget.values.toList(), pts.filter { it.outcome != ProbeOutcome.Ok }.map { it.elapsedMs / 1000.0 })
    }

    fun signalChart(r: ConnectionReport): ChartSpec.SignalTimeline {
        val pts = r.linkSamples.filter { it.rssi != null }
        val yMin = minOf(-90.0, (pts.minOf { it.rssi!! } - 3).toDouble())
        val yMax = maxOf(-30.0, (pts.maxOf { it.rssi!! } + 3).toDouble())
        return ChartSpec.SignalTimeline(maxOf(1.0, r.activeSeconds), yMin, yMax, pts.map { it.elapsedMs / 1000.0 to it.rssi!! },
            r.events.filter { it.type == ConnectionEventType.Roam }.map { it.startElapsedMs / 1000.0 })
    }
}
