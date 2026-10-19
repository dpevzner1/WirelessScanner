package com.wirelessscanner.android.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.AppGraph
import com.wirelessscanner.android.CenterView
import com.wirelessscanner.android.Step
import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.report.ReportText
import com.wirelessscanner.core.selection.ExplorerModel
import com.wirelessscanner.core.spectrum.SpectrumLayout
import kotlin.math.abs

@Composable
fun CenterArea(g: AppGraph, modifier: Modifier = Modifier) {
    val center by g.center.collectAsState()
    val cap by g.capture.collectAsState()
    Column(modifier.background(Hud.Bg).padding(8.dp)) {
        Segments(CenterView.entries.map { it.label }, center.ordinal) { g.center.value = CenterView.entries[it] }
        Box(Modifier.weight(1f).fillMaxWidth().padding(top = 6.dp)) {
            when (center) {
                CenterView.Spectrum -> SpectrumView(g)
                CenterView.Waterfall -> WaterfallView(g)
                CenterView.Grid -> GridView(g)
                CenterView.Saturation -> SaturationStage(g)
                CenterView.Connection -> ConnectionStage(g)
            }
        }
        if (cap.active && center in listOf(CenterView.Spectrum, CenterView.Waterfall, CenterView.Grid)) CaptureLog(g, Modifier.heightIn(max = 220.dp))
    }
}

// ------------------------------------------------------------------ spectrum
private val bandsAll = listOf("All", "2.4 GHz", "5 GHz", "6 GHz")

@Composable
fun SpectrumView(g: AppGraph) {
    val live by g.live.collectAsState()
    g.explorerView.collectAsState().value
    var band by rememberSaveable { mutableStateOf(0) }
    val heard6 = live.aps.any { it.band == "6 GHz" }
    val axes = when (band) { 1 -> listOf(SpectrumLayout.BAND_24); 2 -> listOf(SpectrumLayout.BAND_5); 3 -> listOf(SpectrumLayout.BAND_6)
        else -> listOfNotNull(SpectrumLayout.BAND_24, SpectrumLayout.BAND_5, if (heard6) SpectrumLayout.BAND_6 else null) }
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Segments(bandsAll, band) { band = it }
            Muted("  Tap a block to select its access point.", size = 12)
        }
        val visible = live.aps.filter { g.selection.matches(it.ssid, it.bssid) || g.selection.isEmpty }
        Column(Modifier.weight(1f)) {
            axes.forEach { axis -> BandPanel(g, axis, live.aps, visible, Modifier.weight(1f).fillMaxWidth().padding(top = 6.dp)) }
        }
    }
}

@Composable
private fun BandPanel(g: AppGraph, axis: SpectrumLayout.BandAxis, all: List<AccessPoint>, aps: List<AccessPoint>, modifier: Modifier) {
    val inBand = aps.filter { it.band == axis.band && it.channel > 0 }
    val load = SpectrumLayout.channelLoad(axis, all)
    val best = SpectrumLayout.leastBusyChannel(axis, load)
    val textPaint = remember { android.graphics.Paint().apply { color = android.graphics.Color.rgb(0xF3, 0xD9, 0xB8); textSize = 26f; isAntiAlias = true } }
    val mutedPaint = remember { android.graphics.Paint().apply { color = android.graphics.Color.rgb(0xC9, 0xA2, 0x7A); textSize = 22f; isAntiAlias = true } }
    Column(modifier.border(1.dp, Hud.Border, RoundedCornerShape(6.dp)).padding(6.dp)) {
        Row { Text(axis.band, color = Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 13.sp); Muted("   ${inBand.size} APs · least busy channel $best", size = 12) }
        Canvas(Modifier.fillMaxSize().semantics { contentDescription = "${axis.band} spectrum: ${inBand.size} access points; least busy channel $best" }
            .pointerInput(inBand, axis) {
                detectTapGestures { pos ->
                    val left = 60f; val w = size.width - left - 10f
                    val mhz = axis.lowMhz + (pos.x - left) / w * (axis.highMhz - axis.lowMhz)
                    val hit = inBand.filter { val (lo, hi) = SpectrumLayout.occupancy(it); mhz in (lo - 4)..(hi + 4) }.maxByOrNull { it.rssi }
                    hit?.let { g.explorer.toggleAp(it.ssid, it.bssid) }
                }
            }) {
            val left = 60f; val top = 6f; val bottom = 54f
            val w = size.width - left - 10f; val h = size.height - top - bottom
            fun x(mhz: Double) = left + ((mhz - axis.lowMhz) / (axis.highMhz - axis.lowMhz)).toFloat() * w
            fun y(dbm: Double) = top + ((-20 - dbm) / 80.0).toFloat() * h
            if (axis.dfsLowMhz != null) drawRect(Color(0x22D03B3B), Offset(x(axis.dfsLowMhz!!), top), Size(x(axis.dfsHighMhz!!) - x(axis.dfsLowMhz!!), h))
            // tier thresholds as lines; axis labels only every 20 dB so they never collide
            for (db in listOf(-67, -70, -80)) drawLine(Color(0x55FF8800), Offset(left, y(db.toDouble())), Offset(left + w, y(db.toDouble())))
            for (db in listOf(-30, -50, -70, -90)) {
                drawLine(Color(0x334A2A12), Offset(left, y(db.toDouble())), Offset(left + w, y(db.toDouble())))
                drawContext.canvas.nativeCanvas.drawText("$db", 4f, y(db.toDouble()) + 8, mutedPaint)
            }
            axis.tickChannels.forEach { ch -> val px = x(SpectrumLayout.channelFrequencyMhz(axis.band, ch)); drawLine(Hud.Border, Offset(px, top + h), Offset(px, top + h + 6)); drawContext.canvas.nativeCanvas.drawText("$ch", px - 10, top + h + 28, mutedPaint) }
            // load strip
            val maxLoad = maxOf(1.0, load.values.maxOrNull() ?: 1.0)
            axis.channels.forEach { ch -> val f = SpectrumLayout.channelFrequencyMhz(axis.band, ch); val l = load[ch] ?: 0.0
                drawRect(Hud.Accent.copy(alpha = (0.15 + 0.85 * l / maxLoad).toFloat().coerceIn(0f, 1f) * if (l > 0) 1f else 0.1f), Offset(x(f - 8), top + h + 34), Size(x(f + 8) - x(f - 8), 12f)) }
            // blocks: context first, focused on top
            val sorted = inBand.sortedBy { g.explorer.identityColorFor(it.ssid) != null }
            val placed = ArrayList<android.graphics.RectF>()
            for (ap in sorted) {
                val (lo, hi) = SpectrumLayout.occupancy(ap)
                val col = g.explorer.identityColorFor(ap.ssid)?.let { argb(it) } ?: Color(0xFF7A6A55)
                val selected = g.selection.isExplicitlySelected(ap.ssid, ap.bssid)
                val p = Path().apply { moveTo(x(lo), top + h); lineTo(x(lo + (hi - lo) * 0.12), y(ap.rssi.toDouble())); lineTo(x(hi - (hi - lo) * 0.12), y(ap.rssi.toDouble())); lineTo(x(hi), top + h); close() }
                drawPath(p, col.copy(alpha = if (selected) 0.35f else 0.15f))
                drawPath(p, col, style = Stroke(width = if (selected) 4f else 2f))
                if (g.explorer.identityColorFor(ap.ssid) != null || selected) {
                    val label = "${ap.ssid.take(16)} ${ap.rssi}"
                    val tw = textPaint.measureText(label)
                    val lx = (x((lo + hi) / 2) - tw / 2).coerceIn(left, left + w - tw)
                    var ly = y(ap.rssi.toDouble()) - 6
                    var r = android.graphics.RectF(lx, ly - 24, lx + tw, ly + 4)
                    var tries = 0
                    while (placed.any { android.graphics.RectF.intersects(it, r) } && tries < 4) { ly -= 28; r = android.graphics.RectF(lx, ly - 24, lx + tw, ly + 4); tries++ }
                    if (ly - 24 >= top && placed.none { android.graphics.RectF.intersects(it, r) }) { placed.add(r); drawContext.canvas.nativeCanvas.drawText(label, lx, ly, textPaint) }
                }
            }
        }
    }
}

// ------------------------------------------------------------------ waterfall
@Composable
fun WaterfallView(g: AppGraph) {
    val live by g.live.collectAsState()
    var band by rememberSaveable { mutableStateOf(1) }
    val axis = listOf(SpectrumLayout.BAND_24, SpectrumLayout.BAND_5, SpectrumLayout.BAND_6)[band.coerceIn(0, 2)]
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) { Segments(listOf("2.4 GHz", "5 GHz", "6 GHz"), band) { band = it }; Muted("   Newest scan at the top · last ${live.waterfall.size} fresh scans (max 60)", size = 12) }
        val rows = live.waterfall.map { scan -> SpectrumLayout.strongestPerChannel(axis, scan.filter { g.selection.matches(it.ssid, it.bssid) || g.selection.isEmpty }) }
        if (rows.isEmpty()) Muted("The waterfall fills with each fresh scan.", Modifier.padding(12.dp))
        Canvas(Modifier.fillMaxSize().padding(top = 6.dp).semantics { contentDescription = "${axis.band} waterfall with ${rows.size} scans" }) {
            if (rows.isEmpty()) return@Canvas
            val cw = size.width / axis.channels.size; val rh = size.height / 60f
            rows.forEachIndexed { r, row ->
                axis.channels.forEachIndexed { i, ch -> row[ch]?.let { rssi -> val t = SpectrumLayout.intensity(rssi).toFloat()
                    drawRect(Color(1f, 0.25f + 0.35f * t, 0f, 0.15f + 0.85f * t), Offset(i * cw, r * rh), Size(cw - 1, rh)) } }
            }
        }
    }
}

// ------------------------------------------------------------------ grid
@Composable
fun GridView(g: AppGraph) {
    val live by g.live.collectAsState()
    g.explorerView.collectAsState().value
    val aps = live.aps.filter { g.selection.matches(it.ssid, it.bssid) }.sortedByDescending { it.rssi }
    val heads = listOf("SSID", "BSSID", "BAND", "CH", "WIDTH", "RSSI", "SNR EST.", "QUALITY", "SECURITY", "VENDOR", "STANDARD", "STATIONS", "BUSY %")
    val widths = listOf(170, 150, 70, 50, 70, 70, 80, 75, 150, 140, 90, 80, 70)
    Column(Modifier.fillMaxSize().horizontalScroll(rememberScrollState())) {
        Row(Modifier.background(Hud.Panel2)) { heads.forEachIndexed { i, h -> Text(h, color = Hud.Accent, fontSize = 12.sp, fontWeight = FontWeight.Bold, modifier = Modifier.width(widths[i].dp).padding(8.dp)) } }
        LazyColumn {
            items(aps, key = { it.bssid }) { a ->
                val (tier, col) = ExplorerModel.tier(a.rssi)
                Row(Modifier.heightIn(min = 44.dp).border(0.5.dp, Hud.Border), verticalAlignment = Alignment.CenterVertically) {
                    listOf(a.ssid, a.bssid, a.band, "${a.channel}", a.channelWidth?.let { "$it MHz" } ?: "?", "${a.rssi} $tier", "~${a.snr} dB", "${a.quality}%", a.securityType,
                        a.vendorOui ?: "", a.phyType ?: "", a.stationCount?.toString() ?: "", a.channelUtilizationPct?.let { ReportText.fmt(it, "0") } ?: "").forEachIndexed { i, c ->
                        Text(c, color = if (i == 5) argb(col) else Hud.Text, fontSize = 13.sp, modifier = Modifier.width(widths[i].dp).padding(horizontal = 8.dp))
                    }
                }
            }
        }
    }
}

@Composable
fun CaptureLog(g: AppGraph, modifier: Modifier = Modifier) {
    val cap by g.capture.collectAsState()
    Column(modifier.fillMaxWidth().padding(top = 6.dp).border(1.dp, Hud.Border, RoundedCornerShape(6.dp)).padding(6.dp)) {
        Text("${if (cap.paused) "⏸ PAUSED" else "● RECORDING"} — ${cap.session?.name} · ${cap.session?.facilityName} · zone ${cap.surveyPoint} · ${cap.modeText}", color = if (cap.paused) Hud.Warn else Hud.Critical, fontSize = 13.sp, fontWeight = FontWeight.Bold)
        Column(Modifier.horizontalScroll(rememberScrollState())) {
            Row { listOf("TIME" to 90, "SSID" to 150, "BSSID" to 150, "CH" to 50, "BAND" to 70, "RSSI" to 60, "CHANGE" to 90, "SNR EST." to 70, "QUALITY" to 70, "VAR dB" to 60).forEach { (h, w) -> Text(h, color = Hud.Accent, fontSize = 11.sp, modifier = Modifier.width(w.dp)) } }
            LazyColumn {
                items(cap.logs) { r ->
                    Row {
                        val s = r.sample
                        listOf(ReportText.local(s.timestamp, g.zone, "HH:mm:ss") to 90, s.ssid to 150, s.bssid to 150, "${s.channel}" to 50, s.band to 70, "${s.rssi}" to 60).forEach { (t, w) -> Text(t, color = Hud.Text, fontSize = 12.sp, modifier = Modifier.width(w.dp)) }
                        Text(r.fluctuation, color = argb(r.color), fontSize = 12.sp, modifier = Modifier.width(90.dp))
                        listOf("~${s.snr}" to 70, "${s.quality}%" to 70, ReportText.fmt(s.jitter, "0") to 60).forEach { (t, w) -> Text(t, color = Hud.Text, fontSize = 12.sp, modifier = Modifier.width(w.dp)) }
                    }
                }
            }
        }
    }
}

// ------------------------------------------------------------------ saturation stage
@Composable
fun SaturationStage(g: AppGraph) {
    val s by g.sat.collectAsState()
    var focus by rememberSaveable { mutableStateOf<String?>(null) }
    val report = if (s.step == Step.Results) s.report else s.live
    if (report == null) { Muted(if (s.step == Step.Setup) "Set up the saturation test in the panel. Live tiles, the timeline and events appear here while it runs." else "Waiting for the first scan…", Modifier.padding(12.dp)); return }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        val tiles = if (s.step == Step.Results) com.wirelessscanner.core.forms.saturationTiles(report, report.cycles.lastOrNull()) else s.tiles
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            tiles.forEach { t ->
                Column(Modifier.widthIn(min = 260.dp, max = 360.dp).border(if (focus == t.bssid) 2.dp else 1.dp, if (focus == t.bssid) Hud.Accent else Hud.Border, RoundedCornerShape(6.dp))
                    .background(Hud.Panel).clickableNoRipple { focus = t.bssid }.padding(8.dp)) {
                    Text(t.title, color = Hud.Text, fontSize = 12.sp)
                    Row(verticalAlignment = Alignment.CenterVertically) { Text(t.current, color = argb(t.color), fontSize = 22.sp, fontWeight = FontWeight.Bold); Badge(t.label, argb(t.color), Modifier.padding(start = 8.dp)) }
                    Muted(t.stats, size = 11); Muted(t.events, size = 11)
                }
            }
        }
        val target = report.targets.firstOrNull { it.bssid == focus } ?: report.targets.firstOrNull()
        if (target != null) {
            SectionTitle("RSSI TIMELINE — ${target.ssid.ifEmpty { "<hidden>" }} ${target.bssid}")
            val spec = com.wirelessscanner.core.report.SaturationReports(g.reports).timeline(report, target.bssid)
            ChartCanvas(spec, Modifier.fillMaxWidth().height(220.dp))
        }
        SectionTitle("EVENTS")
        val feed = if (s.step == Step.Results) report.events.map { e -> "${ReportText.local(e.startUtc, g.zone, "HH:mm:ss")}  ${e.type.name}  ${e.subject}  ${e.detail}" } else s.feed
        if (feed.isEmpty()) Muted("No events yet.") else feed.take(200).forEach { Mono(it) }
    }
}

// ------------------------------------------------------------------ connection stage
@Composable
fun ConnectionStage(g: AppGraph) {
    val s by g.conn.collectAsState()
    val report = if (s.step == Step.Results) s.report else s.live
    if (s.step == Step.Setup) { Muted("Set up the connection test in the panel. The path tiles, round-trip timelines and events appear here while it runs.", Modifier.padding(12.dp)); return }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        val tiles = if (s.step == Step.Results && report != null) s.tiles.map { t -> if (t.segment == ConnectionSegment.WifiLink) com.wirelessscanner.core.forms.linkTile(report.linkSamples.lastOrNull(), report.config.probeWifiLink) else com.wirelessscanner.core.forms.segmentTile(t.segment, report, report.config) } else s.tiles
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            tiles.forEachIndexed { i, t ->
                if (i > 0) Text("→", color = Hud.Muted, fontSize = 20.sp, modifier = Modifier.align(Alignment.CenterVertically))
                Column(Modifier.width(170.dp).border(1.dp, argb(t.color), RoundedCornerShape(6.dp)).background(Hud.Panel).padding(8.dp)
                    .semantics { contentDescription = "${t.title}: ${t.status}, ${t.line1}, ${t.line2}" }) {
                    Text(t.title, color = Hud.Text, fontSize = 12.sp, fontWeight = FontWeight.Bold)
                    Badge(t.status, argb(t.color)); Text(t.line1, color = Hud.Text, fontSize = 16.sp); Muted(t.line2, size = 11)
                }
            }
        }
        if (s.activity.isNotEmpty()) Text(s.activity, color = Hud.Accent2, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
        if (report != null) {
            for (seg in listOf(ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns)) {
                val pm = ConnectionAnalyzer.primaryMethod(seg, report.probes)
                val pts = report.probes.filter { it.segment == seg && it.method == pm }
                if (pts.isEmpty()) continue
                SectionTitle("${ConnectionAnalyzer.name(seg).uppercase()} — round trip (ms), red ticks = failed")
                ChartCanvas(com.wirelessscanner.core.report.ConnectionReports(g.reports).probeChart(report, pts), Modifier.fillMaxWidth().height(110.dp))
            }
            if (report.linkSamples.any { it.rssi != null }) { SectionTitle("WI-FI SIGNAL (dBm) — vertical lines = roams"); ChartCanvas(com.wirelessscanner.core.report.ConnectionReports(g.reports).signalChart(report), Modifier.fillMaxWidth().height(100.dp)) }
        }
        SectionTitle("EVENTS")
        val feed = if (s.step == Step.Results && report != null) report.events.map { e -> "${ReportText.local(e.startUtc, g.zone, "HH:mm:ss")}  ${e.type.name}  ${e.attributedTo?.let { "[" + ConnectionAnalyzer.name(it) + "] " } ?: ""}${e.detail}" } else s.feed
        if (feed.isEmpty()) Muted("No outages, spikes, roams or gaps yet.") else feed.take(200).forEach { Mono(it) }
    }
}

/** Draws a core ChartSpec on screen using the PDF chart code on a dark-friendly background. */
@Composable
fun ChartCanvas(spec: com.wirelessscanner.core.report.ChartSpec, modifier: Modifier) {
    Canvas(modifier.background(Color(0xFFF7F3EE), RoundedCornerShape(4.dp))) {
        val c = drawContext.canvas.nativeCanvas
        val scale = density * 0.9f
        c.save(); c.scale(scale, scale)
        com.wirelessscanner.android.platform.Charts.draw(c, spec, size.width / scale, size.height / scale)
        c.restore()
    }
}

@Composable
private fun Modifier.clickableNoRipple(onClick: () -> Unit): Modifier = this.then(Modifier.pointerInput(Unit) { detectTapGestures { onClick() } })
