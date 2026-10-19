package com.wirelessscanner.core.domain

import java.time.Instant

// Port of WirelessScanner.Domain (INetworkAdapterProvider.cs, ScanCycle.cs, ChannelMath.cs). Field names and meanings match C#.

/** One BSS as heard by the adapter. Null = not reported; never guessed. */
data class AccessPoint(
    val bssid: String,
    val ssid: String,
    val hostname: String? = null,
    val band: String,
    val channel: Int,
    val channelWidth: Int?,
    val rssi: Int,
    val noiseFloor: Int? = null,
    val snr: Int,
    val quality: Int,
    val securityType: String,
    val networkType: String = "Infrastructure",
    val vendorOui: String? = null,
    val supportedRates: List<String>? = null,
    val lastSeen: Instant,
    /** Age of the most recent beacon/probe response when the list was read (null = unknown). DR-0003. */
    val beaconAgeMs: Double? = null,
    val phyType: String? = null,
    val beaconIntervalTu: Int? = null,
    val centerFrequencyMhz: Double? = null,
    val stationCount: Int? = null,
    val channelUtilizationPct: Double? = null,
    val admissionCapacity: Int? = null,
    /** TSF offset (DR-0013). Android does not expose TSF: always null on Android (platform limit U2a). */
    val tsfOffsetMs: Double? = null,
    val wpsManufacturer: String? = null,
    val wpsModel: String? = null,
    val wpsModelNumber: String? = null,
    val wpsDeviceName: String? = null,
    val wpsUuid: String? = null,
    /** Wi-Fi 7 MLD address (Android API 33+): shared by the links of one multi-link AP. Android-only evidence. */
    val apMldMac: String? = null,
)

/** The measuring device's own Wi-Fi association. DR-0012. */
data class ClientConnectionInfo(
    val connected: Boolean = false,
    val state: String = "",
    val ssid: String? = null,
    val bssid: String? = null,
    val phyType: String? = null,
    val signalQualityPct: Int? = null,
    val rxRateMbps: Double? = null,
    val txRateMbps: Double? = null,
    val channel: Int? = null,
    val band: String? = null,
)

data class NetworkInterfaceInfo(
    val id: String,
    val name: String,
    val description: String,
    val isConnected: Boolean,
    val macAddress: String,
    val ipAddress: String,
) {
    val displayName get() = "$name [$description] | MAC: $macAddress | IP: $ipAddress"
}

/**
 * Outcome of one acquisition cycle (DR-0003).
 * Fresh: a new scan completed before the list was read. Stale: no completion confirmed (on Android also: throttled);
 * the list may be cached. Failed: the list could not be read. Skipped: no acquisition attempted.
 */
enum class ScanCycleStatus { Fresh, Stale, Failed, Skipped }

data class ScanCycleResult(
    val status: ScanCycleStatus,
    val requestedUtc: Instant,
    val completedUtc: Instant,
    val durationMs: Double,
    val accessPoints: List<AccessPoint>,
    val errorCode: String? = null,
    val errorMessage: String? = null,
) {
    companion object {
        fun failure(requestedUtc: Instant, code: String, message: String): ScanCycleResult {
            val now = Instant.now()
            return ScanCycleResult(ScanCycleStatus.Failed, requestedUtc, now, (now.toEpochMilli() - requestedUtc.toEpochMilli()).toDouble(), emptyList(), code, message)
        }
    }
}

object WirelessScannerConstants {
    /** Assumed noise floor used only to ESTIMATE SNR; neither Windows nor Android reports a noise floor. */
    const val ASSUMED_NOISE_FLOOR_DBM = -95
}

/** 802.11 channel geometry shared by the spectrum view and the analytics (DR-0010, DR-0012). Pure functions. */
object ChannelMath {
    const val DFS_LOW_MHZ = 5250.0
    const val DFS_HIGH_MHZ = 5730.0

    fun channelFrequencyMhz(band: String, channel: Int): Double = when (band) {
        "2.4 GHz" -> if (channel == 14) 2484.0 else 2407.0 + 5 * channel
        "5 GHz" -> 5000.0 + 5 * channel
        "6 GHz" -> 5950.0 + 5 * channel
        else -> Double.NaN
    }

    private val centers5x40 = intArrayOf(38, 46, 54, 62, 102, 110, 118, 126, 134, 142, 151, 159)
    private val centers5x80 = intArrayOf(42, 58, 106, 122, 138, 155)
    private val centers5x160 = intArrayOf(50, 114)

    /** Centre frequency of the occupied channel block (same rules as C#, incl. the HT40+/- assumption in 2.4 GHz). */
    fun blockCenterMhz(band: String, primaryChannel: Int, widthMhz: Int?): Double {
        val w = widthMhz ?: 20
        when (band) {
            "2.4 GHz" -> {
                val f = channelFrequencyMhz(band, primaryChannel)
                return if (w >= 40) f + (if (primaryChannel < 8) 10 else -10) else f
            }
            "5 GHz" -> {
                val centers = when {
                    w == 40 -> centers5x40
                    w == 80 -> centers5x80
                    w >= 160 -> centers5x160
                    else -> null
                } ?: return channelFrequencyMhz(band, primaryChannel)
                val halfSpan = w / 10
                val center = centers.firstOrNull { kotlin.math.abs(it - primaryChannel) <= halfSpan } ?: 0
                return channelFrequencyMhz(band, if (center == 0) primaryChannel else center)
            }
            "6 GHz" -> {
                val n = maxOf(1, w / 20)
                val index = maxOf(0, (primaryChannel - 1) / 4)
                val block = index / n
                val centerCh = 1 + block * n * 4 + (n * 4 - 4) / 2
                return channelFrequencyMhz(band, centerCh)
            }
        }
        return Double.NaN
    }

    fun occupancy(band: String, primaryChannel: Int, widthMhz: Int?): Pair<Double, Double> {
        val c = blockCenterMhz(band, primaryChannel, widthMhz)
        val half = (widthMhz ?: 20) / 2.0
        return (c - half) to (c + half)
    }

    fun occupancy(ap: AccessPoint) = occupancy(ap.band, ap.channel, ap.channelWidth)

    /** True when two occupied blocks share spectrum (same band). Touching edges do not count. */
    fun overlaps(a: AccessPoint, b: AccessPoint): Boolean {
        if (a.band != b.band || a.channel <= 0 || b.channel <= 0) return false
        val (al, ah) = occupancy(a)
        val (bl, bh) = occupancy(b)
        return al < bh && bl < ah
    }

    fun isDfs(band: String, primaryChannel: Int, widthMhz: Int?): Boolean {
        if (band != "5 GHz" || primaryChannel <= 0) return false
        val (lo, hi) = occupancy(band, primaryChannel, widthMhz)
        return lo < DFS_HIGH_MHZ && hi > DFS_LOW_MHZ
    }

    /** Android: frequency (MHz) → (band, channel). Mirrors the 802.11 channel plan used by the Windows parser. */
    fun bandAndChannel(frequencyMhz: Int): Pair<String, Int> = when {
        frequencyMhz == 2484 -> "2.4 GHz" to 14
        frequencyMhz in 2400..2500 -> "2.4 GHz" to (frequencyMhz - 2407) / 5
        frequencyMhz == 5935 -> "6 GHz" to 2
        frequencyMhz in 5955..7125 ->"6 GHz" to (frequencyMhz - 5950) / 5
        frequencyMhz in 4900..5900 -> "5 GHz" to (frequencyMhz - 5000) / 5
        else -> "Unknown" to 0
    }
}
