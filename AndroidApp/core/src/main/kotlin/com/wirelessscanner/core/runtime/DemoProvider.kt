package com.wirelessscanner.core.runtime

import com.wirelessscanner.core.domain.*
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.random.Random

/**
 * Port of MockNetworkAdapterProvider.cs (demo mode): the same synthetic networks, small RSSI fluctuation, every cycle fresh.
 * Addresses are synthetic stand-ins (privacy rule).
 */
class DemoProvider(private val random: Random = Random.Default) : NetworkAdapterProvider {
    private val base: List<AccessPoint> = run {
        val now = Instant.now()
        fun ap(b: String, s: String, host: String?, band: String, ch: Int, w: Int, rssi: Int, snr: Int, q: Int, sec: String, vendor: String, rates: List<String>?, tsf: Double? = null) =
            AccessPoint(b, s, host, band, ch, w, rssi, null, snr, q, sec, "Infrastructure", vendor, rates, now, tsfOffsetMs = tsf)
        listOf(
            ap("8C:5A:25:04:91:B0", "PlantOps", "ap-ops-01.local", "5 GHz", 36, 80, -52, 43, 85, "WPA2-Enterprise", "Cisco Systems", listOf("54", "150", "300", "867")),
            ap("8C:5A:25:04:91:B1", "PlantOps", "ap-ops-02.local", "2.4 GHz", 6, 20, -68, 27, 64, "WPA2-Enterprise", "Cisco Systems", listOf("11", "54", "150")),
            ap("A0:B1:C2:D3:E4:01", "Facilities-IoT", "iot-gateway-01", "2.4 GHz", 1, 20, -74, 18, 52, "WPA2-Personal", "Hewlett Packard", listOf("11", "54")),
            ap("A0:B1:C2:D3:E4:02", "Facilities-IoT", "iot-gateway-02", "5 GHz", 149, 40, -82, 12, 36, "WPA2-Personal", "Hewlett Packard", listOf("54", "300")),
            ap("00:17:88:A1:B2:C3", "Guest-Portal", null, "2.4 GHz", 11, 20, -58, 32, 70, "Open", "Philips Lighting", listOf("11", "54")),
            ap("9A:3C:5E:1F:42:70", "Home", null, "2.4 GHz", 6, 20, -45, 50, 90, "WPA3-Personal", "Unknown Vendor", null, 81_000_000.2),
            ap("9A:3C:5E:1F:42:73", "Home-IoT", null, "2.4 GHz", 6, 20, -46, 49, 88, "WPA2-Personal", "Unknown Vendor", null, 81_000_000.5),
            ap("9A:3C:5E:26:42:70", "Home", null, "5 GHz", 149, 80, -50, 45, 80, "WPA3-Personal", "Unknown Vendor", null, 81_000_950.1),
            ap("9A:3C:5E:26:42:74", "Home-Guest", null, "5 GHz", 149, 80, -51, 44, 78, "WPA2-Personal", "Unknown Vendor", null, 81_000_950.6),
            ap("9A:3C:5E:26:42:76", "<Hidden SSID>", null, "5 GHz", 149, 80, -52, 43, 76, "WPA2-Personal", "Unknown Vendor", null, 81_000_950.9),
            ap("3C:5D:7E:19:61:43", "Home", null, "5 GHz", 44, 80, -71, 24, 58, "WPA3-Personal", "Unknown Vendor", null, 3_700_000.0),
        )
    }
    private val cycles = AtomicInteger()
    var cycleStatusOverride: ((Int) -> ScanCycleStatus)? = null

    override suspend fun interfaces() = listOf(NetworkInterfaceInfo("demo-wlan0", "Demo Wi-Fi", "Simulated adapter (demo mode)", true, "02:00:00:00:00:01", "192.0.2.100"))

    fun accessPoints(): List<AccessPoint> = base.map { ap ->
        val r = (ap.rssi + random.nextInt(-3, 4)).coerceIn(-100, -30)
        ap.copy(rssi = r, quality = (2 * (r + 100)).coerceIn(0, 100), snr = r - WirelessScannerConstants.ASSUMED_NOISE_FLOOR_DBM, lastSeen = Instant.now())
    }

    override suspend fun scan(interfaceId: String, timeoutMs: Long): ScanCycleResult {
        val requested = Instant.now()
        val n = cycles.incrementAndGet()
        val status = cycleStatusOverride?.invoke(n) ?: ScanCycleStatus.Fresh
        if (status == ScanCycleStatus.Failed) return ScanCycleResult.failure(requested, "demo_failure", "Simulated failure")
        val list = accessPoints().map { it.copy(beaconAgeMs = 50.0) }
        val done = Instant.now()
        return ScanCycleResult(status, requested, done, (done.toEpochMilli() - requested.toEpochMilli()).toDouble(), list,
            if (status == ScanCycleStatus.Stale) "scan_timeout" else null, if (status == ScanCycleStatus.Stale) "Simulated stale cycle" else null)
    }

    override suspend fun connectionInfo(interfaceId: String) = ClientConnectionInfo(true, "connected", "PlantOps", "8C:5A:25:04:91:B0", "802.11ax", 85, 866.7, 780.0, 36, "5 GHz")
}
