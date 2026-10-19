package com.wirelessscanner.android.ui

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.AppGraph
import com.wirelessscanner.android.Step
import com.wirelessscanner.android.SurveyMode
import com.wirelessscanner.android.platform.Exporter
import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.forms.*
import com.wirelessscanner.core.report.ExportFile
import com.wirelessscanner.core.report.ExportFields
import com.wirelessscanner.core.report.ExportMetadata
import com.wirelessscanner.core.report.ReportText
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun ModePanel(g: AppGraph, modifier: Modifier = Modifier) {
    val mode by g.mode.collectAsState()
    Column(modifier.background(Hud.Bg).padding(8.dp).verticalScroll(rememberScrollState())) {
        when (mode) {
            SurveyMode.Monitor -> { SectionTitle("MONITOR"); Muted(SurveyMode.Monitor.description); Muted("Choose CAPTURE, SATURATION TEST or CONNECTION TEST above to record or test.", Modifier.padding(top = 8.dp)) }
            SurveyMode.Capture -> CapturePanel(g)
            SurveyMode.Saturation -> SaturationPanel(g)
            SurveyMode.Connection -> ConnectionPanel(g)
        }
    }
}

// ------------------------------------------------------------------ export
/**
 * Export actions (DR-0015): format choice, then "Share or send" (Sharesheet: email, chat, Drive, OneDrive…) or "Save a copy"
 * (system file picker: any installed document provider). A CSV bundle is shared as several files and saved as one ZIP.
 */
@Composable
fun ExportActions(title: String, baseName: String, makeFiles: suspend (AppGraph.Format) -> List<ExportFile>) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var format by rememberSaveable { mutableStateOf(0) }
    var status by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var pendingSave by remember { mutableStateOf<ExportFile?>(null) }
    val saver = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri: Uri? ->
        val f = pendingSave; pendingSave = null
        if (uri == null || f == null) { status = "Save cancelled."; return@rememberLauncherForActivityResult }
        scope.launch { status = try { withContext(Dispatchers.IO) { Exporter.writeTo(context, uri, f.bytes) }; "Saved ${f.name}." } catch (e: Exception) { "Could not save: ${e.message}" } }
    }
    val fmt = AppGraph.Format.entries[format]
    SectionTitle("EXPORT")
    Segments(listOf("PDF report", "CSV bundle", "JSON"), format) { format = it }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        HudButton("SHARE OR SEND", Modifier.weight(1f), enabled = !busy, primary = true) {
            busy = true; status = "Preparing ${fmt.label}…"
            scope.launch {
                try {
                    val files = withContext(Dispatchers.Default) { makeFiles(fmt) }
                    val written = withContext(Dispatchers.IO) { Exporter.write(context, baseName, files) }
                    context.startActivity(Exporter.shareIntent(context, written, title))
                    status = "Choose an app: email, Drive, OneDrive or any other."
                } catch (e: android.content.ActivityNotFoundException) { status = "No app on this tablet can receive these files. Use SAVE A COPY." }
                catch (e: Exception) { status = "Export failed: ${e.message}" }
                busy = false
            }
        }
        HudButton("SAVE A COPY", Modifier.weight(1f), enabled = !busy) {
            busy = true; status = "Preparing ${fmt.label}…"
            scope.launch {
                try {
                    val files = withContext(Dispatchers.Default) { makeFiles(fmt) }
                    val one = Exporter.saveable(baseName, files)
                    pendingSave = one
                    saver.launch(one.name)
                    status = "Choose where to save (Drive, OneDrive, Downloads…)."
                } catch (e: android.content.ActivityNotFoundException) { status = "No document provider is available." }
                catch (e: Exception) { status = "Export failed: ${e.message}" }
                busy = false
            }
        }
    }
    if (status.isNotEmpty()) Muted(status, Modifier.padding(top = 4.dp))
}

// ------------------------------------------------------------------ capture
@Composable
fun CapturePanel(g: AppGraph) {
    val cap by g.capture.collectAsState()
    var name by rememberSaveable { mutableStateOf("") }
    var facility by rememberSaveable { mutableStateOf("") }
    var scope by rememberSaveable { mutableStateOf("") }
    var zone by rememberSaveable { mutableStateOf("") }
    var notes by rememberSaveable { mutableStateOf("") }
    var stationary by rememberSaveable { mutableStateOf(false) }
    var confirmStop by remember { mutableStateOf(false) }
    SectionTitle("CAPTURE SESSION")
    Muted(SurveyMode.Capture.description, size = 12)
    if (!cap.active) {
        Field("Session name", name, placeholder = "Session_yyyyMMdd_HHmmss") { name = it }
        Field("Facility", facility, placeholder = "General Facility") { facility = it }
        Field("Scope", scope, placeholder = "All SSIDs") { scope = it }
        Field("Survey point / zone", zone, placeholder = "Default") { zone = it }
        Field("Notes", notes, singleLine = false) { notes = it }
        Segments(listOf("Roaming survey", "Stationary capture"), if (stationary) 1 else 0) { stationary = it == 1 }
        Muted(if (stationary) "Fixed spot. For full peak/drop/dropout analysis and reports use the SATURATION TEST." else "Walk the site and observe signal peaks and dips.", size = 12)
        Muted("Captured: ${g.selection.describe { s -> g.live.value.aps.count { it.ssid == s } }}", Modifier.padding(vertical = 6.dp), 12)
        HudButton("START CAPTURE", Modifier.fillMaxWidth(), primary = true) { g.startCapture(name, facility, scope, zone, notes, if (stationary) CaptureSessionMode.Stationary else CaptureSessionMode.Roaming) }
    } else {
        Text(if (cap.paused) "⏸ CAPTURE PAUSED" else "● RECORDING", color = if (cap.paused) Hud.Warn else Hud.Critical, fontWeight = FontWeight.Bold, fontSize = 16.sp)
        KeyValue("Session", cap.session?.name ?: ""); KeyValue("Facility", cap.session?.facilityName ?: ""); KeyValue("Scope", cap.session?.scope ?: "")
        KeyValue("Zone", cap.surveyPoint); KeyValue("Mode", cap.modeText); KeyValue("Samples shown", "${cap.logs.size} (latest 200)")
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 8.dp)) {
            if (cap.paused) HudButton("RESUME", Modifier.weight(1f), primary = true) { g.resumeCapture() } else HudButton("PAUSE", Modifier.weight(1f)) { g.pauseCapture() }
            HudButton("STOP", Modifier.weight(1f), danger = true) { confirmStop = true }
        }
    }
    cap.finished?.let { s ->
        SectionTitle("SESSION SAVED — EXPORT")
        Muted("${s.name}: export now or later from HISTORY → CAPTURE SESSIONS.", size = 12)
        SessionExport(g, s)
        HudButton("DONE", Modifier.fillMaxWidth().padding(top = 6.dp)) { g.dismissCaptureExport() }
    }
    if (confirmStop) ConfirmDialog("Stop capture", "Stop the capture session? Samples are saved on this tablet.", "STOP", { g.stopCapture() }) { confirmStop = false }
}

@Composable
fun SessionExport(g: AppGraph, s: CaptureSession) {
    var client by rememberSaveable { mutableStateOf("") }
    var surveyor by rememberSaveable { mutableStateOf("") }
    var notes by rememberSaveable { mutableStateOf("") }
    Field("Client name (PDF)", client) { client = it }
    Field("Surveyor name (PDF)", surveyor) { surveyor = it }
    Field("Notes (PDF)", notes) { notes = it }
    ExportActions("Capture session ${s.name}", "session_" + com.wirelessscanner.core.report.SessionReports.safe(s.name)) { f -> g.sessionFiles(s, f, ExportMetadata(client, surveyor, notes), ExportFields()) }
}

// ------------------------------------------------------------------ saturation
@Composable
fun PlaceEditor(place: PlaceFields, g: AppGraph, forConnection: Boolean, onChange: (PlaceFields) -> Unit) {
    val s by g.sat.collectAsState()
    SectionTitle("WHERE")
    Field("Test area / location (required)", place.location) { onChange(place.copy(location = it)) }
    Field("Facility", place.facility) { onChange(place.copy(facility = it)) }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Field("Operator", place.operator, Modifier.weight(1f)) { onChange(place.copy(operator = it)) }
        Field("Client", place.client, Modifier.weight(1f)) { onChange(place.copy(client = it)) }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Field("Latitude", place.latitude, Modifier.weight(1f), number = true) { onChange(place.copy(latitude = it, geoSource = "manual", geoCapturedUtc = null)) }
        Field("Longitude", place.longitude, Modifier.weight(1f), number = true) { onChange(place.copy(longitude = it, geoSource = "manual", geoCapturedUtc = null)) }
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        HudButton(if (s.locating) "DETECTING…" else "DETECT LOCATION", enabled = !s.locating) { g.detectLocation(forConnection) }
        Muted("  ${s.geoStatus}", size = 12)
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Field("Building", place.building, Modifier.weight(1f)) { onChange(place.copy(building = it)) }
        Field("Floor", place.floor, Modifier.weight(1f)) { onChange(place.copy(floor = it)) }
        Field("Zone", place.zone, Modifier.weight(1f)) { onChange(place.copy(zone = it)) }
    }
    Field("Notes", place.notes, singleLine = false) { onChange(place.copy(notes = it)) }
}

@Composable
fun SaturationPanel(g: AppGraph) {
    val s by g.sat.collectAsState()
    g.explorerView.collectAsState().value
    SectionTitle("SATURATION TEST")
    when (s.step) {
        Step.Setup -> SaturationSetup(g, s)
        Step.Running -> SaturationRunning(g, s)
        Step.Results -> s.report?.let { SaturationResults(g, it) }
    }
}

@Composable
private fun SaturationSetup(g: AppGraph, s: com.wirelessscanner.android.SatState) {
    val f = s.form
    var confirmAll by remember { mutableStateOf(false) }
    var advanced by rememberSaveable { mutableStateOf(false) }
    Muted(SurveyMode.Saturation.description, size = 12)
    SectionTitle("WHAT")
    Panel(Modifier.fillMaxWidth()) { Text(g.selection.describe { ssid -> g.live.value.aps.count { it.ssid == ssid } }, color = Hud.Text, fontSize = 13.sp); Muted("Tick networks or access points in the network list to choose what is tested.", size = 12) }
    SectionTitle("HOW LONG")
    Dropdown("Duration", DURATION_OPTIONS.map { it.label }, f.durationIndex, Modifier.fillMaxWidth()) { g.setSatForm(f.copy(durationIndex = it)) }
    if (f.durationOption.custom) Field("Custom duration (minutes, 1–1440)", f.customMinutes, number = true) { g.setSatForm(f.copy(customMinutes = it)) }
    Dropdown("Scan interval", INTERVAL_OPTIONS.map { "$it s" }, INTERVAL_OPTIONS.indexOf(f.intervalSeconds).coerceAtLeast(0), Modifier.fillMaxWidth().padding(top = 6.dp)) { g.setSatForm(f.copy(intervalSeconds = INTERVAL_OPTIONS[it])) }
    if (!g.settings.demoMode && g.wifi.throttleEnabled == true) Text("Android scan throttling is ON: at most 4 scans per 2 minutes, so cycles faster than 30 s are recorded as stale. Turn it off in SETTINGS → WI-FI SCANNING for one scan per interval.", color = Hud.Warn, fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp))
    Field("Test name", f.name, placeholder = "Saturation yyyy-MM-dd HH:mm") { g.setSatForm(f.copy(name = it)) }
    PlaceEditor(f.place, g, false) { g.setSatForm(f.copy(place = it)) }
    CheckRow("Keep the tablet awake during the test", f.keepAwake) { g.setSatForm(f.copy(keepAwake = it)) }
    TextButton({ advanced = !advanced }, Modifier.heightIn(min = 48.dp)) { Text(if (advanced) "▾ Advanced thresholds" else "▸ Advanced thresholds", color = Hud.Accent) }
    if (advanced) {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Field("Drop dB", f.dropDb, Modifier.weight(1f), number = true) { g.setSatForm(f.copy(dropDb = it)) }
            Field("Peak dB", f.peakDb, Modifier.weight(1f), number = true) { g.setSatForm(f.copy(peakDb = it)) }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Field("Dropout cycles", f.dropoutCycles, Modifier.weight(1f), number = true) { g.setSatForm(f.copy(dropoutCycles = it)) }
            Field("Baseline window", f.baselineWindow, Modifier.weight(1f), number = true) { g.setSatForm(f.copy(baselineWindow = it)) }
        }
    }
    Text(s.validation.summary, color = Hud.Text, fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp))
    if (s.validation.message.isNotEmpty()) Text(s.validation.message, color = Hud.Warn, fontSize = 13.sp)
    if (s.error.isNotEmpty()) Text(s.error, color = Hud.Critical, fontSize = 13.sp)
    HudButton("START TEST", Modifier.fillMaxWidth(), enabled = s.validation.canStart, primary = true) { if (g.selection.isEmpty) confirmAll = true else g.startSaturation() }
    if (confirmAll) ConfirmDialog("Test all networks", "Nothing is selected in the network list. Test every visible network (environment scope)?\n\nTip: tick SSIDs or access points in the list to test only those.", "TEST ALL", { g.startSaturation() }) { confirmAll = false }
}

@Composable
private fun SaturationRunning(g: AppGraph, s: com.wirelessscanner.android.SatState) {
    var marker by rememberSaveable { mutableStateOf("") }
    var confirmStop by remember { mutableStateOf(false) }
    Text(s.runStatus, color = if (s.paused) Hud.Warn else Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 14.sp)
    if (s.indeterminate) LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 6.dp), color = Hud.Accent)
    else LinearProgressIndicator({ (s.progressPct / 100).toFloat() }, Modifier.fillMaxWidth().padding(vertical = 6.dp), color = Hud.Accent)
    KeyValue("Elapsed (active)", s.elapsed); KeyValue("Remaining", s.remaining); Muted(s.cycleText, size = 12)
    if (s.warning.isNotEmpty()) Text(s.warning, color = Hud.Warn, fontSize = 12.sp)
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 8.dp)) {
        if (s.paused) HudButton("RESUME", Modifier.weight(1f), primary = true) { g.resumeSaturation() } else HudButton("PAUSE", Modifier.weight(1f)) { g.pauseSaturation() }
        HudButton("STOP", Modifier.weight(1f), danger = true) { confirmStop = true }
    }
    MarkerInput(marker, { marker = it }) { g.satMarker(it); marker = "" }
    if (confirmStop) ConfirmDialog("Stop test", "Stop the saturation test now? Results collected so far will be analysed and saved.", "STOP", { g.stopSaturation() }) { confirmStop = false }
}

@Composable
fun MarkerInput(value: String, onChange: (String) -> Unit, onAdd: (String) -> Unit) {
    SectionTitle("MARKER")
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        listOf("Door closed", "Door opened", "Microwave on", "People entered", "Moved the tablet").forEach { q -> HudChip(q, false) { onAdd(q) } }
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Field("Marker text", value, Modifier.weight(1f)) { onChange(it) }
        HudButton("ADD", Modifier.padding(start = 6.dp), enabled = value.isNotBlank()) { onAdd(value) }
    }
}

@Composable
fun SaturationResults(g: AppGraph, r: SaturationReport, fromHistory: Boolean = false, onClose: (() -> Unit)? = null) {
    Text("${r.config.name} — ${r.config.location} — ${r.status} — ${formatSpan(r.activeSeconds)} active, ${ReportText.n0(r.cycles.size)} cycles (${ReportText.n0(r.health.fresh)} fresh / ${ReportText.n0(r.health.stale)} stale / ${ReportText.n0(r.health.failed)} failed)", color = Hud.Text, fontSize = 13.sp)
    SectionTitle("RESULTS")
    Table(listOf("Target", "Grade", "Score", "Tier", "Avail. %", "Mean", "σ", "Min/Max", "Drops", "Dropouts", "Ch. chg"),
        r.targets.map { t -> listOf("${t.ssid.ifEmpty { "<hidden>" }} (${t.bssid})", t.grade, ReportText.fmt(t.stabilityScore, "0.0"), t.tier, ReportText.fmt(t.availabilityPct, "0.0"),
            ReportText.fmt(t.mean, "0.0"), ReportText.fmt(t.stdDev, "0.0"), "${t.min ?: "—"} / ${t.max ?: "—"}", "${t.dropCount}", "${t.dropoutCount}", "${t.channelChangeCount}") },
        listOf(240, 50, 60, 80, 70, 60, 50, 80, 55, 70, 60))
    r.targets.forEach { t -> Text("${t.ssid.ifEmpty { "<hidden>" }} (${t.bssid})", color = Hud.Accent2, fontSize = 13.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 6.dp)); t.findings.forEach { Muted("• $it", size = 12) } }
    if (r.unitSummaries.isNotEmpty()) {
        SectionTitle("ACCESS POINTS (PHYSICAL UNITS)")
        r.unitSummaries.forEach { u -> Text("${u.name} · ${u.confidence} · on air ${ReportText.fmt(u.unitAvailabilityPct, "0.0")} % · best mean ${ReportText.fmt(u.bestMeanDbm, "0.0")} dBm", color = Hud.Text, fontSize = 13.sp); u.findings.forEach { Muted("• $it", size = 12) } }
    }
    ExportActions("Saturation test ${r.config.name}", "saturation_" + com.wirelessscanner.core.report.SessionReports.safe(r.config.name)) { f -> g.saturationFiles(r, f) }
    if (!fromHistory) HudButton("NEW TEST", Modifier.fillMaxWidth().padding(top = 8.dp)) { g.newSaturation() }
    onClose?.let { HudButton("CLOSE", Modifier.fillMaxWidth().padding(top = 8.dp)) { it() } }
}

// ------------------------------------------------------------------ connection
@Composable
fun ConnectionPanel(g: AppGraph) {
    val s by g.conn.collectAsState()
    SectionTitle("CONNECTION TEST")
    when (s.step) {
        Step.Setup -> ConnectionSetup(g, s)
        Step.Running -> ConnectionRunning(g, s)
        Step.Results -> s.report?.let { ConnectionResults(g, it) }
    }
}

@Composable
private fun ConnectionSetup(g: AppGraph, s: com.wirelessscanner.android.ConnState) {
    val f = s.form
    Muted(SurveyMode.Connection.description, size = 12)
    SectionTitle("WHAT IS TESTED (YOUR CONNECTION)")
    Panel(Modifier.fillMaxWidth()) {
        Text(ConnectionForm.contextText(s.context, s.reading, g.unitNameFor(s.context?.bssid)), color = Hud.Text, fontSize = 13.sp)
        val w = ConnectionForm.contextWarning(s.context); if (w.isNotEmpty()) Text(w, color = Hud.Warn, fontSize = 12.sp)
        HudButton("REFRESH", Modifier.padding(top = 6.dp)) { g.refreshContext() }
    }
    SectionTitle("PARTS OF THE PATH")
    CheckRow("Wi-Fi link (signal, rate, roams)", f.probeWifiLink) { g.setConnForm(f.copy(probeWifiLink = it)) }
    CheckRow("Router (gateway)", f.probeGateway) { g.setConnForm(f.copy(probeGateway = it)) }
    CheckRow("ISP hop", f.probeIsp) { g.setConnForm(f.copy(probeIsp = it)) }
    CheckRow("Internet", f.probeInternet) { g.setConnForm(f.copy(probeInternet = it)) }
    if (f.probeInternet) Field("Internet targets (IPv4)", f.internetTargets) { g.setConnForm(f.copy(internetTargets = it)) }
    CheckRow("DNS", f.probeDns) { g.setConnForm(f.copy(probeDns = it)) }
    CheckRow("Web (HTTPS)", f.probeWeb) { g.setConnForm(f.copy(probeWeb = it)) }
    SectionTitle("HOW LONG")
    Dropdown("Duration", DURATION_OPTIONS.map { it.label }, f.durationIndex, Modifier.fillMaxWidth()) { g.setConnForm(f.copy(durationIndex = it)) }
    if (f.durationOption.custom) Field("Custom duration (minutes, 1–1440)", f.customMinutes, number = true) { g.setConnForm(f.copy(customMinutes = it)) }
    Segments(listOf("Light (1 s)", "Detailed (0.5 s)"), f.intensity.ordinal, Modifier.padding(top = 6.dp)) { g.setConnForm(f.copy(intensity = ConnectionIntensity.entries[it])) }
    Field("Test name", f.name, placeholder = "Connection yyyy-MM-dd HH:mm") { g.setConnForm(f.copy(name = it)) }
    PlaceEditor(f.place, g, true) { g.setConnForm(f.copy(place = it)) }
    SectionTitle("OPTIONS")
    CheckRow("Also run a saturation test of the connected access point", f.alsoSaturation) { g.setConnForm(f.copy(alsoSaturation = it)) }
    CheckRow("Stop if the tablet moves to another network", f.stopOnNetworkChange) { g.setConnForm(f.copy(stopOnNetworkChange = it)) }
    CheckRow("Keep the tablet awake during the test", f.keepAwake) { g.setConnForm(f.copy(keepAwake = it)) }
    CheckRow("Speed test at start", f.speedAtStart) { g.setConnForm(f.copy(speedAtStart = it)) }
    Dropdown("Speed test every", SCHEDULE_OPTIONS.map { if (it == 0) "Not scheduled" else "$it min" }, SCHEDULE_OPTIONS.indexOf(f.speedEveryMinutes).coerceAtLeast(0), Modifier.fillMaxWidth()) { g.setConnForm(f.copy(speedEveryMinutes = SCHEDULE_OPTIONS[it])) }
    CheckRow("Two-way test at start (LAN responder)", f.twoWayAtStart) { g.setConnForm(f.copy(twoWayAtStart = it)) }
    Dropdown("Two-way test every", SCHEDULE_OPTIONS.map { if (it == 0) "Not scheduled" else "$it min" }, SCHEDULE_OPTIONS.indexOf(f.twoWayEveryMinutes).coerceAtLeast(0), Modifier.fillMaxWidth()) { g.setConnForm(f.copy(twoWayEveryMinutes = SCHEDULE_OPTIONS[it])) }
    Text(s.validation.summary, color = Hud.Text, fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp))
    if (s.validation.message.isNotEmpty()) Text(s.validation.message, color = Hud.Warn, fontSize = 13.sp)
    if (s.error.isNotEmpty()) Text(s.error, color = Hud.Critical, fontSize = 13.sp)
    HudButton("START TEST", Modifier.fillMaxWidth(), enabled = s.validation.canStart, primary = true) { g.startConnection() }
}

@Composable
private fun ConnectionRunning(g: AppGraph, s: com.wirelessscanner.android.ConnState) {
    var marker by rememberSaveable { mutableStateOf("") }
    var confirmStop by remember { mutableStateOf(false) }
    var confirmSpeed by remember { mutableStateOf(false) }
    Text(s.runStatus, color = if (s.paused) Hud.Warn else Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 14.sp)
    if (s.indeterminate) LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 6.dp), color = Hud.Accent)
    else LinearProgressIndicator({ (s.progressPct / 100).toFloat() }, Modifier.fillMaxWidth().padding(vertical = 6.dp), color = Hud.Accent)
    KeyValue("Elapsed (active)", s.elapsed); KeyValue("Remaining", s.remaining)
    if (s.activity.isNotEmpty()) Text(s.activity, color = Hud.Accent2, fontSize = 13.sp)
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 8.dp)) {
        if (s.paused) HudButton("RESUME", Modifier.weight(1f), primary = true) { g.resumeConnection() } else HudButton("PAUSE", Modifier.weight(1f)) { g.pauseConnection() }
        HudButton("STOP", Modifier.weight(1f), danger = true) { confirmStop = true }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        HudButton("RUN SPEED TEST", Modifier.weight(1f)) { confirmSpeed = true }
        HudButton("RUN TWO-WAY TEST", Modifier.weight(1f)) { g.runTwoWayNow() }
    }
    if (s.actionStatus.isNotEmpty()) Muted(s.actionStatus, Modifier.padding(top = 4.dp), 12)
    MarkerInput(marker, { marker = it }) { g.connMarker(it); marker = "" }
    if (confirmStop) ConfirmDialog("Stop test", "Stop the connection test now? Results collected so far will be analysed and saved.", "STOP", { g.stopConnection() }) { confirmStop = false }
    if (confirmSpeed) {
        val sec = g.settings.speedServer.secondsPerDirection
        ConfirmDialog("Speed test", "Run a speed test? It saturates the connection for about ${2 * sec + 4} s (other users of this network will notice) and transfers as much data as the link allows. Regular probes pause meanwhile.", "RUN", { g.runSpeedNow() }) { confirmSpeed = false }
    }
}

@Composable
fun ConnectionResults(g: AppGraph, r: ConnectionReport, fromHistory: Boolean = false, onClose: (() -> Unit)? = null) {
    Text("${r.config.name} — ${r.config.location} — ${r.status} — ${formatSpan(r.activeSeconds)} active · ${ReportText.n0(r.totals.probesSent)} probes", color = Hud.Text, fontSize = 13.sp)
    SectionTitle("SUMMARY")
    r.verdicts.forEach { v ->
        val col = when (v.rating) { "GOOD" -> Hud.Good; "FAIR" -> Hud.Warn; "POOR" -> Hud.Critical; else -> Hud.Dim }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(vertical = 2.dp)) { Text(v.use, color = Hud.Text, fontSize = 13.sp, modifier = Modifier.width(130.dp)); Badge(v.rating, col); Muted("  ${v.reason}", size = 11) }
    }
    Muted("Voice estimate: ${if (r.voiceMos != null) "R ${ReportText.fmt(r.voiceRFactor, "0.0")}, MOS ${ReportText.fmt(r.voiceMos, "0.00")} (estimate)" else "not available"}", size = 12)
    val problems = r.problemsBySegment.entries.sortedBy { it.key.ordinal }.map { "${ConnectionAnalyzer.name(it.key)} ${it.value}" }
    Text(if (problems.isEmpty()) "No outages or latency spikes were attributed to any segment." else "Where problems happened: " + problems.joinToString(" · "), color = Hud.Accent2, fontSize = 13.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(vertical = 4.dp))
    SectionTitle("SEGMENTS")
    Table(listOf("Segment", "Method", "Sent", "Loss %", "Avail. %", "p50", "p95", "Jitter", "Outages"),
        r.segments.map { s -> listOf(ConnectionAnalyzer.name(s.segment), s.method.name, "${s.sent}", ReportText.fmt(s.lossPct, "0.##"), ReportText.fmt(s.availabilityPct, "0.##"),
            ReportText.fmt(s.rttP50, "0.0"), ReportText.fmt(s.rttP95, "0.0"), ReportText.fmt(s.jitterMs, "0.0"), "${s.outages}") },
        listOf(140, 70, 60, 60, 70, 60, 60, 60, 60))
    if (r.speedTests.isNotEmpty()) { SectionTitle("SPEED"); r.speedTests.forEach { t -> Muted(t.error?.let { "Failed: $it" } ?: "Down ${ReportText.fmt(t.downloadMbps, "0.0")} / up ${ReportText.fmt(t.uploadMbps, "0.0")} Mbps · +${ReportText.fmt(t.latencyIncreaseMs, "0")} ms under load · grade ${t.bufferbloatGrade}") } }
    if (r.twoWayTests.isNotEmpty()) { SectionTitle("TWO-WAY"); r.twoWayTests.forEach { t -> Muted(t.error?.let { "Failed: $it" } ?: "Loss up ${ReportText.fmt(t.upLossPct, "0.##")} % / down ${ReportText.fmt(t.downLossPct, "0.##")} % · MOS ${ReportText.fmt(t.mosUp, "0.00")} / ${ReportText.fmt(t.mosDown, "0.00")} (est.)") } }
    ExportActions("Connection test ${r.config.name}", "connection_" + com.wirelessscanner.core.report.SessionReports.safe(r.config.name)) { f -> g.connectionFiles(r, f) }
    if (!fromHistory) HudButton("NEW TEST", Modifier.fillMaxWidth().padding(top = 8.dp)) { g.newConnection() }
    onClose?.let { HudButton("CLOSE", Modifier.fillMaxWidth().padding(top = 8.dp)) { it() } }
}
