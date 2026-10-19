package com.wirelessscanner.core.domain

import java.time.Instant

// Port of ISessionManager.cs, ISessionRepository.cs, TelemetrySample.cs, IStatisticsCalculator.cs, IAppLogger.cs and the
// repository interfaces of Saturation.cs / ConnectionTest.cs. Suspend functions replace Task.

enum class CaptureSessionMode { Roaming, Stationary }
enum class CaptureSessionStatus { Active, Paused, Completed, Error }

data class CaptureSession(
    val sessionId: String,
    val name: String,
    val scope: String,
    val facilityName: String,
    val interfaceId: String,
    val interfaceName: String,
    val startTime: Instant,
    val endTime: Instant?,
    val mode: CaptureSessionMode,
    val status: CaptureSessionStatus,
)

data class TelemetrySample(
    val timestamp: Instant,
    val surveyPoint: String,
    val bssid: String,
    val ssid: String,
    val band: String,
    val channel: Int,
    val rssi: Int,
    val snr: Int,
    val quality: Int,
    /** Scan-to-scan RSSI variation in dB ("jitter" in 1.0 naming; not latency jitter). */
    val jitter: Double,
    val noiseFloor: Int?,
)

data class TelemetryStats(val minRssi: Int, val maxRssi: Int, val meanRssi: Double, val stdDevRssi: Double, val avgJitter: Double)

interface AppLogger {
    fun info(message: String)
    fun warn(message: String)
    fun error(message: String, t: Throwable? = null)
}

object NullLogger : AppLogger {
    override fun info(message: String) {}
    override fun warn(message: String) {}
    override fun error(message: String, t: Throwable?) {}
}

interface SessionRepository {
    suspend fun saveSession(session: CaptureSession)
    suspend fun saveSamples(samples: List<TelemetrySample>, sessionId: String)
    suspend fun updateSessionStatus(sessionId: String, status: CaptureSessionStatus, endTime: Instant?)
    suspend fun getSession(sessionId: String): CaptureSession?
    suspend fun getSamples(sessionId: String): List<TelemetrySample>
    suspend fun getAllSessions(): List<CaptureSession>
    suspend fun deleteSession(sessionId: String)
    suspend fun purgeSessionsByAge(threshold: Instant)
}

interface SaturationRepository {
    suspend fun saveTest(config: SaturationTestConfig, startUtc: Instant, status: SaturationTestStatus)
    suspend fun updateTestStatus(testId: String, status: SaturationTestStatus, endUtc: Instant?, activeSeconds: Double? = null)
    suspend fun saveCycle(testId: String, cycle: SaturationCycle)
    suspend fun saveMarker(testId: String, marker: SaturationMarker)
    suspend fun getTests(): List<SaturationTestRecord>
    suspend fun getTest(testId: String): SaturationTestRecord?
    suspend fun getCycles(testId: String): List<SaturationCycle>
    suspend fun getMarkers(testId: String): List<SaturationMarker>
    suspend fun deleteTest(testId: String)
}

data class ConnectionData(
    val probes: List<ConnectionProbeResult>,
    val link: List<ConnectionLinkSample>,
    val speed: List<SpeedTestResult>,
    val twoWay: List<TwoWayResult>,
    val markers: List<ConnectionMarker>,
)

interface ConnectionRepository {
    suspend fun saveTest(config: ConnectionTestConfig, startUtc: Instant, status: ConnectionTestStatus)
    suspend fun updateTestStatus(testId: String, status: ConnectionTestStatus, endUtc: Instant?, activeSeconds: Double? = null)
    suspend fun saveProbes(testId: String, probes: List<ConnectionProbeResult>)
    suspend fun saveLinkSamples(testId: String, samples: List<ConnectionLinkSample>)
    suspend fun saveMarker(testId: String, marker: ConnectionMarker)
    suspend fun saveSpeedTest(testId: String, result: SpeedTestResult)
    suspend fun saveTwoWay(testId: String, result: TwoWayResult)
    suspend fun getTests(): List<ConnectionTestRecord>
    suspend fun getData(testId: String): ConnectionData
    suspend fun deleteTest(testId: String)
}

interface SettingsRepository {
    fun get(key: String): String?
    fun set(key: String, value: String?)
}

/** Acquisition for one adapter (Android: the device's single Wi-Fi radio). */
interface NetworkAdapterProvider {
    suspend fun interfaces(): List<NetworkInterfaceInfo>
    /** One acquisition cycle: request a fresh scan, wait for completion or timeout, read the list (DR-0003). */
    suspend fun scan(interfaceId: String, timeoutMs: Long): ScanCycleResult
    suspend fun connectionInfo(interfaceId: String): ClientConnectionInfo? = null
}

interface GeoLocationProvider {
    suspend fun current(timeoutMs: Long): Pair<GeoTag?, String?>
}
