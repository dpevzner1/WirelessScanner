package com.wirelessscanner.core.runtime

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.roundTo
import com.wirelessscanner.core.domain.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import java.time.Duration
import java.time.Instant

/** Port of PathDiscovery (ConnectionTestRunner.cs): TTL-limited probes find the ISP hop at start. */
object PathDiscovery {
    const val MAX_TTL = 10

    /** RFC 1918, link-local and loopback are local; CGNAT 100.64/10 counts as the ISP side. */
    fun isLocalAddress(ip: String): Boolean {
        if (!ConnectionTestConfig.isIPv4(ip)) return false
        val b = ip.split('.').map { it.toInt() }
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] in 16..31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
    }

    suspend fun discover(prober: ConnectionProber, ctx: ConnectionContext, target: String): ConnectionContext {
        val src = ctx.localIPv4 ?: return ctx
        val hops = ArrayList<String>()
        var isp: String? = null
        var ispTtl: Int? = null
        for (ttl in 1..MAX_TTL) {
            currentCoroutineContext().ensureActive()
            var r = prober.icmp(target, src, ttl, 1000)
            if (r.outcome == ProbeOutcome.Timeout) r = prober.icmp(target, src, ttl, 1000)
            if (r.outcome == ProbeOutcome.TtlExpired && r.from != null) {
                hops.add(r.from)
                if (isp == null && !isLocalAddress(r.from) && r.from !in ctx.gateways) { isp = r.from; ispTtl = ttl; break }
            } else if (r.outcome == ProbeOutcome.Ok) { hops.add(r.from ?: target); break }
            else hops.add("*")
        }
        var echo = false
        if (isp != null) for (i in 0 until 2) { if (echo) break; echo = prober.icmp(isp, src, 64, 1000).outcome == ProbeOutcome.Ok }
        return ctx.copy(pathHops = hops, ispHop = isp, ispHopTtl = ispTtl, ispHopAnswersEcho = echo)
    }
}

/**
 * Port of ConnectionTestRunner.cs (DR-0014). One tick per tickMilliseconds of ACTIVE time; each tick takes a link sample and
 * launches the due probes concurrently. Late wake-ups are recorded as gaps. Speed and two-way runs pause the regular probes.
 */
class ConnectionTestRunner(
    private val prober: ConnectionProber,
    private val repository: ConnectionRepository,
    private val analyzer: ConnectionAnalyzer,
    private val logger: AppLogger,
    private val writer: OrderedPersistenceWriter,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
) {
    private val lock = Any()
    private val probes = ArrayList<ConnectionProbeResult>()
    private val link = ArrayList<ConnectionLinkSample>()
    private val speed = ArrayList<SpeedTestResult>()
    private val twoWay = ArrayList<TwoWayResult>()
    private val markers = ArrayList<ConnectionMarker>()
    private val pendingProbes = ArrayList<ConnectionProbeResult>()
    private val pendingLink = ArrayList<ConnectionLinkSample>()
    private val sinceProgress = ArrayList<ConnectionProbeResult>()
    private val inflight = ArrayList<Job>()
    private val active = ActiveClock()
    private val capacityGate = Mutex()

    @Volatile var config: ConnectionTestConfig? = null
        private set
    private var loop: Job? = null
    private var startUtc: Instant = Instant.EPOCH
    @Volatile private var paused = false
    @Volatile private var capacityRunning = false
    @Volatile private var stopRequested = false
    @Volatile private var finalReport: ConnectionReport? = null
    @Volatile private var currentIp: String? = null
    @Volatile private var currentGateway: String? = null
    @Volatile private var lastSsid: String? = null
    @Volatile private var activity: String? = null
    @Volatile var status: ConnectionTestStatus? = null
        private set

    var speedClientFactory: (() -> Pair<SpeedTestClient, SpeedServerSettings>?)? = null
    var twoWayClientFactory: (() -> TwoWayClient?)? = null

    private val progressListeners = ArrayList<(ConnectionProgress) -> Unit>()
    private val completedListeners = ArrayList<(ConnectionReport) -> Unit>()
    fun addProgressListener(l: (ConnectionProgress) -> Unit) { progressListeners.add(l) }
    fun addCompletedListener(l: (ConnectionReport) -> Unit) { completedListeners.add(l) }

    val isRunning get() = loop?.isActive == true

    suspend fun start(cfgIn: ConnectionTestConfig) {
        check(!isRunning) { "A connection test is already running." }
        var ctx = requireNotNull(cfgIn.contextAtStart) { "The connection was not read before starting." }
        require(ctx.wifiConnected && !ctx.localIPv4.isNullOrEmpty()) { "This device is not connected over Wi-Fi (no Wi-Fi IPv4 address). Connect first; the test never connects for you." }
        require(cfgIn.tickMilliseconds >= 20) { "Tick too short." }
        var cfg = cfgIn
        if (cfg.probeIsp && ctx.ispHop == null && cfg.internetTargets.isNotEmpty()) {
            activity = "Finding the ISP hop…"
            try {
                ctx = PathDiscovery.discover(prober, ctx, cfg.internetTargets[0])
                logger.info("Path discovery: ${ctx.pathHops.joinToString(",") { if (it == "*") "*" else if (PathDiscovery.isLocalAddress(it)) "local" else "public" }}; ISP hop ${if (ctx.ispHop != null) "at TTL ${ctx.ispHopTtl}" else "not found"}.")
            } catch (e: CancellationException) { throw e } catch (e: Exception) { logger.warn("Path discovery failed: ${e.javaClass.simpleName}: ${e.message}") }
            cfg = cfg.copy(contextAtStart = ctx)
        }
        synchronized(lock) {
            listOf(probes, link, speed, twoWay, markers, pendingProbes, pendingLink, sinceProgress).forEach { (it as MutableList<*>).clear() }
            inflight.clear()
        }
        config = cfg
        currentIp = ctx.localIPv4; currentGateway = ctx.gateways.firstOrNull(); lastSsid = ctx.ssid
        startUtc = Instant.now()
        paused = false; stopRequested = false; capacityRunning = false; finalReport = null; activity = null
        active.reset(); active.start()
        status = ConnectionTestStatus.Running
        val start = startUtc
        val c = cfg
        writer.enqueue("connection-test ${c.testId}") { repository.saveTest(c, start, ConnectionTestStatus.Running) }
        logger.info("Connection test '${c.name}' started; tick ${c.tickMilliseconds} ms; duration ${c.duration?.toString() ?: "until stopped"}.")
        loop = scope.launch { loopBody() }
    }

    private suspend fun loopBody() {
        val cfg0 = config!!
        val tickMs = cfg0.tickMilliseconds.toLong()
        var tick = 0L
        var lastSpeedMinute = if (cfg0.speedTestAtStart) -1L else 0L
        var lastTwoWayMinute = if (cfg0.twoWayAtStart) -1L else 0L
        try {
            while (currentCoroutineContext().isActive && !stopRequested) {
                val dMs = cfg0.duration?.toMillis()
                if (dMs != null && active.elapsedMs >= dMs) break
                val due = tick * tickMs
                val now = active.elapsedMs
                if (now < due) { delay(minOf(due - now, 250)); continue }
                if (paused || capacityRunning) { delay(minOf(tickMs, 100)); tick = active.elapsedMs / tickMs + 1; continue }
                val late = now - due
                val gap = if (late >= 3 * tickMs) late else null
                if (gap != null) tick = now / tickMs

                runTick(config!!, tick, gap)

                val c = config!!
                if ((tick == 30L || (tick > 30 && tick % 300 == 0L)) && c.probeIsp && c.contextAtStart?.ispHop == null && c.internetTargets.isNotEmpty()) {
                    try {
                        val again = PathDiscovery.discover(prober, c.contextAtStart!!.copy(localIPv4 = currentIp ?: c.contextAtStart.localIPv4), c.internetTargets[0])
                        if (again.ispHop != null) {
                            config = c.copy(contextAtStart = again)
                            addMarker("ISP hop found on a later attempt (TTL ${again.ispHopTtl}); ISP probing starts now")
                        }
                    } catch (e: CancellationException) { throw e } catch (e: Exception) { logger.warn("Second path discovery failed: ${e.message}") }
                }

                val minute = active.elapsedMs / 60_000
                val sf = speedClientFactory
                if (sf != null && ((lastSpeedMinute < 0 && tick >= 2) || (cfg0.speedTestEveryMinutes > 0 && minute > 0 && minute % cfg0.speedTestEveryMinutes == 0L && minute != lastSpeedMinute && lastSpeedMinute >= 0))) {
                    lastSpeedMinute = maxOf(minute, 0)
                    sf()?.let { (client, settings) -> scope.launch { runSpeedTest(client, settings) } }
                }
                val tf = twoWayClientFactory
                if (tf != null && ((lastTwoWayMinute < 0 && tick >= 2) || (cfg0.twoWayEveryMinutes > 0 && minute > 0 && minute % cfg0.twoWayEveryMinutes == 0L && minute != lastTwoWayMinute && lastTwoWayMinute >= 0))) {
                    lastTwoWayMinute = maxOf(minute, 0)
                    tf()?.let { client -> scope.launch { runTwoWay(client) } }
                }
                tick++
            }
        } catch (e: CancellationException) {
        } catch (e: Exception) {
            logger.error("Connection test loop failed: ${e.message}", e)
            status = ConnectionTestStatus.Failed
        } finally {
            active.stop()
            withContext(NonCancellable) { drainInflight(); finish() }
        }
    }

    private suspend fun runTick(cfg: ConnectionTestConfig, tick: Long, gapMs: Long?) {
        val wall = Instant.now()
        val elapsed = active.elapsedMs
        val ctx = cfg.contextAtStart!!
        if (tick % 10 == 0L && tick > 0) {
            try {
                val fresh = prober.getContext(cfg.interfaceId)
                currentIp = fresh.localIPv4
                currentGateway = fresh.gateways.firstOrNull() ?: currentGateway
            } catch (e: CancellationException) { throw e } catch (e: Exception) { logger.warn("Context refresh failed: ${e.message}") }
        }
        var sample: ConnectionLinkSample? = null
        if (cfg.probeWifiLink) {
            val r = try { prober.readLink(cfg.interfaceId) } catch (e: CancellationException) { throw e } catch (e: Exception) { logger.warn("Link read failed: ${e.message}"); null }
            val bytes = prober.interfaceBytes(cfg.interfaceId)
            val s = ConnectionLinkSample(tick, wall, elapsed, r?.connected ?: false, r?.ssid, r?.bssid, r?.channel, r?.rssi, r?.signalQualityPct,
                r?.rxRateMbps, r?.txRateMbps, currentIp, currentGateway, bytes?.first, bytes?.second, r?.counters, gapMs)
            sample = s
            synchronized(lock) { link.add(s); pendingLink.add(s) }
            if (r?.connected == true && r.ssid != null && lastSsid != null && r.ssid != lastSsid && cfg.stopOnNetworkChange) {
                logger.info("Connection test stopping: network changed $lastSsid → ${r.ssid}.")
                addMarker("Stopped: network changed to ${r.ssid}")
                stopRequested = true
            }
            if (r?.connected == true && r.ssid != null) lastSsid = r.ssid
        }

        val src = currentIp
        fun launch(seg: ConnectionSegment, target: String, method: ProbeMethod, probe: suspend (String) -> ProbeReply, map: ((ProbeReply) -> ProbeReply)? = null) {
            val job = scope.launch {
                var reply = if (src == null) ProbeReply.fail(ProbeOutcome.Unreachable, "no Wi-Fi IPv4 address") else try { probe(src) }
                    catch (e: CancellationException) { ProbeReply.fail(ProbeOutcome.Skipped, "test stopped") }
                    catch (e: Exception) { ProbeReply.fail(ProbeOutcome.Error, e.javaClass.simpleName + ": " + e.message) }
                if (map != null) reply = map(reply)
                val result = ConnectionProbeResult(tick, wall, elapsed, seg, target, method, reply.outcome, if (reply.outcome == ProbeOutcome.Ok) reply.rttMs else null, reply.detail)
                synchronized(lock) { probes.add(result); pendingProbes.add(result); sinceProgress.add(result) }
            }
            synchronized(lock) { inflight.add(job) }
        }

        val gw = currentGateway
        if (cfg.probeGateway && tick % ConnectionTestConfig.GATEWAY_EVERY_TICKS == 0L && gw != null)
            launch(ConnectionSegment.Gateway, gw, ProbeMethod.Icmp, { s -> prober.icmp(gw, s, 64, ConnectionTestConfig.GATEWAY_TIMEOUT_MS) })
        val isp = ctx.ispHop
        if (cfg.probeIsp && tick % ConnectionTestConfig.ISP_EVERY_TICKS == 0L && isp != null) {
            val ttl = ctx.ispHopTtl
            if (ctx.ispHopAnswersEcho) launch(ConnectionSegment.IspHop, isp, ProbeMethod.Icmp, { s -> prober.icmp(isp, s, 64, ConnectionTestConfig.ISP_TIMEOUT_MS) })
            else if (ttl != null && cfg.internetTargets.isNotEmpty()) {
                val via = cfg.internetTargets[0]
                launch(ConnectionSegment.IspHop, isp, ProbeMethod.IcmpTtl, { s -> prober.icmp(via, s, ttl, ConnectionTestConfig.ISP_TIMEOUT_MS) }) { r ->
                    when {
                        r.outcome == ProbeOutcome.TtlExpired && r.from == isp -> r.copy(outcome = ProbeOutcome.Ok)
                        r.outcome == ProbeOutcome.TtlExpired -> r.copy(outcome = ProbeOutcome.Error, detail = "answer from ${r.from} (path changed)")
                        else -> r
                    }
                }
            }
        }
        if (cfg.probeInternet && tick % ConnectionTestConfig.INTERNET_EVERY_TICKS == 0L) for (t in cfg.internetTargets) {
            launch(ConnectionSegment.Internet, t, ProbeMethod.Tcp, { s -> prober.tcpConnect(t, cfg.internetTcpPort, s, ConnectionTestConfig.INTERNET_TIMEOUT_MS) })
            launch(ConnectionSegment.Internet, t, ProbeMethod.Icmp, { s -> prober.icmp(t, s, 64, ConnectionTestConfig.INTERNET_TIMEOUT_MS) })
        }
        if (cfg.probeDns && tick % ConnectionTestConfig.DNS_EVERY_TICKS == 0L) for (server in ctx.dnsServers.filter { ConnectionTestConfig.isIPv4(it) })
            launch(ConnectionSegment.Dns, server, ProbeMethod.Dns, { s -> prober.dnsQuery(server, cfg.dnsQueryName, s, ConnectionTestConfig.DNS_TIMEOUT_MS) })
        if (cfg.probeWeb && tick % ConnectionTestConfig.WEB_EVERY_TICKS == 0L && cfg.webUrl.isNotBlank())
            launch(ConnectionSegment.Web, cfg.webUrl, ProbeMethod.Https, { s -> prober.https(cfg.webUrl, s, ConnectionTestConfig.WEB_TIMEOUT_MS) })

        flushPending(cfg.testId)
        publishProgress(cfg, tick, sample)
        synchronized(lock) { inflight.removeAll { it.isCompleted } }
    }

    private fun flushPending(testId: String) {
        val (p, l) = synchronized(lock) {
            val a = pendingProbes.toList(); pendingProbes.clear()
            val b = pendingLink.toList(); pendingLink.clear()
            a to b
        }
        if (p.isNotEmpty()) writer.enqueue("connection-probes $testId") { repository.saveProbes(testId, p) }
        if (l.isNotEmpty()) writer.enqueue("connection-link $testId") { repository.saveLinkSamples(testId, l) }
    }

    private fun publishProgress(cfg: ConnectionTestConfig, tick: Long, sample: ConnectionLinkSample?) {
        val recent = synchronized(lock) { val r = sinceProgress.toList(); sinceProgress.clear(); r }
        val act = active.elapsedSeconds
        val remaining = cfg.duration?.let { maxOf(0.0, it.toMillis() / 1000.0 - act) }
        val p = ConnectionProgress(cfg, status ?: ConnectionTestStatus.Running, startUtc, act, remaining, tick, recent, sample, activity)
        progressListeners.forEach { safe { it(p) } }
    }

    private suspend fun drainInflight() {
        val pending = synchronized(lock) { inflight.toList() }
        if (withTimeoutOrNull(8000) { pending.joinAll() } == null) logger.warn("Some probes were still running when the test ended.")
    }

    private suspend fun finish() {
        val cfg = config!!
        val final = when {
            status == ConnectionTestStatus.Failed -> ConnectionTestStatus.Failed
            stopRequested -> ConnectionTestStatus.Stopped
            else -> ConnectionTestStatus.Completed
        }
        status = final
        val end = Instant.now()
        val planned = cfg.duration
        val act = if (planned != null && final == ConnectionTestStatus.Completed) minOf(active.elapsedSeconds, planned.toMillis() / 1000.0) else active.elapsedSeconds
        flushPending(cfg.testId)
        writer.enqueue("connection-status ${cfg.testId} $final") { repository.updateTestStatus(cfg.testId, final, end, act) }
        writer.flush()
        val s = snapshot()
        val report = analyzer.analyze(cfg, s.probes, s.link, s.speed, s.twoWay, s.markers, startUtc, end, final, act)
        finalReport = report
        logger.info("Connection test '${cfg.name}' $final: ${report.totals.probesSent} probes, ${report.events.size} events.")
        completedListeners.forEach { safe { it(report) } }
    }

    private inline fun safe(a: () -> Unit) { try { a() } catch (e: Exception) { logger.error("Connection test subscriber error: ${e.message}", e) } }

    fun pause() {
        if (!isRunning || paused) return
        paused = true; active.stop(); status = ConnectionTestStatus.Paused
        addMarker("Paused")
        val id = config!!.testId
        writer.enqueue("connection-status $id Paused") { repository.updateTestStatus(id, ConnectionTestStatus.Paused, null) }
    }

    fun resume() {
        if (!isRunning || !paused) return
        addMarker("Resumed")
        paused = false; active.start(); status = ConnectionTestStatus.Running
        val id = config!!.testId
        writer.enqueue("connection-status $id Running") { repository.updateTestStatus(id, ConnectionTestStatus.Running, null) }
    }

    suspend fun stop(): ConnectionReport? {
        val l = loop ?: return finalReport
        stopRequested = true
        l.cancel(); l.join()
        return finalReport
    }

    suspend fun waitForCompletion(): ConnectionReport? { loop?.join(); return finalReport }

    fun addMarker(text: String) {
        val cfg = config ?: return
        if (text.isBlank()) return
        val m = ConnectionMarker(Instant.now(), active.elapsedMs, text.trim())
        synchronized(lock) { markers.add(m) }
        writer.enqueue("connection-marker ${cfg.testId}") { repository.saveMarker(cfg.testId, m) }
    }

    data class Snapshot(val probes: List<ConnectionProbeResult>, val link: List<ConnectionLinkSample>, val speed: List<SpeedTestResult>,
                        val twoWay: List<TwoWayResult>, val markers: List<ConnectionMarker>, val startUtc: Instant, val activeSeconds: Double)

    fun snapshot(): Snapshot = synchronized(lock) {
        Snapshot(probes.toList(), link.toList(), speed.toList(), twoWay.toList(), markers.toList(), startUtc, active.elapsedSeconds)
    }

    val currentActivity get() = activity

    // ---- stage B
    suspend fun runSpeedTest(client: SpeedTestClient, settings: SpeedServerSettings): SpeedTestResult? {
        val cfg = config
        val src = currentIp
        if (!isRunning || cfg == null || src == null) return null
        if (!capacityGate.tryLock()) return null
        capacityRunning = true
        val startWall = Instant.now()
        val startMs = active.elapsedMs
        val result: SpeedTestResult = try {
            activity = "Speed test against ${client.server}…"
            drainInflight()
            val lan = settings.kind == SpeedServerKind.Responder
            val latencyTarget = if (lan) currentGateway ?: "" else cfg.internetTargets.firstOrNull() ?: ""
            suspend fun one(): Double? {
                val r = if (lan) prober.icmp(latencyTarget, src, 64, 1000) else prober.tcpConnect(latencyTarget, cfg.internetTcpPort, src, 1500)
                return if (r.outcome == ProbeOutcome.Ok) r.rttMs else null
            }
            suspend fun sample(keepGoing: () -> Boolean, max: Int): List<Double> {
                val list = ArrayList<Double>()
                for (i in 0 until max) {
                    if (!keepGoing()) break
                    one()?.let { list.add(it) }
                    delay(250)
                }
                return list
            }
            val idle = sample({ true }, 12)
            val duration = Duration.ofSeconds(settings.secondsPerDirection.coerceIn(3, 30).toLong())
            val streams = settings.streams.coerceIn(1, 16)
            val downJob = scope.async { client.download(src, streams, duration) }
            val loadedDown = sample({ !downJob.isCompleted }, 400)
            val down = downJob.await()
            val upJob = scope.async { client.upload(src, streams, duration) }
            val loadedUp = sample({ !upJob.isCompleted }, 400)
            val up = upJob.await()
            fun median(v: List<Double>) = if (v.isEmpty()) null else ConnectionAnalyzer.percentile(v.sorted(), 50.0)
            val idleMed = median(idle); val downMed = median(loadedDown); val upMed = median(loadedUp)
            val worstLoaded = if (downMed != null || upMed != null) maxOf(downMed ?: 0.0, upMed ?: 0.0) else null
            val increase = if (idleMed != null && worstLoaded != null) maxOf(0.0, worstLoaded - idleMed) else null
            SpeedTestResult(
                startUtc = startWall, elapsedMs = startMs, provider = client.provider, server = client.server, streams = streams,
                secondsPerDirection = duration.seconds.toDouble(),
                downloadMbps = if (down.seconds > 0 && down.bytes > 0) (down.bytes * 8 / down.seconds / 1e6).roundTo(2) else null,
                uploadMbps = if (up.seconds > 0 && up.bytes > 0) (up.bytes * 8 / up.seconds / 1e6).roundTo(2) else null,
                bytesDown = down.bytes, bytesUp = up.bytes, idleLatencyMs = idleMed, loadedLatencyDownMs = downMed, loadedLatencyUpMs = upMed,
                loadedLatencyP95Ms = ConnectionAnalyzer.percentile((loadedDown + loadedUp).sorted(), 95.0),
                latencyIncreaseMs = increase?.roundTo(3), bufferbloatGrade = bufferbloatGrade(increase),
                responsivenessRpm = worstLoaded?.takeIf { it > 0 }?.let { Math.rint(60000 / it) },
                latencyTarget = (if (lan) "router " else "internet ") + latencyTarget, error = down.error ?: up.error,
            )
        } catch (e: CancellationException) {
            SpeedTestResult(startUtc = startWall, elapsedMs = startMs, provider = client.provider, server = client.server, error = "cancelled")
        } catch (e: Exception) {
            SpeedTestResult(startUtc = startWall, elapsedMs = startMs, provider = client.provider, server = client.server, error = e.message)
        } finally {
            activity = null; capacityRunning = false; capacityGate.unlock()
        }
        synchronized(lock) { speed.add(result) }
        writer.enqueue("connection-speed ${cfg.testId}") { repository.saveSpeedTest(cfg.testId, result) }
        return result
    }

    // ---- stage C
    suspend fun runTwoWay(client: TwoWayClient): TwoWayResult? {
        val cfg = config
        val src = currentIp
        if (!isRunning || cfg == null || src == null) return null
        if (!capacityGate.tryLock()) return null
        capacityRunning = true
        val startWall = Instant.now()
        val startMs = active.elapsedMs
        val result: TwoWayResult = try {
            activity = "Two-way test with ${client.responder}…"
            drainInflight()
            var r = client.runStream(src, Duration.ofSeconds(10), 50, 172)
            if (client is SpeedTestClient && r.error == null) {
                val d = client.download(src, 4, Duration.ofSeconds(5))
                val u = client.upload(src, 4, Duration.ofSeconds(5))
                r = r.copy(
                    lanDownMbps = if (d.seconds > 0 && d.bytes > 0) (d.bytes * 8 / d.seconds / 1e6).roundTo(2) else null,
                    lanUpMbps = if (u.seconds > 0 && u.bytes > 0) (u.bytes * 8 / u.seconds / 1e6).roundTo(2) else null,
                )
            }
            r.copy(startUtc = startWall, elapsedMs = startMs)
        } catch (e: CancellationException) {
            TwoWayResult(startUtc = startWall, elapsedMs = startMs, responder = client.responder, error = "cancelled")
        } catch (e: Exception) {
            TwoWayResult(startUtc = startWall, elapsedMs = startMs, responder = client.responder, error = e.message)
        } finally {
            activity = null; capacityRunning = false; capacityGate.unlock()
        }
        synchronized(lock) { twoWay.add(result) }
        writer.enqueue("connection-twoway ${cfg.testId}") { repository.saveTwoWay(cfg.testId, result) }
        return result
    }

    companion object {
        fun bufferbloatGrade(increaseMs: Double?): String = when {
            increaseMs == null -> ""
            increaseMs < 5 -> "A+"
            increaseMs < 30 -> "A"
            increaseMs < 60 -> "B"
            increaseMs < 200 -> "C"
            increaseMs < 400 -> "D"
            else -> "F"
        }
    }
}
