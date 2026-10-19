package com.wirelessscanner.android.platform

import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.location.LocationManager
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.TrafficStats
import android.net.wifi.ScanResult
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.SystemClock
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.ie.InformationElements
import com.wirelessscanner.core.net.DnsPacket
import com.wirelessscanner.core.net.PingParser
import com.wirelessscanner.core.responder.NetBinder
import kotlinx.coroutines.*
import java.io.IOException
import java.net.*
import java.time.Instant
import java.util.concurrent.TimeUnit
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLException
import kotlin.random.Random

const val ADAPTER_ID = "wlan0"

/**
 * The tablet's Wi-Fi radio as a NetworkAdapterProvider (Windows: WlanAdapter). One cycle = startScan + wait for
 * SCAN_RESULTS_AVAILABLE. Android allows 4 scans per 2 minutes unless throttling is off in Developer options: a refused or
 * not-updated scan is a STALE cycle with the reason, never presented as fresh (DR-0015: guide + detect + fall back).
 */
class WifiProvider(private val context: Context, private val log: AppLog) : NetworkAdapterProvider {
    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    @Volatile private var pending: CompletableDeferred<Boolean>? = null
    @Volatile var lastRefusedAt: Instant? = null
        private set
    @Volatile var refusedCount = 0
        private set

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context?, i: Intent?) {
            pending?.complete(i?.getBooleanExtra(WifiManager.EXTRA_RESULTS_UPDATED, false) ?: false)
        }
    }

    init {
        val f = IntentFilter(WifiManager.SCAN_RESULTS_AVAILABLE_ACTION)
        if (Build.VERSION.SDK_INT >= 33) context.applicationContext.registerReceiver(receiver, f, Context.RECEIVER_NOT_EXPORTED)
        else context.applicationContext.registerReceiver(receiver, f)
    }

    val wifiEnabled get() = wifi.isWifiEnabled
    val locationEnabled get() = (context.getSystemService(Context.LOCATION_SERVICE) as LocationManager).isLocationEnabled
    /** API 30+: whether Android throttles app scans (Developer options → Wi-Fi scan throttling). Null below API 30. */
    val throttleEnabled: Boolean? get() = if (Build.VERSION.SDK_INT >= 30) wifi.isScanThrottleEnabled else null
    val recentlyRefused get() = lastRefusedAt?.let { Instant.now().minusSeconds(130) < it } ?: false

    override suspend fun interfaces(): List<NetworkInterfaceInfo> {
        val ni = runCatching { NetworkInterface.getByName(ADAPTER_ID) }.getOrNull()
        val ip = ni?.inetAddresses?.toList()?.firstOrNull { it is Inet4Address }?.hostAddress ?: ""
        return listOf(NetworkInterfaceInfo(ADAPTER_ID, "Wi-Fi", "${Build.MANUFACTURER} ${Build.MODEL} Wi-Fi", wifi.isWifiEnabled, "randomised per network", ip))
    }

    @SuppressLint("MissingPermission")
    override suspend fun scan(interfaceId: String, timeoutMs: Long): ScanCycleResult {
        val requested = Instant.now()
        val t0 = SystemClock.elapsedRealtime()
        fun elapsed() = (SystemClock.elapsedRealtime() - t0).toDouble()
        if (!Permissions.hasLocation(context)) return ScanCycleResult.failure(requested, "no_permission", "Location permission is required by Android to read Wi-Fi scan results.")
        if (!wifi.isWifiEnabled && !wifi.isScanAlwaysAvailable) return ScanCycleResult.failure(requested, "wifi_off", "Wi-Fi is off. Turn on Wi-Fi to scan.")
        if (!locationEnabled) return ScanCycleResult.failure(requested, "location_off", "Location is off. Android returns no Wi-Fi scan results while location is off.")
        val d = CompletableDeferred<Boolean>()
        pending = d
        @Suppress("DEPRECATION")
        val accepted = try { wifi.startScan() } catch (e: SecurityException) { return ScanCycleResult.failure(requested, "no_permission", e.message ?: "permission") }
        val (status, code, msg) = if (!accepted) {
            lastRefusedAt = Instant.now(); refusedCount++
            Triple(ScanCycleStatus.Stale, "scan_throttled", "Android refused the scan request (scan throttling: 4 scans per 2 minutes). Showing the last results.")
        } else {
            when (withTimeoutOrNull(timeoutMs) { d.await() }) {
                true -> Triple(ScanCycleStatus.Fresh, null, null)
                false -> Triple(ScanCycleStatus.Stale, "scan_failed", "The scan did not produce new results; showing the last results.")
                null -> Triple(ScanCycleStatus.Stale, "scan_timeout", "No scan completion within ${timeoutMs / 1000} s; showing the last results.")
            }
        }
        pending = null
        val list = try { wifi.scanResults.map(::toAccessPoint) } catch (e: SecurityException) { return ScanCycleResult.failure(requested, "no_permission", e.message ?: "permission") }
        return ScanCycleResult(status, requested, Instant.now(), elapsed(), list, code, msg)
    }

    @SuppressLint("MissingPermission")
    override suspend fun connectionInfo(interfaceId: String): ClientConnectionInfo? {
        @Suppress("DEPRECATION") val info: WifiInfo = wifi.connectionInfo ?: return null
        val connected = info.networkId != -1 && info.bssid != null && info.bssid != "02:00:00:00:00:00"
        if (!connected) return ClientConnectionInfo(false, "disconnected")
        val (band, ch) = ChannelMath.bandAndChannel(info.frequency)
        return ClientConnectionInfo(true, "connected", ssidOf(info), info.bssid?.uppercase(), if (Build.VERSION.SDK_INT >= 30) InformationElements.standardName(info.wifiStandard) else null,
            InformationElements.qualityFromRssi(info.rssi), if (Build.VERSION.SDK_INT >= 29) info.rxLinkSpeedMbps.toDouble().takeIf { it > 0 } else null,
            if (Build.VERSION.SDK_INT >= 29) info.txLinkSpeedMbps.toDouble().takeIf { it > 0 } else info.linkSpeed.toDouble(), ch, band)
    }

    companion object {
        fun ssidOf(info: WifiInfo): String? {
            @Suppress("DEPRECATION") val s = info.ssid ?: return null
            if (s == WifiManager.UNKNOWN_SSID) return null
            return s.removeSurrounding("\"")
        }

        fun toAccessPoint(r: ScanResult): AccessPoint {
            @Suppress("DEPRECATION") val rawSsid = r.SSID ?: ""
            val ssid = rawSsid.removeSurrounding("\"").ifEmpty { "<Hidden SSID>" }
            val (band, channel) = ChannelMath.bandAndChannel(r.frequency)
            val ie: ByteArray? = if (Build.VERSION.SDK_INT >= 30) InformationElements.assemble(r.informationElements.map {
                val buf = it.bytes; val b = ByteArray(buf.remaining()); buf.get(b); InformationElements.Element(it.id, it.idExt, b)
            }) else null
            val caps = r.capabilities ?: ""
            val privacy = listOf("WEP", "WPA", "RSN", "SAE", "EAP", "PSK", "OWE").any { caps.uppercase().contains(it) }
            val width = ie?.let { InformationElements.parseChannelWidth(it, band) } ?: when (r.channelWidth) {
                ScanResult.CHANNEL_WIDTH_20MHZ -> 20; ScanResult.CHANNEL_WIDTH_40MHZ -> 40; ScanResult.CHANNEL_WIDTH_80MHZ -> 80
                ScanResult.CHANNEL_WIDTH_160MHZ, ScanResult.CHANNEL_WIDTH_80MHZ_PLUS_MHZ -> 160; 5 -> 320; else -> null
            }
            val load = ie?.let { InformationElements.parseBssLoad(it) }
            val wps = ie?.let { InformationElements.parseWps(it) }
            val ageMs = (SystemClock.elapsedRealtime() * 1000 - r.timestamp) / 1000.0
            val bssid = (r.BSSID ?: "").uppercase()
            return AccessPoint(
                bssid = bssid, ssid = ssid, band = band, channel = channel, channelWidth = width, rssi = r.level,
                snr = r.level - WirelessScannerConstants.ASSUMED_NOISE_FLOOR_DBM, quality = InformationElements.qualityFromRssi(r.level),
                securityType = if (ie != null && ie.isNotEmpty()) InformationElements.parseSecurity(ie, privacy) else InformationElements.securityFromCapabilities(caps),
                vendorOui = InformationElements.resolveVendor(bssid), supportedRates = ie?.let { InformationElements.parseRates(it) }?.takeIf { it.isNotEmpty() },
                lastSeen = Instant.now().minusMillis(ageMs.toLong().coerceAtLeast(0)), beaconAgeMs = ageMs.coerceAtLeast(0.0),
                phyType = if (Build.VERSION.SDK_INT >= 30) InformationElements.standardName(r.wifiStandard) else null,
                centerFrequencyMhz = r.frequency.toDouble(), stationCount = load?.stations, channelUtilizationPct = load?.utilizationPct, admissionCapacity = load?.admissionCapacity,
                wpsManufacturer = wps?.manufacturer, wpsModel = wps?.model, wpsModelNumber = wps?.modelNumber, wpsDeviceName = wps?.deviceName, wpsUuid = wps?.uuid,
                apMldMac = if (Build.VERSION.SDK_INT >= 33) r.apMldMacAddress?.toString()?.uppercase() else null,
            )
        }
    }
}

/** Finds the Wi-Fi Network and binds sockets to it (probes never leave over mobile data or Ethernet). */
class WifiNetwork(private val context: Context) {
    private val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    @Suppress("DEPRECATION")
    fun network(): Network? = cm.allNetworks.firstOrNull { cm.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true }
    fun link(n: Network?): LinkProperties? = n?.let { cm.getLinkProperties(it) }
    @Suppress("DEPRECATION")
    fun others(): List<String> = cm.allNetworks.mapNotNull { n ->
        val c = cm.getNetworkCapabilities(n) ?: return@mapNotNull null
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || !c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) return@mapNotNull null
        when { c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "mobile data"; c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "Ethernet"; c.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> "VPN"; else -> "other network" }
    }

    fun binder(): NetBinder {
        val n = network()
        return object : NetBinder {
            override fun newSocket(): Socket = (n?.socketFactory ?: javax.net.SocketFactory.getDefault()).createSocket()
            override fun newDatagramSocket(): DatagramSocket = DatagramSocket().also { s -> n?.bindSocket(s) }
            override fun resolve(host: String): InetAddress = (n?.getAllByName(host) ?: InetAddress.getAllByName(host)).first { it is Inet4Address }
            override fun openHttp(url: URL): HttpURLConnection = ((n?.openConnection(url)) ?: url.openConnection()) as HttpURLConnection
        }
    }
}

/** Connection-test probes on Android (Windows: WindowsConnectionProber). ICMP via the system ping; TCP/DNS/HTTPS bound to Wi-Fi. */
class AndroidProber(private val context: Context, private val wifiNet: WifiNetwork, private val provider: WifiProvider) : ConnectionProber {
    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager

    override suspend fun getContext(interfaceId: String): ConnectionContext = withContext(Dispatchers.IO) {
        val n = wifiNet.network()
        val lp = wifiNet.link(n)
        val info = provider.connectionInfo(interfaceId)
        if (n == null || lp == null || info == null || !info.connected) return@withContext ConnectionContext(false, interfaceId, "Wi-Fi", problem = if (!wifi.isWifiEnabled) "Wi-Fi is off." else "Not connected over Wi-Fi.")
        val ip = lp.linkAddresses.map { it.address }.firstOrNull { it is Inet4Address }?.hostAddress
        val gws = lp.routes.filter { it.isDefaultRoute && it.gateway is Inet4Address }.mapNotNull { it.gateway?.hostAddress }.distinct()
        @Suppress("DEPRECATION") val rssi = wifi.connectionInfo?.rssi
        ConnectionContext(true, interfaceId, lp.interfaceName ?: "Wi-Fi", null, info.ssid, info.bssid, info.channel, info.band, info.phyType, rssi,
            info.rxRateMbps, info.txRateMbps, ip, gws, lp.dnsServers.mapNotNull { it.hostAddress }, wifiNet.others(),
            problem = if (ip == null) "The Wi-Fi network has no IPv4 address." else null)
    }

    override suspend fun readLink(interfaceId: String): WlanLinkReading? {
        val c = provider.connectionInfo(interfaceId) ?: return null
        @Suppress("DEPRECATION") val rssi = wifi.connectionInfo?.rssi
        return WlanLinkReading(c.connected, c.state, c.ssid, c.bssid, c.phyType, if (c.connected) rssi else null, c.signalQualityPct, c.rxRateMbps, c.txRateMbps, c.channel, null)
    }

    /** Whole-device traffic (Android exposes no per-interface counters to apps). */
    override fun interfaceBytes(interfaceId: String): Pair<Long, Long>? {
        val rx = TrafficStats.getTotalRxBytes(); val tx = TrafficStats.getTotalTxBytes()
        return if (rx < 0 || tx < 0) null else rx to tx
    }

    override suspend fun icmp(target: String, source: String, ttl: Int, timeoutMs: Int): ProbeReply = withContext(Dispatchers.IO) {
        val p = try {
            ProcessBuilder("/system/bin/ping", "-n", "-c", "1", "-W", PingParser.timeoutSeconds(timeoutMs).toString(), "-t", ttl.toString(), "-I", source, target)
                .redirectErrorStream(true).start()
        } catch (e: IOException) { return@withContext ProbeReply.fail(ProbeOutcome.Error, "ping unavailable: ${e.message}") }
        try {
            val finished = p.waitFor(timeoutMs + 1500L, TimeUnit.MILLISECONDS)
            if (!finished) { p.destroy(); return@withContext ProbeReply.fail(ProbeOutcome.Timeout) }
            PingParser.parse(p.inputStream.bufferedReader().readText(), p.exitValue(), target)
        } finally { p.destroy() }
    }

    override suspend fun tcpConnect(target: String, port: Int, source: String, timeoutMs: Int): ProbeReply = withContext(Dispatchers.IO) {
        val n = wifiNet.network() ?: return@withContext ProbeReply.fail(ProbeOutcome.Unreachable, "no Wi-Fi network")
        val s = n.socketFactory.createSocket()
        try {
            s.tcpNoDelay = true; s.setSoLinger(true, 0)
            val t0 = System.nanoTime()
            s.connect(InetSocketAddress(InetAddress.getByName(target), port), timeoutMs)
            ProbeReply(ProbeOutcome.Ok, (System.nanoTime() - t0) / 1e6, target)
        } catch (e: SocketTimeoutException) { ProbeReply.fail(ProbeOutcome.Timeout) }
        catch (e: ConnectException) {
            if (e.message?.contains("ECONNREFUSED") == true || e.message?.contains("refused", true) == true) ProbeReply.fail(ProbeOutcome.Refused, "host reachable, port $port closed")
            else if (e.message?.contains("ETIMEDOUT") == true) ProbeReply.fail(ProbeOutcome.Timeout)
            else ProbeReply.fail(ProbeOutcome.Unreachable, e.message)
        }
        catch (e: NoRouteToHostException) { ProbeReply.fail(ProbeOutcome.Unreachable, e.message) }
        catch (e: IOException) { ProbeReply.fail(ProbeOutcome.Error, e.message) }
        finally { try { s.close() } catch (_: IOException) {} }
    }

    override suspend fun dnsQuery(server: String, name: String, source: String, timeoutMs: Int): ProbeReply = withContext(Dispatchers.IO) {
        val n = wifiNet.network() ?: return@withContext ProbeReply.fail(ProbeOutcome.Unreachable, "no Wi-Fi network")
        val id = Random.nextInt(1, 65535)
        val q = DnsPacket.buildQuery(id, name)
        DatagramSocket().use { s ->
            try {
                n.bindSocket(s); s.soTimeout = timeoutMs
                val t0 = System.nanoTime()
                s.send(DatagramPacket(q, q.size, InetAddress.getByName(server), 53))
                val buf = ByteArray(1500)
                val deadline = t0 + timeoutMs * 1_000_000L
                while (true) {
                    val dp = DatagramPacket(buf, buf.size)
                    s.soTimeout = maxOf(1, ((deadline - System.nanoTime()) / 1_000_000).toInt())
                    s.receive(dp)
                    val parsed = DnsPacket.parseResponse(buf, dp.length, id) ?: continue
                    return@withContext DnsPacket.reply(parsed.first, parsed.second, (System.nanoTime() - t0) / 1e6, server)
                }
                @Suppress("UNREACHABLE_CODE") ProbeReply.fail(ProbeOutcome.Error)
            } catch (e: SocketTimeoutException) { ProbeReply.fail(ProbeOutcome.Timeout) }
            catch (e: IOException) { ProbeReply.fail(if (e is PortUnreachableException || e is NoRouteToHostException) ProbeOutcome.Unreachable else ProbeOutcome.Error, e.message) }
        }
    }

    override suspend fun https(url: String, source: String, timeoutMs: Int): ProbeReply = withContext(Dispatchers.IO) {
        val n = wifiNet.network() ?: return@withContext ProbeReply.fail(ProbeOutcome.Unreachable, "no Wi-Fi network")
        val t0 = System.nanoTime()
        try {
            val c = n.openConnection(URL(url)) as HttpURLConnection
            c.useCaches = false; c.connectTimeout = timeoutMs; c.readTimeout = timeoutMs; c.instanceFollowRedirects = false
            c.setRequestProperty("User-Agent", "WirelessScanner-ConnectionTest/1"); c.setRequestProperty("Connection", "close")
            try {
                val code = c.responseCode
                val ms = (System.nanoTime() - t0) / 1e6
                if (code < 400) ProbeReply(ProbeOutcome.Ok, ms, url, "HTTP $code") else ProbeReply.fail(ProbeOutcome.HttpError, "HTTP $code")
            } finally { c.disconnect() }
        } catch (e: SocketTimeoutException) { ProbeReply.fail(ProbeOutcome.Timeout) }
        catch (e: UnknownHostException) { ProbeReply.fail(ProbeOutcome.DnsError, "name not resolved") }
        catch (e: SSLException) { ProbeReply.fail(ProbeOutcome.HttpError, "TLS failure") }
        catch (e: NoRouteToHostException) { ProbeReply.fail(ProbeOutcome.Unreachable, e.message) }
        catch (e: IOException) { ProbeReply.fail(ProbeOutcome.HttpError, e.message) }
    }
}

object Permissions {
    fun hasLocation(c: Context) = c.checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION) == android.content.pm.PackageManager.PERMISSION_GRANTED
    fun required(): Array<String> = buildList {
        add(android.Manifest.permission.ACCESS_FINE_LOCATION); add(android.Manifest.permission.ACCESS_COARSE_LOCATION)
        if (Build.VERSION.SDK_INT >= 33) { add(android.Manifest.permission.NEARBY_WIFI_DEVICES); add(android.Manifest.permission.POST_NOTIFICATIONS) }
    }.toTypedArray()
}
