package com.wirelessscanner.android

import android.app.Application
import android.content.Context
import android.os.Build
import com.wirelessscanner.android.platform.*
import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.api.ApiData
import com.wirelessscanner.core.api.ApiHttpServer
import com.wirelessscanner.core.api.RestApi
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.forms.*
import com.wirelessscanner.core.report.*
import com.wirelessscanner.core.responder.HttpSpeedClient
import com.wirelessscanner.core.responder.ResponderClient
import com.wirelessscanner.core.responder.ResponderProtocol
import com.wirelessscanner.core.responder.ResponderServer
import com.wirelessscanner.core.runtime.*
import com.wirelessscanner.core.selection.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update
import java.io.Closeable
import java.security.SecureRandom
import java.time.Instant
import java.time.ZoneId
import java.util.Base64

enum class SurveyMode(val label: String, val glyph: String, val description: String) {
    Monitor("MONITOR", "◉", "Monitor: watch the networks around you. Nothing is recorded and nothing is sent."),
    Capture("CAPTURE", "●", "Capture: record the selected networks to a session (walk-through or fixed spot). Listens only; nothing is sent."),
    Saturation("SATURATION TEST", "◎", "Saturation test: leave the tablet in place and measure how stable the selected networks' signal is. Listens to the radio only; sends nothing."),
    Connection("CONNECTION TEST", "⇄", "Connection test: check the connection this tablet is on, end to end (Wi-Fi → router → ISP → internet, DNS, web) with tiny probes, and see where problems happen. Never changes the connection."),
}

enum class CenterView(val label: String) { Spectrum("SPECTRUM"), Waterfall("WATERFALL"), Grid("GRID"), Saturation("SATURATION"), Connection("CONNECTION") }

data class LogRow(val sample: TelemetrySample, val fluctuation: String, val color: Long)

data class LiveState(
    val aps: List<AccessPoint> = emptyList(),
    val lastCycle: ScanCycleStatus? = null,
    val lastCycleText: String = "Waiting for the first scan…",
    val statusText: String = "Starting…",
    val waterfall: List<List<AccessPoint>> = emptyList(),
    val problem: String? = null,
    val throttled: Boolean = false,
    val throttleEnabled: Boolean? = null,
    val connected: ClientConnectionInfo? = null,
    val scanIntervalSeconds: Int = 5,
)

data class CaptureState(
    val active: Boolean = false, val paused: Boolean = false, val session: CaptureSession? = null,
    val surveyPoint: String = "Default", val notes: String = "", val modeText: String = "", val logs: List<LogRow> = emptyList(),
    val finished: CaptureSession? = null,
)

enum class Step { Setup, Running, Results }

data class SatState(
    val step: Step = Step.Setup, val form: SaturationForm = SaturationForm(), val validation: SaturationForm.Validation = SaturationForm.Validation("", false, ""),
    val config: SaturationTestConfig? = null, val tiles: List<SaturationTile> = emptyList(), val live: SaturationReport? = null,
    val progressPct: Double = 0.0, val indeterminate: Boolean = false, val elapsed: String = "00:00:00", val remaining: String = "",
    val cycleText: String = "", val warning: String = "", val runStatus: String = "", val paused: Boolean = false, val feed: List<String> = emptyList(),
    val report: SaturationReport? = null, val geoStatus: String = "", val locating: Boolean = false, val error: String = "",
)

data class ConnState(
    val step: Step = Step.Setup, val form: ConnectionForm = ConnectionForm(), val validation: ConnectionForm.Validation = ConnectionForm.Validation("", false, ""),
    val context: ConnectionContext? = null, val reading: Boolean = false, val config: ConnectionTestConfig? = null,
    val tiles: List<SegmentTile> = emptyList(), val live: ConnectionReport? = null, val progressPct: Double = 0.0, val indeterminate: Boolean = false,
    val elapsed: String = "00:00:00", val remaining: String = "", val activity: String = "", val warning: String = "", val runStatus: String = "",
    val paused: Boolean = false, val feed: List<String> = emptyList(), val actionStatus: String = "", val report: ConnectionReport? = null, val error: String = "",
)

data class ServiceState(val running: Boolean = false, val text: String = "Stopped", val error: String? = null)

/** The app's object graph and state (Windows: App.xaml.cs composition root + MainWindowViewModel/Saturation/Connection view models). */
class AppGraph(val app: Application) {
    val log = AppLog(app)
    val settings = AppSettings(app)
    val db = Db(app)
    val wifi = WifiProvider(app, log)
    val wifiNet = WifiNetwork(app)
    val prober = AndroidProber(app, wifiNet, wifi)
    val geo = AndroidGeo(app)
    val demo = DemoProvider()
    val zone: ZoneId get() = ZoneId.systemDefault()
    val reports get() = ReportContext(BuildConfig.VERSION_NAME, "Android", zone)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val provider = object : NetworkAdapterProvider {
        override suspend fun interfaces() = if (settings.demoMode) demo.interfaces() else wifi.interfaces()
        override suspend fun scan(interfaceId: String, timeoutMs: Long) = if (settings.demoMode) demo.scan(interfaceId, timeoutMs) else wifi.scan(interfaceId, timeoutMs)
        override suspend fun connectionInfo(interfaceId: String) = if (settings.demoMode) demo.connectionInfo(interfaceId) else wifi.connectionInfo(interfaceId)
    }

    val scheduler = ScanScheduler(provider, log, scope).also { it.setAdapter(ADAPTER_ID); it.scanTimeoutMs = 6000 }
    val writer = OrderedPersistenceWriter(log)
    val selection = NetworkSelection()
    val grouper = PhysicalUnitGrouper()
    val explorer = ExplorerModel(selection, grouper)
    val sessions = SessionManager(db.sessions, log, writer)
    val satAnalyzer = SaturationAnalyzer()
    val connAnalyzer = ConnectionAnalyzer()
    val satRunner = SaturationTestRunner(scheduler, db.saturation, satAnalyzer, log, writer)
    val connRunner = ConnectionTestRunner(prober, db.connection, connAnalyzer, log, writer)

    val mode = MutableStateFlow(SurveyMode.Monitor)
    val center = MutableStateFlow(CenterView.Spectrum)
    val live = MutableStateFlow(LiveState())
    val filters = MutableStateFlow(ExplorerFilters())
    val explorerView = MutableStateFlow(ExplorerView(emptyList(), emptyList(), "", "", "", false))
    val capture = MutableStateFlow(CaptureState())
    val sat = MutableStateFlow(SatState())
    val conn = MutableStateFlow(ConnState())
    val api = MutableStateFlow(ServiceState())
    val responderRole = MutableStateFlow(ServiceState())
    val toast = MutableStateFlow<String?>(null)

    private var liveCadence: Closeable? = null
    private var lastProcessed = 0L
    private val lastRssi = HashMap<String, Int>()
    private val waterfall = ArrayDeque<List<AccessPoint>>()
    private var satEventCount = 0
    private var connEventCount = 0
    private var lastConnAnalysis = 0L

    init {
        grouper.loadOverrides(settings.unitOverrides)
        grouper.addOverridesListener { settings.unitOverrides = it }
        selection.unitResolver = grouper
        selection.addChangedListener { refreshExplorer(); revalidate() }
        grouper.addChangedListener { refreshExplorer() }
        scheduler.addCycleListener { c -> scope.launch { processCycle(c) } }
        satRunner.addProgressListener(::onSatProgress)
        satRunner.addCompletedListener(::onSatCompleted)
        connRunner.addProgressListener(::onConnProgress)
        connRunner.addCompletedListener(::onConnCompleted)
        connRunner.speedClientFactory = { speedClient()?.let { it to settings.speedServer } }
        connRunner.twoWayClientFactory = { responderClient() }
        revalidate()
        if (settings.apiEnabled) startApi()
        if (settings.responderRoleEnabled) startResponderRole()
    }

    // ------------------------------------------------------------------ live scanning
    private fun liveIntervalMs(): Long = if (!settings.demoMode && (wifi.throttleEnabled == true || wifi.recentlyRefused)) 30_000 else 5_000

    /** The live view scans while the app is visible; captures and tests keep their own cadence when it is not. */
    fun setVisible(visible: Boolean) {
        liveCadence?.close(); liveCadence = null
        if (visible) {
            liveCadence = scheduler.requestCadence(liveIntervalMs())
            live.update { it.copy(scanIntervalSeconds = (liveIntervalMs() / 1000).toInt(), throttleEnabled = wifi.throttleEnabled) }
            scope.launch { scanNow() }
        } else if (capture.value.active) liveCadence = scheduler.requestCadence(liveIntervalMs())
    }

    fun scanNow() { scope.launch { scheduler.scanNow() } }

    private suspend fun processCycle(c: ScheduledCycle) {
        val result = c.result
        synchronized(this) { if (c.sequence <= lastProcessed) return; lastProcessed = c.sequence }
        val now = java.time.LocalTime.now().withNano(0)
        val problem = when (result.errorCode) {
            "wifi_off" -> "Wi-Fi is off. Turn on Wi-Fi to scan."
            "location_off" -> "Location is off. Android returns no Wi-Fi scan results while location is off."
            "no_permission" -> "Location permission is required to read Wi-Fi scan results."
            else -> null
        }
        val throttled = result.errorCode == "scan_throttled"
        if (result.status == ScanCycleStatus.Failed) {
            live.update { it.copy(lastCycle = result.status, lastCycleText = "Scan FAILED at $now: ${result.errorMessage}", statusText = "Scan failure: ${result.errorMessage}", problem = problem) }
            if (capture.value.active && !capture.value.paused) log.warn("Capture gap: scan cycle failed (${result.errorCode}) ${result.errorMessage}")
            return
        }
        // capture: only demonstrably fresh entries (Windows ProcessCycleAsync)
        val limitMs = liveIntervalMs() + 5000.0
        fun fresh(ap: AccessPoint) = ap.beaconAgeMs?.let { it <= limitMs } ?: (result.status == ScanCycleStatus.Fresh)
        var captured = 0; var skipped = 0
        val cap = capture.value
        if (cap.active && !cap.paused) {
            val batch = ArrayList<TelemetrySample>(); val rows = ArrayList<LogRow>()
            for (ap in result.accessPoints) {
                if (!selection.matches(ap.ssid, ap.bssid)) continue
                if (!fresh(ap)) { skipped++; continue }
                val variation = sessions.calculateJitterFor(ap.bssid, ap.rssi)
                val s = TelemetrySample(Instant.now(), cap.surveyPoint, ap.bssid, ap.ssid, ap.band, ap.channel, ap.rssi, ap.snr, ap.quality, variation, ap.noiseFloor)
                batch.add(s)
                val last = lastRssi[ap.bssid]
                val (txt, col) = when { last == null -> "-" to 0xFF888888; ap.rssi > last -> "▲ +${ap.rssi - last} dB" to 0xFF00FF66; ap.rssi < last -> "▼ ${ap.rssi - last} dB" to 0xFFFF3300; else -> "• 0 dB" to 0xFF888888 }
                lastRssi[ap.bssid] = ap.rssi
                rows.add(LogRow(s, txt, col))
            }
            sessions.saveSamples(batch)
            captured = batch.size
            capture.update { it.copy(logs = (rows.reversed() + it.logs).take(200)) }
        }
        grouper.observe(result.accessPoints, Instant.now())
        explorer.update(result.accessPoints, result.status == ScanCycleStatus.Fresh)
        if (result.status == ScanCycleStatus.Fresh) { synchronized(waterfall) { waterfall.addFirst(result.accessPoints); while (waterfall.size > 60) waterfall.removeLast() } }
        val conn = runCatching { provider.connectionInfo(ADAPTER_ID) }.getOrNull()
        explorer.connectedBssid = conn?.takeIf { it.connected }?.bssid
        val status = result.status.name.uppercase() + if (throttled) " (THROTTLED)" else ""
        live.update {
            it.copy(aps = result.accessPoints, lastCycle = result.status,
                lastCycleText = "Last scan $now: $status in ${ReportText.fmt(result.durationMs, "0")} ms, ${result.accessPoints.size} APs" + (if (result.status == ScanCycleStatus.Stale && !throttled) " (${result.errorMessage})" else ""),
                statusText = "Scan complete. Found ${result.accessPoints.size} access points." + if (cap.active && !cap.paused) " Captured $captured fresh samples" + (if (skipped > 0) ", skipped $skipped stale." else ".") else "",
                waterfall = synchronized(waterfall) { waterfall.toList() }, problem = problem, throttled = throttled || wifi.recentlyRefused,
                throttleEnabled = wifi.throttleEnabled, connected = conn, scanIntervalSeconds = (liveIntervalMs() / 1000).toInt())
        }
        refreshExplorer()
    }

    fun refreshExplorer() { explorerView.value = explorer.view(filters.value) }
    fun setFilters(f: ExplorerFilters) { filters.value = f; refreshExplorer() }

    fun setMode(m: SurveyMode) {
        mode.value = m
        when (m) {
            SurveyMode.Saturation -> if (sat.value.step != Step.Setup) center.value = CenterView.Saturation
            SurveyMode.Connection -> { center.value = CenterView.Connection; if (conn.value.step == Step.Setup) refreshContext(); prefillConnectionPlace() }
            else -> {}
        }
    }

    // ------------------------------------------------------------------ capture sessions
    fun startCapture(name: String, facility: String, scope0: String, surveyPoint: String, notes: String, mode0: CaptureSessionMode) {
        val device = runBlocking { provider.interfaces().first() }
        val scopeWithNotes = if (notes.isBlank()) scope0 else "$scope0 | Notes: $notes"
        try {
            sessions.activeSurveyPoint = surveyPoint
            val s = sessions.startSession(name, scopeWithNotes, facility, device, mode0)
            lastRssi.clear()
            capture.value = CaptureState(true, false, s, sessions.activeSurveyPoint, notes, if (mode0 == CaptureSessionMode.Stationary) "Stationary Capture" else "Roaming Survey")
            live.update { it.copy(statusText = "Session '${s.name}' (${mode0}) started.") }
            updateService()
        } catch (e: Exception) { toast.value = "Could not start the capture: ${e.message}" }
    }

    fun pauseCapture() { sessions.pauseSession(); capture.update { it.copy(paused = true) }; live.update { it.copy(statusText = "Session paused.") }; updateService() }
    fun resumeCapture() { sessions.resumeSession(); capture.update { it.copy(paused = false) }; live.update { it.copy(statusText = "Session resumed.") }; updateService() }
    fun stopCapture() {
        val s = sessions.activeSession
        sessions.stopSession()
        capture.update { it.copy(active = false, paused = false, finished = s?.copy(status = CaptureSessionStatus.Completed, endTime = Instant.now())) }
        live.update { it.copy(statusText = "Session completed and saved on this tablet.") }
        updateService()
    }
    fun dismissCaptureExport() = capture.update { it.copy(finished = null) }

    // ------------------------------------------------------------------ saturation test
    fun setSatForm(f: SaturationForm) { sat.update { it.copy(form = f) }; revalidate() }

    fun revalidate() {
        val adapterReady = settings.demoMode || (wifi.wifiEnabled && wifi.locationEnabled && Permissions.hasLocation(app))
        sat.update { it.copy(validation = it.form.validate(adapterReady, selection, satRunner.isRunning)) }
        conn.update { it.copy(validation = it.form.validate(it.context, it.reading, hasSpeedServer(), settings.responder.isConfigured, connRunner.isRunning)) }
    }

    private suspend fun environment(interfaceName: String): SaturationEnvironment {
        val c = runCatching { provider.connectionInfo(ADAPTER_ID) }.getOrNull()
        val now = Instant.now()
        return SaturationEnvironment(BuildConfig.VERSION_NAME, "Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}, ${Build.SUPPORTED_ABIS.firstOrNull() ?: ""})",
            "${Build.MANUFACTURER} ${Build.MODEL}", zone.id, zone.rules.getOffset(now).totalSeconds / 60, interfaceName, "randomised per network",
            "Android ${Build.VERSION.RELEASE} / ${Build.DISPLAY}", null, Build.MANUFACTURER, c, now)
    }

    /** Called after the user confirmed "test all" when nothing is selected. */
    fun startSaturation() {
        revalidate()
        if (!sat.value.validation.canStart) return
        scope.launch {
            val device = provider.interfaces().first()
            var cfg = sat.value.form.buildConfig(selection, ADAPTER_ID, device.description)
            cfg = cfg.copy(environment = environment(device.description))
            satEventCount = 0
            sat.update { it.copy(step = Step.Running, config = cfg, tiles = emptyList(), live = null, report = null, paused = false, progressPct = 0.0,
                indeterminate = cfg.duration == null, runStatus = "RUNNING — ${cfg.name} @ ${cfg.location}", cycleText = "Waiting for the first scan…",
                elapsed = "00:00:00", remaining = cfg.duration?.let { d -> formatSpan(d.seconds.toDouble()) } ?: "until stopped", warning = "", feed = emptyList(), error = "") }
            center.value = CenterView.Saturation
            selection.lock()
            try { satRunner.start(cfg); updateService() }
            catch (e: Exception) { selection.unlock(); sat.update { it.copy(step = Step.Setup, error = "Could not start: ${e.message}") } }
            revalidate()
        }
    }

    private fun onSatProgress(p: SaturationProgress) {
        val report = satAnalyzer.analyze(p.config, p.cycles, p.markers, p.startUtc, null, p.status, p.activeSeconds)
        val h = report.health
        val newEvents = report.events.drop(satEventCount).map { e -> "${ReportText.local(e.startUtc, zone, "HH:mm:ss")}  ${e.type.name.padEnd(21)} ${e.subject}  ${e.detail}" }
        satEventCount = report.events.size
        sat.update {
            it.copy(live = report, tiles = saturationTiles(report, p.lastCycle), elapsed = formatSpan(p.activeSeconds),
                remaining = p.remainingSeconds?.let { r -> formatSpan(r) } ?: "until stopped",
                progressPct = p.config.duration?.let { d -> (100 * p.activeSeconds / (d.toMillis() / 1000.0)).coerceIn(0.0, 100.0) } ?: 0.0,
                cycleText = "Cycle ${ReportText.n0(p.lastCycle.sequence.toInt())}: ${ReportText.n0(h.fresh)} fresh · ${ReportText.n0(h.stale)} stale · ${ReportText.n0(h.failed)} failed",
                warning = saturationWarnings(report, p.lastCycle, null, !settings.demoMode && (wifi.throttleEnabled == true || wifi.recentlyRefused)),
                feed = (newEvents.reversed() + it.feed).take(300))
        }
        updateService()
    }

    private fun onSatCompleted(r: SaturationReport) {
        selection.unlock()
        sat.update { it.copy(step = Step.Results, report = r, paused = false) }
        revalidate(); updateService()
    }

    fun pauseSaturation() { satRunner.pause(); sat.update { it.copy(paused = true, runStatus = "PAUSED — scanning suspended; paused time is not counted.") } }
    fun resumeSaturation() { satRunner.resume(); sat.update { it.copy(paused = false, runStatus = "RUNNING — ${it.config?.name} @ ${it.config?.location}") } }
    fun stopSaturation() { sat.update { it.copy(runStatus = "Stopping…") }; scope.launch { satRunner.stop() } }
    fun satMarker(text: String) {
        if (text.isBlank()) return
        satRunner.addMarker(text)
        sat.update { it.copy(feed = listOf("${java.time.LocalTime.now().withNano(0)}  Marker                ${text.trim()}") + it.feed) }
    }
    fun newSaturation() { sat.update { it.copy(step = Step.Setup, report = null, live = null, tiles = emptyList()) }; revalidate() }

    fun detectLocation(forConnection: Boolean = false) {
        sat.update { it.copy(locating = true, geoStatus = "Detecting location…") }
        scope.launch {
            val (tag, err) = geo.current(15_000)
            if (tag != null) {
                val p = { f: PlaceFields -> f.copy(latitude = ReportText.fmt(tag.latitude, "0.000000"), longitude = ReportText.fmt(tag.longitude, "0.000000"),
                    accuracy = ReportText.fmt(tag.accuracyMeters, "0"), geoSource = tag.source, geoCapturedUtc = tag.capturedUtc) }
                sat.update { it.copy(form = it.form.copy(place = p(it.form.place)), geoStatus = "Location detected (±${ReportText.fmt(tag.accuracyMeters, "0")} m).", locating = false) }
                if (forConnection) conn.update { it.copy(form = it.form.copy(place = p(it.form.place))) }
            } else sat.update { it.copy(geoStatus = err ?: "Location not available.", locating = false) }
            revalidate()
        }
    }

    // ------------------------------------------------------------------ connection test
    fun setConnForm(f: ConnectionForm) { conn.update { it.copy(form = f) }; revalidate() }

    private fun prefillConnectionPlace() {
        val s = sat.value.form.place
        conn.update { c -> if (c.form.place.location.isBlank() && s.location.isNotBlank()) c.copy(form = c.form.copy(place = s)) else c }
        revalidate()
    }

    fun refreshContext() {
        if (connRunner.isRunning) return
        conn.update { it.copy(reading = true) }; revalidate()
        scope.launch {
            val ctx = try { prober.getContext(ADAPTER_ID) } catch (e: Exception) { ConnectionContext(problem = "Could not read the connection: ${e.message}") }
            conn.update { it.copy(context = ctx, reading = false) }; revalidate()
        }
    }

    fun unitNameFor(bssid: String?): String? = bssid?.let { b -> grouper.unitOf(b)?.let { grouper.get(it)?.name } }

    fun startConnection() {
        scope.launch {
            val ctx = try { prober.getContext(ADAPTER_ID) } catch (e: Exception) { ConnectionContext(problem = e.message) }
            conn.update { it.copy(context = ctx) }; revalidate()
            if (!conn.value.validation.canStart) return@launch
            var linked: String? = null
            if (conn.value.form.alsoSaturation && !satRunner.isRunning) {
                val bssid = ctx.bssid
                if (bssid != null && !selection.isLocked) {
                    selection.clear()
                    val unit = grouper.unitOf(bssid)?.let { grouper.get(it) }
                    if (unit != null && unit.members.size > 1) selection.toggleUnit(unit) else explorer.toggleAp(ctx.ssid ?: "", bssid)
                }
                setSatForm(sat.value.form.copy(place = conn.value.form.place))
                startSaturation(); delay(500)
                linked = satRunner.config?.testId
            }
            val device = provider.interfaces().first()
            var cfg = conn.value.form.buildConfig(ctx, ADAPTER_ID, device.description).copy(linkedSaturationTestId = linked)
            cfg = cfg.copy(environment = environment(device.description))
            connEventCount = 0
            val segs = listOf(ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web)
            conn.update { it.copy(step = Step.Running, config = cfg, live = null, report = null, paused = false, progressPct = 0.0, indeterminate = cfg.duration == null,
                runStatus = "RUNNING — ${cfg.name} @ ${cfg.location}", elapsed = "00:00:00", remaining = cfg.duration?.let { d -> formatSpan(d.seconds.toDouble()) } ?: "until stopped",
                tiles = segs.map { s -> if (s == ConnectionSegment.WifiLink) linkTile(null, cfg.probeWifiLink) else segmentTile(s, null, cfg) }, feed = emptyList(), actionStatus = "", error = "") }
            center.value = CenterView.Connection
            try { connRunner.start(cfg); updateService() }
            catch (e: Exception) { conn.update { it.copy(step = Step.Setup, error = "Could not start: ${e.message}") } }
            revalidate()
        }
    }

    private fun onConnProgress(p: ConnectionProgress) {
        val snap = connRunner.snapshot()
        val due = System.currentTimeMillis() - lastConnAnalysis >= (if (snap.probes.size > 50_000) 5000 else 2000)
        var report: ConnectionReport? = null
        if (due) { lastConnAnalysis = System.currentTimeMillis(); report = connAnalyzer.analyze(p.config, snap.probes, snap.link, snap.speed, snap.twoWay, snap.markers, snap.startUtc, null, p.status, snap.activeSeconds) }
        conn.update { s ->
            var tiles = s.tiles.map { t -> if (t.segment == ConnectionSegment.WifiLink && p.lastLink != null) linkTile(p.lastLink, p.config.probeWifiLink) else t }
            var feed = s.feed
            if (report != null) {
                tiles = tiles.map { t -> if (t.segment == ConnectionSegment.WifiLink) t else segmentTile(t.segment, report, p.config) }
                val add = report.events.drop(connEventCount).map { e -> "${ReportText.local(e.startUtc, zone, "HH:mm:ss")}  ${e.type.name.padEnd(13)} ${e.attributedTo?.let { "[" + ConnectionAnalyzer.name(it) + "] " } ?: ""}${e.detail}" }
                connEventCount = report.events.size
                feed = (add.reversed() + feed).take(300)
            }
            s.copy(elapsed = formatSpan(p.activeSeconds), remaining = p.remainingSeconds?.let { formatSpan(it) } ?: "until stopped",
                progressPct = p.config.duration?.let { d -> (100 * p.activeSeconds / (d.toMillis() / 1000.0)).coerceIn(0.0, 100.0) } ?: 0.0,
                activity = p.activity ?: "", tiles = tiles, live = report ?: s.live, feed = feed, config = p.config)
        }
        updateService()
    }

    private fun onConnCompleted(r: ConnectionReport) { conn.update { it.copy(step = Step.Results, report = r, paused = false) }; revalidate(); updateService() }
    fun pauseConnection() { connRunner.pause(); conn.update { it.copy(paused = true, runStatus = "PAUSED — probes suspended; paused time is not counted.") } }
    fun resumeConnection() { connRunner.resume(); conn.update { it.copy(paused = false, runStatus = "RUNNING — ${it.config?.name} @ ${it.config?.location}") } }
    fun stopConnection() { conn.update { it.copy(runStatus = "Stopping…") }; scope.launch { connRunner.stop() } }
    fun connMarker(text: String) { if (text.isNotBlank()) { connRunner.addMarker(text); conn.update { it.copy(feed = listOf("${java.time.LocalTime.now().withNano(0)}  Marker        ${text.trim()}") + it.feed) } } }
    fun newConnection() { conn.update { it.copy(step = Step.Setup, report = null, live = null) }; refreshContext() }

    fun hasSpeedServer() = when (settings.speedServer.kind) { SpeedServerKind.Responder -> settings.responder.isConfigured; SpeedServerKind.Http -> settings.speedServer.httpDownloadUrl.isNotBlank(); else -> false }
    fun speedClient(): SpeedTestClient? = when (settings.speedServer.kind) {
        SpeedServerKind.Responder -> responderClient()
        SpeedServerKind.Http -> if (settings.speedServer.httpDownloadUrl.isNotBlank()) HttpSpeedClient(settings.speedServer, wifiNet.binder()) else null
        else -> null
    }
    fun responderClient(): ResponderClient? = settings.responder.takeIf { it.isConfigured }?.let { runCatching { ResponderClient(it, wifiNet.binder()) }.getOrNull() }

    fun runSpeedNow() {
        val client = speedClient() ?: run { conn.update { it.copy(actionStatus = "Set up a test server first: SETTINGS → Test servers.") }; return }
        conn.update { it.copy(actionStatus = "Speed test running…") }
        scope.launch {
            val r = connRunner.runSpeedTest(client, settings.speedServer)
            conn.update { it.copy(actionStatus = when {
                r == null -> "Speed test not run (no test running, or another capacity test is in progress)."
                r.error != null -> "Speed test failed: ${r.error}"
                else -> "Down ${ReportText.fmt(r.downloadMbps, "0.0")} Mbps · up ${ReportText.fmt(r.uploadMbps, "0.0")} Mbps · latency +${ReportText.fmt(r.latencyIncreaseMs, "0")} ms under load (grade ${r.bufferbloatGrade})."
            }) }
        }
    }

    fun runTwoWayNow() {
        val client = responderClient() ?: run { conn.update { it.copy(actionStatus = "Pair a LAN responder first: SETTINGS → Test servers.") }; return }
        conn.update { it.copy(actionStatus = "Two-way test running (about 20 s)…") }
        scope.launch {
            val r = connRunner.runTwoWay(client)
            conn.update { it.copy(actionStatus = when {
                r == null -> "Two-way test not run (no test running, or another capacity test is in progress)."
                r.error != null -> "Two-way test failed: ${r.error}"
                else -> "Loss up ${ReportText.fmt(r.upLossPct, "0.##")} % / down ${ReportText.fmt(r.downLossPct, "0.##")} % · jitter up ${ReportText.fmt(r.upJitterMs, "0.0")} / down ${ReportText.fmt(r.downJitterMs, "0.0")} ms · MOS ${ReportText.fmt(r.mosUp, "0.00")} / ${ReportText.fmt(r.mosDown, "0.00")} (est.)"
            }) }
        }
    }

    // ------------------------------------------------------------------ foreground service
    fun updateService() {
        val parts = ArrayList<String>()
        val c = capture.value
        if (c.active) parts.add(if (c.paused) "Capture paused" else "Recording")
        if (satRunner.isRunning) parts.add("Saturation test ${sat.value.elapsed}")
        if (connRunner.isRunning) parts.add("Connection test ${conn.value.elapsed}")
        TestService.update(app, if (parts.isEmpty()) null else parts.joinToString(" · "))
    }

    fun modeStatusText(): String {
        val parts = ArrayList<String>()
        val c = capture.value
        if (c.active) parts.add(if (c.paused) "⏸ CAPTURE PAUSED" else "● RECORDING")
        if (satRunner.isRunning) parts.add("● TESTING SIGNAL · selection locked")
        if (connRunner.isRunning) parts.add("● TESTING CONNECTION")
        return if (parts.isEmpty()) "Monitoring · nothing recorded" else parts.joinToString("   ")
    }

    // ------------------------------------------------------------------ exports
    enum class Format(val label: String) { Pdf("PDF"), Csv("CSV"), Json("JSON") }

    fun saturationFiles(r: SaturationReport, f: Format): List<ExportFile> {
        val sr = SaturationReports(reports)
        val prefix = "saturation_" + SessionReports.safe(r.config.name)
        return when (f) {
            Format.Pdf -> listOf(ExportFile("$prefix.pdf", "application/pdf", PdfRenderer.render(sr.document(r))))
            Format.Csv -> sr.csvBundle(prefix, r)
            Format.Json -> listOf(sr.json(r).copy(name = "$prefix.json"))
        }
    }

    fun connectionFiles(r: ConnectionReport, f: Format): List<ExportFile> {
        val cr = ConnectionReports(reports)
        val prefix = "connection_" + SessionReports.safe(r.config.name)
        return when (f) {
            Format.Pdf -> listOf(ExportFile("$prefix.pdf", "application/pdf", PdfRenderer.render(cr.document(r))))
            Format.Csv -> cr.csvBundle(prefix, r)
            Format.Json -> listOf(cr.json(r).copy(name = "$prefix.json"))
        }
    }

    suspend fun sessionFiles(s: CaptureSession, f: Format, meta: ExportMetadata, fields: ExportFields): List<ExportFile> {
        val samples = db.sessions.getSamples(s.sessionId)
        val sr = SessionReports(reports)
        return when (f) {
            Format.Pdf -> listOf(ExportFile("${SessionReports.safe(s.name)}.pdf", "application/pdf", PdfRenderer.render(sr.document(s, samples, meta, fields))))
            Format.Csv -> listOf(sr.csv(s, samples, fields))
            Format.Json -> listOf(sr.json(s, samples, fields))
        }
    }

    // ------------------------------------------------------------------ history
    suspend fun openSaturation(rec: SaturationTestRecord): SaturationReport =
        satAnalyzer.analyze(rec.config, db.saturation.getCycles(rec.config.testId), db.saturation.getMarkers(rec.config.testId), rec.startUtc, rec.endUtc, rec.status, rec.activeSeconds)

    suspend fun openConnection(rec: ConnectionTestRecord): ConnectionReport {
        val d = db.connection.getData(rec.config.testId)
        return connAnalyzer.analyze(rec.config, d.probes, d.link, d.speed, d.twoWay, d.markers, rec.startUtc, rec.endUtc, rec.status, rec.activeSeconds ?: 0.0)
    }

    // ------------------------------------------------------------------ REST API
    private val apiData = object : ApiData {
        override fun live() = live.value.aps
        override fun connection(): Any = connectionApiStatus(connRunner.isRunning, conn.value.config, conn.value.live, conn.value.report, conn.value.elapsed)
        override suspend fun connectionTests() = db.connection.getTests()
        override suspend fun sessions() = db.sessions.getAllSessions()
        override suspend fun session(id: String) = db.sessions.getSession(id)?.let { it to db.sessions.getSamples(id) }
        override suspend fun stats() = Triple(db.sessions.getAllSessions().size, db.sampleCount(), db.sizeMb())
    }
    private val restApi = RestApi(apiData, { settings.apiKey }, BuildConfig.VERSION_NAME, { settings.apiPort })
    private val apiServer = ApiHttpServer(restApi) { log.info(it) }

    fun startApi() {
        if (settings.apiKey.isEmpty()) settings.apiKey = newApiKey()
        try {
            apiServer.start(settings.apiPort, settings.apiLan)
            api.value = ServiceState(true, "Running on ${if (settings.apiLan) "this tablet's Wi-Fi address" else "localhost"}, port ${settings.apiPort}")
        } catch (e: Exception) { api.value = ServiceState(false, "Stopped", apiServer.lastError ?: e.message) }
    }
    fun stopApi() { apiServer.stop(); api.value = ServiceState(false, "Stopped") }
    fun newApiKey(): String = ByteArray(24).also { SecureRandom().nextBytes(it) }.let { Base64.getUrlEncoder().withoutPadding().encodeToString(it) }

    // ------------------------------------------------------------------ responder role
    private var responder: ResponderServer? = null
    fun startResponderRole() {
        stopResponderRole()
        if (settings.responderRoleKey.isEmpty()) settings.responderRoleKey = ResponderProtocol.keyToText(ResponderProtocol.newKey())
        try {
            val r = ResponderServer(ResponderProtocol.keyFromText(settings.responderRoleKey), settings.responderRolePort, null, "${Build.MANUFACTURER} ${Build.MODEL}") { log.info(it) }
            r.start(); responder = r
            val ip = wifiNet.link(wifiNet.network())?.linkAddresses?.map { it.address }?.firstOrNull { it is java.net.Inet4Address }?.hostAddress ?: "this tablet"
            responderRole.value = ServiceState(true, "Listening on $ip, TCP/UDP port ${r.port}")
        } catch (e: Exception) { responderRole.value = ServiceState(false, "Stopped", "Could not start the responder: ${e.message}") }
    }
    fun stopResponderRole() { responder?.stop(); responder = null; responderRole.value = ServiceState(false, "Stopped") }

    // ------------------------------------------------------------------ diagnostics
    fun diagnosticsFiles(context: Context): List<ExportFile> {
        val apk = runCatching { java.io.File(context.packageCodePath).readBytes().let { java.security.MessageDigest.getInstance("SHA-256").digest(it).joinToString("") { b -> "%02X".format(b) } } }.getOrDefault("")
        val info = buildString {
            appendLine("WirelessScanner ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE}) — Android diagnostics")
            appendLine("Generated: ${Instant.now()}")
            appendLine("APK SHA-256: $apk")
            appendLine("Device: ${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}), ABI ${Build.SUPPORTED_ABIS.joinToString()}")
            appendLine("Wi-Fi enabled: ${wifi.wifiEnabled}; location enabled: ${wifi.locationEnabled}; location permission: ${Permissions.hasLocation(app)}")
            appendLine("Scan throttling enabled (API 30+): ${wifi.throttleEnabled}; refused scans this run: ${wifi.refusedCount}")
            appendLine("Demo mode: ${settings.demoMode}; REST API: ${api.value.text}; responder role: ${responderRole.value.text}")
            appendLine("Scheduler: ${scheduler.cyclesCompleted} cycles, ${scheduler.providerCalls} provider calls; DB ${ReportText.fmt(db.sizeMb(), "0.00")} MB")
            appendLine(); appendLine("Recent log:"); log.recentLines().forEach { appendLine(it) }
        }
        return listOf(ExportFile("wirelessscanner-diagnostics.txt", "text/plain", info.toByteArray())) +
            log.files().map { ExportFile(it.name, "text/plain", it.readBytes()) }
    }
}
