package com.wirelessscanner.core

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.api.ApiData
import com.wirelessscanner.core.api.ApiHttpServer
import com.wirelessscanner.core.api.RestApi
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.ie.InformationElements
import com.wirelessscanner.core.parity.Fixture
import com.wirelessscanner.core.parity.Fixture.arr
import com.wirelessscanner.core.parity.Fixture.obj
import com.wirelessscanner.core.parity.Fixture.str
import com.wirelessscanner.core.report.*
import com.wirelessscanner.core.responder.NetBinder
import com.wirelessscanner.core.responder.ResponderClient
import com.wirelessscanner.core.responder.ResponderProtocol
import com.wirelessscanner.core.responder.ResponderServer
import com.wirelessscanner.core.runtime.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.HttpURLConnection
import java.net.URL
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset

class CoreTests {
    private fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }

    @Test fun informationElementsMatchWindows() {
        val d = Fixture.load("ie.json").obj()
        for (c in d["cases"].arr().map { it.obj() }) {
            val ie = unhex(c["ie"].str()); val name = c["name"].str()
            assertEquals(name, c["security"], InformationElements.parseSecurity(ie, c["privacy"] as Boolean))
            assertEquals(name, (c["width"] as Number?)?.toInt(), InformationElements.parseChannelWidth(ie, c["band"].str()))
            assertEquals(name, c["rates"], InformationElements.parseRates(ie))
            val load = InformationElements.parseBssLoad(ie)
            val el = c["bssLoad"]?.obj()
            if (el == null) assertNull(name, load) else {
                assertEquals(name, (el["stations"] as Number).toInt(), load!!.stations)
                assertEquals(name, (el["utilizationPct"] as Number).toDouble(), load.utilizationPct, 1e-9)
                assertEquals(name, (el["admissionCapacity"] as Number).toInt(), load.admissionCapacity)
            }
            val wps = InformationElements.parseWps(ie)
            val ew = c["wps"]?.obj()
            if (ew == null) assertNull(name, wps) else {
                assertEquals(ew["manufacturer"], wps!!.manufacturer); assertEquals(ew["model"], wps.model); assertEquals(ew["modelNumber"], wps.modelNumber)
                assertEquals(ew["deviceName"], wps.deviceName); assertEquals(ew["uuid"], wps.uuid)
            }
        }
    }

    @Test fun androidElementsReassembleToTheRawStream() {
        val raw = InformationElements.assemble(listOf(InformationElements.Element(11, 0, byteArrayOf(7, 0, -128, 0x10, 0x27)),
            InformationElements.Element(255, 36, byteArrayOf(0, 0, 2))))
        assertArrayEquals(byteArrayOf(11, 5, 7, 0, -128, 0x10, 0x27, -1, 4, 36, 0, 0, 2), raw)
        assertEquals("WPA2/WPA3-Personal", InformationElements.securityFromCapabilities("[WPA2-PSK-CCMP][RSN-SAE+PSK-CCMP][ESS]"))
        assertEquals("WPA2-Enterprise", InformationElements.securityFromCapabilities("[WPA2-EAP-CCMP][RSN-EAP-CCMP][ESS]"))
        assertEquals("Open", InformationElements.securityFromCapabilities("[ESS]"))
        assertEquals("OWE (Enhanced Open)", InformationElements.securityFromCapabilities("[RSN-OWE-CCMP][ESS]"))
    }

    @Test fun frequencyMapsToBandAndChannel() {
        assertEquals("2.4 GHz" to 6, ChannelMath.bandAndChannel(2437))
        assertEquals("2.4 GHz" to 14, ChannelMath.bandAndChannel(2484))
        assertEquals("5 GHz" to 36, ChannelMath.bandAndChannel(5180))
        assertEquals("5 GHz" to 165, ChannelMath.bandAndChannel(5825))
        assertEquals("6 GHz" to 1, ChannelMath.bandAndChannel(5955))
        assertEquals("6 GHz" to 2, ChannelMath.bandAndChannel(5935))
    }

    private fun sampleConfig() = SaturationTestConfig(name = "Round trip", scope = SaturationScope.Mixed, targetSsids = listOf("A"), targetBssids = listOf("02:00:00:00:00:01"),
        targetUnits = listOf(SaturationUnitTarget("U-1", "Unit", UnitConfidence.High, listOf("e"), listOf(SaturationUnitMember("02:00:00:00:00:01", "A", "5 GHz", "5 GHz@x")))),
        duration = null, geo = GeoTag(1.5, -2.25, 10.0, "manual", Instant.parse("2026-10-19T09:00:00Z"), "B", "2", "Z"),
        environment = SaturationEnvironment("1.5.0", "Android 15", "Tablet", "UTC", 0, "wlan0", "02:00:00:00:00:99", null, null, null,
            ClientConnectionInfo(true, "connected", "A", "02:00:00:00:00:01", "802.11ax", 80, 866.7, 780.0, 36, "5 GHz"), Instant.parse("2026-10-19T09:00:00Z")))

    @Test fun storageRoundTripKeepsEveryField() {
        val cfg = sampleConfig()
        assertEquals(cfg, JsonRead.read(Json.write(cfg), SaturationTestConfig::class.java))
        val cycle = SaturationCycle(3, Instant.parse("2026-10-19T09:00:10.123Z"), 10_000, ScanCycleStatus.Stale, 1234.5, 2, "scan_timeout", "x",
            listOf(SaturationObservation(3, "02:00:00:00:00:01", "A", ObservationStatus.Observed, -50, 90, 36, "5 GHz", 80, 120.0, 1, 2, "WPA2", "V", "802.11ax", 100, 5180.0, 4, 37.5, -70)))
        assertEquals(cycle, JsonRead.read(Json.write(cycle), SaturationCycle::class.java))
        val cc = ConnectionTestConfig(name = "C", duration = Duration.ofMinutes(15).plusMillis(250), contextAtStart = ConnectionContext(true, ssid = "A", gateways = listOf("192.0.2.1")))
        assertEquals(cc, JsonRead.read(Json.write(cc), ConnectionTestConfig::class.java))
        assertEquals(Duration.ofDays(2).plusHours(3).plusMillis(5), JsonRead.timeSpan(ReportText.timeSpan(Duration.ofDays(2).plusHours(3).plusMillis(5))))
        assertEquals("00:15:00", ReportText.timeSpan(Duration.ofMinutes(15)))
        val overrides = UnitOverrides(mutableMapOf("02:00:00:00:00:01" to "M-1"), mutableMapOf("U-1" to "Router"))
        assertEquals(overrides, JsonRead.read(Json.write(overrides), UnitOverrides::class.java))
    }

    @Test fun secretsNeverReachJson() {
        val s = Json.write(ResponderSettings("192.0.2.10", 5410, "c2VjcmV0LWtleS1zaG91bGQtbm90LWxlYWs="))
        assertFalse(s.contains("c2VjcmV0"))
        assertTrue(s.contains("\"isConfigured\": true"))
    }

    @Test fun csvNeutralisesFormulasButKeepsNumbers() {
        assertEquals("'=HYPERLINK(\"\"x\"\")".let { "\"$it\"" }, ReportText.csv("=HYPERLINK(\"x\")"))
        assertEquals("-67", ReportText.csv("-67"))
        assertEquals("'+cmd", ReportText.csv("+cmd"))
        assertEquals("'@SUM(A1)", ReportText.csv("@SUM(A1)"))
        assertEquals("\"a,b\"", ReportText.csv("a,b"))
        assertEquals("", ReportText.csv(null))
    }

    @Test fun numberFormatsFollowDotNet() {
        assertEquals("1.5", ReportText.fmt(1.5, "0.##"))
        assertEquals("2", ReportText.fmt(2.0, "0.##"))
        assertEquals("0.000", ReportText.fmt(0.0, "0.000"))
        assertEquals("3", ReportText.fmt(2.5, "0"))
        assertEquals("-3", ReportText.fmt(-2.5, "0"))
        assertEquals("+5.5", ReportText.signedHours(330)); assertEquals("-5", ReportText.signedHours(-300)); assertEquals("0", ReportText.signedHours(0))
        assertEquals("2026-10-19T09:00:00.1Z", Json.isoUtc(Instant.parse("2026-10-19T09:00:00.100Z")))
        assertEquals("2026-10-19T09:00:00Z", Json.isoUtc(Instant.parse("2026-10-19T09:00:00Z")))
        assertEquals("1.02:03:04", ReportText.dur(93784.0)); assertEquals("02:03:04", ReportText.dur(7384.0))
    }

    @Test fun exportsProduceEveryFileFromOneReport() {
        val ctx = ReportContext("1.5.0", zone = ZoneOffset.UTC, now = { Instant.parse("2026-10-19T10:00:00Z") })
        val cfg = sampleConfig().copy(scope = SaturationScope.AllVisible, targetUnits = emptyList())
        val obs = { seq: Long, r: Int? -> SaturationObservation(seq, "02:00:00:00:00:01", "=evil", if (r == null) ObservationStatus.Missing else ObservationStatus.Observed, r, 80, if (r == null) null else 36, if (r == null) null else "5 GHz", 80, 10.0, 0, 0) }
        val cycles = (1L..30L).map { SaturationCycle(it, Instant.parse("2026-10-19T09:00:00Z").plusSeconds(5 * it), (it - 1) * 5000, ScanCycleStatus.Fresh, 1000.0, 1, null, null, listOf(obs(it, if (it in 10L..12L) null else -55 - (it % 3).toInt()))) }
        val report = SaturationAnalyzer().analyze(cfg, cycles, listOf(SaturationMarker(Instant.parse("2026-10-19T09:01:00Z"), 60_000, "Door")), Instant.parse("2026-10-19T09:00:00Z"), Instant.parse("2026-10-19T09:02:30Z"), SaturationTestStatus.Completed, 150.0)
        val sr = SaturationReports(ctx)
        val files = sr.csvBundle("t", report)
        assertEquals(listOf("t_info.csv", "t_samples.csv", "t_events.csv", "t_summary.csv", "t_cycles.csv", "t_markers.csv"), files.map { it.name })
        val samples = String(files[1].bytes, Charsets.UTF_8)
        assertTrue(samples.startsWith("﻿# WirelessScanner saturation test samples"))
        assertTrue("formula neutralised", samples.contains(",'=evil,"))
        val json = String(sr.json(report).bytes, Charsets.UTF_8)
        assertTrue(json.contains("\"schema\": \"wirelessscanner.saturation_report.v3\""))
        assertTrue(json.contains("\"freshnessLimit\": \"00:00:10\""))
        val doc = sr.document(report)
        assertTrue(doc.blocks.any { it is Block.Chart })
        assertTrue(doc.blocks.filterIsInstance<Block.Heading>().map { it.text }.containsAll(listOf("1. Test details", "2. Executive summary", "4. Scan health", "7. Tester markers")))
    }

    private class FakeData : ApiData {
        override fun live() = listOf(AccessPoint("02:00:00:00:00:01", "A", band = "5 GHz", channel = 36, channelWidth = 80, rssi = -50, snr = 45, quality = 100, securityType = "WPA2", lastSeen = Instant.EPOCH))
        override fun connection(): Any? = null
        override suspend fun connectionTests() = emptyList<ConnectionTestRecord>()
        override suspend fun sessions() = emptyList<CaptureSession>()
        override suspend fun session(id: String): Pair<CaptureSession, List<TelemetrySample>>? = null
        override suspend fun stats() = Triple(1, 2, 0.5)
    }

    @Test fun restApiAuthenticatesAndServesWindowsNames() = runBlocking {
        val api = RestApi(FakeData(), { "k3y-value" }, "1.5.0", { 8080 })
        assertEquals(401, api.handle("GET", "/api/status", emptyMap()).status)
        assertEquals(401, api.handle("GET", "/api/status?apikey=k3y-value", mapOf("X-API-Key" to "k3y-value")).status)
        assertEquals(405, api.handle("POST", "/api/status", mapOf("X-API-Key" to "k3y-value")).status)
        val live = api.handle("GET", "/api/live", mapOf("x-api-key" to "k3y-value"))
        assertEquals(200, live.status)
        assertTrue(live.body.contains("\"BSSID\": \"02:00:00:00:00:01\""))
        assertEquals(400, api.handle("GET", "/api/sessions/not-a-guid", mapOf("X-API-Key" to "k3y-value")).status)
        assertEquals(404, api.handle("GET", "/api/nothing", mapOf("X-API-Key" to "k3y-value")).status)
        assertTrue(api.handle("GET", "/api/connection", mapOf("X-API-Key" to "k3y-value")).body.contains("No connection test is running."))
        // over a real socket on localhost
        val server = ApiHttpServer(api)
        server.start(0.let { java.net.ServerSocket(0).use { s -> s.localPort } }, lan = false)
        try {
            val port = (server.javaClass.getDeclaredField("server").also { it.isAccessible = true }.get(server) as java.net.ServerSocket).localPort
            val c = URL("http://127.0.0.1:$port/api/stats").openConnection() as HttpURLConnection
            c.setRequestProperty("X-API-Key", "k3y-value")
            assertEquals(200, c.responseCode)
            assertTrue(c.inputStream.readBytes().toString(Charsets.UTF_8).contains("\"totalSamples\": 2"))
        } finally { server.stop() }
    }

    @Test fun responderLoopbackTwoWayAndThroughput() = runBlocking {
        val key = ResponderProtocol.newKey()
        val server = ResponderServer(key, 0, java.net.InetAddress.getLoopbackAddress(), "test")
        server.start()
        try {
            val client = ResponderClient(ResponderSettings("127.0.0.1", server.port, ResponderProtocol.keyToText(key)), NetBinder.Local("127.0.0.1"))
            val (ok, msg) = client.check()
            assertTrue(msg, ok)
            val r = client.runStream("127.0.0.1", Duration.ofSeconds(1), 50, 172)
            assertNull(r.error)
            assertEquals(50, r.sent)
            assertEquals(0.0, r.upLossPct!!, 0.0)
            assertEquals(0.0, r.downLossPct!!, 0.0)
            assertNotNull(r.rttP50Ms)
            val down = client.download("127.0.0.1", 2, Duration.ofSeconds(2))
            assertNull(down.error); assertTrue(down.bytes > 0)
            val up = client.upload("127.0.0.1", 2, Duration.ofSeconds(2))
            assertNull(up.error); assertTrue(up.bytes > 0)
            val wrong = ResponderClient(ResponderSettings("127.0.0.1", server.port, ResponderProtocol.keyToText(ResponderProtocol.newKey())), NetBinder.Local("127.0.0.1"))
            assertFalse(wrong.check().first)
        } finally { server.stop() }
    }

    private class MemSat : SaturationRepository {
        val cycles = ArrayList<SaturationCycle>(); var status: SaturationTestStatus? = null
        override suspend fun saveTest(config: SaturationTestConfig, startUtc: Instant, status: SaturationTestStatus) { this.status = status }
        override suspend fun updateTestStatus(testId: String, status: SaturationTestStatus, endUtc: Instant?, activeSeconds: Double?) { this.status = status }
        override suspend fun saveCycle(testId: String, cycle: SaturationCycle) { cycles.add(cycle) }
        override suspend fun saveMarker(testId: String, marker: SaturationMarker) {}
        override suspend fun getTests() = emptyList<SaturationTestRecord>()
        override suspend fun getTest(testId: String): SaturationTestRecord? = null
        override suspend fun getCycles(testId: String) = cycles
        override suspend fun getMarkers(testId: String) = emptyList<SaturationMarker>()
        override suspend fun deleteTest(testId: String) {}
    }

    @Test fun saturationRunnerSharesTheSchedulerAndPersistsInOrder() = runBlocking {
        val scheduler = ScanScheduler(DemoProvider())
        val repo = MemSat()
        val writer = OrderedPersistenceWriter(NullLogger)
        val runner = SaturationTestRunner(scheduler, repo, SaturationAnalyzer(), NullLogger, writer)
        runner.start(SaturationTestConfig(scope = SaturationScope.AllVisible, interval = Duration.ofMillis(200), duration = Duration.ofSeconds(2), interfaceId = "demo-wlan0"))
        val report = runner.waitForCompletion()!!
        assertEquals(SaturationTestStatus.Completed, report.status)
        assertTrue("cycles ${report.cycles.size}", report.cycles.size in 6..12)
        assertEquals(report.cycles.map { it.sequence }, repo.cycles.map { it.sequence })
        assertEquals(SaturationTestStatus.Completed, repo.status)
        assertTrue(report.targets.isNotEmpty())
        scheduler.dispose()
    }

    private class FakeProber : ConnectionProber {
        override suspend fun getContext(interfaceId: String) = ConnectionContext(true, ssid = "A", localIPv4 = "192.0.2.50", gateways = listOf("192.0.2.1"), dnsServers = listOf("192.0.2.1"))
        override suspend fun readLink(interfaceId: String) = WlanLinkReading(true, "connected", "A", "02:00:00:00:00:01", rssi = -50, channel = 36)
        override fun interfaceBytes(interfaceId: String) = 1000L to 500L
        override suspend fun icmp(target: String, source: String, ttl: Int, timeoutMs: Int) =
            if (ttl < 64 && target != "198.51.100.1") ProbeReply(ProbeOutcome.TtlExpired, null, if (ttl == 1) "192.0.2.1" else "198.51.100.1") else ProbeReply(ProbeOutcome.Ok, 3.0)
        override suspend fun tcpConnect(target: String, port: Int, source: String, timeoutMs: Int) = ProbeReply(ProbeOutcome.Ok, 15.0)
        override suspend fun dnsQuery(server: String, name: String, source: String, timeoutMs: Int) = ProbeReply(ProbeOutcome.Ok, 20.0)
        override suspend fun https(url: String, source: String, timeoutMs: Int) = ProbeReply(ProbeOutcome.Ok, 120.0)
    }

    private class MemConn : ConnectionRepository {
        val probes = ArrayList<ConnectionProbeResult>()
        override suspend fun saveTest(config: ConnectionTestConfig, startUtc: Instant, status: ConnectionTestStatus) {}
        override suspend fun updateTestStatus(testId: String, status: ConnectionTestStatus, endUtc: Instant?, activeSeconds: Double?) {}
        override suspend fun saveProbes(testId: String, probes: List<ConnectionProbeResult>) { this.probes.addAll(probes) }
        override suspend fun saveLinkSamples(testId: String, samples: List<ConnectionLinkSample>) {}
        override suspend fun saveMarker(testId: String, marker: ConnectionMarker) {}
        override suspend fun saveSpeedTest(testId: String, result: SpeedTestResult) {}
        override suspend fun saveTwoWay(testId: String, result: TwoWayResult) {}
        override suspend fun getTests() = emptyList<ConnectionTestRecord>()
        override suspend fun getData(testId: String) = ConnectionData(probes, emptyList(), emptyList(), emptyList(), emptyList())
        override suspend fun deleteTest(testId: String) {}
    }

    @Test fun connectionRunnerDiscoversTheIspHopAndProbesEverySegment() = runBlocking {
        val prober = FakeProber()
        val repo = MemConn()
        val runner = ConnectionTestRunner(prober, repo, ConnectionAnalyzer(), NullLogger, OrderedPersistenceWriter(NullLogger))
        runner.start(ConnectionTestConfig(duration = Duration.ofMillis(1500), tickMilliseconds = 50, contextAtStart = prober.getContext("x")))
        val r = runner.waitForCompletion()!!
        assertEquals("198.51.100.1", r.config.contextAtStart!!.ispHop)
        assertEquals(setOf(ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web), r.segments.map { it.segment }.toSet())
        assertEquals(r.totals.probesSent, repo.probes.count { it.outcome != ProbeOutcome.Skipped })
        assertEquals("GOOD", r.verdicts.first { it.use == "Browsing" }.rating)
    }
}
