package com.wirelessscanner.core.analytics

import com.wirelessscanner.core.domain.*

/** Port of SaturationTargetTracker (SPEC-SAT-001 §5): which BSSIDs a saturation test follows. */
class SaturationTargetTracker(private val config: SaturationTestConfig) {
    private val order = ArrayList<String>()
    private val ssidByBssid = HashMap<String, String>() // key upper-case
    private val targetSsids = config.targetSsids.toHashSet()

    init {
        if (config.scope in setOf(SaturationScope.SingleAp, SaturationScope.MultipleAps, SaturationScope.Mixed, SaturationScope.SingleUnit, SaturationScope.MultipleUnits)) {
            val ssidOfUnitMember = LinkedHashMap<String, String>()
            config.targetUnits.flatMap { it.members }.forEach { ssidOfUnitMember.putIfAbsent(it.bssid.uppercase(), it.ssid) }
            config.targetBssids.forEach { register(it.uppercase(), ssidOfUnitMember[it.uppercase()] ?: "") }
        }
    }

    val bssids: List<String> get() = order
    fun ssidOf(bssid: String) = ssidByBssid[bssid.uppercase()] ?: ""

    fun inScope(ap: AccessPoint): Boolean = when (config.scope) {
        SaturationScope.SingleAp, SaturationScope.MultipleAps, SaturationScope.SingleUnit, SaturationScope.MultipleUnits -> ssidByBssid.containsKey(ap.bssid.uppercase())
        SaturationScope.SingleSsid, SaturationScope.MultipleSsids -> ap.ssid in targetSsids
        SaturationScope.Mixed -> ssidByBssid.containsKey(ap.bssid.uppercase()) || ap.ssid in targetSsids
        else -> true
    }

    fun register(bssid: String, ssid: String) {
        val k = bssid.uppercase()
        val existing = ssidByBssid[k]
        if (existing != null) {
            if (existing.isEmpty() && ssid.isNotEmpty()) ssidByBssid[k] = ssid
            return
        }
        ssidByBssid[k] = ssid
        order.add(bssid)
    }
}

/** Port of SaturationObservationBuilder: one ScanCycleResult → per-target observations (SPEC §4). */
object SaturationObservationBuilder {
    fun build(config: SaturationTestConfig, tracker: SaturationTargetTracker, result: ScanCycleResult, sequence: Long): List<SaturationObservation> {
        if (result.status == ScanCycleStatus.Failed || result.status == ScanCycleStatus.Skipped)
            return tracker.bssids.map { unknown(sequence, it, tracker.ssidOf(it)) }

        val byBssid = HashMap<String, AccessPoint>()
        for (ap in result.accessPoints) {
            byBssid[ap.bssid.uppercase()] = ap
            if (tracker.inScope(ap)) tracker.register(ap.bssid.uppercase(), ap.ssid)
        }
        val limitMs = config.freshnessLimit.toMillis().toDouble()
        return tracker.bssids.map { bssid ->
            val ap = byBssid[bssid.uppercase()]
            if (ap != null) {
                val fresh = isFresh(ap, result.status, limitMs)
                val (co, overlap, strongest) = coChannel(ap, result.accessPoints) { isFresh(it, result.status, limitMs) }
                SaturationObservation(
                    sequence, bssid, ap.ssid, if (fresh) ObservationStatus.Observed else ObservationStatus.Stale,
                    ap.rssi, ap.quality, ap.channel, ap.band, ap.channelWidth, ap.beaconAgeMs, co, overlap,
                    security = ap.securityType, vendor = ap.vendorOui, phyType = ap.phyType, beaconIntervalTu = ap.beaconIntervalTu,
                    centerFrequencyMhz = ap.centerFrequencyMhz, stationCount = ap.stationCount, channelUtilizationPct = ap.channelUtilizationPct,
                    strongestOverlapRssi = strongest,
                )
            } else if (result.status == ScanCycleStatus.Fresh) {
                SaturationObservation(sequence, bssid, tracker.ssidOf(bssid), ObservationStatus.Missing, null, null, null, null, null, null, null, null)
            } else unknown(sequence, bssid, tracker.ssidOf(bssid))
        }
    }

    private fun unknown(seq: Long, bssid: String, ssid: String) =
        SaturationObservation(seq, bssid, ssid, ObservationStatus.Unknown, null, null, null, null, null, null, null, null)

    private fun isFresh(ap: AccessPoint, cycle: ScanCycleStatus, limitMs: Double) =
        ap.beaconAgeMs?.let { it <= limitMs } ?: (cycle == ScanCycleStatus.Fresh)

    /** Fresh neighbours only: co-channel, overlapping blocks, strongest of both (SAT-ANALYTICS-2). */
    fun coChannel(ap: AccessPoint, all: List<AccessPoint>, isFresh: ((AccessPoint) -> Boolean)? = null): Triple<Int?, Int?, Int?> {
        if (ap.channel <= 0) return Triple(null, null, null)
        val others = all.filter { !it.bssid.equals(ap.bssid, true) && it.band == ap.band && it.channel > 0 && (isFresh == null || isFresh(it)) }
        val co = others.filter { it.channel == ap.channel }
        val overlap = others.filter { it.channel != ap.channel && ChannelMath.overlaps(ap, it) }
        val strongest = (co + overlap).maxOfOrNull { it.rssi }
        return Triple(co.size, overlap.size, strongest)
    }
}
