package com.wirelessscanner.android.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.state.ToggleableState
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelessscanner.android.AppGraph
import com.wirelessscanner.core.selection.*

/** Network explorer (Windows NetworkExplorerView): adapter card, search, chips, sort, GROUP BY, tri-state tree, footer actions. */
@Composable
fun Explorer(g: AppGraph, modifier: Modifier = Modifier, onClose: (() -> Unit)? = null) {
    val view by g.explorerView.collectAsState()
    val f by g.filters.collectAsState()
    val live by g.live.collectAsState()
    val expanded = remember { mutableStateMapOf<String, Boolean>() }
    var info by remember { mutableStateOf<Pair<String, String>?>(null) }
    var unitMenu by remember { mutableStateOf<UnitRow?>(null) }
    var apMenu by remember { mutableStateOf<ApRow?>(null) }
    var rename by remember { mutableStateOf<UnitRow?>(null) }
    var showFilters by remember { mutableStateOf(false) }
    // Slide-over only: keep the row order fixed while the list is open, so a rescan never moves a row under the finger between
    // ticks. New rows join at the end; a change of search, filters, sort or grouping re-sorts. The list re-sorts when reopened.
    val stable = onClose != null
    val order = remember(f) { mutableListOf<String>() }
    fun <T> pin(rows: List<T>, key: (T) -> String): List<T> {
        if (!stable) return rows
        val keys = rows.map(key)
        order.retainAll(keys.toSet()); keys.forEach { if (it !in order) order.add(it) }
        val at = order.withIndex().associate { it.value to it.index }
        return rows.sortedBy { at[key(it)] ?: Int.MAX_VALUE }
    }
    val groups = pin(view.groups) { it.ssid }
    val units = pin(view.units) { it.id }

    Column(modifier.background(Hud.Bg).padding(8.dp)) {
        val c = live.connected
        val active = listOf(f.band24, f.band5, f.band6, f.fairOrBetter, f.secured, f.selectedOnly).count { it } + (if (f.sort != ExplorerSort.Signal) 1 else 0)
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("NETWORK EXPLORER", color = Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 14.sp, letterSpacing = 1.sp, modifier = Modifier.weight(1f))
            if (g.settings.demoMode) Badge("DEMO DATA", Hud.Accent2, Modifier.padding(end = 6.dp))
            if (onClose != null) HudButton("CLOSE ▸") { onClose() }
        }
        Text(if (c?.connected == true) "This tablet: CONNECTED to ${c.ssid ?: "<hidden>"} · ${c.band ?: ""} ch ${c.channel ?: "?"}${c.rxRateMbps?.let { " · ${it.toInt()} Mbps" } ?: ""}" else "This tablet: not connected (scanning still works)",
            color = Hud.Muted, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Field("Search SSID, BSSID or vendor", f.search, Modifier.weight(1f)) { g.setFilters(f.copy(search = it)) }
            HudButton(if (active > 0) "FILTERS ($active)" else "FILTERS", primary = active > 0) { showFilters = true }
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("GROUP BY", color = Hud.Muted, fontSize = 11.sp)
            Segments(listOf("Network", "Access point"), f.grouping.ordinal) { g.setFilters(f.copy(grouping = ExplorerGrouping.entries[it])) }
        }
        if (active > 0) Text("Filtered: " + listOfNotNull(if (f.band24) "2.4 GHz" else null, if (f.band5) "5 GHz" else null, if (f.band6) "6 GHz" else null,
            if (f.fairOrBetter) "≥ −70 dBm" else null, if (f.secured) "secured" else null, if (f.selectedOnly) "selected only" else null,
            if (f.sort != ExplorerSort.Signal) "sorted by ${f.sort.name.lowercase()}" else null).joinToString(" · "), color = Hud.Accent2, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)

        if (view.emptyText.isNotEmpty()) Muted(view.emptyText, Modifier.padding(8.dp))
        LazyColumn(Modifier.weight(1f)) {
            if (f.grouping == ExplorerGrouping.Network) items(groups, key = { "g:" + it.ssid }) { grp ->
                val open = expanded[grp.ssid] ?: false
                GroupRowView(grp, open, onExpand = { expanded[grp.ssid] = !open }, onToggle = { if (g.explorer.toggleGroup(grp.ssid)) expanded[grp.ssid] = true }, onInfo = { info = grp.displayName to grp.checkHint })
                if (open) grp.aps.forEach { ap -> ApRowView(ap, onToggle = { g.explorer.toggleAp(ap.ssid, ap.bssid) }, onInfo = { info = ap.displaySsid to ap.details }, onMenu = null) }
            } else items(units, key = { "u:" + it.id }) { u ->
                val open = expanded["u:" + u.id] ?: false
                UnitRowView(u, open, onExpand = { expanded["u:" + u.id] = !open }, onToggle = { if (g.explorer.toggleUnit(u.id)) expanded["u:" + u.id] = true },
                    onInfo = { info = u.name to u.evidence }, onMenu = { unitMenu = u })
                if (open) u.aps.forEach { ap -> ApRowView(ap, onToggle = { g.explorer.toggleAp(ap.ssid, ap.bssid) }, onInfo = { info = ap.displaySsid to ap.details }, onMenu = { apMenu = ap }) }
            }
        }
        Muted(view.countText, size = 12)
        Text(view.scopeText, color = Hud.Muted, fontSize = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
            HudButton("FORCE SCAN", Modifier.weight(1f)) { g.scanNow() }
            HudButton("SELECT SHOWN", Modifier.weight(1.1f), enabled = !view.locked) { g.explorer.selectShown(view) }
            HudButton("CLEAR", Modifier.weight(0.8f), enabled = !view.locked) { g.selection.clear() }
        }
    }

    if (showFilters) AlertDialog({ showFilters = false }, containerColor = Hud.Panel, title = { Text("Filters and sort") },
        confirmButton = { TextButton({ showFilters = false }, Modifier.heightIn(min = 48.dp)) { Text("DONE", color = Hud.Accent) } },
        dismissButton = { TextButton({ g.setFilters(f.copy(band24 = false, band5 = false, band6 = false, fairOrBetter = false, secured = false, selectedOnly = false, sort = ExplorerSort.Signal)) }, Modifier.heightIn(min = 48.dp)) { Text("CLEAR FILTERS") } },
        text = {
            val ff by g.filters.collectAsState()
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Muted("Band", size = 12)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    HudChip("2.4 GHz", ff.band24) { g.setFilters(ff.copy(band24 = !ff.band24)) }
                    HudChip("5 GHz", ff.band5) { g.setFilters(ff.copy(band5 = !ff.band5)) }
                    HudChip("6 GHz", ff.band6) { g.setFilters(ff.copy(band6 = !ff.band6)) }
                }
                Muted("Show only", Modifier.padding(top = 8.dp), 12)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    HudChip("≥ −70 dBm", ff.fairOrBetter) { g.setFilters(ff.copy(fairOrBetter = !ff.fairOrBetter)) }
                    HudChip("Secured", ff.secured) { g.setFilters(ff.copy(secured = !ff.secured)) }
                    HudChip("Selected", ff.selectedOnly) { g.setFilters(ff.copy(selectedOnly = !ff.selectedOnly)) }
                }
                Muted("Sort by", Modifier.padding(top = 8.dp), 12)
                Segments(listOf("Signal", "Name", "Channel"), ff.sort.ordinal) { g.setFilters(ff.copy(sort = ExplorerSort.entries[it])) }
            }
        })
    info?.let { (t, b) -> InfoDialog(t, b) { info = null } }
    unitMenu?.let { u ->
        AlertDialog({ unitMenu = null }, confirmButton = { TextButton({ unitMenu = null }) { Text("Close") } }, containerColor = Hud.Panel, title = { Text(u.name) }, text = {
            Column {
                Muted(u.evidence)
                HudButton("RENAME", Modifier.fillMaxWidth().padding(top = 8.dp), enabled = g.explorer.canEditUnits) { rename = u; unitMenu = null }
                HudButton("RESET TO AUTOMATIC GROUPING", Modifier.fillMaxWidth().padding(top = 6.dp), enabled = g.explorer.canEditUnits) { g.explorer.resetUnit(u.id); unitMenu = null }
                Muted("To split or move a single BSSID, open the access point and use ⋮ on that row.", Modifier.padding(top = 6.dp), 12)
            }
        })
    }
    apMenu?.let { ap ->
        AlertDialog({ apMenu = null }, confirmButton = { TextButton({ apMenu = null }) { Text("Close") } }, containerColor = Hud.Panel, title = { Text("${ap.displaySsid} · ${ap.bssid}") }, text = {
            Column(Modifier.heightIn(max = 420.dp).verticalScrollable()) {
                HudButton("THIS IS A SEPARATE DEVICE (SPLIT)", Modifier.fillMaxWidth(), enabled = g.explorer.canEditUnits) { g.explorer.splitAp(ap.bssid); apMenu = null }
                Muted("Belongs to another access point:", Modifier.padding(top = 8.dp), 12)
                g.explorer.moveTargets(ap.bssid).forEach { (id, name) -> HudButton("MOVE TO $name", Modifier.fillMaxWidth().padding(top = 4.dp), enabled = g.explorer.canEditUnits) { g.explorer.moveAp(ap.bssid, id); apMenu = null } }
            }
        })
    }
    rename?.let { u ->
        var name by remember(u.id) { mutableStateOf(u.name) }
        AlertDialog({ rename = null }, containerColor = Hud.Panel, title = { Text("Rename access point") }, text = { Field("Name", name) { name = it } },
            confirmButton = { TextButton({ g.explorer.renameUnit(u.id, name); rename = null }) { Text("Save", color = Hud.Accent) } }, dismissButton = { TextButton({ rename = null }) { Text("Cancel") } })
    }
}

@Composable
private fun Modifier.verticalScrollable(): Modifier = this.then(Modifier.verticalScroll(rememberScrollState()))

@Composable
private fun TriCheck(check: Boolean?, enabled: Boolean, label: String, onToggle: () -> Unit) {
    TriStateCheckbox(state = when (check) { true -> ToggleableState.On; null -> ToggleableState.Indeterminate; else -> ToggleableState.Off }, onClick = onToggle, enabled = enabled,
        modifier = Modifier.size(48.dp).semantics { contentDescription = label },
        colors = CheckboxDefaults.colors(checkedColor = Hud.Accent, checkmarkColor = Color.Black, uncheckedColor = Hud.Muted, disabledUncheckedColor = Hud.Dim, disabledCheckedColor = Hud.BorderStrong))
}

@Composable
private fun Bars(n: Int, color: Color) = Text("▂▄▆█".take(n), color = color, fontSize = 13.sp).also { }

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GroupRowView(r: GroupRow, open: Boolean, onExpand: () -> Unit, onToggle: () -> Unit, onInfo: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).combinedClickable(onClick = onExpand, onLongClick = onInfo).padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(48.dp).clickable(role = Role.Button) { onExpand() }.semantics { contentDescription = if (open) "Collapse" else "Expand" }, contentAlignment = Alignment.Center) { Text(if (open) "▾" else "▸", color = Hud.Accent, fontSize = 18.sp) }
        TriCheck(r.check, r.canToggle, "${r.displayName}, ${r.apCount} access points, " + when (r.check) { true -> if (r.whole) "whole network selected" else "all selected"; null -> "partly selected"; else -> "not selected" }, onToggle)
        r.swatch?.let { Box(Modifier.size(width = 6.dp, height = 28.dp).background(argb(it), RoundedCornerShape(2.dp))) }
        Column(Modifier.weight(1f).padding(start = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(r.displayName, color = if (r.hidden) Hud.Muted else Hud.Text, fontWeight = FontWeight.Bold, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, false))
                if (r.connected) Badge("CONNECTED", Hud.Good, Modifier.padding(start = 6.dp))
            }
            Text(r.subText, color = Hud.Muted, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Column(horizontalAlignment = Alignment.End) { Row(verticalAlignment = Alignment.CenterVertically) { Bars(r.bars, argb(r.tierColor)); Text(" ${r.bestRssi} dBm", color = Hud.Text, fontSize = 12.sp) }; Badge(r.tier, argb(r.tierColor)) }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun UnitRowView(u: UnitRow, open: Boolean, onExpand: () -> Unit, onToggle: () -> Unit, onInfo: () -> Unit, onMenu: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).combinedClickable(onClick = onExpand, onLongClick = onMenu).padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(48.dp).clickable { onExpand() }, contentAlignment = Alignment.Center) { Text(if (open) "▾" else "▸", color = Hud.Accent, fontSize = 18.sp) }
        TriCheck(u.check, u.canToggle, "${u.name}, " + when (u.check) { true -> "whole access point selected"; null -> "partly selected"; else -> "not selected" }, onToggle)
        Column(Modifier.weight(1f).padding(start = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(u.name, color = Hud.Text, fontWeight = FontWeight.Bold, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, false))
                Badge(u.confidenceText, argb(u.confidenceColor), Modifier.padding(start = 6.dp))
                if (u.connected) Badge("CONNECTED", Hud.Good, Modifier.padding(start = 6.dp))
            }
            Muted(u.subText, size = 12)
            if (u.meshText.isNotEmpty()) Text(u.meshText, color = Hud.Accent2, fontSize = 12.sp)
        }
        Column(horizontalAlignment = Alignment.End) { Bars(u.bars, argb(u.tierColor)); Badge(u.tier, argb(u.tierColor)) }
        Box(Modifier.size(48.dp).clickable { onInfo() }.semantics { contentDescription = "Evidence" }, contentAlignment = Alignment.Center) { Text("ⓘ", color = Hud.Accent, fontSize = 18.sp) }
        Box(Modifier.size(48.dp).clickable { onMenu() }.semantics { contentDescription = "Rename or reset" }, contentAlignment = Alignment.Center) { Text("⋮", color = Hud.Accent, fontSize = 20.sp) }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ApRowView(a: ApRow, onToggle: () -> Unit, onInfo: () -> Unit, onMenu: (() -> Unit)?) {
    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).padding(start = 40.dp).combinedClickable(enabled = a.canToggle, onClick = onToggle, onLongClick = onMenu ?: onInfo)
        .semantics { contentDescription = "${a.displaySsid} ${a.bssid}, ${a.detailText}, ${a.rssi} dBm ${a.tier}, " + if (a.selected) "selected" else "not selected" },
        verticalAlignment = Alignment.CenterVertically) {
        Checkbox(a.selected, { onToggle() }, enabled = a.canToggle, modifier = Modifier.size(48.dp), colors = CheckboxDefaults.colors(checkedColor = Hud.Accent, checkmarkColor = Color.Black, uncheckedColor = Hud.Muted))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(a.bssid, color = Hud.Text, fontSize = 13.sp)
                if (a.connected) Badge("CONNECTED", Hud.Good, Modifier.padding(start = 6.dp))
            }
            Muted("${a.displaySsid} · ${a.detailText}", size = 12)
        }
        Spark(a.spark, a.identityColor?.let { argb(it) } ?: Hud.Muted)
        Column(horizontalAlignment = Alignment.End, modifier = Modifier.padding(start = 6.dp)) { Text("${a.rssi} dBm", color = argb(a.tierColor), fontSize = 13.sp); Muted(a.tier, size = 10) }
        Box(Modifier.size(48.dp).clickable { onInfo() }.semantics { contentDescription = "Details" }, contentAlignment = Alignment.Center) { Text("ⓘ", color = Hud.Accent, fontSize = 16.sp) }
        if (onMenu != null) Box(Modifier.size(48.dp).clickable { onMenu() }.semantics { contentDescription = "Split or move" }, contentAlignment = Alignment.Center) { Text("⋮", color = Hud.Accent, fontSize = 20.sp) }
    }
}

/** 12-scan sparkline (Windows NetworkApItem.Spark). */
@Composable
fun Spark(values: List<Int>, color: Color) {
    Canvas(Modifier.size(width = 56.dp, height = 20.dp)) {
        if (values.size < 2) return@Canvas
        val lo = values.min() - 1; val hi = values.max() + 1
        val pts = values.mapIndexed { i, v -> Offset(i * size.width / (values.size - 1), size.height - (v - lo) * size.height / (hi - lo)) }
        for (i in 1 until pts.size) drawLine(color, pts[i - 1], pts[i], strokeWidth = 2f)
    }
}
