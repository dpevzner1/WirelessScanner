package com.wirelessscanner.core.analytics

import com.wirelessscanner.core.domain.*
import java.time.Instant
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.sqrt

/**
 * Port of WirelessScanner.Application/SaturationAnalyzer.cs — SAT-ANALYTICS-3 (SPEC-SAT-001 §6, DR-0004, DR-0012, DR-0013).
 * Pure and deterministic; numbers must equal the C# implementation (checked by parity fixtures).
 */
class SaturationAnalyzer {
    val version get() = ANALYTICS_VERSION

    private data class Pt(val c: SaturationCycle, val o: SaturationObservation)

    fun analyze(
        config: SaturationTestConfig, cycles: List<SaturationCycle>, markers: List<SaturationMarker>,
        startUtc: Instant, endUtc: Instant?, status: SaturationTestStatus, measuredActiveSeconds: Double? = null,
    ): SaturationReport {
        val ordered = cycles.sortedBy { it.sequence }
        val th = config.thresholds
        val intervalMs = config.interval.toMillis().toDouble()
        val activeSeconds = activeSeconds(config, ordered, measuredActiveSeconds)
        val endMs = activeSeconds * 1000.0

        val weightBySeq = HashMap<Long, Double>()
        for (i in ordered.indices) {
            val w = if (i + 1 < ordered.size) (ordered[i + 1].elapsedMs - ordered[i].elapsedMs).toDouble() else minOf(intervalMs, endMs - ordered[i].elapsedMs)
            weightBySeq[ordered[i].sequence] = w.coerceIn(0.0, 2 * intervalMs)
        }

        val keys = ArrayList<String>()
        val series = LinkedHashMap<String, ArrayList<Pt>>()
        for (c in ordered) for (o in c.observations) {
            val k = o.bssid.uppercase()
            val list = series.getOrPut(k) { keys.add(o.bssid); ArrayList() }
            list.add(Pt(c, o))
        }

        val events = ArrayList<SaturationEvent>()
        val targets = ArrayList<SaturationTargetSummary>()
        for (bssid in keys) {
            val (summary, targetEvents) = analyzeTarget(bssid, series[bssid.uppercase()]!!, th, weightBySeq, activeSeconds)
            targets.add(summary); events.addAll(targetEvents)
        }

        val ssids = ArrayList<SaturationSsidSummary>()
        if (config.scope == SaturationScope.SingleSsid || config.scope == SaturationScope.MultipleSsids || config.scope == SaturationScope.Mixed) {
            val ssidTargets = config.targetSsids.toHashSet()
            val groups = LinkedHashMap<String, MutableList<SaturationTargetSummary>>()
            targets.filter { it.ssid.isNotEmpty() && (config.scope != SaturationScope.Mixed || it.ssid in ssidTargets) }
                .forEach { groups.getOrPut(it.ssid) { ArrayList() }.add(it) }
            for ((ssid, g) in groups) {
                val (s, ev) = analyzeSsid(ssid, g.map { it.bssid }, ordered, th)
                ssids.add(s); events.addAll(ev)
            }
        }

        val units = config.targetUnits.map { analyzeUnit(it, ordered) }
        return SaturationReport(
            config, ANALYTICS_VERSION, startUtc, endUtc, activeSeconds, status, health(ordered), targets, ssids,
            events.sortedWith(compareBy<SaturationEvent> { it.startElapsedMs }.thenBy { it.type.ordinal }),
            markers.sortedBy { it.elapsedMs }, ordered, units,
        )
    }

    companion object {
        const val ANALYTICS_VERSION = "SAT-ANALYTICS-3"
        const val UNIT_CONSISTENCY_FINDING_DB = 3.0
        const val SSID_ONLY_OUTAGE_FINDING_PCT = 5.0
        const val MIN_RATE_HOURS = 0.25
        const val MIN_TREND_SPAN_SECONDS = 15.0 * 60

        private fun display(ssid: String) = ssid.ifEmpty { "<Hidden SSID>" }

        fun analyzeUnit(unit: SaturationUnitTarget, ordered: List<SaturationCycle>): SaturationUnitSummary {
            val fresh = ordered.filter { it.status == ScanCycleStatus.Fresh }
            val members = unit.members.map { it.bssid.uppercase() }.toHashSet()
            fun observedIn(c: SaturationCycle): Map<String, Int> {
                val d = LinkedHashMap<String, Int>()
                c.observations.filter { it.bssid.uppercase() in members && it.status == ObservationStatus.Observed && it.rssi != null }
                    .forEach { if (!d.containsKey(it.bssid.uppercase())) d[it.bssid.uppercase()] = it.rssi!! }
                return d
            }
            val perCycle = fresh.map(::observedIn)
            val onAir = perCycle.count { it.isNotEmpty() }
            val availability = if (fresh.isEmpty()) null else 100.0 * onAir / fresh.size
            val bestMean = if (onAir == 0) null else perCycle.filter { it.isNotEmpty() }.map { it.values.max().toDouble() }.average()

            val findings = ArrayList<String>()
            if (unit.confidence == UnitConfidence.Medium || unit.confidence == UnitConfidence.Single)
                findings.add("Grouping confidence ${unit.confidence}: inferred from address patterns only; confirm it in the explorer before relying on unit-level results.")
            if (availability != null && availability < 100)
                findings.add("The access point was completely off air (no member observed) in ${Fmt.f(100 - availability, 1)}% of fresh cycles.")

            val radios = ArrayList<SaturationRadioSummary>()
            val radioGroups = LinkedHashMap<String, MutableList<SaturationUnitMember>>()
            unit.members.forEach { radioGroups.getOrPut(it.radioKey) { ArrayList() }.add(it) }
            val sortedRadios = radioGroups.entries.sortedWith(compareBy<Map.Entry<String, MutableList<SaturationUnitMember>>> { it.value.first().band }.thenBy { it.key })
            for ((key, radio) in sortedRadios) {
                val bssids = radio.map { it.bssid }
                var meanDelta: Double? = null
                var maxDelta: Double? = null
                var worstPair: String? = null
                for (i in bssids.indices) for (j in i + 1 until bssids.size) {
                    val a = bssids[i].uppercase(); val b = bssids[j].uppercase()
                    val deltas = perCycle.filter { it.containsKey(a) && it.containsKey(b) }.map { abs(it[a]!! - it[b]!!).toDouble() }
                    if (deltas.isEmpty()) continue
                    val mean = deltas.average()
                    if (meanDelta == null || mean > meanDelta) { meanDelta = mean; worstPair = "${bssids[i]} / ${bssids[j]}" }
                    maxDelta = maxOf(maxDelta ?: 0.0, deltas.max())
                }
                val outage = LinkedHashMap<String, Double>()
                if (bssids.size > 1) for (b in bssids) {
                    val radioUp = perCycle.filter { d -> bssids.any { x -> !x.equals(b, true) && d.containsKey(x.uppercase()) } }
                    if (radioUp.isEmpty()) continue
                    outage[b] = 100.0 * radioUp.count { !it.containsKey(b.uppercase()) } / radioUp.size
                }
                val ssidOf = radio.associate { it.bssid.uppercase() to it.ssid }
                val label = "${radio.first().band} radio"
                if (meanDelta != null && meanDelta > UNIT_CONSISTENCY_FINDING_DB)
                    findings.add("$label: SSIDs differ by ${Fmt.f(meanDelta, 1)} dB on average ($worstPair); one radio should be within ~1-2 dB, so the grouping may be wrong or the SSIDs use different power settings.")
                outage.entries.filter { it.value >= SSID_ONLY_OUTAGE_FINDING_PCT }.sortedByDescending { it.value }.forEach { (b, pct) ->
                    findings.add("$label: SSID '${display(ssidOf[b.uppercase()]!!)}' ($b) was missing in ${Fmt.f(pct, 1)}% of the cycles in which its radio was on air: an SSID/service issue, not RF coverage.")
                }
                radios.add(SaturationRadioSummary(
                    radioKey = key, band = radio.first().band, bssids = bssids,
                    ssids = radio.map { display(it.ssid) }.distinct(),
                    consistencyMeanDeltaDb = meanDelta?.roundTo(2), consistencyMaxDeltaDb = maxDelta,
                    ssidOnlyOutagePct = outage.mapValues { it.value.roundTo(2) },
                ))
            }
            return SaturationUnitSummary(
                id = unit.id, name = unit.name, confidence = unit.confidence, evidence = unit.evidence,
                bssidCount = members.size, ssidCount = unit.members.map { it.ssid }.distinct().size,
                unitAvailabilityPct = availability?.roundTo(2), bestMeanDbm = bestMean?.roundTo(2), radios = radios, findings = findings,
            )
        }

        fun activeSeconds(config: SaturationTestConfig, ordered: List<SaturationCycle>, measured: Double?): Double {
            val last = if (ordered.isEmpty()) 0.0 else ordered.last().elapsedMs / 1000.0
            if (measured != null && measured >= last) return measured
            val reconstructed = last + config.interval.toMillis() / 1000.0
            val d = config.duration ?: return reconstructed
            return maxOf(last, minOf(reconstructed, d.toMillis() / 1000.0))
        }

        private fun health(cycles: List<SaturationCycle>): SaturationCycleHealth {
            val scanned = cycles.filter { it.status != ScanCycleStatus.Skipped }
            var maxGap = 0.0
            for (i in 1 until cycles.size) maxGap = maxOf(maxGap, (cycles[i].elapsedMs - cycles[i - 1].elapsedMs) / 1000.0)
            val errors = LinkedHashMap<String, Int>()
            cycles.filter { !it.errorCode.isNullOrEmpty() }.forEach { errors[it.errorCode!!] = (errors[it.errorCode] ?: 0) + 1 }
            return SaturationCycleHealth(
                cycles.size, cycles.count { it.status == ScanCycleStatus.Fresh }, cycles.count { it.status == ScanCycleStatus.Stale },
                cycles.count { it.status == ScanCycleStatus.Failed }, cycles.count { it.status == ScanCycleStatus.Skipped },
                if (scanned.isNotEmpty()) scanned.map { it.durationMs }.average() else null,
                if (scanned.isNotEmpty()) scanned.maxOf { it.durationMs } else null, maxGap, errors,
            )
        }

        /** Nearest-rank percentile of an ascending list. */
        fun percentile(ascending: List<Number>, p: Double): Double? {
            if (ascending.isEmpty()) return null
            val rank = ceil(p / 100.0 * ascending.size).toInt().coerceIn(1, ascending.size)
            return ascending[rank - 1].toDouble()
        }

        private fun median(values: Collection<Int>): Double {
            val v = values.sorted()
            val n = v.size
            return if (n % 2 == 1) v[n / 2].toDouble() else (v[n / 2 - 1] + v[n / 2]) / 2.0
        }

        fun grade(score: Double?): String = when {
            score == null -> "n/a"
            score >= 90 -> "A"
            score >= 75 -> "B"
            score >= 60 -> "C"
            score >= 40 -> "D"
            else -> "F"
        }

        /** Text tier for an RSSI value (colour is never the only signal). */
        fun tier(rssi: Double?): String = when {
            rssi == null -> "n/a"
            rssi >= -55 -> "Excellent"
            rssi >= -67 -> "Good"
            rssi >= -70 -> "Fair"
            rssi >= -80 -> "Weak"
            else -> "Unusable"
        }
    }

    private fun analyzeTarget(
        bssid: String, s: List<Pt>, th: SaturationThresholds, weightBySeq: Map<Long, Double>, activeSeconds: Double,
    ): Pair<SaturationTargetSummary, List<SaturationEvent>> {
        val events = ArrayList<SaturationEvent>()
        val observed = s.filter { it.o.status == ObservationStatus.Observed && it.o.rssi != null }
        val ssid = s.map { it.o.ssid }.lastOrNull { it.isNotEmpty() } ?: ""
        val nObserved = observed.size
        val nMissing = s.count { it.o.status == ObservationStatus.Missing }
        val nStale = s.count { it.o.status == ObservationStatus.Stale }
        val nUnknown = s.count { it.o.status == ObservationStatus.Unknown }
        val availability = if (nObserved + nMissing > 0) 100.0 * nObserved / (nObserved + nMissing) else null

        val rssi = observed.map { it.o.rssi!! }
        val sorted = rssi.sorted()
        val mean = if (rssi.isNotEmpty()) rssi.average() else null
        val sd = mean?.let { m -> sqrt(rssi.sumOf { (it - m) * (it - m) } / rssi.size) }

        val variation = ArrayList<Double>()
        for (i in 1 until observed.size) variation.add(abs(observed[i].o.rssi!! - observed[i - 1].o.rssi!!).toDouble())
        val varSorted = variation.sorted()

        fun w(p: Pt) = weightBySeq[p.c.sequence] ?: 0.0
        val totalW = observed.sumOf { w(it) }
        fun pct(limit: Int) = if (totalW > 0) 100.0 * observed.filter { it.o.rssi!! < limit }.sumOf { w(it) } / totalW else 0.0
        fun longest(limit: Int): Double {
            var best = 0.0; var run = 0.0
            for (x in observed) {
                if (x.o.rssi!! < limit) { run += w(x); best = maxOf(best, run) } else run = 0.0
            }
            return best / 1000.0
        }

        events.addAll(detectExcursions(bssid, ssid, observed, th))
        events.addAll(detectDropouts(bssid, ssid, s, th))
        events.addAll(detectChannelChanges(bssid, ssid, s))

        val drops = events.count { it.type == SaturationEventType.Drop }
        val peaks = events.count { it.type == SaturationEventType.Peak }
        val dropouts = events.count { it.type == SaturationEventType.Dropout }
        val chg = events.count { it.type == SaturationEventType.ChannelChange }
        val worstDrop = events.filter { it.type == SaturationEventType.Drop }.mapNotNull { it.magnitudeDb }.maxOrNull()
        val longestDropout = events.filter { it.type == SaturationEventType.Dropout }.map { it.durationSeconds ?: 0.0 }.maxOrNull() ?: 0.0

        var score: Double? = null
        if (nObserved > 0 && sd != null) {
            val hours = maxOf(activeSeconds / 3600.0, MIN_RATE_HOURS)
            val raw = 100 - 4 * sd - 0.5 * (100 - (availability ?: 100.0)) - 3 * drops / hours - 5 * dropouts / hours - 5 * chg / hours
            score = raw.coerceIn(0.0, 100.0).roundTo(1)
        }

        val coList = observed.mapNotNull { it.o.coChannelCount }
        val ovList = observed.mapNotNull { it.o.overlapCount }
        val channels = s.filter { it.o.channel != null }.map { it.o.channel!! to it.o.band }.distinct()
        val primary = mostCommon(s.mapNotNull { it.o.channel })
        val width = mostCommon(s.mapNotNull { it.o.channelWidth })

        fun <T> latest(f: (SaturationObservation) -> T?): T? = s.map { f(it.o) }.lastOrNull { it != null }
        val lq = observed.mapNotNull { it.o.linkQuality }
        val load = observed.filter { it.o.stationCount != null || it.o.channelUtilizationPct != null }
        val stations = load.mapNotNull { it.o.stationCount }
        val util = load.mapNotNull { it.o.channelUtilizationPct }
        val margins = observed.filter { it.o.strongestOverlapRssi != null }.map { it.o.rssi!! - it.o.strongestOverlapRssi!! }.sorted()
        val observedSpan = if (observed.size >= 2) (observed.last().c.elapsedMs - observed.first().c.elapsedMs) / 1000.0 else 0.0
        val bandForDfs = s.map { it.o.band }.lastOrNull { it != null }

        val summary = SaturationTargetSummary(
            bssid = bssid,
            vendor = latest { it.vendor },
            security = latest { it.security },
            phyType = latest { it.phyType },
            beaconIntervalTu = latest { it.beaconIntervalTu },
            centerFrequencyMhz = latest { it.centerFrequencyMhz },
            isDfs = if (primary != null && bandForDfs != null) ChannelMath.isDfs(bandForDfs, primary, width) else null,
            linkQualityMean = if (lq.isNotEmpty()) lq.average() else null,
            linkQualityMin = lq.minOrNull(),
            snrEstimatedMeanDb = mean?.let { it - WirelessScannerConstants.ASSUMED_NOISE_FLOOR_DBM },
            loadAdvertisedPct = if (nObserved > 0) 100.0 * load.size / nObserved else null,
            stationCountMean = if (stations.isNotEmpty()) stations.average() else null,
            stationCountMax = stations.maxOrNull(),
            channelUtilizationMeanPct = if (util.isNotEmpty()) util.average() else null,
            channelUtilizationMaxPct = util.maxOrNull(),
            interferenceMarginMedianDb = if (margins.isNotEmpty()) median(margins) else null,
            interferenceMarginMinDb = margins.firstOrNull()?.toDouble(),
            strongestOverlapRssiMax = observed.mapNotNull { it.o.strongestOverlapRssi }.maxOrNull(),
            observedSpanSeconds = observedSpan,
            ssid = ssid,
            bands = s.mapNotNull { it.o.band }.distinct().joinToString(", "),
            channels = channels.joinToString(", ") { "${it.first}" },
            primaryChannel = primary,
            channelWidth = width,
            cyclesTracked = s.size,
            observed = nObserved, missing = nMissing, stale = nStale, unknown = nUnknown,
            availabilityPct = availability,
            min = sorted.firstOrNull(), max = sorted.lastOrNull(), mean = mean,
            median = percentile(sorted, 50.0), stdDev = sd,
            p5 = percentile(sorted, 5.0), p25 = percentile(sorted, 25.0), p75 = percentile(sorted, 75.0), p95 = percentile(sorted, 95.0),
            iqr = if (sorted.isNotEmpty()) percentile(sorted, 75.0)!! - percentile(sorted, 25.0)!! else null,
            range = if (sorted.isNotEmpty()) sorted.last() - sorted.first() else null,
            variationMean = if (variation.isNotEmpty()) variation.average() else null,
            variationP95 = percentile(varSorted, 95.0),
            variationMax = variation.maxOrNull(),
            trendDbPerHour = if (observedSpan >= MIN_TREND_SPAN_SECONDS) slope(observed) else null,
            pctBelowGood = if (nObserved > 0) pct(th.goodDbm) else null,
            pctBelowFair = if (nObserved > 0) pct(th.fairDbm) else null,
            pctBelowUnusable = if (nObserved > 0) pct(th.unusableDbm) else null,
            longestBelowFairSeconds = longest(th.fairDbm),
            longestBelowUnusableSeconds = longest(th.unusableDbm),
            dropCount = drops, peakCount = peaks, dropoutCount = dropouts, channelChangeCount = chg,
            worstDropDb = worstDrop, longestDropoutSeconds = longestDropout,
            coChannelMean = if (coList.isNotEmpty()) coList.average() else null, coChannelMax = coList.maxOrNull(),
            overlapMean = if (ovList.isNotEmpty()) ovList.average() else null, overlapMax = ovList.maxOrNull(),
            stabilityScore = score, grade = grade(score), tier = tier(mean),
        )
        return summary.copy(findings = findings(summary, th, observedSpan)) to events
    }

    /** GroupBy + OrderByDescending(count) + First: ties resolve to the value seen first (stable sort). */
    private fun mostCommon(values: List<Int>): Int? {
        if (values.isEmpty()) return null
        val counts = LinkedHashMap<Int, Int>()
        values.forEach { counts[it] = (counts[it] ?: 0) + 1 }
        return counts.entries.sortedByDescending { it.value }.first().key
    }

    private fun slope(observed: List<Pt>): Double? {
        if (observed.size < 2) return null
        val xs = observed.map { it.c.elapsedMs / 3_600_000.0 }
        val ys = observed.map { it.o.rssi!!.toDouble() }
        val mx = xs.average(); val my = ys.average()
        val sxx = xs.sumOf { (it - mx) * (it - mx) }
        if (sxx <= 0) return null
        val sxy = xs.indices.sumOf { (xs[it] - mx) * (ys[it] - my) }
        return sxy / sxx
    }

    private fun detectExcursions(bssid: String, ssid: String, observed: List<Pt>, th: SaturationThresholds): List<SaturationEvent> {
        val events = ArrayList<SaturationEvent>()
        val window = ArrayDeque<Int>()
        var open: SaturationEventType? = null
        var baselineAtStart = 0.0
        var startCycle: SaturationCycle? = null
        var extreme = 0
        var extremeCycle: SaturationCycle? = null
        var cyclesInEvent = 0
        var lastCycle: SaturationCycle? = null

        fun trim() { while (window.size > th.baselineWindow) window.removeFirst() }
        fun close(endCycle: SaturationCycle?) {
            val type = open ?: return
            val sc = startCycle ?: return
            val duration = if (endCycle != null) (endCycle.elapsedMs - sc.elapsedMs) / 1000.0 else (lastCycle!!.elapsedMs - sc.elapsedMs) / 1000.0
            val magnitude = if (type == SaturationEventType.Drop) baselineAtStart - extreme else extreme - baselineAtStart
            val word = if (type == SaturationEventType.Drop) "Drop" else "Peak"
            events.add(SaturationEvent(
                type, bssid, ssid, sc.wallUtc, endCycle?.wallUtc, sc.elapsedMs, endCycle?.elapsedMs, duration, cyclesInEvent,
                baselineAtStart, extreme, magnitude,
                if (endCycle != null && extremeCycle != null) (endCycle.elapsedMs - extremeCycle!!.elapsedMs) / 1000.0 else null,
                if (endCycle == null) "$word of ${Fmt.f(magnitude, 0)} dB vs baseline ${Fmt.f(baselineAtStart, 1)} dBm; not recovered by end of test"
                else "$word of ${Fmt.f(magnitude, 0)} dB vs baseline ${Fmt.f(baselineAtStart, 1)} dBm ($extreme dBm), recovered after ${Fmt.f(duration, 0)} s",
            ))
            open = null
        }

        for (p in observed) {
            val c = p.c
            val r = p.o.rssi!!
            lastCycle = c
            if (open == SaturationEventType.Drop) {
                cyclesInEvent++
                if (r < extreme) { extreme = r; extremeCycle = c }
                if (r >= baselineAtStart - th.dropDb / 2) { close(c); window.addLast(r); trim() }
                continue
            }
            if (open == SaturationEventType.Peak) {
                cyclesInEvent++
                if (r > extreme) { extreme = r; extremeCycle = c }
                if (r <= baselineAtStart + th.peakDb / 2) { close(c); window.addLast(r); trim() }
                continue
            }
            if (window.size >= th.minBaselineSamples) {
                val baseline = median(window)
                if (r <= baseline - th.dropDb || r >= baseline + th.peakDb) {
                    open = if (r <= baseline - th.dropDb) SaturationEventType.Drop else SaturationEventType.Peak
                    baselineAtStart = baseline; startCycle = c; extreme = r; extremeCycle = c; cyclesInEvent = 1
                    continue
                }
            }
            window.addLast(r); trim()
        }
        close(null)
        return events
    }

    private fun detectDropouts(bssid: String, ssid: String, s: List<Pt>, th: SaturationThresholds): List<SaturationEvent> {
        val events = ArrayList<SaturationEvent>()
        var first: SaturationCycle? = null
        var run = 0
        for (p in s) {
            if (p.o.status == ObservationStatus.Missing) {
                if (first == null) first = p.c
                run++
            } else if (p.o.status == ObservationStatus.Observed) {
                val f = first
                if (run >= th.dropoutMissedCycles && f != null) {
                    val d = (p.c.elapsedMs - f.elapsedMs) / 1000.0
                    events.add(SaturationEvent(SaturationEventType.Dropout, bssid, ssid, f.wallUtc, p.c.wallUtc, f.elapsedMs, p.c.elapsedMs, d, run,
                        null, null, null, null, "Not seen in $run consecutive fresh scans (${Fmt.f(d, 0)} s)"))
                }
                first = null; run = 0
            }
        }
        val f = first
        if (run >= th.dropoutMissedCycles && f != null) {
            val last = s.last().c
            val d = (last.elapsedMs - f.elapsedMs) / 1000.0
            events.add(SaturationEvent(SaturationEventType.Dropout, bssid, ssid, f.wallUtc, null, f.elapsedMs, null, d, run,
                null, null, null, null, "Not seen in the last $run consecutive fresh scans; not recovered by end of test"))
        }
        return events
    }

    private fun detectChannelChanges(bssid: String, ssid: String, s: List<Pt>): List<SaturationEvent> {
        val events = ArrayList<SaturationEvent>()
        var prev: Triple<Int, String?, Int?>? = null
        for (p in s) {
            val o = p.o
            if (!(o.status == ObservationStatus.Observed || o.status == ObservationStatus.Stale)) continue
            val ch = o.channel ?: continue
            val pv = prev
            if (pv != null && (pv.first != ch || pv.second != o.band)) {
                val widthNote = if (pv.third != o.channelWidth) ", width ${pv.third?.toString() ?: "?"}→${o.channelWidth?.toString() ?: "?"} MHz" else ""
                events.add(SaturationEvent(SaturationEventType.ChannelChange, bssid, ssid, p.c.wallUtc, p.c.wallUtc, p.c.elapsedMs, p.c.elapsedMs, 0.0, 1,
                    null, o.rssi, null, null, "Channel ${pv.first} (${pv.second ?: ""}) → $ch (${o.band ?: ""})$widthNote"))
            }
            prev = Triple(ch, o.band, o.channelWidth)
        }
        return events
    }

    private fun analyzeSsid(ssid: String, bssids: List<String>, cycles: List<SaturationCycle>, th: SaturationThresholds): Pair<SaturationSsidSummary, List<SaturationEvent>> {
        val set = bssids.map { it.uppercase() }.toHashSet()
        val events = ArrayList<SaturationEvent>()
        val best = ArrayList<Int>()
        var eligible = 0; var available = 0
        var current: String? = null
        var currentRssi = 0
        for (c in cycles) {
            val obs = c.observations.filter { it.bssid.uppercase() in set }
            val seen = obs.filter { it.status == ObservationStatus.Observed && it.rssi != null }
            if (seen.isNotEmpty() || (c.status == ScanCycleStatus.Fresh && obs.any { it.status == ObservationStatus.Missing })) eligible++
            if (seen.isEmpty()) continue
            available++
            val top = seen.sortedByDescending { it.rssi }.first()
            val tr = top.rssi!!
            best.add(tr)
            val cur = current
            val currentObs = if (cur == null) null else seen.firstOrNull { it.bssid.equals(cur, true) }
            if (cur == null) { current = top.bssid; currentRssi = tr; continue }
            if (top.bssid.equals(cur, true)) { currentRssi = tr; continue }
            val previousGone = currentObs == null
            val stronger = currentObs != null && tr >= currentObs.rssi!! + th.strongestChangeHysteresisDb
            if (previousGone || stronger) {
                val fromRssi = currentObs?.rssi ?: currentRssi
                events.add(SaturationEvent(SaturationEventType.StrongestBssidChange, ssid, ssid, c.wallUtc, c.wallUtc, c.elapsedMs, c.elapsedMs, 0.0, 1,
                    null, tr, (tr - fromRssi).toDouble(), null,
                    if (previousGone) "Strongest BSSID $cur not observed; now ${top.bssid} ($tr dBm)"
                    else "Strongest BSSID $cur ($fromRssi dBm) → ${top.bssid} ($tr dBm)"))
                current = top.bssid; currentRssi = tr
            } else if (currentObs != null) currentRssi = currentObs.rssi!!
        }
        return SaturationSsidSummary(ssid, bssids.size, if (eligible > 0) 100.0 * available / eligible else null,
            if (best.isNotEmpty()) best.average() else null, best.minOrNull(), best.maxOrNull(), events.size) to events
    }

    private fun findings(t: SaturationTargetSummary, th: SaturationThresholds, observedSpanSeconds: Double): List<String> {
        val f = ArrayList<String>()
        if (t.observed == 0) { f.add("Never observed fresh during the test."); return f }
        if (t.dropoutCount > 0) f.add("Disappeared ${t.dropoutCount} time(s); longest outage ${Fmt.f(t.longestDropoutSeconds, 0)} s.")
        if (t.dropCount > 0) f.add("${t.dropCount} drop(s) of ≥ ${Fmt.f(th.dropDb, 0)} dB below baseline; worst ${Fmt.f(t.worstDropDb, 0)} dB.")
        if (t.peakCount > 0) f.add("${t.peakCount} peak(s) of ≥ ${Fmt.f(th.peakDb, 0)} dB above baseline.")
        if ((t.pctBelowUnusable ?: 0.0) > 0) f.add("Below the unusable threshold (${th.unusableDbm} dBm) for ${Fmt.f(t.pctBelowUnusable, 1)}% of observed time; longest continuous ${Fmt.f(t.longestBelowUnusableSeconds, 0)} s.")
        else if ((t.pctBelowFair ?: 0.0) > 0) f.add("Below the fair threshold (${th.fairDbm} dBm, i.e. Weak or worse) for ${Fmt.f(t.pctBelowFair, 1)}% of observed time; longest continuous ${Fmt.f(t.longestBelowFairSeconds, 0)} s.")
        val im = t.interferenceMarginMinDb
        if (im != null && im < 10) f.add("Interference margin as low as ${Fmt.f(im, 0)} dB (strongest overlapping neighbour ${Fmt.s(t.strongestOverlapRssiMax)} dBm); aim for ≥ 20 dB.")
        val cu = t.channelUtilizationMaxPct
        if (cu != null && cu >= 50) f.add("AP reports channel busy up to ${Fmt.f(cu, 0)}% of the time (BSS Load).")
        if (t.channelChangeCount > 0) f.add("Changed channel ${t.channelChangeCount} time(s) (channels ${t.channels}).")
        val slope = t.trendDbPerHour
        if (slope != null && abs(slope) >= 2 && t.observed >= 10 && observedSpanSeconds >= MIN_TREND_SPAN_SECONDS)
            f.add("Trending ${if (slope < 0) "down" else "up"} ${Fmt.f(abs(slope), 1)} dB/hour.")
        val a = t.availabilityPct
        if (a != null && a < 99) f.add("Availability ${Fmt.f(a, 1)}% of fresh scans.")
        if (f.isEmpty()) f.add("Stable: mean ${Fmt.f(t.mean, 1)} dBm (${t.tier}), σ ${Fmt.f(t.stdDev, 1)} dB, no events at current thresholds.")
        return f
    }
}
