package com.wirelessscanner.core.domain

import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlin.math.max
import kotlin.math.round

// Port of WirelessScanner.Domain/ConnectionTest.cs (DR-0014). Quality of the connection the device is ALREADY on,
// measured end to end with small probes bound to the Wi-Fi network. The connection itself is never changed.

/** Order = upstream order used for fault attribution. */
enum class ConnectionSegment { WifiLink, Gateway, IspHop, Internet, Dns, Web, LanTwoWay }
enum class ProbeMethod { Icmp, IcmpTtl, Tcp, Dns, Https, Udp }
enum class ProbeOutcome { Ok, Timeout, Refused, Unreachable, TtlExpired, DnsError, HttpError, Error, Skipped }
enum class ConnectionIntensity { Light, Detailed }
enum class ConnectionTestStatus { Running, Paused, Completed, Stopped, Failed }

data class ProbeReply(val outcome: ProbeOutcome, val rttMs: Double?, val from: String? = null, val detail: String? = null) {
    companion object {
        fun fail(outcome: ProbeOutcome, detail: String? = null) = ProbeReply(outcome, null, null, detail)
    }
}

data class ConnectionProbeResult(
    val sequence: Long,
    val wallUtc: Instant,
    val elapsedMs: Long,
    val segment: ConnectionSegment,
    val target: String,
    val method: ProbeMethod,
    val outcome: ProbeOutcome,
    val rttMs: Double?,
    val detail: String? = null,
)

/** Cumulative 802.11 frame counters. Android does not expose them (platform limit N4a): always null on Android. */
data class WlanFrameCounters(val txFrames: Long, val txRetries: Long, val txFailed: Long, val ackFailures: Long, val rxFrames: Long, val fcsErrors: Long)

data class WlanLinkReading(
    val connected: Boolean = false,
    val state: String = "",
    val ssid: String? = null,
    val bssid: String? = null,
    val phyType: String? = null,
    val rssi: Int? = null,
    val signalQualityPct: Int? = null,
    val rxRateMbps: Double? = null,
    val txRateMbps: Double? = null,
    val channel: Int? = null,
    val counters: WlanFrameCounters? = null,
)

data class ConnectionLinkSample(
    val sequence: Long,
    val wallUtc: Instant,
    val elapsedMs: Long,
    val connected: Boolean,
    val ssid: String?,
    val bssid: String?,
    val channel: Int?,
    val rssi: Int?,
    val signalQualityPct: Int?,
    val rxRateMbps: Double?,
    val txRateMbps: Double?,
    val localIp: String?,
    val gateway: String?,
    val ifBytesIn: Long?,
    val ifBytesOut: Long?,
    val counters: WlanFrameCounters?,
    val gapMs: Long? = null,
)

data class ConnectionContext(
    val wifiConnected: Boolean = false,
    val interfaceId: String = "",
    val interfaceName: String = "",
    val interfaceIndex: Int? = null,
    val ssid: String? = null,
    val bssid: String? = null,
    val channel: Int? = null,
    val band: String? = null,
    val phyType: String? = null,
    val rssi: Int? = null,
    val rxRateMbps: Double? = null,
    val txRateMbps: Double? = null,
    val localIPv4: String? = null,
    val gateways: List<String> = emptyList(),
    val dnsServers: List<String> = emptyList(),
    val otherActiveInterfaces: List<String> = emptyList(),
    val pathHops: List<String> = emptyList(),
    val ispHop: String? = null,
    val ispHopTtl: Int? = null,
    val ispHopAnswersEcho: Boolean = false,
    val problem: String? = null,
)

enum class SpeedServerKind { None, Responder, Http }

data class SpeedServerSettings(
    val kind: SpeedServerKind = SpeedServerKind.None,
    val httpDownloadUrl: String = "",
    val httpUploadUrl: String = "",
    val streams: Int = 4,
    val secondsPerDirection: Int = 10,
)

/** The paired LAN responder. The key is stored in the Android Keystore and never written to reports. */
data class ResponderSettings(val address: String = "", val port: Int = 5410, val key: String = "") {
    val isConfigured get() = address.isNotBlank() && key.isNotBlank()
}

data class ConnectionTestConfig(
    val testId: String = UUID.randomUUID().toString(),
    val name: String = "",
    val duration: Duration? = Duration.ofMinutes(15),
    val intensity: ConnectionIntensity = ConnectionIntensity.Light,
    val interfaceId: String = "",
    val interfaceName: String = "",
    val probeWifiLink: Boolean = true,
    val probeGateway: Boolean = true,
    val probeIsp: Boolean = true,
    val probeInternet: Boolean = true,
    val probeDns: Boolean = true,
    val probeWeb: Boolean = true,
    val internetTargets: List<String> = DEFAULT_INTERNET_TARGETS,
    val internetTcpPort: Int = 443,
    val dnsQueryName: String = "example.com",
    val webUrl: String = "https://one.one.one.one/cdn-cgi/trace",
    val location: String = "",
    val facility: String = "",
    val operator: String = "",
    val client: String = "",
    val notes: String = "",
    val geo: GeoTag = GeoTag(),
    val keepAwake: Boolean = true,
    val stopOnNetworkChange: Boolean = false,
    val linkedSaturationTestId: String? = null,
    val speedTestAtStart: Boolean = false,
    val speedTestEveryMinutes: Int = 0,
    val twoWayAtStart: Boolean = false,
    val twoWayEveryMinutes: Int = 0,
    val contextAtStart: ConnectionContext? = null,
    val environment: SaturationEnvironment? = null,
    /** Base tick. Light = 1 s, Detailed = 0.5 s. */
    val tickMilliseconds: Int = 1000,
) {
    /** Approximate probe traffic in kbit/s (both directions, IP headers included). Same formula as C#. */
    fun estimatedKbps(): Double {
        val perSec = 1000.0 / tickMilliseconds
        var bytes = 0.0
        if (probeGateway) bytes += 2 * 74 * perSec / GATEWAY_EVERY_TICKS
        if (probeIsp) bytes += 2 * 74 * perSec / ISP_EVERY_TICKS
        if (probeInternet) bytes += internetTargets.size * (2 * 74 + 4 * 60) * perSec / INTERNET_EVERY_TICKS
        if (probeDns) {
            val v4 = contextAtStart?.dnsServers?.count { isIPv4(it) } ?: 1
            bytes += max(1, v4) * 2 * 90 * perSec / DNS_EVERY_TICKS
        }
        if (probeWeb) bytes += 9000 * perSec / WEB_EVERY_TICKS
        return round(bytes * 8 / 1000 * 10) / 10
    }

    companion object {
        val DEFAULT_INTERNET_TARGETS = listOf("1.1.1.1", "8.8.8.8", "9.9.9.9")
        const val GATEWAY_EVERY_TICKS = 1
        const val ISP_EVERY_TICKS = 2
        const val INTERNET_EVERY_TICKS = 2
        const val DNS_EVERY_TICKS = 10
        const val WEB_EVERY_TICKS = 30
        const val GATEWAY_TIMEOUT_MS = 1000
        const val ISP_TIMEOUT_MS = 1500
        const val INTERNET_TIMEOUT_MS = 2000
        const val DNS_TIMEOUT_MS = 2000
        const val WEB_TIMEOUT_MS = 5000

        fun isIPv4(s: String): Boolean {
            val p = s.split('.')
            return p.size == 4 && p.all { it.isNotEmpty() && it.length <= 3 && it.all(Char::isDigit) && it.toInt() <= 255 }
        }
    }
}

data class ConnectionMarker(val wallUtc: Instant, val elapsedMs: Long, val text: String)

enum class ConnectionEventType { Outage, LatencySpike, LossBurst, Roam, Disconnect, Reconnect, IpChange, NetworkChange, SleepGap, SpeedTest, TwoWayTest }

data class ConnectionEvent(
    val type: ConnectionEventType,
    val segment: ConnectionSegment?,
    val attributedTo: ConnectionSegment?,
    val startUtc: Instant,
    val endUtc: Instant?,
    val startElapsedMs: Long,
    val endElapsedMs: Long?,
    val durationSeconds: Double?,
    val probes: Int?,
    val peakRttMs: Double?,
    val detail: String,
)

data class ConnectionSegmentSummary(
    val segment: ConnectionSegment = ConnectionSegment.WifiLink,
    val target: String = "",
    val method: ProbeMethod = ProbeMethod.Icmp,
    val sent: Int = 0,
    val ok: Int = 0,
    val failed: Int = 0,
    val failureReasons: Map<String, Int> = emptyMap(),
    val lossPct: Double? = null,
    val availabilityPct: Double? = null,
    val rttMin: Double? = null,
    val rttP50: Double? = null,
    val rttP95: Double? = null,
    val rttP99: Double? = null,
    val rttMax: Double? = null,
    val rttMean: Double? = null,
    val jitterMs: Double? = null,
    val delayVariationP95Ms: Double? = null,
    val spikes: Int = 0,
    val outages: Int = 0,
    val longestOutageSeconds: Double? = null,
)

data class UseVerdict(val use: String, val rating: String, val reason: String)

data class ConnectionLinkSummary(
    val samples: Int = 0,
    val roams: Int = 0,
    val disconnects: Int = 0,
    val bssids: List<String> = emptyList(),
    val rssiMean: Double? = null,
    val rssiMin: Int? = null,
    val rxRateMean: Double? = null,
    val txRateMean: Double? = null,
    val countersAvailable: Boolean = false,
    val retryPct: Double? = null,
    val txFailedPct: Double? = null,
    val fcsErrorPct: Double? = null,
    val pcTrafficMeanKbps: Double? = null,
    val pcTrafficMaxKbps: Double? = null,
)

data class SpeedTestResult(
    val startUtc: Instant = Instant.EPOCH,
    val elapsedMs: Long = 0,
    val provider: String = "",
    val server: String = "",
    val streams: Int = 0,
    val secondsPerDirection: Double = 0.0,
    val downloadMbps: Double? = null,
    val uploadMbps: Double? = null,
    val bytesDown: Long = 0,
    val bytesUp: Long = 0,
    val idleLatencyMs: Double? = null,
    val loadedLatencyDownMs: Double? = null,
    val loadedLatencyUpMs: Double? = null,
    val loadedLatencyP95Ms: Double? = null,
    val latencyIncreaseMs: Double? = null,
    val bufferbloatGrade: String = "",
    val responsivenessRpm: Double? = null,
    val latencyTarget: String = "",
    val error: String? = null,
)

data class TwoWayResult(
    val startUtc: Instant = Instant.EPOCH,
    val elapsedMs: Long = 0,
    val responder: String = "",
    val seconds: Double = 0.0,
    val packetsPerSecond: Int = 0,
    val payloadBytes: Int = 0,
    val sent: Int = 0,
    val receivedByResponder: Int = 0,
    val repliesSent: Int = 0,
    val receivedBack: Int = 0,
    val upLossPct: Double? = null,
    val downLossPct: Double? = null,
    val rttP50Ms: Double? = null,
    val rttP95Ms: Double? = null,
    val upJitterMs: Double? = null,
    val downJitterMs: Double? = null,
    val upReordered: Int = 0,
    val downReordered: Int = 0,
    val duplicates: Int = 0,
    val mosUp: Double? = null,
    val mosDown: Double? = null,
    val lanDownMbps: Double? = null,
    val lanUpMbps: Double? = null,
    val error: String? = null,
)

data class ConnectionTotals(val probesSent: Int, val probesOk: Int, val probesFailed: Int, val linkSamples: Int, val sleepGaps: Int, val unmeasuredSeconds: Double)

data class ConnectionReport(
    val config: ConnectionTestConfig,
    val analyticsVersion: String,
    val startUtc: Instant,
    val endUtc: Instant?,
    val activeSeconds: Double,
    val status: ConnectionTestStatus,
    val segments: List<ConnectionSegmentSummary>,
    val targets: List<ConnectionSegmentSummary>,
    val events: List<ConnectionEvent>,
    val problemsBySegment: Map<ConnectionSegment, Int>,
    val verdicts: List<UseVerdict>,
    val voiceRFactor: Double?,
    val voiceMos: Double?,
    val link: ConnectionLinkSummary,
    val speedTests: List<SpeedTestResult>,
    val twoWayTests: List<TwoWayResult>,
    val markers: List<ConnectionMarker>,
    val totals: ConnectionTotals,
    val probes: List<ConnectionProbeResult>,
    val linkSamples: List<ConnectionLinkSample>,
)

data class ConnectionTestRecord(val config: ConnectionTestConfig, val startUtc: Instant, val endUtc: Instant?, val status: ConnectionTestStatus, val probeCount: Int, val activeSeconds: Double?)

data class ConnectionProgress(
    val config: ConnectionTestConfig,
    val status: ConnectionTestStatus,
    val startUtc: Instant,
    val activeSeconds: Double,
    val remainingSeconds: Double?,
    val tick: Long,
    val lastProbes: List<ConnectionProbeResult>,
    val lastLink: ConnectionLinkSample?,
    val activity: String?,
)

// ---- platform services (implemented in :app)

/** Sends probes bound to the Wi-Fi network. Implementations never throw for network failures. */
interface ConnectionProber {
    suspend fun getContext(interfaceId: String): ConnectionContext
    suspend fun readLink(interfaceId: String): WlanLinkReading?
    fun interfaceBytes(interfaceId: String): Pair<Long, Long>?
    suspend fun icmp(target: String, source: String, ttl: Int, timeoutMs: Int): ProbeReply
    suspend fun tcpConnect(target: String, port: Int, source: String, timeoutMs: Int): ProbeReply
    suspend fun dnsQuery(server: String, name: String, source: String, timeoutMs: Int): ProbeReply
    suspend fun https(url: String, source: String, timeoutMs: Int): ProbeReply
}

data class TransferResult(val bytes: Long, val seconds: Double, val error: String?)

interface SpeedTestClient {
    val provider: String
    val server: String
    suspend fun download(source: String, streams: Int, duration: Duration): TransferResult
    suspend fun upload(source: String, streams: Int, duration: Duration): TransferResult
}

interface TwoWayClient {
    val responder: String
    suspend fun runStream(source: String, duration: Duration, packetsPerSecond: Int, payloadBytes: Int): TwoWayResult
    suspend fun check(): Pair<Boolean, String>
}
