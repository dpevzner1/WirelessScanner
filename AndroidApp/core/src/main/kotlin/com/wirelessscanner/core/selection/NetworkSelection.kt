package com.wirelessscanner.core.selection

import com.wirelessscanner.core.domain.AccessPoint
import com.wirelessscanner.core.domain.PhysicalUnit
import com.wirelessscanner.core.domain.SaturationScope

enum class GroupSelectionState { None, Partial, All }

/** Current physical-unit grouping (DR-0013), implemented by PhysicalUnitGrouper. */
interface UnitResolver {
    fun unitOf(bssid: String): String?
    fun get(unitId: String): PhysicalUnit?
}

/** Insertion-ordered, case-insensitive string set (C# HashSet with OrdinalIgnoreCase). */
class CiSet : Iterable<String> {
    private val map = LinkedHashMap<String, String>()
    val size get() = map.size
    fun add(s: String): Boolean = if (map.containsKey(s.uppercase())) false else { map[s.uppercase()] = s; true }
    fun remove(s: String) = map.remove(s.uppercase()) != null
    operator fun contains(s: String) = map.containsKey(s.uppercase())
    fun clear() = map.clear()
    fun isEmpty() = map.isEmpty()
    override fun iterator() = map.values.toList().iterator()
}

/**
 * Port of WirelessScanner.Application/NetworkSelection.cs: the one selection shared by the live view, capture sessions and
 * saturation tests (DR-0010, DR-0011, DR-0013). Whole SSIDs, fixed BSSIDs and physical units. Empty = everything.
 * While locked (a test is running) every mutation is refused.
 */
class NetworkSelection {
    private val ssidSet = LinkedHashSet<String>()
    private val bssidSet = CiSet()
    private val ssidOfBssid = LinkedHashMap<String, String>() // key: upper-case BSSID
    private val focus = ArrayList<String>()
    private val unitSet = LinkedHashSet<String>()
    private var frozenUnitBssids: CiSet? = null
    private val listeners = ArrayList<() -> Unit>()

    fun addChangedListener(l: () -> Unit) { listeners.add(l) }
    private fun changed() = listeners.forEach { it() }

    val ssids: Set<String> get() = ssidSet
    val bssids: List<String> get() = bssidSet.toList()
    val units: Set<String> get() = unitSet
    val isEmpty get() = ssidSet.isEmpty() && bssidSet.isEmpty() && unitSet.isEmpty()
    var isLocked = false
        private set
    var unitResolver: UnitResolver? = null

    fun matches(ssid: String, bssid: String) = isEmpty || isExplicitlySelected(ssid, bssid)
    fun isExplicitlySelected(ssid: String, bssid: String) = ssid in ssidSet || bssid in bssidSet || inSelectedUnit(bssid)
    fun isUnitSelected(unitId: String) = unitId in unitSet

    private fun inSelectedUnit(bssid: String): Boolean {
        if (unitSet.isEmpty()) return false
        frozenUnitBssids?.let { return bssid in it }
        val u = unitResolver?.unitOf(bssid) ?: return false
        return u in unitSet
    }

    fun unitBssids(): List<String> {
        frozenUnitBssids?.let { return it.toList() }
        val set = CiSet()
        for (id in unitSet) unitResolver?.get(id)?.members?.forEach { set.add(it.bssid) }
        return set.toList()
    }

    fun stateOfUnit(unit: PhysicalUnit): GroupSelectionState {
        if (unit.id in unitSet) return GroupSelectionState.All
        val members = unit.members
        val n = members.count { it.ssid in ssidSet || it.bssid in bssidSet }
        return if (n == 0) GroupSelectionState.None else if (n == members.size) GroupSelectionState.All else GroupSelectionState.Partial
    }

    fun toggleUnit(unit: PhysicalUnit): Boolean {
        if (isLocked) return false
        val members = unit.members
        if (!unitSet.remove(unit.id)) {
            members.forEach { removeBssid(it.bssid) }
            unitSet.add(unit.id)
        }
        members.map { it.ssid }.distinct().forEach { touch(it, notify = false) }
        changed()
        return true
    }

    fun isWholeSsid(ssid: String) = ssid in ssidSet

    fun stateOf(ssid: String, bssidsOfSsid: Collection<String>): GroupSelectionState {
        if (ssid in ssidSet) return GroupSelectionState.All
        val n = bssidsOfSsid.count { it in bssidSet || inSelectedUnit(it) }
        return if (n == 0) GroupSelectionState.None else if (n == bssidsOfSsid.size) GroupSelectionState.All else GroupSelectionState.Partial
    }

    fun toggleGroup(ssid: String, bssidsOfSsid: Collection<String>): Boolean {
        if (isLocked || isHiddenSsid(ssid)) return false
        val wholeOrFull = stateOf(ssid, bssidsOfSsid) == GroupSelectionState.All
        bssidsOfSsid.forEach { removeBssid(it) }
        if (wholeOrFull) ssidSet.remove(ssid) else ssidSet.add(ssid)
        touch(ssid)
        return true
    }

    fun toggleAp(ssid: String, bssid: String, bssidsOfSsid: Collection<String>): Boolean {
        if (isLocked) return false
        val unitId = unitResolver?.unitOf(bssid)
        val unit = unitId?.let { unitResolver?.get(it) }
        if (unitId != null && unitId in unitSet && unit != null) {
            unitSet.remove(unitId)
            unit.members.filter { !it.bssid.equals(bssid, true) }.forEach { addBssid(it.ssid, it.bssid) }
            unit.members.map { it.ssid }.distinct().forEach { touch(it, notify = false) }
            changed()
            return true
        }
        if (ssidSet.remove(ssid)) bssidsOfSsid.filter { !it.equals(bssid, true) }.forEach { addBssid(ssid, it) }
        else if (bssid in bssidSet) removeBssid(bssid)
        else addBssid(ssid, bssid)
        touch(ssid)
        return true
    }

    fun setSsid(ssid: String, selected: Boolean): Boolean {
        if (isLocked) return false
        if (selected) ssidSet.add(ssid) else ssidSet.remove(ssid)
        touch(ssid); return true
    }

    fun setBssid(ssid: String, bssid: String, selected: Boolean): Boolean {
        if (isLocked) return false
        if (selected) addBssid(ssid, bssid) else removeBssid(bssid)
        touch(ssid); return true
    }

    fun clear(): Boolean {
        if (isLocked) return false
        ssidSet.clear(); bssidSet.clear(); ssidOfBssid.clear(); unitSet.clear(); focus.clear()
        changed(); return true
    }

    fun lock() {
        frozenUnitBssids = if (unitSet.isEmpty()) null else CiSet().also { s -> unitBssids().forEach { s.add(it) } }
        isLocked = true
        changed()
    }

    fun unlock() { isLocked = false; frozenUnitBssids = null; changed() }

    private fun addBssid(ssid: String, bssid: String) { bssidSet.add(bssid); ssidOfBssid[bssid.uppercase()] = ssid }
    private fun removeBssid(bssid: String) { bssidSet.remove(bssid); ssidOfBssid.remove(bssid.uppercase()) }

    /** Keep focus order stable: colour follows the network, never its rank. */
    private fun touch(ssid: String, notify: Boolean = true) {
        val hasAny = ssid in ssidSet || ssidOfBssid.values.contains(ssid) ||
            unitSet.any { id -> unitResolver?.get(id)?.members?.any { it.ssid == ssid } == true }
        if (hasAny && ssid !in focus) focus.add(ssid)
        if (!hasAny) focus.remove(ssid)
        if (notify) changed()
    }

    val focusOrder: List<String> get() = focus

    fun focusSlot(ssid: String, visible: Iterable<AccessPoint>): Int {
        val order = if (isEmpty) autoFocus(visible) else focus
        val i = order.indexOf(ssid)
        return if (i in 0 until MAX_FOCUS_COLORS) i else -1
    }

    fun deriveScope(): SaturationScope {
        val s = ssidSet.size; val b = bssidSet.size; val u = unitSet.size
        if (u > 0) return when {
            s == 0 && b == 0 && u == 1 -> SaturationScope.SingleUnit
            s == 0 && b == 0 -> SaturationScope.MultipleUnits
            else -> SaturationScope.Mixed
        }
        return when {
            s == 0 && b == 0 -> SaturationScope.AllVisible
            s == 1 && b == 0 -> SaturationScope.SingleSsid
            s > 1 && b == 0 -> SaturationScope.MultipleSsids
            s == 0 && b == 1 -> SaturationScope.SingleAp
            s == 0 && b > 1 -> SaturationScope.MultipleAps
            else -> SaturationScope.Mixed
        }
    }

    fun describe(apsInSsid: (String) -> Int): String {
        if (isEmpty) return "Nothing selected: everything is shown and captured; a saturation test would ask before covering all networks."
        val parts = ArrayList<String>()
        if (ssidSet.isNotEmpty()) {
            val aps = ssidSet.sumOf(apsInSsid)
            parts.add("${ssidSet.size} SSID${if (ssidSet.size == 1) "" else "s"} ($aps AP${if (aps == 1) "" else "s"} now, follows new APs)")
        }
        if (unitSet.isNotEmpty()) {
            val m = unitBssids().size
            parts.add("${unitSet.size} access point${if (unitSet.size == 1) "" else "s"} (hardware: $m BSSID${if (m == 1) "" else "s"}, all SSIDs and radios)")
        }
        if (!bssidSet.isEmpty()) parts.add("${bssidSet.size} AP${if (bssidSet.size == 1) "" else "s"} (fixed)")
        return parts.joinToString(" + ") + if (deriveScope() == SaturationScope.Mixed) " · mixed" else ""
    }

    companion object {
        /** Overlapping spectrum shapes need all-pairs colour separation; only 3 validated slots pass (DR-0010). */
        const val MAX_FOCUS_COLORS = 3

        fun isHiddenSsid(ssid: String) = ssid.isEmpty() || ssid == "<Hidden SSID>"

        fun autoFocus(visible: Iterable<AccessPoint>): List<String> {
            val groups = LinkedHashMap<String, Int>()
            visible.filter { !isHiddenSsid(it.ssid) }.forEach { groups[it.ssid] = maxOf(groups[it.ssid] ?: Int.MIN_VALUE, it.rssi) }
            return groups.entries.sortedWith(compareByDescending<Map.Entry<String, Int>> { it.value }.thenBy { it.key })
                .take(MAX_FOCUS_COLORS).map { it.key }
        }
    }
}
