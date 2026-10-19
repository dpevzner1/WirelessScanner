package com.wirelessscanner.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.*
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.platform.Permissions
import com.wirelessscanner.android.ui.*

class MainActivity : ComponentActivity() {
    private val graph get() = (application as WirelessScannerApp).graph

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { WirelessScannerTheme { Root(graph) } }
    }

    override fun onStart() { super.onStart(); graph.setVisible(true) }

    /** Rotation and split-screen recreate the activity; that is not leaving the app (A-LRN-044). */
    override fun onStop() { if (!isChangingConfigurations) graph.setVisible(false); super.onStop() }
}

private enum class Dest(val label: String, val glyph: String) { Live("LIVE SURVEY", "◉"), History("HISTORY", "☰"), Settings("SETTINGS", "⚙") }

@Composable
private fun Root(g: AppGraph) {
    val context = LocalContext.current
    var granted by remember { mutableStateOf(Permissions.hasLocation(context)) }
    var asked by rememberSaveable { mutableStateOf(false) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        granted = Permissions.hasLocation(context); asked = true; g.revalidate(); g.setVisible(true)
    }
    Surface(Modifier.fillMaxSize(), color = Hud.Bg) {
        if (!granted && !g.settings.demoMode) PermissionScreen(asked, onAllow = { launcher.launch(Permissions.required()) },
            onSettings = { context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null))) },
            onDemo = { g.settings.demoMode = true; granted = true; g.setVisible(true) })
        else AppShell(g)
    }
}

@Composable
private fun PermissionScreen(asked: Boolean, onAllow: () -> Unit, onSettings: () -> Unit, onDemo: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(32.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Text("WIRELESSSCANNER", color = Hud.Accent, fontSize = 30.sp, fontWeight = FontWeight.Bold)
        Text("Wi-Fi survey, signal saturation and connection quality", color = Hud.Text, fontSize = 18.sp, modifier = Modifier.padding(top = 8.dp), textAlign = TextAlign.Center)
        Column(Modifier.widthIn(max = 640.dp).padding(top = 24.dp)) {
            Muted("To list nearby Wi-Fi networks Android requires location access and \"Nearby devices\" permission. WirelessScanner uses them only to read Wi-Fi scan results and, if you press DETECT, to tag a test with its position. Nothing is sent anywhere unless you share a report or switch on the REST API.", size = 15)
            Muted("Notifications are used to show a running capture or test while the screen is off.", Modifier.padding(top = 8.dp), 15)
            if (asked) Text("Location access was not granted. Allow it in the app settings (Permissions → Location → Allow only while using the app, precise).", color = Hud.Warn, fontSize = 14.sp, modifier = Modifier.padding(top = 12.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 20.dp)) {
                HudButton("ALLOW ACCESS", Modifier.weight(1f), primary = true) { onAllow() }
                if (asked) HudButton("OPEN APP SETTINGS", Modifier.weight(1f)) { onSettings() }
            }
            TextButton(onDemo, Modifier.padding(top = 8.dp).heightIn(min = 48.dp)) { Text("Explore with demo data instead", color = Hud.Muted) }
        }
    }
}

@Composable
private fun AppShell(g: AppGraph) {
    var dest by rememberSaveable { mutableStateOf(Dest.Live) }
    val toast by g.toast.collectAsState()
    val snack = remember { SnackbarHostState() }
    LaunchedEffect(toast) { toast?.let { snack.showSnackbar(it); g.toast.value = null } }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val compact = maxWidth < 600.dp
        Scaffold(containerColor = Hud.Bg, snackbarHost = { SnackbarHost(snack) },
            bottomBar = { if (compact) NavigationBar(containerColor = Hud.Panel) { Dest.entries.forEach { d -> NavigationBarItem(dest == d, { dest = d }, icon = { Text(d.glyph, fontSize = 18.sp) }, label = { Text(d.label, fontSize = 10.sp) }) } } }) { pad ->
            Row(Modifier.fillMaxSize().padding(pad)) {
                if (!compact) NavigationRail(containerColor = Hud.Panel) {
                    Spacer(Modifier.height(8.dp))
                    Dest.entries.forEach { d -> NavigationRailItem(dest == d, { dest = d }, icon = { Text(d.glyph, fontSize = 20.sp, color = if (dest == d) Hud.Accent else Hud.Text) },
                        label = { Text(d.label, fontSize = 10.sp) }, modifier = Modifier.padding(vertical = 4.dp)) }
                }
                Box(Modifier.weight(1f).fillMaxHeight()) {
                    when (dest) {
                        Dest.Live -> LiveSurvey(g)
                        Dest.History -> BoxWithConstraints { HistoryScreen(g, maxWidth >= 840.dp) }
                        Dest.Settings -> SettingsScreen(g)
                    }
                }
            }
        }
    }
}

/**
 * LIVE SURVEY (UX_SPEC §3, owner decision 2026-10-19): the Windows three-pane layout only when the window is large enough
 * (≥ 1360 × 760 dp available); otherwise one main view with the network list sliding over it (back and forth in one tap,
 * selection applied live) and the test panel beside it (landscape) or below it (portrait). The layout follows the live window
 * size, so rotation and split screen re-flow without losing state.
 */
@Composable
private fun LiveSurvey(g: AppGraph) {
    val mode by g.mode.collectAsState()
    val live by g.live.collectAsState()
    g.capture.collectAsState().value; g.sat.collectAsState().value; g.conn.collectAsState().value
    g.explorerView.collectAsState().value
    var panelOpen by rememberSaveable { mutableStateOf(true) }
    var networksOpen by rememberSaveable { mutableStateOf(false) }
    var showInfo by remember { mutableStateOf(false) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val w = maxWidth.value; val h = maxHeight.value
        val threePane = w >= 1360 && h >= 760
        val landscape = w > h
        Column(Modifier.fillMaxSize()) {
            // one-row header: modes, ⓘ (what this mode does), scan freshness
            Row(Modifier.fillMaxWidth().background(Hud.Panel).padding(horizontal = 6.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                Segments(SurveyMode.entries.map { "${it.glyph} ${it.label}" }, mode.ordinal, Modifier.weight(1f)) { g.setMode(SurveyMode.entries[it]); panelOpen = true; networksOpen = false }
                Box(Modifier.size(48.dp).clickable { showInfo = true }, contentAlignment = Alignment.Center) { Text("ⓘ", color = Hud.Accent, fontSize = 20.sp) }
                val fresh = when { live.throttled -> "THROTTLED" to Hud.Warn; live.lastCycle?.name == "Fresh" -> "FRESH" to Hud.Good; live.lastCycle?.name == "Stale" -> "STALE" to Hud.Warn; live.lastCycle?.name == "Failed" -> "FAILED" to Hud.Critical; else -> "WAITING" to Hud.Dim }
                Badge(fresh.first, fresh.second)
            }
            val status = g.modeStatusText()
            Row(Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(status, color = if (status.startsWith("Monitoring")) Hud.Muted else Hud.Critical, fontSize = 13.sp, fontWeight = FontWeight.Bold)
                Text("   ${live.lastCycleText}", color = Hud.Dim, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            live.problem?.let { Banner(it, Hud.Critical) }
            if (live.throttled && !g.settings.demoMode) Banner("Android is throttling Wi-Fi scans (4 per 2 minutes): between scans results are STALE and the live view scans every ${live.scanIntervalSeconds} s. SETTINGS → WI-FI SCANNING shows how to turn throttling off.", Hud.Warn)
            if (g.settings.demoMode) Banner("DEMO DATA: example networks, not this tablet's radio. Turn demo mode off in SETTINGS → WI-FI SCANNING.", Hud.Accent2)

            if (threePane) Row(Modifier.weight(1f).fillMaxWidth()) {
                Explorer(g, Modifier.width(400.dp).fillMaxHeight())
                CenterArea(g, Modifier.weight(1f).fillMaxHeight())
                if (mode != SurveyMode.Monitor) PanelColumn(g, mode, panelOpen, { panelOpen = it }, 420)
            } else Column(Modifier.weight(1f).fillMaxWidth()) {
                // selection bar: always visible; one tap opens the network list
                Row(Modifier.fillMaxWidth().background(Hud.Panel2).clickable { networksOpen = true }.heightIn(min = 56.dp).padding(horizontal = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(if (g.selection.isEmpty) "Nothing selected — every network is shown" else "Selected: " + g.selection.describe { s -> live.aps.count { it.ssid == s } },
                            color = Hud.Text, fontSize = 14.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        if (g.selection.isLocked) Text("Locked while a test runs", color = Hud.Warn, fontSize = 12.sp)
                    }
                    HudButton("◂ NETWORKS", primary = true) { networksOpen = true }
                }
                Box(Modifier.weight(1f).fillMaxWidth()) {
                    if (landscape) Row(Modifier.fillMaxSize()) {
                        CenterArea(g, Modifier.weight(1f).fillMaxHeight())
                        if (mode != SurveyMode.Monitor) PanelColumn(g, mode, panelOpen, { panelOpen = it }, if (w >= 1100) 420 else 360)
                    } else Column(Modifier.fillMaxSize()) {
                        CenterArea(g, Modifier.weight(1f).fillMaxWidth())
                        if (mode != SurveyMode.Monitor) {
                            TextButton({ panelOpen = !panelOpen }, Modifier.fillMaxWidth().heightIn(min = 48.dp).background(Hud.Panel)) {
                                Text(if (panelOpen) "▾ HIDE ${mode.label} PANEL" else "▴ SHOW ${mode.label} PANEL", color = Hud.Accent, fontWeight = FontWeight.Bold)
                            }
                            if (panelOpen) ModePanel(g, Modifier.fillMaxWidth().fillMaxHeight(0.55f))
                        }
                    }
                    // The network list slides in from the right, under the right-aligned NETWORKS button. Selections keep it open
                    // (multi-select); it slides closed only on CLOSE, a tap outside it, or Back.
                    // The view stays visible to its left and reacts live.
                    androidx.compose.animation.AnimatedVisibility(networksOpen, Modifier.fillMaxSize(), enter = fadeIn(tween(220)), exit = fadeOut(tween(220))) {
                        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.35f))
                            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) { networksOpen = false })
                    }
                    androidx.compose.animation.AnimatedVisibility(networksOpen, Modifier.align(Alignment.CenterEnd).fillMaxHeight().fillMaxWidth(if (landscape) 0.58f else 0.9f),
                        enter = slideInHorizontally(tween(260)) { it }, exit = slideOutHorizontally(tween(220)) { it }) {
                        Explorer(g, Modifier.fillMaxSize().border(1.dp, Hud.BorderStrong), onClose = { networksOpen = false })
                    }
                }
            }
        }
    }
    if (showInfo) InfoDialog(mode.label, mode.description + "\n\n" + live.lastCycleText) { showInfo = false }
    BackHandler(enabled = networksOpen) { networksOpen = false }
}

@Composable
private fun PanelColumn(g: AppGraph, mode: SurveyMode, open: Boolean, setOpen: (Boolean) -> Unit, widthDp: Int) {
    if (open) Column(Modifier.width(widthDp.dp).fillMaxHeight().border(1.dp, Hud.Border)) {
        TextButton({ setOpen(false) }, Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("HIDE PANEL ▸", color = Hud.Accent, fontWeight = FontWeight.Bold) }
        ModePanel(g, Modifier.weight(1f))
    } else Box(Modifier.width(56.dp).fillMaxHeight().background(Hud.Panel).clickable { setOpen(true) }, contentAlignment = Alignment.Center) {
        Text("◂\n${mode.label.split(' ').first()}", color = Hud.Accent, fontSize = 14.sp, textAlign = TextAlign.Center)
    }
}

@Composable
private fun Banner(text: String, color: Color) =
    Text(text, color = color, fontSize = 13.sp, modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 3.dp).border(1.dp, color, RoundedCornerShape(4.dp)).padding(8.dp))
