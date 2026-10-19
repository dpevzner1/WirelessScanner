package com.wirelessscanner.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.AppGraph
import com.wirelessscanner.android.BuildConfig
import com.wirelessscanner.android.platform.Exporter
import com.wirelessscanner.core.domain.ResponderSettings
import com.wirelessscanner.core.domain.SpeedServerKind
import com.wirelessscanner.core.domain.SpeedServerSettings
import com.wirelessscanner.core.responder.ResponderProtocol
import kotlinx.coroutines.launch

/** SETTINGS (Windows): REST API, TEST SERVERS, plus Android's Wi-Fi scanning guide, responder role, demo mode and diagnostics. */
@Composable
fun SettingsScreen(g: AppGraph) {
    var tab by rememberSaveable { mutableStateOf(0) }
    Column(Modifier.fillMaxSize().background(Hud.Bg).padding(8.dp)) {
        Text("SETTINGS", color = Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 16.sp, letterSpacing = 1.sp)
        Segments(listOf("WI-FI SCANNING", "REST API", "TEST SERVERS", "RESPONDER ROLE", "ABOUT & DIAGNOSTICS"), tab, Modifier.padding(vertical = 6.dp)) { tab = it }
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).widthIn(max = 900.dp)) {
            when (tab) { 0 -> WifiSettings(g); 1 -> ApiSettings(g); 2 -> ServerSettings(g); 3 -> ResponderRoleSettings(g); else -> AboutSettings(g) }
        }
    }
}

private fun copy(context: Context, label: String, text: String) =
    (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText(label, text))

@Composable
private fun WifiSettings(g: AppGraph) {
    val context = LocalContext.current
    val live by g.live.collectAsState()
    var demo by remember { mutableStateOf(g.settings.demoMode) }
    SectionTitle("WI-FI SCANNING")
    val throttle = g.wifi.throttleEnabled
    Panel(Modifier.fillMaxWidth()) {
        KeyValue("Wi-Fi", if (g.wifi.wifiEnabled) "ON" else "OFF — turn it on to scan")
        KeyValue("Location", if (g.wifi.locationEnabled) "ON" else "OFF — Android returns no scan results while location is off")
        KeyValue("Scan throttling", when (throttle) { true -> "ON — at most 4 scans per 2 minutes"; false -> "OFF — one scan per interval"; null -> "unknown on this Android version" })
        KeyValue("Refused scans (this run)", "${g.wifi.refusedCount}")
        KeyValue("Live scan interval", "${live.scanIntervalSeconds} s" + if (live.scanIntervalSeconds > 5) " (slowed because scans are throttled)" else "")
    }
    SectionTitle("TURN OFF SCAN THROTTLING (RECOMMENDED FOR TESTS)")
    Muted("Android limits apps to 4 Wi-Fi scans every 2 minutes. While it does, the live view scans every 30 s and saturation tests faster than 30 s record stale cycles (never presented as fresh).", size = 13)
    Muted("1. Settings → About tablet → Software information → tap Build number 7 times (Developer options).\n2. Settings → Developer options → Wi-Fi scan throttling → OFF.\n3. Come back here: the status above shows OFF.", Modifier.padding(vertical = 6.dp), 13)
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        HudButton("OPEN DEVELOPER OPTIONS") { runCatching { context.startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)) }.onFailure { g.toast.value = "Developer options are not enabled yet (step 1)." } }
        HudButton("WI-FI SETTINGS") { context.startActivity(Intent(Settings.ACTION_WIFI_SETTINGS)) }
        HudButton("LOCATION SETTINGS") { context.startActivity(Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS)) }
    }
    SectionTitle("DEMO MODE")
    SwitchRow("Demo mode (synthetic networks)", demo, "Shows built-in example networks instead of this tablet's radio, for training. Off for real surveys.") {
        demo = it; g.settings.demoMode = it; g.scanNow(); g.revalidate()
    }
}

@Composable
private fun ApiSettings(g: AppGraph) {
    val context = LocalContext.current
    val api by g.api.collectAsState()
    var port by remember { mutableStateOf(g.settings.apiPort.toString()) }
    var lan by remember { mutableStateOf(g.settings.apiLan) }
    var showKey by remember { mutableStateOf(false) }
    var key by remember { mutableStateOf(g.settings.apiKey) }
    SectionTitle("REST API")
    Muted("Read live networks, sessions and connection-test status from scripts. The key is sent in the X-API-Key header; keys in the URL are refused.", size = 13)
    Panel(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Text(if (api.running) "RUNNING" else "STOPPED", color = if (api.running) Hud.Good else Hud.Dim, fontWeight = FontWeight.Bold)
        Muted(api.text, size = 12); api.error?.let { Text(it, color = Hud.Critical, fontSize = 12.sp) }
    }
    Field("Port (1024–65535)", port, number = true) { port = it.filter(Char::isDigit).take(5) }
    SwitchRow("Allow other devices on this Wi-Fi", lan, "Off = only apps on this tablet (localhost).") { lan = it }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Field("API key", if (showKey) key else "•".repeat(minOf(24, key.length)), Modifier.weight(1f)) { if (showKey) key = it }
        HudButton(if (showKey) "HIDE" else "SHOW") { showKey = !showKey }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        HudButton("GENERATE") { key = g.newApiKey(); showKey = true }
        HudButton("COPY KEY", enabled = key.isNotEmpty()) { copy(context, "API key", key); g.toast.value = "API key copied." }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        HudButton("APPLY AND START", primary = true) {
            val p = port.toIntOrNull()
            if (p == null || p !in 1024..65535) { g.toast.value = "Choose a port between 1024 and 65535."; return@HudButton }
            g.settings.apiPort = p; g.settings.apiLan = lan; g.settings.apiKey = key; g.settings.apiEnabled = true; g.startApi()
        }
        HudButton("STOP", enabled = api.running) { g.settings.apiEnabled = false; g.stopApi() }
    }
    SectionTitle("ENDPOINTS (GET)")
    listOf("/api/status", "/api/live", "/api/connection", "/api/connection/tests", "/api/sessions", "/api/sessions/{id}", "/api/stats").forEach { Mono(it) }
}

@Composable
private fun ServerSettings(g: AppGraph) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val sp = g.settings.speedServer
    var kind by remember { mutableStateOf(sp.kind) }
    var dl by remember { mutableStateOf(sp.httpDownloadUrl) }
    var ul by remember { mutableStateOf(sp.httpUploadUrl) }
    var streams by remember { mutableStateOf(sp.streams.toString()) }
    var seconds by remember { mutableStateOf(sp.secondsPerDirection.toString()) }
    val rs = g.settings.responder
    var addr by remember { mutableStateOf(rs.address) }
    var rport by remember { mutableStateOf(rs.port.toString()) }
    var rkey by remember { mutableStateOf(rs.key) }
    var status by remember { mutableStateOf("") }
    SectionTitle("SPEED TEST SERVER (STAGE B)")
    Muted("No public speed-test provider is preset: some publish results with your IP address. Use the LAN responder or your own HTTP server.", size = 13)
    Segments(listOf("None", "LAN responder", "Own HTTP server"), kind.ordinal, Modifier.padding(vertical = 6.dp)) { kind = SpeedServerKind.entries[it] }
    if (kind == SpeedServerKind.Http) {
        Field("Download URL (large file)", dl, placeholder = "http://192.0.2.10/1G.bin") { dl = it }
        Field("Upload URL (accepts POST)", ul, placeholder = "http://192.0.2.10/upload") { ul = it }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Field("Streams (1–16)", streams, Modifier.weight(1f), number = true) { streams = it.filter(Char::isDigit) }
        Field("Seconds per direction (3–30)", seconds, Modifier.weight(1f), number = true) { seconds = it.filter(Char::isDigit) }
    }
    SectionTitle("LAN RESPONDER (STAGE C)")
    Muted("Run WirelessScanner.Responder on a wired computer (or the responder role on another tablet), then paste its address and key here.", size = 13)
    Field("Responder address", addr, placeholder = "192.0.2.10") { addr = it.trim() }
    Field("Port", rport, number = true) { rport = it.filter(Char::isDigit) }
    Field("Responder key (from the responder)", rkey) { rkey = it.trim() }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        HudButton("SAVE", primary = true) {
            g.settings.speedServer = SpeedServerSettings(kind, dl.trim(), ul.trim(), (streams.toIntOrNull() ?: 4).coerceIn(1, 16), (seconds.toIntOrNull() ?: 10).coerceIn(3, 30))
            val keyOk = rkey.isEmpty() || runCatching { ResponderProtocol.keyFromText(rkey) }.isSuccess
            if (!keyOk) { status = "The responder key is not valid. Copy it again from the responder (--show-key)."; return@HudButton }
            g.settings.responder = ResponderSettings(addr, rport.toIntOrNull() ?: 5410, rkey)
            g.revalidate(); status = "Saved."
        }
        HudButton("CHECK RESPONDER", enabled = addr.isNotBlank() && rkey.isNotBlank()) {
            status = "Checking…"
            scope.launch {
                g.settings.responder = ResponderSettings(addr, rport.toIntOrNull() ?: 5410, rkey)
                status = g.responderClient()?.check()?.second ?: "The responder key is not valid."
            }
        }
    }
    if (status.isNotEmpty()) Muted(status, Modifier.padding(top = 4.dp))
}

@Composable
private fun ResponderRoleSettings(g: AppGraph) {
    val context = LocalContext.current
    val state by g.responderRole.collectAsState()
    var showKey by remember { mutableStateOf(false) }
    var port by remember { mutableStateOf(g.settings.responderRolePort.toString()) }
    SectionTitle("RESPONDER ROLE")
    Muted("Let another WirelessScanner (Windows or Android) run two-way and LAN speed tests against this tablet. Only devices with the key are answered; replies are never larger than requests.", size = 13)
    Panel(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Text(if (state.running) "LISTENING" else "STOPPED", color = if (state.running) Hud.Good else Hud.Dim, fontWeight = FontWeight.Bold)
        Muted(state.text, size = 12); state.error?.let { Text(it, color = Hud.Critical, fontSize = 12.sp) }
    }
    Field("Port", port, number = true) { port = it.filter(Char::isDigit).take(5) }
    val key = g.settings.responderRoleKey
    if (key.isNotEmpty()) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(if (showKey) key else "Key hidden", color = Hud.Text, fontSize = 13.sp, modifier = Modifier.weight(1f).padding(top = 14.dp))
        HudButton(if (showKey) "HIDE" else "SHOW KEY") { showKey = !showKey }
        HudButton("COPY") { copy(context, "Responder key", key); g.toast.value = "Responder key copied." }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        HudButton("START", primary = true) { g.settings.responderRolePort = port.toIntOrNull()?.coerceIn(1024, 65535) ?: 5410; g.settings.responderRoleEnabled = true; g.startResponderRole() }
        HudButton("STOP", enabled = state.running) { g.settings.responderRoleEnabled = false; g.stopResponderRole() }
        HudButton("NEW KEY") { g.settings.responderRoleKey = ResponderProtocol.keyToText(ResponderProtocol.newKey()); if (state.running) g.startResponderRole(); showKey = true }
    }
}

@Composable
private fun AboutSettings(g: AppGraph) {
    val context = LocalContext.current
    SectionTitle("ABOUT")
    KeyValue("App", "WirelessScanner ${BuildConfig.VERSION_NAME} for Android (port of the Windows app 1.5.0)")
    KeyValue("Device", "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}, Android ${android.os.Build.VERSION.RELEASE} (API ${android.os.Build.VERSION.SDK_INT})")
    KeyValue("Analytics", "SAT-ANALYTICS-3 · CONN-ANALYTICS-1 (same as Windows)")
    KeyValue("Data on this tablet", "${com.wirelessscanner.core.report.ReportText.fmt(g.db.sizeMb(), "0.00")} MB (private app storage; removed when the app is uninstalled)")
    SectionTitle("MEASUREMENT LIMITS ON ANDROID")
    Muted("• RSSI is reported by the Wi-Fi firmware and is not calibrated; compare results from the same device.\n• Android exposes no noise floor (SNR is an estimate), no radio timer (TSF) and no 802.11 frame counters.\n• Android limits app scans unless scan throttling is turned off (WI-FI SCANNING).\n• Physical access points are inferred from WPS identity, Wi-Fi 7 multi-link (MLD) address and MAC family.", size = 13)
    SectionTitle("DIAGNOSTICS")
    Muted("Share the app log and device details (no passwords, keys or scan data) when reporting a problem.", size = 13)
    HudButton("SHARE DIAGNOSTICS", Modifier.padding(top = 6.dp)) {
        try {
            val files = Exporter.write(context, "diagnostics", g.diagnosticsFiles(context))
            context.startActivity(Exporter.shareIntent(context, files, "WirelessScanner diagnostics"))
        } catch (e: Exception) { g.toast.value = "Could not share diagnostics: ${e.message}" }
    }
}
