package com.wirelessscanner.core.analytics

import com.wirelessscanner.core.domain.*
import java.time.Instant
import kotlin.math.abs
import kotlin.math.ceil

/**
 * Port of WirelessScanner.Application/ConnectionAnalyzer.cs — CONN-ANALYTICS-1 (DR-0014, CONNECTION_TEST_SPEC.md).
 * Pure and deterministic; numbers must equal the C# implementation (checked by parity fixtures).
 */
class ConnectionAnalyzer {
    val version get() = ANALYTICS_VERSION

    private data class Round(val seq: Long, val ms: Long, val wall: Instant, val ok: Boolean)

    fun analyze(
        config: ConnectionTestConfig, probes: List<ConnectionProbeResult>, link: List<ConnectionLinkSample>,
        speed: List<SpeedTestResult>, twoWay: List<TwoWayResult>, markers: List<ConnectionMarker>,
        startUtc: Instant, endUtc: Instant?, status: ConnectionTestStatus, activeSeconds: Double,
    ): ConnectionReport {
        val ordered = probes.sortedWith(compareBy<ConnectionProbeResult> { it.elapsedMs }.thenBy { it.sequence })
        val linkOrdered = link.sortedWith(compareBy<ConnectionLinkSample> { it.elapsedMs }.thenBy { it.sequence })
        val sent = ordered.filter { it.outcome != ProbeOutcome.Skipped }

        var events = ArrayList<ConnectionEvent>()
        val targets = ArrayList<ConnectionSegmentSummary>()
        val byTarget = LinkedHashMap<Triple<ConnectionSegment, String, ProbeMethod>, MutableList<ConnectionProbeResult>>()
        sent.forEach { byTarget.getOrPut(Triple(it.segment, it.target, it.method)) { ArrayList() }.add(it) }
        for ((key, list) in byTarget.entries.sortedWith(compareBy<Map.Entry<Triple<ConnectionSegment, String, ProbeMethod>, MutableList<ConnectionProbeResult>>> { it.key.first.ordinal }
            .thenBy { it.key.second }.thenBy { it.key.third.ordinal })) {
            val spikes = spikes(list)
            targets.add(stats(key.first, key.second, key.third, list, list.map { listOf(it) }).copy(spikes = spikes.size))
            events.addAll(spikes)
        }

        var segments = ArrayList<ConnectionSegmentSummary>()
        val failedRounds = LinkedHashMap<ConnectionSegment, List<Round>>()
        for (seg in ConnectionSegment.entries) {
            if (seg == ConnectionSegment.WifiLink || seg == ConnectionSegment.LanTwoWay) continue
            val pm = primaryMethod(seg, sent)
            val segProbes = sent.filter { it.segment == seg && it.method == pm }
            if (segProbes.isEmpty()) continue
            val roundsMap = LinkedHashMap<Long, MutableList<ConnectionProbeResult>>()
            segProbes.forEach { roundsMap.getOrPut(it.sequence) { ArrayList() }.add(it) }
            val rounds = roundsMap.entries.sortedBy { it.key }.map { it.value.toList() }
            failedRounds[seg] = rounds.map { r -> Round(r[0].sequence, r[0].elapsedMs, r[0].wallUtc, r.any { it.outcome == ProbeOutcome.Ok }) }
            val target = if (seg == ConnectionSegment.Internet) segProbes.map { it.target }.distinct().joinToString(" | ") else segProbes[0].target
            segments.add(stats(seg, target, segProbes[0].method, segProbes, rounds).copy(spikes = targets.filter { it.segment == seg }.sumOf { it.spikes }))
        }

        events.addAll(linkEvents(config, linkOrdered))

        for ((seg, rounds) in failedRounds) {
            for ((startIdx, endIdx) in runs(rounds.map { !it.ok }, OUTAGE_ROUNDS)) {
                val first = rounds[startIdx]
                val after = if (endIdx + 1 < rounds.size) rounds[endIdx + 1] else null
                val endMs = after?.ms ?: rounds[endIdx].ms
                val (attr, why) = attribute(seg, first.ms, endMs, failedRounds, linkOrdered)
                val n = endIdx - startIdx + 1
                val detail = "${name(seg)} unreachable for $n consecutive probes" + (if (after == null) " (still failing at the end)" else "") + ". $why"
                events.add(ConnectionEvent(ConnectionEventType.Outage, seg, attr, first.wall, after?.wall, first.ms, after?.ms, (endMs - first.ms) / 1000.0, n, null, detail))
            }
        }
        segments = ArrayList(segments.map { s ->
            val o = events.filter { it.type == ConnectionEventType.Outage && it.segment == s.segment }
            s.copy(outages = o.size, longestOutageSeconds = if (o.isEmpty()) null else o.mapNotNull { it.durationSeconds }.maxOrNull())
        })

        val before = events.toList()
        events = ArrayList(before.map { if (it.type == ConnectionEventType.LatencySpike) attributeSpike(it, before) else it })

        for (s in speed) events.add(ConnectionEvent(ConnectionEventType.SpeedTest, null, null, s.startUtc, null, s.elapsedMs, null, null, null, null,
            if (s.error != null) "Speed test failed: ${s.error}"
            else "Speed test (${s.provider}): down ${f1(s.downloadMbps)} Mbps, up ${f1(s.uploadMbps)} Mbps, latency +${f1(s.latencyIncreaseMs)} ms under load (grade ${s.bufferbloatGrade}). Regular probes paused meanwhile."))
        for (t in twoWay) events.add(ConnectionEvent(ConnectionEventType.TwoWayTest, ConnectionSegment.LanTwoWay, null, t.startUtc, null, t.elapsedMs, null, t.seconds, t.sent, t.rttP95Ms,
            if (t.error != null) "Two-way test failed: ${t.error}"
            else "Two-way test (${t.responder}): loss up ${f1(t.upLossPct)} % / down ${f1(t.downLossPct)} %, jitter up ${f1(t.upJitterMs)} / down ${f1(t.downJitterMs)} ms, MOS up ${f2(t.mosUp)} / down ${f2(t.mosDown)}."))

        val sortedEvents = events.sortedWith(compareBy<ConnectionEvent> { it.startElapsedMs }.thenBy { it.type.ordinal })
        val problems = LinkedHashMap<ConnectionSegment, Int>()
        sortedEvents.filter { it.attributedTo != null }.forEach { problems[it.attributedTo!!] = (problems[it.attributedTo] ?: 0) + 1 }

        val internet = segments.firstOrNull { it.segment == ConnectionSegment.Internet }
        val dns = segments.firstOrNull { it.segment == ConnectionSegment.Dns }
        val (r, mos) = voice(internet)
        val verdicts = verdicts(internet, dns)
        val linkSummary = linkSummary(linkOrdered, sortedEvents)
        val gaps = linkOrdered.filter { it.gapMs != null }
        val totals = ConnectionTotals(sent.size, sent.count { it.outcome == ProbeOutcome.Ok }, sent.count { it.outcome != ProbeOutcome.Ok },
            linkOrdered.size, gaps.size, (gaps.sumOf { it.gapMs!! } / 1000.0).roundTo(3))

        return ConnectionReport(config, ANALYTICS_VERSION, startUtc, endUtc, activeSeconds, status, segments, targets, sortedEvents, problems,
            verdicts, r, mos, linkSummary, speed.sortedBy { it.elapsedMs }, twoWay.sortedBy { it.elapsedMs }, markers.sortedBy { it.elapsedMs },
            totals, ordered, linkOrdered)
    }

    companion object {
        const val ANALYTICS_VERSION = "CONN-ANALYTICS-1"
        const val OUTAGE_ROUNDS = 3
        const val BASELINE_WINDOW = 30
        const val MIN_BASELINE = 10
        const val SPIKE_FACTOR = 3.0
        const val SPIKE_MIN_INCREASE_MS = 100.0
        private val UPSTREAM = listOf(ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet)

        data class VerdictThreshold(val use: String, val goodP95: Double, val goodJitter: Double, val goodLoss: Double, val fairP95: Double, val fairJitter: Double, val fairLoss: Double)
        val VERDICT_THRESHOLDS = listOf(
            VerdictThreshold("Browsing", 200.0, 100.0, 2.0, 500.0, 200.0, 5.0),
            VerdictThreshold("Streaming video", 300.0, 100.0, 1.0, 800.0, 200.0, 3.0),
            VerdictThreshold("Video calls", 150.0, 30.0, 1.0, 300.0, 50.0, 3.0),
            VerdictThreshold("Online gaming", 60.0, 15.0, 0.5, 100.0, 30.0, 1.5),
        )

        fun primaryMethod(seg: ConnectionSegment, sent: List<ConnectionProbeResult>): ProbeMethod {
            val ofSeg = sent.filter { it.segment == seg }
            if (seg == ConnectionSegment.Internet && ofSeg.any { it.method == ProbeMethod.Tcp }) return ProbeMethod.Tcp
            if (ofSeg.isEmpty()) return ProbeMethod.Icmp // C# default(ProbeMethod)
            val counts = LinkedHashMap<ProbeMethod, Int>()
            ofSeg.forEach { counts[it.method] = (counts[it.method] ?: 0) + 1 }
            return counts.entries.sortedWith(compareByDescending<Map.Entry<ProbeMethod, Int>> { it.value }.thenBy { it.key.ordinal }).first().key
        }

        internal fun stats(seg: ConnectionSegment, target: String, method: ProbeMethod, list: List<ConnectionProbeResult>, rounds: List<List<ConnectionProbeResult>>): ConnectionSegmentSummary {
            val ok = list.filter { it.outcome == ProbeOutcome.Ok && it.rttMs != null }
            val sorted = ok.map { it.rttMs!! }.sorted()
            val deltas = ArrayList<Double>()
            val byTarget = LinkedHashMap<String, MutableList<ConnectionProbeResult>>()
            ok.forEach { byTarget.getOrPut(it.target) { ArrayList() }.add(it) }
            for ((_, t) in byTarget) {
                val series = t.sortedBy { it.elapsedMs }.map { it.rttMs!! }
                for (i in 1 until series.size) deltas.add(abs(series[i] - series[i - 1]))
            }
            val sentN = list.size
            val failed = sentN - ok.size
            val reasons = java.util.TreeMap<String, Int>()
            list.filter { it.outcome != ProbeOutcome.Ok }.forEach { reasons[it.outcome.name] = (reasons[it.outcome.name] ?: 0) + 1 }
            return ConnectionSegmentSummary(
                segment = seg, target = target, method = method, sent = sentN, ok = ok.size, failed = failed,
                failureReasons = LinkedHashMap(reasons),
                lossPct = if (sentN == 0) null else rnd(100.0 * failed / sentN),
                availabilityPct = if (rounds.isEmpty()) null else rnd(100.0 * rounds.count { r -> r.any { it.outcome == ProbeOutcome.Ok } } / rounds.size),
                rttMin = sorted.firstOrNull()?.let(::rnd),
                rttP50 = percentile(sorted, 50.0), rttP95 = percentile(sorted, 95.0), rttP99 = percentile(sorted, 99.0),
                rttMax = sorted.lastOrNull()?.let(::rnd),
                rttMean = if (sorted.isEmpty()) null else rnd(sorted.average()),
                jitterMs = if (deltas.isEmpty()) null else rnd(deltas.average()),
                delayVariationP95Ms = percentile(deltas.sorted(), 95.0),
            )
        }

        fun percentile(sorted: List<Double>, p: Double): Double? {
            if (sorted.isEmpty()) return null
            val rank = ceil(p / 100.0 * sorted.size).toInt()
            return rnd(sorted[rank.coerceIn(1, sorted.size) - 1])
        }

        private fun rnd(v: Double) = v.roundTo(3)
        private fun f1(v: Double?) = if (v == null) "n/a" else Fmt.f(v, 1)
        private fun f2(v: Double?) = if (v == null) "n/a" else Fmt.f(v, 2)

        fun name(s: ConnectionSegment) = when (s) {
            ConnectionSegment.WifiLink -> "Wi-Fi link"
            ConnectionSegment.Gateway -> "Router (gateway)"
            ConnectionSegment.IspHop -> "ISP hop"
            ConnectionSegment.Internet -> "Internet"
            ConnectionSegment.Dns -> "DNS"
            ConnectionSegment.Web -> "Web (HTTPS)"
            else -> "Wi-Fi two-way (LAN responder)"
        }

        internal fun runs(values: List<Boolean>, minLength: Int): List<Pair<Int, Int>> {
            val out = ArrayList<Pair<Int, Int>>()
            var i = 0
            while (i < values.size) {
                if (!values[i]) { i++; continue }
                var j = i
                while (j + 1 < values.size && values[j + 1]) j++
                if (j - i + 1 >= minLength) out.add(i to j)
                i = j + 1
            }
            return out
        }

        /** Simplified E-model (ITU-T G.107 derived): an estimate, never a measurement of call quality. */
        internal fun voice(internet: ConnectionSegmentSummary?): Pair<Double?, Double?> {
            val rtt = internet?.rttMean ?: return null to null
            val jitter = internet.jitterMs ?: return null to null
            val loss = internet.lossPct ?: return null to null
            val eff = rtt / 2 + 2 * jitter + 10
            var r = if (eff < 160) 93.2 - eff / 40 else 93.2 - (eff - 120) / 10
            r = (r - 2.5 * loss).coerceIn(0.0, 100.0)
            val mos = (1 + 0.035 * r + 0.000007 * r * (r - 60) * (100 - r)).coerceIn(1.0, 4.5)
            return r.roundTo(1) to mos.roundTo(2)
        }

        internal fun verdicts(internet: ConnectionSegmentSummary?, dns: ConnectionSegmentSummary?): List<UseVerdict> {
            val list = ArrayList<UseVerdict>()
            for (t in VERDICT_THRESHOLDS) {
                val p95 = internet?.rttP95; val j = internet?.jitterMs; val loss = internet?.lossPct
                if (p95 == null || j == null || loss == null) { list.add(UseVerdict(t.use, "NOT TESTED", "Internet segment not measured.")); continue }
                var rating = if (p95 <= t.goodP95 && j <= t.goodJitter && loss <= t.goodLoss) "GOOD"
                else if (p95 <= t.fairP95 && j <= t.fairJitter && loss <= t.fairLoss) "FAIR" else "POOR"
                val d = dns?.rttP95
                if (t.use == "Browsing" && d != null && d > 300 && rating == "GOOD") rating = "FAIR"
                val reason = "round trip p95 ${Fmt.f(p95, 0)} ms (good ≤ ${Fmt.f(t.goodP95, 0)}), jitter ${Fmt.f(j, 1)} ms (≤ ${Fmt.f(t.goodJitter, 0)}), loss ${Fmt.hash(loss, 2)} % (≤ ${Fmt.hash(t.goodLoss, 2)})" +
                    (if (t.use == "Browsing" && d != null) ", DNS p95 ${Fmt.f(d, 0)} ms" else "")
                list.add(UseVerdict(t.use, rating, reason))
            }
            return list
        }
    }

    private fun spikes(list: List<ConnectionProbeResult>): List<ConnectionEvent> {
        val result = ArrayList<ConnectionEvent>()
        val ok = list.filter { it.outcome == ProbeOutcome.Ok && it.rttMs != null }.sortedBy { it.elapsedMs }
        val window = ArrayList<Double>()
        var start: ConnectionProbeResult? = null
        var last: ConnectionProbeResult? = null
        var peak = 0.0; var baseAtStart = 0.0; var count = 0
        fun close() {
            val s = start ?: return
            val l = last!!
            result.add(ConnectionEvent(ConnectionEventType.LatencySpike, s.segment, s.segment, s.wallUtc, l.wallUtc, s.elapsedMs, l.elapsedMs,
                (l.elapsedMs - s.elapsedMs) / 1000.0, count, rnd(peak),
                "${name(s.segment)} ${s.target}: round trip up to ${Fmt.f(peak, 0)} ms (normal ${Fmt.f(baseAtStart, 0)} ms) over $count probe(s)."))
            start = null
        }
        for (p in ok) {
            val rtt = p.rttMs!!
            var spike = false
            var baseline = 0.0
            if (window.size >= MIN_BASELINE) {
                val s = window.sorted()
                baseline = if (s.size % 2 == 1) s[s.size / 2] else (s[s.size / 2 - 1] + s[s.size / 2]) / 2
                spike = rtt > maxOf(SPIKE_FACTOR * baseline, baseline + SPIKE_MIN_INCREASE_MS)
            }
            if (spike) {
                if (start == null) { start = p; peak = rtt; baseAtStart = baseline; count = 0 }
                count++
                peak = maxOf(peak, rtt)
                last = p
            } else {
                close()
                window.add(rtt)
                if (window.size > BASELINE_WINDOW) window.removeAt(0)
            }
        }
        close()
        return result
    }

    private fun linkEvents(config: ConnectionTestConfig, link: List<ConnectionLinkSample>): List<ConnectionEvent> {
        val out = ArrayList<ConnectionEvent>()
        val tick = maxOf(1, config.tickMilliseconds)
        var prev: ConnectionLinkSample? = null
        var downSince: ConnectionLinkSample? = null
        for (l in link) {
            val gap = l.gapMs
            if (gap != null && gap >= 3L * tick)
                out.add(ConnectionEvent(ConnectionEventType.SleepGap, null, null, l.wallUtc.minusMillis(gap), l.wallUtc, l.elapsedMs - gap, l.elapsedMs,
                    gap / 1000.0, null, null, "Nothing was measured for ${Fmt.f(gap / 1000.0, 1)} s (PC asleep, suspended or stalled). This time is not counted as an outage."))
            val p = prev
            if (p != null) {
                if (p.connected && !l.connected) downSince = l
                val ds = downSince
                if (!p.connected && l.connected && ds != null) {
                    out.add(ConnectionEvent(ConnectionEventType.Disconnect, ConnectionSegment.WifiLink, ConnectionSegment.WifiLink, ds.wallUtc, l.wallUtc,
                        ds.elapsedMs, l.elapsedMs, (l.elapsedMs - ds.elapsedMs) / 1000.0, null, null,
                        "Wi-Fi disconnected for ${Fmt.f((l.elapsedMs - ds.elapsedMs) / 1000.0, 1)} s" + (if (l.ssid != null) ", reconnected to ${l.ssid}" else "") + "."))
                    downSince = null
                }
                if (p.connected && l.connected && p.ssid != null && l.ssid != null && p.ssid != l.ssid)
                    out.add(ConnectionEvent(ConnectionEventType.NetworkChange, ConnectionSegment.WifiLink, null, l.wallUtc, null, l.elapsedMs, null, null, null, null,
                        "The PC moved to another network: ${p.ssid} → ${l.ssid}. Results after this point describe the new network."))
                else if (p.connected && l.connected && p.bssid != null && l.bssid != null && !p.bssid.equals(l.bssid, true))
                    out.add(ConnectionEvent(ConnectionEventType.Roam, ConnectionSegment.WifiLink, null, l.wallUtc, null, l.elapsedMs, null, null, null, null,
                        "Roamed ${p.bssid} → ${l.bssid}" + (if (p.rssi != null && l.rssi != null) " (signal ${p.rssi} → ${l.rssi} dBm)" else "") +
                            (if (p.channel != l.channel) ", channel ${Fmt.s(p.channel)} → ${Fmt.s(l.channel)}" else "") + "."))
                if (l.connected && p.connected && ((p.localIp != null && l.localIp != null && p.localIp != l.localIp) || (p.gateway != null && l.gateway != null && p.gateway != l.gateway)))
                    out.add(ConnectionEvent(ConnectionEventType.IpChange, ConnectionSegment.WifiLink, null, l.wallUtc, null, l.elapsedMs, null, null, null, null,
                        "Address changed: ${Fmt.s(p.localIp)} via ${Fmt.s(p.gateway)} → ${Fmt.s(l.localIp)} via ${Fmt.s(l.gateway)}."))
            }
            prev = l
        }
        val ds = downSince; val p = prev
        if (ds != null && p != null)
            out.add(ConnectionEvent(ConnectionEventType.Disconnect, ConnectionSegment.WifiLink, ConnectionSegment.WifiLink, ds.wallUtc, null, ds.elapsedMs, null,
                (p.elapsedMs - ds.elapsedMs) / 1000.0, null, null, "Wi-Fi disconnected and still disconnected at the end of the test."))
        return out
    }

    private fun attribute(seg: ConnectionSegment, fromMs: Long, toMs: Long, rounds: Map<ConnectionSegment, List<Round>>, link: List<ConnectionLinkSample>): Pair<ConnectionSegment?, String> {
        fun failingDuring(s: ConnectionSegment): Boolean {
            val r = rounds[s] ?: return false
            val inWin = r.filter { it.ms >= fromMs - 1000 && it.ms <= toMs }
            return inWin.isNotEmpty() && inWin.count { !it.ok } * 2 >= inWin.size
        }
        if (link.any { it.elapsedMs >= fromMs - 1000 && it.elapsedMs <= toMs && !it.connected }) return ConnectionSegment.WifiLink to "Cause: the Wi-Fi link itself was down."
        if (seg == ConnectionSegment.IspHop && rounds.containsKey(ConnectionSegment.Internet) && !failingDuring(ConnectionSegment.Internet))
            return null to "The internet stayed reachable, so this is most likely the ISP router rate-limiting probe replies, not a fault."
        if (seg == ConnectionSegment.Dns) return when {
            failingDuring(ConnectionSegment.Gateway) -> ConnectionSegment.Gateway to "Cause: the router was unreachable at the same time."
            failingDuring(ConnectionSegment.Internet) -> ConnectionSegment.Internet to "Cause: the internet was unreachable at the same time."
            else -> ConnectionSegment.Dns to "The internet was reachable, so name resolution itself failed."
        }
        if (seg == ConnectionSegment.Web) return when {
            failingDuring(ConnectionSegment.Gateway) -> ConnectionSegment.Gateway to "Cause: the router was unreachable at the same time."
            failingDuring(ConnectionSegment.Internet) -> ConnectionSegment.Internet to "Cause: the internet was unreachable at the same time."
            failingDuring(ConnectionSegment.Dns) -> ConnectionSegment.Dns to "Cause: name resolution was failing at the same time."
            else -> ConnectionSegment.Web to "Router, internet and DNS were fine, so the web service itself failed."
        }
        for (up in UPSTREAM) {
            if (up == ConnectionSegment.WifiLink) continue
            if (up.ordinal >= seg.ordinal) break
            if (up == ConnectionSegment.IspHop && rounds.containsKey(ConnectionSegment.IspHop) && failingDuring(ConnectionSegment.IspHop))
                return ConnectionSegment.IspHop to "Cause: the ISP hop stopped answering at the same time (the router was fine)."
            if (up == ConnectionSegment.Gateway && failingDuring(ConnectionSegment.Gateway))
                return ConnectionSegment.Gateway to "Cause: the router was unreachable at the same time (Wi-Fi association was up)."
        }
        return seg to when (seg) {
            ConnectionSegment.Gateway -> "The Wi-Fi association was up, so the router or the local network failed."
            ConnectionSegment.Internet -> "Router (and ISP hop, when measured) were fine, so the problem is beyond them (ISP or upstream)."
            else -> ""
        }
    }

    private fun attributeSpike(e: ConnectionEvent, all: List<ConnectionEvent>): ConnectionEvent {
        val from = e.startElapsedMs - 2000
        val to = (e.endElapsedMs ?: e.startElapsedMs) + 2000
        fun overlaps(s: ConnectionSegment) = all.any { it.type == ConnectionEventType.LatencySpike && it.segment == s && it.startElapsedMs <= to && (it.endElapsedMs ?: it.startElapsedMs) >= from }
        val roamNear = all.any { (it.type == ConnectionEventType.Roam || it.type == ConnectionEventType.Disconnect) && it.startElapsedMs >= e.startElapsedMs - 10000 && it.startElapsedMs <= to }
        val (a, why) = when (e.segment) {
            ConnectionSegment.Gateway -> if (roamNear) ConnectionSegment.WifiLink to " Local: right after a roam/reconnect." else ConnectionSegment.Gateway to " Local: Wi-Fi or router (the router itself answered slowly)."
            ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web, ConnectionSegment.IspHop -> when {
                overlaps(ConnectionSegment.Gateway) -> (if (roamNear) ConnectionSegment.WifiLink else ConnectionSegment.Gateway) to " Local: the router was slow at the same time."
                e.segment != ConnectionSegment.IspHop && overlaps(ConnectionSegment.IspHop) -> ConnectionSegment.IspHop to " ISP: the ISP hop was slow at the same time."
                else -> e.segment to (if (e.segment == ConnectionSegment.Internet) " Upstream: router and ISP hop were normal." else "")
            }
            else -> e.segment to ""
        }
        return e.copy(attributedTo = a, detail = e.detail + why)
    }

    private fun linkSummary(link: List<ConnectionLinkSample>, events: List<ConnectionEvent>): ConnectionLinkSummary {
        val connected = link.filter { it.connected }
        val rssi = connected.mapNotNull { it.rssi }
        val rx = connected.mapNotNull { it.rxRateMbps }
        val tx = connected.mapNotNull { it.txRateMbps }
        var dTx = 0L; var dRetry = 0L; var dFail = 0L; var dRx = 0L; var dFcs = 0L
        var prev: WlanFrameCounters? = null
        fun d(a: Long, b: Long) = if (b >= a) b - a else 0
        for (c in link.mapNotNull { it.counters }) {
            prev?.let { p -> dTx += d(p.txFrames, c.txFrames); dRetry += d(p.txRetries, c.txRetries); dFail += d(p.txFailed, c.txFailed); dRx += d(p.rxFrames, c.rxFrames); dFcs += d(p.fcsErrors, c.fcsErrors) }
            prev = c
        }
        val counters = prev != null && (dTx > 0 || dRx > 0)
        val rates = ArrayList<Double>()
        for (i in 1 until link.size) {
            val a = link[i - 1]; val b = link[i]
            val ai = a.ifBytesIn; val ao = a.ifBytesOut; val bi = b.ifBytesIn; val bo = b.ifBytesOut
            if (ai != null && ao != null && bi != null && bo != null && b.elapsedMs > a.elapsedMs && bi >= ai && bo >= ao)
                rates.add((bi - ai + bo - ao) * 8.0 / (b.elapsedMs - a.elapsedMs))
        }
        val bssids = LinkedHashMap<String, String>()
        connected.mapNotNull { it.bssid }.forEach { bssids.putIfAbsent(it.uppercase(), it) }
        return ConnectionLinkSummary(
            samples = link.size,
            roams = events.count { it.type == ConnectionEventType.Roam },
            disconnects = events.count { it.type == ConnectionEventType.Disconnect },
            bssids = bssids.values.toList(),
            rssiMean = if (rssi.isEmpty()) null else rnd(rssi.average()),
            rssiMin = rssi.minOrNull(),
            rxRateMean = if (rx.isEmpty()) null else rnd(rx.average()),
            txRateMean = if (tx.isEmpty()) null else rnd(tx.average()),
            countersAvailable = counters,
            retryPct = if (counters && dTx > 0) rnd(100.0 * dRetry / dTx) else null,
            txFailedPct = if (counters && dTx > 0) rnd(100.0 * dFail / dTx) else null,
            fcsErrorPct = if (counters && dRx + dFcs > 0) rnd(100.0 * dFcs / (dRx + dFcs)) else null,
            pcTrafficMeanKbps = if (rates.isEmpty()) null else rnd(rates.average()),
            pcTrafficMaxKbps = rates.maxOrNull()?.let(::rnd),
        )
    }
}
