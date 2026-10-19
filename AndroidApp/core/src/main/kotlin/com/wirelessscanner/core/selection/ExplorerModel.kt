package com.wirelessscanner.core.selection

import com.wirelessscanner.core.domain.AccessPoint
import com.wirelessscanner.core.domain.PhysicalUnit
import com.wirelessscanner.core.domain.UnitConfidence

enum class ExplorerSort { Signal, Name, Channel }
enum class ExplorerGrouping { Network, AccessPoint }

/** Explorer filters (chips + search + sort + grouping), the same set as the Windows explorer. */
data class ExplorerFilters(
    val search: String = "",
    val band24: Boolean = false, val band5: Boolean = false, val band6: Boolean = false,
    val fairOrBetter: Boolean = false, val secured: Boolean = false, val selectedOnly: Boolean = false,
    val sort: ExplorerSort = ExplorerSort.Signal,
    val grouping: ExplorerGrouping = ExplorerGrouping.Network,
)

/** One AP row (port of NetworkApItem). */
data class ApRow(
    val ssid: String, val bssid: String, val displaySsid: String, val rssi: Int, val channel: Int, val band: String, val widthText: String, val detailText: String,
    val tier: String, val tierColor: Long, val bars: Int, val selected: Boolean, val canToggle: Boolean, val spark: List<Int>, val identityColor: Long?,
    val connected: Boolean, val details: String, val vendor: String?, val security: String, val quality: Int, val snr: Int,
)

/** One SSID group (port of NetworkGroupItem). check: true = all, null = partial, false = none. */
data class GroupRow(
    val ssid: String, val displayName: String, val hidden: Boolean, val apCount: Int, val bestRssi: Int, val lowestChannel: Int, val subText: String,
    val tier: String, val tierColor: Long, val bars: Int, val check: Boolean?, val whole: Boolean, val canToggle: Boolean, val swatch: Long?,
    val connected: Boolean, val checkHint: String, val aps: List<ApRow>,
)

/** One physical unit (port of UnitGroupItem). */
data class UnitRow(
    val id: String, val name: String, val confidence: UnitConfidence, val confidenceText: String, val confidenceColor: Long, val evidence: String,
    val subText: String, val meshText: String, val bestRssi: Int, val lowestChannel: Int, val tier: String, val tierColor: Long, val bars: Int,
    val check: Boolean?, val whole: Boolean, val canToggle: Boolean, val connected: Boolean, val checkHint: String, val aps: List<ApRow>,
)

data class ExplorerView(
    val groups: List<GroupRow>, val units: List<UnitRow>, val countText: String, val scopeText: String, val emptyText: String, val locked: Boolean,
)

/**
 * Port of NetworkExplorerViewModel.cs (DR-0010, DR-0013) without UI types: the latest scan, a 12-value sparkline history
 * per BSSID, and the rows the Compose explorer draws. Selection is the shared [NetworkSelection].
 */
class ExplorerModel(val selection: NetworkSelection, val units: PhysicalUnitGrouper?) {
    private val history = HashMap<String, ArrayDeque<Int>>()
    var all: List<AccessPoint> = emptyList()
        private set
    var connectedBssid: String? = null

    fun update(aps: List<AccessPoint>, cycleFresh: Boolean) {
        all = aps
        for (ap in aps) {
            val fresh = ap.beaconAgeMs?.let { it <= 10_000 } ?: cycleFresh
            if (!fresh) continue
            val q = history.getOrPut(ap.bssid.uppercase()) { ArrayDeque() }
            q.addLast(ap.rssi)
            while (q.size > 12) q.removeFirst()
        }
    }

    fun historyOf(bssid: String): List<Int> = history[bssid.uppercase()]?.toList() ?: emptyList()
    fun bssidsOf(ssid: String) = all.filter { it.ssid == ssid }.map { it.bssid }

    fun toggleGroup(ssid: String) = selection.toggleGroup(ssid, bssidsOf(ssid))
    fun toggleAp(ssid: String, bssid: String) = selection.toggleAp(ssid, bssid, bssidsOf(ssid))
    fun toggleUnit(id: String): Boolean { val u = units?.get(id) ?: return false; return selection.toggleUnit(u) }

    fun selectShown(view: ExplorerView) {
        for (g in view.groups) {
            if (g.hidden) g.aps.filter { !it.selected }.forEach { selection.toggleAp(it.ssid, it.bssid, bssidsOf(it.ssid)) }
            else if (!selection.isWholeSsid(g.ssid)) selection.toggleGroup(g.ssid, bssidsOf(g.ssid))
        }
    }

    val canEditUnits get() = units != null && !selection.isLocked
    fun renameUnit(id: String, name: String?) { if (canEditUnits) units!!.rename(id, name) }
    fun splitAp(bssid: String) { if (canEditUnits) units!!.moveToNewUnit(bssid) }
    fun moveAp(bssid: String, unitId: String) { if (canEditUnits) units!!.moveToUnit(bssid, unitId) }
    fun resetUnit(id: String) { if (canEditUnits) units!!.reset(id) }
    fun moveTargets(bssid: String): List<Pair<String, String>> {
        val u = units ?: return emptyList()
        val current = u.unitOf(bssid)
        return u.units.filter { it.id != current }.map { it.id to it.name }
    }

    private fun passes(a: AccessPoint, f: ExplorerFilters, ignoreSearch: Boolean = false): Boolean {
        val anyBand = f.band24 || f.band5 || f.band6
        if (anyBand && !((f.band24 && a.band == "2.4 GHz") || (f.band5 && a.band == "5 GHz") || (f.band6 && a.band == "6 GHz"))) return false
        if (f.fairOrBetter && a.rssi < -70) return false
        if (f.secured && (a.securityType == "Open" || a.securityType.startsWith("OWE"))) return false
        if (f.selectedOnly && !selection.isExplicitlySelected(a.ssid, a.bssid)) return false
        val q = f.search.trim()
        if (!ignoreSearch && q.isNotEmpty() && !(a.ssid.contains(q, true) || a.bssid.contains(q, true) || (a.vendorOui ?: "").contains(q, true))) return false
        return true
    }

    fun identityColorFor(ssid: String): Long? = selection.focusSlot(ssid, all).let { if (it >= 0) IDENTITY_COLORS[it] else null }
    private fun isConnected(bssid: String) = connectedBssid?.equals(bssid, true) == true

    private fun apRow(ap: AccessPoint, selected: Boolean, identity: Long?, canToggle: Boolean): ApRow {
        val width = ap.channelWidth?.let { "$it MHz" } ?: "width ?"
        val (tier, color) = tier(ap.rssi)
        val disp = if (NetworkSelection.isHiddenSsid(ap.ssid)) "<Hidden SSID>" else ap.ssid
        return ApRow(ap.ssid, ap.bssid, disp, ap.rssi, ap.channel, ap.band, width, "ch ${ap.channel} · ${ap.band} · $width", tier, color, barsFor(ap.rssi),
            selected, canToggle, historyOf(ap.bssid), identity, isConnected(ap.bssid),
            "$disp\n${ap.bssid} · ${ap.vendorOui}\nch ${ap.channel} · ${ap.band} · $width\nRSSI ${ap.rssi} dBm ($tier) · quality ${ap.quality}% · SNR est. ~${ap.snr} dB\nSecurity: ${ap.securityType}",
            ap.vendorOui, ap.securityType, ap.quality, ap.snr)
    }

    fun view(f: ExplorerFilters): ExplorerView {
        val locked = selection.isLocked
        val scope = (if (locked) "LOCKED (test running) · " else "") + selection.describe { s -> all.count { it.ssid == s } }
        val sel = all.count { selection.isExplicitlySelected(it.ssid, it.bssid) }
        if (f.grouping == ExplorerGrouping.AccessPoint && units != null) return unitView(f, scope, sel, locked)

        val bySsid = LinkedHashMap<String, MutableList<AccessPoint>>()
        all.forEach { bySsid.getOrPut(it.ssid) { ArrayList() }.add(it) }
        var groups = ArrayList<GroupRow>()
        for ((ssid, grp) in bySsid) {
            val shown = grp.filter { passes(it, f) }
            if (shown.isEmpty()) continue
            val state = selection.stateOf(ssid, grp.map { it.bssid })
            val whole = selection.isWholeSsid(ssid)
            val identity = identityColorFor(ssid)
            val hidden = NetworkSelection.isHiddenSsid(ssid)
            val best = grp.maxOf { it.rssi }
            val secCounts = LinkedHashMap<String, Int>(); grp.forEach { secCounts[it.securityType] = (secCounts[it.securityType] ?: 0) + 1 }
            val security = secCounts.entries.sortedByDescending { it.value }.first().key
            val vendor = grp.map { it.vendorOui }.firstOrNull { !it.isNullOrEmpty() && it != "Unknown Vendor" } ?: "Unknown Vendor"
            val bands = grp.map { it.band }.distinct().sorted().joinToString(" / ")
            val (tier, color) = tier(best)
            val check = when (state) { GroupSelectionState.All -> true; GroupSelectionState.Partial -> null; else -> false }
            groups.add(GroupRow(ssid, if (hidden) "<Hidden SSID>" else ssid, hidden, grp.size, best, grp.minOf { it.channel },
                "${grp.size} AP${if (grp.size == 1) "" else "s"} · $security · $bands · $vendor", tier, color, barsFor(best), check, whole, !hidden && !locked,
                if (state != GroupSelectionState.None || identity != null) identity ?: CONTEXT_COLOR else null, grp.any { isConnected(it.bssid) },
                if (hidden) "Hidden networks can only be selected per access point (BSSID)." else if (whole) "Whole network selected: follows access points that appear later." else "Select every access point of this network (follows new ones).",
                shown.sortedByDescending { it.rssi }.map { apRow(it, selection.isExplicitlySelected(it.ssid, it.bssid) || whole, identity, !locked) }))
        }
        groups = ArrayList(when (f.sort) {
            ExplorerSort.Name -> groups.sortedWith(compareBy<GroupRow> { it.hidden }.thenBy(String.CASE_INSENSITIVE_ORDER) { it.ssid })
            ExplorerSort.Channel -> groups.sortedWith(compareBy<GroupRow> { it.lowestChannel }.thenByDescending { it.bestRssi })
            else -> groups.sortedWith(compareByDescending<GroupRow> { it.bestRssi }.thenBy { it.ssid })
        })
        val empty = if (all.isEmpty()) "No networks yet. Scanning starts automatically once Wi-Fi and location are on." else if (groups.isEmpty()) "No networks match these filters. Clear a chip or the search." else ""
        return ExplorerView(groups, emptyList(), "${bySsid.size} SSIDs · ${all.size} APs · $sel selected", scope, empty, locked)
    }

    private fun unitView(f: ExplorerFilters, scope: String, sel: Int, locked: Boolean): ExplorerView {
        val g = units!!
        val byBssid = all.associateBy { it.bssid.uppercase() }
        val rows = ArrayList<UnitRow>()
        for (unit in g.units) {
            val members = unit.members.mapNotNull { byBssid[it.bssid.uppercase()] }
            val q = f.search.trim()
            val shown = members.filter { passes(it, f) || (q.isNotEmpty() && unit.name.contains(q, true) && passes(it, f, ignoreSearch = true)) }
            if (members.isEmpty() || shown.isEmpty()) continue
            rows.add(unitRow(unit, members, shown, g.units, locked))
        }
        val sorted = when (f.sort) {
            ExplorerSort.Name -> rows.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })
            ExplorerSort.Channel -> rows.sortedWith(compareBy<UnitRow> { it.lowestChannel }.thenByDescending { it.bestRssi })
            else -> rows.sortedWith(compareByDescending<UnitRow> { it.bestRssi }.thenBy { it.id })
        }
        val unitCount = g.units.count { u -> u.members.any { byBssid.containsKey(it.bssid.uppercase()) } }
        val multi = g.units.count { u -> u.members.count { byBssid.containsKey(it.bssid.uppercase()) } > 1 }
        val empty = if (all.isEmpty()) "No networks yet. Scanning starts automatically once Wi-Fi and location are on." else if (sorted.isEmpty()) "No access points match these filters. Clear a chip or the search." else ""
        return ExplorerView(emptyList(), sorted, "$unitCount access points ($multi with several BSSIDs) · ${all.size} BSSIDs · $sel selected", scope, empty, locked)
    }

    private fun unitRow(unit: PhysicalUnit, members: List<AccessPoint>, shown: List<AccessPoint>, allUnits: List<PhysicalUnit>, locked: Boolean): UnitRow {
        val best = members.maxOf { it.rssi }
        val (tier, color) = tier(best)
        val radios = unit.radios.size; val ssids = unit.members.map { it.ssid }.distinct().size; val bssids = unit.members.size
        val bands = unit.radios.map { it.band }.distinct().sorted().joinToString(" / ")
        val confText = when (unit.confidence) { UnitConfidence.High -> "HIGH"; UnitConfidence.Medium -> "MEDIUM"; UnitConfidence.Manual -> "MANUAL"; else -> "SINGLE" }
        val confColor = when (unit.confidence) { UnitConfidence.High -> 0xFFFF8800; UnitConfidence.Medium -> 0xFFC2B5A3; UnitConfidence.Manual -> 0xFF3987E5; else -> 0xFF7A6A55 }
        val names = unit.sameSsidUnitIds.mapNotNull { id -> allUnits.firstOrNull { it.id == id }?.name }
        val state = selection.stateOfUnit(unit)
        val whole = selection.isUnitSelected(unit.id)
        return UnitRow(unit.id, unit.name, unit.confidence, confText, confColor,
            "$confText confidence (inferred; Android does not report device identity)\n" + unit.evidence.joinToString("\n") { "• $it" } + "\nLong-press or ⋮ to rename, split or reset this grouping.",
            "$radios radio${if (radios == 1) "" else "s"} · $ssids SSID${if (ssids == 1) "" else "s"} · $bssids BSSID${if (bssids == 1) "" else "s"} · $bands",
            if (unit.isMeshCandidate) "Same SSID as ${names.joinToString(", ")}: mesh node or extender candidate" else "",
            best, members.minOf { it.channel }, tier, color, barsFor(best),
            when (state) { GroupSelectionState.All -> true; GroupSelectionState.Partial -> null; else -> false }, whole, !locked,
            unit.members.any { isConnected(it.bssid) },
            if (whole) "Whole access point selected: every SSID and radio it broadcasts." else "Select this access point: every SSID and radio it broadcasts (hidden ones included).",
            shown.sortedWith(compareBy<AccessPoint> { it.band }.thenByDescending { it.rssi }).map { apRow(it, selection.isExplicitlySelected(it.ssid, it.bssid), identityColorFor(it.ssid), !locked) })
    }

    companion object {
        /** Validated categorical identity colours (slots 1–3) and the context colour (same as Windows). */
        val IDENTITY_COLORS = listOf(0xFF3987E5, 0xFFD95926, 0xFF199E70)
        const val CONTEXT_COLOR = 0xFF5B5246

        fun tier(rssi: Int): Pair<String, Long> = when {
            rssi >= -55 -> "EXCELLENT" to 0xFF0CA30C
            rssi >= -67 -> "GOOD" to 0xFF0CA30C
            rssi >= -70 -> "FAIR" to 0xFFFAB219
            rssi >= -80 -> "WEAK" to 0xFFEC835A
            else -> "UNUSABLE" to 0xFFD03B3B
        }

        fun barsFor(rssi: Int) = if (rssi >= -55) 4 else if (rssi >= -67) 3 else if (rssi >= -75) 2 else 1
    }
}
