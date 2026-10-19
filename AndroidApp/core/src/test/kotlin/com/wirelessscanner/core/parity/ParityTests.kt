package com.wirelessscanner.core.parity

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.analytics.SaturationObservationBuilder
import com.wirelessscanner.core.analytics.SaturationTargetTracker
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.parity.Fixture.arr
import com.wirelessscanner.core.parity.Fixture.assertSameJson
import com.wirelessscanner.core.parity.Fixture.instant
import com.wirelessscanner.core.parity.Fixture.int
import com.wirelessscanner.core.parity.Fixture.long
import com.wirelessscanner.core.parity.Fixture.num
import com.wirelessscanner.core.parity.Fixture.obj
import com.wirelessscanner.core.parity.Fixture.optInt
import com.wirelessscanner.core.parity.Fixture.optNum
import com.wirelessscanner.core.parity.Fixture.optStr
import com.wirelessscanner.core.parity.Fixture.str
import com.wirelessscanner.core.report.Json
import com.wirelessscanner.core.responder.ResponderClient
import com.wirelessscanner.core.responder.ResponderProtocol
import com.wirelessscanner.core.runtime.ConnectionTestRunner
import com.wirelessscanner.core.runtime.PathDiscovery
import com.wirelessscanner.core.selection.NetworkSelection
import com.wirelessscanner.core.selection.PhysicalUnitGrouper
import com.wirelessscanner.core.spectrum.SpectrumLayout
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Duration

/** The Kotlin core must give the C# answers (fixtures from WirelessScanner.Tests, Category=AndroidParity). */
class ParityTests {
    private fun hex(b: ByteArray) = b.joinToString("") { String.format("%02X", it) }
    private fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }

    @Test fun channelMathAndSpectrum() {
        val d = Fixture.load("channel_math.json").obj()
        for (c in d["cases"].arr().map { it.obj() }) {
            val band = c["band"].str(); val ch = c["channel"].int(); val w = c["width"].optInt()
            val at = "$band ch $ch w $w"
            assertEquals(at, c["frequency"].num(), ChannelMath.channelFrequencyMhz(band, ch), 1e-9)
            assertEquals(at, c["center"].num(), ChannelMath.blockCenterMhz(band, ch, w), 1e-9)
            val (lo, hi) = ChannelMath.occupancy(band, ch, w)
            assertEquals(at, c["low"].num(), lo, 1e-9); assertEquals(at, c["high"].num(), hi, 1e-9)
            assertEquals(at, c["dfs"], ChannelMath.isDfs(band, ch, w))
        }
        val t0 = java.time.Instant.EPOCH
        val aps = d["aps"].arr().map { Fixture.ap(it.obj(), t0) }
        for (s in d["spectrum"].arr().map { it.obj() }) {
            val axis = SpectrumLayout.axisFor(s["band"].str())!!
            val load = SpectrumLayout.channelLoad(axis, aps)
            assertSameJson("load ${axis.band}", s["load"], load.mapKeys { it.key.toString() })
            assertEquals(s["leastBusy"].int(), SpectrumLayout.leastBusyChannel(axis, load))
            assertSameJson("strongest ${axis.band}", s["strongest"], SpectrumLayout.strongestPerChannel(axis, aps).mapKeys { it.key.toString() })
        }
        for (o in d["overlaps"].arr().map { it.obj() })
            assertEquals("${o["a"]}/${o["b"]}", o["overlaps"], ChannelMath.overlaps(aps.first { it.bssid == o["a"] }, aps.first { it.bssid == o["b"] }))
        for (i in d["intensity"].arr().map { it.obj() }) assertEquals(i["value"].num(), SpectrumLayout.intensity(i["rssi"].int()), 1e-12)
    }

    @Test fun saturationBuilderAndAnalyzer() {
        val d = Fixture.load("saturation.json").obj()
        val t0 = d["t0"].instant()
        val scans = d["scans"].arr().map { it.obj() }.map { s ->
            ScanCycleResult(ScanCycleStatus.valueOf(s["status"].str()), s["requestedUtc"].instant(), s["completedUtc"].instant(), s["durationMs"].num(),
                s["aps"].arr().map { Fixture.ap(it.obj(), t0) }, s["errorCode"].optStr(), s["errorMessage"].optStr())
        }
        val markers = d["markers"].arr().map { it.obj() }.map { SaturationMarker(it["wallUtc"].instant(), it["elapsedMs"].long(), it["text"].str()) }
        for (run in d["runs"].arr().map { it.obj() }) {
            val name = run["name"].str()
            val c = run["config"].obj()
            val th = c["thresholds"].obj()
            val cfg = SaturationTestConfig(
                testId = c["testId"].str(), scope = SaturationScope.valueOf(c["scope"].str()),
                targetSsids = c["targetSsids"].arr().map { it.str() }, targetBssids = c["targetBssids"].arr().map { it.str() },
                targetUnits = c["targetUnits"].arr().map { it.obj() }.map { u ->
                    SaturationUnitTarget(u["id"].str(), u["name"].str(), UnitConfidence.valueOf(u["confidence"].str()), u["evidence"].arr().map { it.str() },
                        u["members"].arr().map { it.obj() }.map { m -> SaturationUnitMember(m["bssid"].str(), m["ssid"].str(), m["band"].str(), m["radioKey"].str()) })
                },
                interval = Duration.ofMillis(c["intervalMs"].long()), duration = c["durationMs"].optNum()?.let { Duration.ofMillis(it.toLong()) },
                thresholds = SaturationThresholds(th["dropDb"].num(), th["peakDb"].num(), th["baselineWindow"].int(), th["minBaselineSamples"].int(),
                    th["dropoutMissedCycles"].int(), th["strongestChangeHysteresisDb"].num(), th["goodDbm"].int(), th["fairDbm"].int(), th["unusableDbm"].int(),
                    th["freshnessToleranceSeconds"].num()),
            )
            val elapsed = run["elapsedMs"].arr().map { it.long() }
            val tracker = SaturationTargetTracker(cfg)
            val cycles = scans.mapIndexed { i, s ->
                val seq = (i + 1).toLong()
                SaturationCycle(seq, s.completedUtc, elapsed[i], s.status, s.durationMs, s.accessPoints.size, s.errorCode, s.errorMessage,
                    SaturationObservationBuilder.build(cfg, tracker, s, seq))
            }
            val r = SaturationAnalyzer().analyze(cfg, cycles, markers, t0, t0.plusSeconds(d["endSeconds"].long()), SaturationTestStatus.Completed, run["measuredActive"].num())
            val e = run["expected"].obj()
            assertSameJson("$name observations", e["observations"], r.cycles.map { it.observations })
            assertEquals(name, e["activeSeconds"].num(), r.activeSeconds, 1e-9)
            assertSameJson("$name health", e["health"], r.health)
            assertSameJson("$name targets", e["targets"], r.targets)
            assertSameJson("$name ssids", e["ssids"], r.ssids)
            assertSameJson("$name events", e["events"], r.events)
            assertSameJson("$name units", e["units"], r.unitSummaries)
        }
    }

    @Test fun physicalUnits() {
        val d = Fixture.load("units.json").obj()
        val t0 = d["t0"].instant()
        val g = PhysicalUnitGrouper()
        val steps = d["steps"].arr().map { it.obj() }
        fun snap() = g.units.map { u -> Json.obj("id" to u.id, "name" to u.name, "isCustomName" to u.isCustomName, "confidence" to u.confidence, "evidence" to u.evidence,
            "radios" to u.radios, "vendor" to u.vendor, "model" to u.model, "sameSsidUnitIds" to u.sameSsidUnitIds, "isMeshCandidate" to u.isMeshCandidate) }
        var k = 0
        d["scans"].arr().forEachIndexed { i, s ->
            g.observe(s.arr().map { Fixture.ap(it.obj(), t0) }, t0.plusSeconds(5L * i))
            assertSameJson(steps[k]["what"].str(), steps[k]["units"], snap()); k++
        }
        g.moveToNewUnit("80:00:00:4C:7D:F3"); assertSameJson("split", steps[k++]["units"], snap())
        g.rename("U-9A00001F4270", "Owner router"); assertSameJson("rename", steps[k++]["units"], snap())
        g.moveToUnit("C4:00:00:31:5B:F0", "U-3C0000194361"); assertSameJson("merge", steps[k++]["units"], snap())
        g.reset("U-3C0000194361"); assertSameJson("reset", steps[k++]["units"], snap())
        for (f in d["families"].arr().map { it.obj() }) assertEquals("${f["a"]}/${f["b"]}", f["same"], PhysicalUnitGrouper.sameFamily(f["a"].str(), f["b"].str()))
    }

    @Test fun connectionAnalytics() {
        val d = Fixture.load("connection.json").obj()
        val t0 = d["t0"].instant()
        val probes = d["probes"].arr().map { it.obj() }.map { p ->
            ConnectionProbeResult(p["sequence"].long(), p["wallUtc"].instant(), p["elapsedMs"].long(), ConnectionSegment.valueOf(p["segment"].str()), p["target"].str(),
                ProbeMethod.valueOf(p["method"].str()), ProbeOutcome.valueOf(p["outcome"].str()), p["rttMs"].optNum(), p["detail"].optStr())
        }
        val link = d["link"].arr().map { it.obj() }.map { l ->
            ConnectionLinkSample(l["sequence"].long(), l["wallUtc"].instant(), l["elapsedMs"].long(), l["connected"] as Boolean, l["ssid"].optStr(), l["bssid"].optStr(),
                l["channel"].optInt(), l["rssi"].optInt(), l["signalQualityPct"].optInt(), l["rxRateMbps"].optNum(), l["txRateMbps"].optNum(), l["localIp"].optStr(), l["gateway"].optStr(),
                (l["ifBytesIn"] as Number?)?.toLong(), (l["ifBytesOut"] as Number?)?.toLong(), null, (l["gapMs"] as Number?)?.toLong())
        }
        val speed = d["speed"].arr().map { it.obj() }.map { s ->
            SpeedTestResult(s["startUtc"].instant(), s["elapsedMs"].long(), s["provider"].str(), s["server"].str(), s["streams"].int(), s["secondsPerDirection"].num(),
                s["downloadMbps"].optNum(), s["uploadMbps"].optNum(), s["bytesDown"].long(), s["bytesUp"].long(), s["idleLatencyMs"].optNum(), s["loadedLatencyDownMs"].optNum(),
                s["loadedLatencyUpMs"].optNum(), s["loadedLatencyP95Ms"].optNum(), s["latencyIncreaseMs"].optNum(), s["bufferbloatGrade"].str(), s["responsivenessRpm"].optNum(),
                s["latencyTarget"].str(), s["error"].optStr())
        }
        val tw = d["twoWayInput"].obj()
        val received = tw["received"].arr().associate { r -> val v = r.arr().map { it.long() }; v[0].toInt() to longArrayOf(v[1], v[2], v[3], v[4]) }
        val e = d["expected"].obj()
        val two = ResponderClient.compute(TwoWayResult(responder = "192.0.2.10:5410", seconds = 10.0, packetsPerSecond = 50, payloadBytes = 172),
            tw["sent"].int(), tw["unique"].int(), tw["dup"].int(), tw["upReordered"].int(), tw["replies"].int(), received, tw["clientDuplicates"].int(), tw["downReordered"].int())
            .copy(startUtc = t0.plusSeconds(450), elapsedMs = 450_000)
        assertSameJson("twoWay", e["twoWay"], two)

        val c = d["config"].obj(); val x = c["contextAtStart"].obj()
        val cfg = ConnectionTestConfig(testId = c["testId"].str(), name = "Parity connection", duration = Duration.ofMinutes(15), location = "Lab",
            contextAtStart = ConnectionContext(wifiConnected = true, ssid = x["ssid"].optStr(), bssid = x["bssid"].optStr(), localIPv4 = x["localIPv4"].optStr(),
                gateways = x["gateways"].arr().map { it.str() }, dnsServers = x["dnsServers"].arr().map { it.str() }, ispHop = x["ispHop"].optStr(), ispHopTtl = x["ispHopTtl"].optInt(),
                pathHops = x["pathHops"].arr().map { it.str() }))
        assertEquals(d["estimatedKbps"].num(), cfg.estimatedKbps(), 1e-9)
        assertEquals(d["estimatedKbpsDetailed"].num(), cfg.copy(tickMilliseconds = 500, intensity = ConnectionIntensity.Detailed).estimatedKbps(), 1e-9)
        val markers = d["markers"].arr().map { it.obj() }.map { ConnectionMarker(it["wallUtc"].instant(), it["elapsedMs"].long(), it["text"].str()) }
        val r = ConnectionAnalyzer().analyze(cfg, probes, link, speed, listOf(two), markers, t0, t0.plusSeconds(900), ConnectionTestStatus.Completed, d["activeSeconds"].num())
        assertSameJson("segments", e["segments"], r.segments)
        assertSameJson("targets", e["targets"], r.targets)
        assertSameJson("events", e["events"], r.events)
        assertSameJson("problems", e["problemsBySegment"], r.problemsBySegment)
        assertSameJson("verdicts", e["verdicts"], r.verdicts)
        assertSameJson("voice", listOf(e["voiceRFactor"], e["voiceMos"]), listOf(r.voiceRFactor, r.voiceMos))
        assertSameJson("link", e["link"], r.link)
        assertSameJson("totals", e["totals"], r.totals)
        for (g in d["grades"].arr().map { it.obj() }) assertEquals("${g["v"]}", g["g"], ConnectionTestRunner.bufferbloatGrade(g["v"].optNum()))
        for (l in d["local"].arr().map { it.obj() }) assertEquals(l["ip"].str(), l["local"], PathDiscovery.isLocalAddress(l["ip"].str()))
    }

    @Test fun responderBytes() {
        val d = Fixture.load("responder.json").obj()
        val key = unhex(d["key"].str()); val nonce = unhex(d["nonce"].str())
        assertEquals(d["keyText"].str(), ResponderProtocol.keyToText(key))
        assertArrayEquals(key, ResponderProtocol.keyFromText(d["keyText"].str()))
        assertEquals(d["clientProof"].str(), hex(ResponderProtocol.clientProof(key, nonce)))
        for (p in d["packets"].arr().map { it.obj() }) {
            val pkt = ResponderProtocol.UdpPacket(p["type"].int().toByte(), p["session"].long(), p["seq"].int(), p["t1"].long(), p["t2"].long(), p["t3"].long(), p["serverCount"].int())
            val bytes = ResponderProtocol.buildUdp(key, pkt, p["length"].int())
            assertEquals(p["bytes"].str(), hex(bytes))
            assertEquals(pkt, ResponderProtocol.parseUdp(key, unhex(p["bytes"].str())))
            val tampered = bytes.copyOf().also { it[30] = (it[30] + 1).toByte() }
            assertEquals(null, ResponderProtocol.parseUdp(key, tampered))
        }
        assertEquals(d["shortPacket"].str(), hex(ResponderProtocol.buildUdp(key, ResponderProtocol.UdpPacket(1, 0x0102030405060708, 42, 123456789, 0, 0, 0), 10)))
    }

    @Test fun networkSelection() {
        val d = Fixture.load("selection.json").obj()
        val aps = d["aps"].arr().map { Fixture.ap(it.obj(), java.time.Instant.EPOCH) }
        assertSameJson("autoFocus", d["autoFocus"], NetworkSelection.autoFocus(aps))
        val sel = NetworkSelection()
        fun of(ssid: String) = aps.filter { it.ssid == ssid }.map { it.bssid }
        val steps = d["steps"].arr().map { it.obj() }
        fun check(i: Int, ok: Boolean) {
            val s = steps[i]
            val what = s["op"].str()
            assertEquals(what, s["ok"], ok)
            assertEquals(what, s["scope"].str(), sel.deriveScope().name)
            assertEquals(what, s["describe"].str(), sel.describe { of(it).size })
            assertSameJson("$what focus", s["focus"], sel.focusOrder)
            assertSameJson("$what ssids", s["ssids"], sel.ssids.sorted())
            assertSameJson("$what bssids", s["bssids"], sel.bssids.sortedWith(String.CASE_INSENSITIVE_ORDER))
            assertSameJson("$what matches", s["matches"], aps.map { sel.matches(it.ssid, it.bssid) })
            assertSameJson("$what states", s["states"], listOf("Alpha", "Beta", "Gamma").map { sel.stateOf(it, of(it)) })
            assertSameJson("$what slots", s["slots"], aps.map { sel.focusSlot(it.ssid, aps) })
        }
        check(0, true)
        check(1, sel.toggleGroup("Alpha", of("Alpha")))
        check(2, sel.toggleAp("Alpha", "02:00:00:00:02:02", of("Alpha")))
        check(3, sel.toggleAp("Beta", "02:00:00:00:02:03", of("Beta")))
        check(4, sel.toggleGroup("", of("")))
        check(5, sel.toggleGroup("Gamma", of("Gamma")))
        sel.lock()
        check(6, sel.toggleGroup("Delta", of("Delta")))
        sel.unlock()
        check(7, sel.toggleAp("Alpha", "02:00:00:00:02:02", of("Alpha")))
        check(8, sel.toggleGroup("Alpha", of("Alpha")))
        check(9, sel.clear())
    }
}
