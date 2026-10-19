package com.wirelessscanner.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.AppGraph
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.forms.formatSpan
import com.wirelessscanner.core.report.ReportText
import kotlinx.coroutines.launch

/** HISTORY / LOGS (Windows): capture sessions, saturation tests, connection tests → open results, export, delete. */
@Composable
fun HistoryScreen(g: AppGraph, wide: Boolean) {
    var tab by rememberSaveable { mutableStateOf(0) }
    Column(Modifier.fillMaxSize().background(Hud.Bg).padding(8.dp)) {
        Text("HISTORY", color = Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 16.sp, letterSpacing = 1.sp)
        Segments(listOf("CAPTURE SESSIONS", "SATURATION TESTS", "CONNECTION TESTS"), tab, Modifier.padding(vertical = 6.dp)) { tab = it }
        when (tab) { 0 -> SessionHistory(g, wide); 1 -> SaturationHistory(g, wide); else -> ConnectionHistory(g, wide) }
    }
}

/** List on the left and the opened item on the right (wide), or list then item (narrow). */
@Composable
private fun <T> MasterDetail(wide: Boolean, items: List<T>, key: (T) -> String, selected: T?, onSelect: (T?) -> Unit, empty: String,
                             row: @Composable (T) -> Unit, detail: @Composable (T) -> Unit) {
    if (wide) Row(Modifier.fillMaxSize()) {
        ListPane(items, key, selected, onSelect, empty, row, Modifier.width(420.dp).fillMaxHeight())
        Box(Modifier.weight(1f).fillMaxHeight().padding(start = 8.dp)) { selected?.let { Column(Modifier.verticalScroll(rememberScrollState())) { detail(it) } } ?: Muted("Choose an item on the left.", Modifier.padding(12.dp)) }
    } else {
        if (selected == null) ListPane(items, key, null, onSelect, empty, row, Modifier.fillMaxSize())
        else Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
            TextButton({ onSelect(null) }, Modifier.heightIn(min = 48.dp)) { Text("◂ Back to the list", color = Hud.Accent) }
            detail(selected)
        }
    }
}

@Composable
private fun <T> ListPane(items: List<T>, key: (T) -> String, selected: T?, onSelect: (T?) -> Unit, empty: String, row: @Composable (T) -> Unit, modifier: Modifier) {
    if (items.isEmpty()) { Muted(empty, modifier.padding(12.dp)); return }
    LazyColumn(modifier) {
        items(items, key = key) { item ->
            Column(Modifier.fillMaxWidth().padding(vertical = 3.dp).border(if (item == selected) 2.dp else 1.dp, if (item == selected) Hud.Accent else Hud.Border, RoundedCornerShape(6.dp))
                .clickable { onSelect(item) }.heightIn(min = 56.dp).padding(10.dp)) { row(item) }
        }
    }
}

@Composable
private fun SessionHistory(g: AppGraph, wide: Boolean) {
    var list by remember { mutableStateOf<List<CaptureSession>>(emptyList()) }
    var selected by remember { mutableStateOf<CaptureSession?>(null) }
    var deleting by remember { mutableStateOf<CaptureSession?>(null) }
    var reload by remember { mutableStateOf(0) }
    LaunchedEffect(reload) { list = g.db.sessions.getAllSessions() }
    MasterDetail(wide, list, { it.sessionId }, selected, { selected = it }, "No capture sessions yet. Start one in LIVE SURVEY → CAPTURE.",
        row = { s ->
            Text(s.name, color = Hud.Text, fontWeight = FontWeight.Bold, fontSize = 14.sp)
            Muted("${ReportText.local(s.startTime, g.zone, "yyyy-MM-dd HH:mm")} · ${s.facilityName} · ${s.mode} · ${s.status}", size = 12)
        },
        detail = { s ->
            var samples by remember(s.sessionId) { mutableStateOf<Int?>(null) }
            LaunchedEffect(s.sessionId) { samples = g.db.sessions.getSamples(s.sessionId).size }
            SectionTitle("CAPTURE SESSION")
            KeyValue("Name", s.name); KeyValue("Facility", s.facilityName); KeyValue("Scope", s.scope); KeyValue("Mode", if (s.mode == CaptureSessionMode.Stationary) "Stationary Capture" else "Roaming Survey")
            KeyValue("Started", ReportText.localWithOffset(s.startTime, g.zone)); KeyValue("Ended", s.endTime?.let { ReportText.localWithOffset(it, g.zone) } ?: "Active")
            KeyValue("Samples", samples?.toString() ?: "…"); KeyValue("Status", s.status.name)
            SessionExport(g, s)
            HudButton("DELETE SESSION", Modifier.fillMaxWidth().padding(top = 12.dp), danger = true) { deleting = s }
        })
    deleting?.let { s -> val scope = rememberCoroutineScope()
        ConfirmDialog("Delete session", "Delete '${s.name}' and all its samples from this tablet? This cannot be undone.", "DELETE", { scope.launch { g.db.sessions.deleteSession(s.sessionId); selected = null; reload++ } }) { deleting = null } }
}

@Composable
private fun SaturationHistory(g: AppGraph, wide: Boolean) {
    var list by remember { mutableStateOf<List<SaturationTestRecord>>(emptyList()) }
    var selected by remember { mutableStateOf<SaturationTestRecord?>(null) }
    var report by remember { mutableStateOf<SaturationReport?>(null) }
    var deleting by remember { mutableStateOf<SaturationTestRecord?>(null) }
    var reload by remember { mutableStateOf(0) }
    val running by g.sat.collectAsState()
    LaunchedEffect(reload, running.step) { list = g.db.saturation.getTests() }
    LaunchedEffect(selected) { report = null; selected?.let { report = g.openSaturation(it) } }
    MasterDetail(wide, list, { it.config.testId }, selected, { selected = it }, "No saturation tests yet. Run one in LIVE SURVEY → SATURATION TEST.",
        row = { t ->
            Text(t.config.name, color = Hud.Text, fontWeight = FontWeight.Bold, fontSize = 14.sp)
            Muted("${ReportText.local(t.startUtc, g.zone, "yyyy-MM-dd HH:mm")} · ${t.config.location} · ${t.config.scope} · ${t.cycleCount} cycles · ${t.status}" +
                (t.config.geo.takeIf { it.hasCoordinates }?.let { " · ${it.coordinatesText}" } ?: ""), size = 12)
        },
        detail = { t ->
            val r = report
            if (r == null) Muted("Analysing…") else SaturationResults(g, r, fromHistory = true)
            HudButton("DELETE TEST", Modifier.fillMaxWidth().padding(top = 12.dp), danger = true, enabled = t.status != SaturationTestStatus.Running && t.status != SaturationTestStatus.Paused) { deleting = t }
        })
    deleting?.let { t -> val scope = rememberCoroutineScope()
        ConfirmDialog("Delete test", "Delete '${t.config.name}' and all its cycles from this tablet? This cannot be undone.", "DELETE", { scope.launch { g.db.saturation.deleteTest(t.config.testId); selected = null; reload++ } }) { deleting = null } }
}

@Composable
private fun ConnectionHistory(g: AppGraph, wide: Boolean) {
    var list by remember { mutableStateOf<List<ConnectionTestRecord>>(emptyList()) }
    var selected by remember { mutableStateOf<ConnectionTestRecord?>(null) }
    var report by remember { mutableStateOf<ConnectionReport?>(null) }
    var deleting by remember { mutableStateOf<ConnectionTestRecord?>(null) }
    var reload by remember { mutableStateOf(0) }
    val running by g.conn.collectAsState()
    LaunchedEffect(reload, running.step) { list = g.db.connection.getTests() }
    LaunchedEffect(selected) { report = null; selected?.let { report = g.openConnection(it) } }
    MasterDetail(wide, list, { it.config.testId }, selected, { selected = it }, "No connection tests yet. Run one in LIVE SURVEY → CONNECTION TEST.",
        row = { t ->
            Text(t.config.name, color = Hud.Text, fontWeight = FontWeight.Bold, fontSize = 14.sp)
            Muted("${ReportText.local(t.startUtc, g.zone, "yyyy-MM-dd HH:mm")} · ${t.config.location} · ${t.config.contextAtStart?.ssid ?: ""} · ${t.activeSeconds?.let { formatSpan(it) } ?: ""} · ${t.status}" +
                (if (t.config.linkedSaturationTestId != null) " · with saturation test" else ""), size = 12)
        },
        detail = { t ->
            val r = report
            if (r == null) Muted("Analysing…") else ConnectionResults(g, r, fromHistory = true)
            HudButton("DELETE TEST", Modifier.fillMaxWidth().padding(top = 12.dp), danger = true, enabled = t.status != ConnectionTestStatus.Running && t.status != ConnectionTestStatus.Paused) { deleting = t }
        })
    deleting?.let { t -> val scope = rememberCoroutineScope()
        ConfirmDialog("Delete test", "Delete '${t.config.name}' and all its probes from this tablet? This cannot be undone.", "DELETE", { scope.launch { g.db.connection.deleteTest(t.config.testId); selected = null; reload++ } }) { deleting = null } }
}
