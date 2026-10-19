package com.wirelessscanner.core.domain

import java.time.Duration
import java.time.Instant
import java.util.Locale
import java.util.UUID

// Port of WirelessScanner.Domain/Saturation.cs and PhysicalUnits.cs (SPEC-SAT-001, DR-0004/0005/0012/0013).

enum class SaturationScope { SingleAp, MultipleAps, SingleSsid, MultipleSsids, AllVisible, Mixed, SingleUnit, MultipleUnits }
enum class ObservationStatus { Observed, Stale, Missing, Unknown }
enum class SaturationEventType { Drop, Peak, Dropout, ChannelChange, StrongestBssidChange }
enum class SaturationTestStatus { Running, Paused, Completed, Stopped, Failed }

data class SaturationThresholds(
    val dropDb: Double = 8.0,
    val peakDb: Double = 8.0,
    val baselineWindow: Int = 12,
    val minBaselineSamples: Int = 3,
    val dropoutMissedCycles: Int = 2,
    val strongestChangeHysteresisDb: Double = 3.0,
    val goodDbm: Int = -67,
    val fairDbm: Int = -70,
    val unusableDbm: Int = -80,
    val freshnessToleranceSeconds: Double = 5.0,
)

/** Where the test was performed. Source: "manual" or "android-location" (Windows: "windows-location"). */
data class GeoTag(
    val latitude: Double? = null,
    val longitude: Double? = null,
    val accuracyMeters: Double? = null,
    val source: String = "manual",
    val capturedUtc: Instant? = null,
    val building: String = "",
    val floor: String = "",
    val zone: String = "",
) {
    val hasCoordinates get() = latitude != null && longitude != null
    val coordinatesText: String
        get() = if (hasCoordinates) String.format(Locale.ROOT, "%.6f, %.6f", latitude, longitude) +
            (accuracyMeters?.let { String.format(Locale.ROOT, " (±%.0f m)", it) } ?: "") else "not recorded"
    val mapUrl: String
        get() = if (hasCoordinates) String.format(Locale.ROOT, "https://www.openstreetmap.org/?mlat=%.6f&mlon=%.6f#map=19/%.6f/%.6f", latitude, longitude, latitude, longitude) else ""

    companion object {
        fun validate(lat: Double?, lon: Double?): String? {
            if ((lat != null) != (lon != null)) return "Enter both latitude and longitude, or neither."
            if (lat != null && (lat < -90 || lat > 90)) return "Latitude must be between -90 and 90."
            if (lon != null && (lon < -180 || lon > 180)) return "Longitude must be between -180 and 180."
            return null
        }
    }
}

data class SaturationEnvironment(
    val appVersion: String = "",
    val osDescription: String = "",
    val machineName: String = "",
    val timeZoneId: String = "",
    val utcOffsetMinutes: Int = 0,
    val adapterDescription: String = "",
    val adapterMac: String = "",
    val driverVersion: String? = null,
    val driverDate: String? = null,
    val driverProvider: String? = null,
    val connectionAtStart: ClientConnectionInfo? = null,
    val capturedUtc: Instant = Instant.EPOCH,
)

// ---- physical units (DR-0013)
enum class UnitConfidence { Single, Medium, High, Manual }
data class UnitMember(val bssid: String, val ssid: String, val band: String, val channel: Int, val rssi: Int)
data class UnitRadio(val key: String, val band: String, val channel: Int, val members: List<UnitMember>)
data class PhysicalUnit(
    val id: String,
    val name: String,
    val isCustomName: Boolean,
    val confidence: UnitConfidence,
    val evidence: List<String>,
    val radios: List<UnitRadio>,
    val vendor: String?,
    val model: String?,
    val sameSsidUnitIds: List<String>,
) {
    val members: List<UnitMember> get() = radios.flatMap { it.members }
    val isMeshCandidate get() = sameSsidUnitIds.isNotEmpty()
}

/** The user's corrections. Assignments keys are BSSIDs (case-insensitive: stored upper-case). */
data class UnitOverrides(
    val assignments: MutableMap<String, String> = mutableMapOf(),
    val names: MutableMap<String, String> = mutableMapOf(),
) {
    fun assignment(bssid: String) = assignments.entries.firstOrNull { it.key.equals(bssid, ignoreCase = true) }?.value
}

data class SaturationUnitMember(val bssid: String, val ssid: String, val band: String, val radioKey: String)
data class SaturationUnitTarget(
    val id: String = "",
    val name: String = "",
    val confidence: UnitConfidence = UnitConfidence.Single,
    val evidence: List<String> = emptyList(),
    val members: List<SaturationUnitMember> = emptyList(),
)

data class SaturationRadioSummary(
    val radioKey: String = "",
    val band: String = "",
    val bssids: List<String> = emptyList(),
    val ssids: List<String> = emptyList(),
    val consistencyMeanDeltaDb: Double? = null,
    val consistencyMaxDeltaDb: Double? = null,
    val ssidOnlyOutagePct: Map<String, Double> = emptyMap(),
)

data class SaturationUnitSummary(
    val id: String = "",
    val name: String = "",
    val confidence: UnitConfidence = UnitConfidence.Single,
    val evidence: List<String> = emptyList(),
    val bssidCount: Int = 0,
    val ssidCount: Int = 0,
    val unitAvailabilityPct: Double? = null,
    val bestMeanDbm: Double? = null,
    val radios: List<SaturationRadioSummary> = emptyList(),
    val findings: List<String> = emptyList(),
)

data class SaturationTestConfig(
    val testId: String = UUID.randomUUID().toString(),
    val name: String = "",
    val scope: SaturationScope = SaturationScope.SingleAp,
    val targetBssids: List<String> = emptyList(),
    val targetSsids: List<String> = emptyList(),
    val targetUnits: List<SaturationUnitTarget> = emptyList(),
    /** Null = run until stopped. */
    val duration: Duration? = Duration.ofMinutes(15),
    val interval: Duration = Duration.ofSeconds(5),
    val scanTimeout: Duration = Duration.ofSeconds(6),
    val location: String = "",
    val facility: String = "",
    val operator: String = "",
    val client: String = "",
    val notes: String = "",
    val geo: GeoTag = GeoTag(),
    val interfaceId: String = "",
    val interfaceName: String = "",
    val thresholds: SaturationThresholds = SaturationThresholds(),
    val keepAwake: Boolean = true,
    val environment: SaturationEnvironment? = null,
) {
    val freshnessLimit: Duration get() = interval.plusMillis((thresholds.freshnessToleranceSeconds * 1000).toLong())
}

data class SaturationObservation(
    val cycleSequence: Long,
    val bssid: String,
    val ssid: String,
    val status: ObservationStatus,
    val rssi: Int?,
    val linkQuality: Int?,
    val channel: Int?,
    val band: String?,
    val channelWidth: Int?,
    val beaconAgeMs: Double?,
    val coChannelCount: Int?,
    val overlapCount: Int?,
    val security: String? = null,
    val vendor: String? = null,
    val phyType: String? = null,
    val beaconIntervalTu: Int? = null,
    val centerFrequencyMhz: Double? = null,
    val stationCount: Int? = null,
    val channelUtilizationPct: Double? = null,
    val strongestOverlapRssi: Int? = null,
)

data class SaturationCycle(
    val sequence: Long,
    val wallUtc: Instant,
    val elapsedMs: Long,
    val status: ScanCycleStatus,
    val durationMs: Double,
    val bssCount: Int,
    val errorCode: String?,
    val errorMessage: String?,
    val observations: List<SaturationObservation>,
)

data class SaturationMarker(val wallUtc: Instant, val elapsedMs: Long, val text: String)

data class SaturationEvent(
    val type: SaturationEventType,
    val subject: String,
    val ssid: String?,
    val startUtc: Instant,
    val endUtc: Instant?,
    val startElapsedMs: Long,
    val endElapsedMs: Long?,
    val durationSeconds: Double?,
    val cycles: Int?,
    val baselineDbm: Double?,
    val extremeRssi: Int?,
    val magnitudeDb: Double?,
    val recoverySeconds: Double?,
    val detail: String,
)

data class SaturationTargetSummary(
    val bssid: String = "",
    val ssid: String = "",
    val bands: String = "",
    val channels: String = "",
    val primaryChannel: Int? = null,
    val channelWidth: Int? = null,
    val cyclesTracked: Int = 0,
    val observed: Int = 0,
    val missing: Int = 0,
    val stale: Int = 0,
    val unknown: Int = 0,
    val availabilityPct: Double? = null,
    val min: Int? = null,
    val max: Int? = null,
    val mean: Double? = null,
    val median: Double? = null,
    val stdDev: Double? = null,
    val p5: Double? = null,
    val p25: Double? = null,
    val p75: Double? = null,
    val p95: Double? = null,
    val iqr: Double? = null,
    val range: Int? = null,
    val variationMean: Double? = null,
    val variationP95: Double? = null,
    val variationMax: Double? = null,
    val trendDbPerHour: Double? = null,
    val observedSpanSeconds: Double = 0.0,
    val pctBelowGood: Double? = null,
    val pctBelowFair: Double? = null,
    val pctBelowUnusable: Double? = null,
    val longestBelowFairSeconds: Double = 0.0,
    val longestBelowUnusableSeconds: Double = 0.0,
    val dropCount: Int = 0,
    val peakCount: Int = 0,
    val dropoutCount: Int = 0,
    val channelChangeCount: Int = 0,
    val worstDropDb: Double? = null,
    val longestDropoutSeconds: Double = 0.0,
    val coChannelMean: Double? = null,
    val coChannelMax: Int? = null,
    val overlapMean: Double? = null,
    val overlapMax: Int? = null,
    val vendor: String? = null,
    val security: String? = null,
    val phyType: String? = null,
    val beaconIntervalTu: Int? = null,
    val centerFrequencyMhz: Double? = null,
    val isDfs: Boolean? = null,
    val linkQualityMean: Double? = null,
    val linkQualityMin: Int? = null,
    val snrEstimatedMeanDb: Double? = null,
    val loadAdvertisedPct: Double? = null,
    val stationCountMean: Double? = null,
    val stationCountMax: Int? = null,
    val channelUtilizationMeanPct: Double? = null,
    val channelUtilizationMaxPct: Double? = null,
    val interferenceMarginMedianDb: Double? = null,
    val interferenceMarginMinDb: Double? = null,
    val strongestOverlapRssiMax: Int? = null,
    val stabilityScore: Double? = null,
    val grade: String = "n/a",
    val tier: String = "n/a",
    val findings: List<String> = emptyList(),
)

data class SaturationSsidSummary(
    val ssid: String,
    val bssidCount: Int,
    val availabilityPct: Double?,
    val bestMean: Double?,
    val bestMin: Int?,
    val bestMax: Int?,
    val strongestBssidChanges: Int,
)

data class SaturationCycleHealth(
    val total: Int,
    val fresh: Int,
    val stale: Int,
    val failed: Int,
    val skipped: Int,
    val meanScanMs: Double?,
    val maxScanMs: Double?,
    val maxGapSeconds: Double,
    val errorCounts: Map<String, Int>,
)

data class SaturationReport(
    val config: SaturationTestConfig,
    val analyticsVersion: String,
    val startUtc: Instant,
    val endUtc: Instant?,
    val activeSeconds: Double,
    val status: SaturationTestStatus,
    val health: SaturationCycleHealth,
    val targets: List<SaturationTargetSummary>,
    val ssids: List<SaturationSsidSummary>,
    val events: List<SaturationEvent>,
    val markers: List<SaturationMarker>,
    val cycles: List<SaturationCycle>,
    val units: List<SaturationUnitSummary>? = null,
) {
    val unitSummaries: List<SaturationUnitSummary> get() = units ?: emptyList()
}

data class SaturationTestRecord(
    val config: SaturationTestConfig,
    val startUtc: Instant,
    val endUtc: Instant?,
    val status: SaturationTestStatus,
    val cycleCount: Int,
    val activeSeconds: Double? = null,
)

data class SaturationProgress(
    val config: SaturationTestConfig,
    val status: SaturationTestStatus,
    val startUtc: Instant,
    val activeSeconds: Double,
    val remainingSeconds: Double?,
    val lastCycle: SaturationCycle,
    val cycles: List<SaturationCycle>,
    val markers: List<SaturationMarker>,
)
