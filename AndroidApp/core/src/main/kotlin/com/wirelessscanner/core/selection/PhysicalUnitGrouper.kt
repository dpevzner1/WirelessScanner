package com.wirelessscanner.core.selection

import com.wirelessscanner.core.domain.*
import java.time.Duration
import java.time.Instant
import kotlin.math.abs

/**
 * Port of WirelessScanner.Application/PhysicalUnitGrouper.cs (DR-0013): infers physical access points from scans.
 * Evidence, strongest first: same radio clock (TSF; never available on Android), same WPS UUID-E, same Wi-Fi 7 MLD address
 * (Android API 33+ only; treated like WPS identity), MAC family. The user's moves and names always win.
 */
class PhysicalUnitGrouper : UnitResolver {
    private class Seen(var ap: AccessPoint, var lastUtc: Instant)

    private val seen = LinkedHashMap<String, Seen>() // key upper-case BSSID
    private val tsf = HashMap<Pair<String, String>, IntArray>() // [agree, disagree]
    private var overridesState = UnitOverrides()
    private var unitList: List<PhysicalUnit> = emptyList()
    private val unitOfMap = HashMap<String, String>()
    private val changedListeners = ArrayList<() -> Unit>()
    private val overridesListeners = ArrayList<(UnitOverrides) -> Unit>()

    fun addChangedListener(l: () -> Unit) { changedListeners.add(l) }
    fun addOverridesListener(l: (UnitOverrides) -> Unit) { overridesListeners.add(l) }

    val units: List<PhysicalUnit> get() = unitList
    val overrides: UnitOverrides get() = clone(overridesState)

    override fun unitOf(bssid: String) = unitOfMap[bssid.uppercase()]
    override fun get(unitId: String) = unitList.firstOrNull { it.id == unitId }

    fun observe(aps: List<AccessPoint>, nowUtc: Instant) {
        for (ap in aps) {
            if (ap.bssid.isEmpty()) continue
            seen[ap.bssid.uppercase()] = Seen(ap, nowUtc)
        }
        val stale = seen.filter { Duration.between(it.value.lastUtc, nowUtc) > RETENTION }.keys.toList()
        for (k in stale) {
            seen.remove(k)
            tsf.keys.filter { it.first == k || it.second == k }.forEach { tsf.remove(it) }
        }
        val timed = aps.filter { it.tsfOffsetMs != null && it.bssid.isNotEmpty() }
        for (i in timed.indices) for (j in i + 1 until timed.size) {
            val a = timed[i]; val b = timed[j]
            if (a.band != b.band) continue
            val v = tsf.getOrPut(pairKey(a.bssid, b.bssid)) { IntArray(2) }
            if (abs(a.tsfOffsetMs!! - b.tsfOffsetMs!!) <= SAME_RADIO_TOLERANCE_MS) v[0]++ else v[1]++
        }
        rebuild()
    }

    fun loadOverrides(o: UnitOverrides?) { overridesState = if (o == null) UnitOverrides() else clone(o); rebuild() }

    fun moveToUnit(bssid: String, unitId: String) { setAssignment(bssid, unitId); commit() }

    fun moveToNewUnit(bssid: String): String {
        val id = "M-" + bssid.uppercase()
        setAssignment(bssid, id); commit(); return id
    }

    fun rename(unitId: String, name: String?) {
        if (name.isNullOrBlank()) overridesState.names.remove(unitId) else overridesState.names[unitId] = name.trim()
        commit()
    }

    fun reset(unitId: String) {
        val members = get(unitId)?.members?.map { it.bssid.uppercase() }?.toHashSet() ?: hashSetOf()
        overridesState.assignments.filter { it.value == unitId || it.key.uppercase() in members }.keys.toList().forEach { overridesState.assignments.remove(it) }
        overridesState.names.remove(unitId)
        commit()
    }

    private fun setAssignment(bssid: String, unitId: String) {
        overridesState.assignments.keys.filter { it.equals(bssid, true) }.forEach { overridesState.assignments.remove(it) }
        overridesState.assignments[bssid] = unitId
    }

    private fun commit() { rebuild(); val c = clone(overridesState); overridesListeners.forEach { it(c) } }

    private fun sameRadioClock(a: String, b: String): Boolean = tsf[pairKey(a, b)]?.let { it[0] > 0 && it[0] >= it[1] } ?: false

    private fun rebuild() {
        val bssids = seen.keys.sorted() // already upper-case: ordinal = OrdinalIgnoreCase order
        val index = bssids.withIndex().associate { it.value to it.index }
        val parent = IntArray(bssids.size) { it }
        fun find(x: Int): Int { var i = x; while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i] }; return i }
        fun union(i: Int, j: Int) { val a = find(i); val b = find(j); if (a != b) parent[maxOf(a, b)] = minOf(a, b) }
        val reasons = HashMap<Int, HashSet<String>>()
        fun reason(i: Int, r: String) { reasons.getOrPut(i) { HashSet() }.add(r) }

        for (i in bssids.indices) {
            val a = seen[bssids[i]]!!.ap
            for (j in i + 1 until bssids.size) {
                val b = seen[bssids[j]]!!.ap
                val uuidConflict = a.wpsUuid != null && b.wpsUuid != null && !a.wpsUuid.equals(b.wpsUuid, true)
                val mldConflict = a.apMldMac != null && b.apMldMac != null && !a.apMldMac.equals(b.apMldMac, true)
                when {
                    a.band == b.band && sameRadioClock(a.bssid, b.bssid) && !uuidConflict -> { union(i, j); reason(i, "radio clock"); reason(j, "radio clock") }
                    a.wpsUuid != null && a.wpsUuid.equals(b.wpsUuid, true) -> { union(i, j); reason(i, "wps uuid"); reason(j, "wps uuid") }
                    a.apMldMac != null && a.apMldMac.equals(b.apMldMac, true) && !uuidConflict -> { union(i, j); reason(i, "mld"); reason(j, "mld") }
                    !uuidConflict && !mldConflict && sameFamily(a.bssid, b.bssid) -> { union(i, j); reason(i, "mac family"); reason(j, "mac family") }
                }
            }
        }

        val autoMembers = LinkedHashMap<Int, MutableList<String>>()
        for (i in bssids.indices) autoMembers.getOrPut(find(i)) { ArrayList() }.add(bssids[i])
        val unitOfBssid = LinkedHashMap<String, String>()
        for ((_, members) in autoMembers) {
            val id = "U-" + members.min().replace(":", "")
            members.forEach { unitOfBssid[it] = id }
        }
        val manualUnits = HashSet<String>()
        for ((bssid, target) in overridesState.assignments) {
            val k = bssid.uppercase()
            if (!unitOfBssid.containsKey(k)) continue
            unitOfBssid[k] = target; manualUnits.add(target)
        }
        for ((bssid, target) in overridesState.assignments) {
            val i = index[bssid.uppercase()] ?: continue
            for (m in autoMembers[find(i)]!!) if (m != bssid.uppercase() && unitOfBssid[m] != target) manualUnits.add(unitOfBssid[m]!!)
        }
        unitOfMap.clear(); unitOfMap.putAll(unitOfBssid)

        val groups = LinkedHashMap<String, MutableList<String>>()
        unitOfBssid.forEach { (b, u) -> groups.getOrPut(u) { ArrayList() }.add(b) }
        val units = ArrayList<PhysicalUnit>()
        for ((unitId, gm) in groups) {
            val members = gm.map { seen[it]!!.ap }.sortedWith(compareBy<AccessPoint> { it.band }.thenBy { it.bssid.uppercase() })
            val radios = radios(members)
            val evidence = ArrayList<String>()
            val memberReasons = members.flatMap { reasons[index[it.bssid.uppercase()]!!] ?: emptySet() }.toHashSet()
            val manual = unitId in manualUnits
            if (manual) evidence.add("Grouping corrected by the user.")
            if ("radio clock" in memberReasons)
                evidence.add("Same radio clock: ${radios.count { it.members.size > 1 && it.key.contains('#') }} radio(s) where several BSSIDs share one TSF timer (within ${SAME_RADIO_TOLERANCE_MS.toInt()} ms).")
            if ("wps uuid" in memberReasons) evidence.add("Same WPS device identity (UUID-E).")
            if ("mld" in memberReasons) evidence.add("Same Wi-Fi 7 multi-link (MLD) address.")
            if ("mac family" in memberReasons) evidence.add("Related MAC addresses (same address family across radios/SSIDs).")
            val models = members.mapNotNull { it.wpsModelNumber ?: it.wpsModel }.distinct()
            if (models.isNotEmpty()) evidence.add("WPS model: " + models.joinToString(", ") + ".")

            val confidence = when {
                manual -> UnitConfidence.Manual
                members.size == 1 -> UnitConfidence.Single
                "radio clock" in memberReasons || "wps uuid" in memberReasons || "mld" in memberReasons || models.isNotEmpty() -> UnitConfidence.High
                else -> UnitConfidence.Medium
            }
            val wpsVendors = LinkedHashMap<String, Int>()
            members.mapNotNull { it.wpsManufacturer }.forEach { wpsVendors[it] = (wpsVendors[it] ?: 0) + 1 }
            val vendor = wpsVendors.entries.sortedByDescending { it.value }.firstOrNull()?.key
                ?: members.map { it.vendorOui }.firstOrNull { !it.isNullOrEmpty() && it != "Unknown Vendor" }
            val model = members.mapNotNull { it.wpsModelNumber ?: it.wpsModel }.firstOrNull()
            val root = members.map { it.bssid }.minBy { it.uppercase() }
            val autoName = (if (model != null) "${vendor ?: ""} $model".trim() else vendor ?: "Access point") + " (${root.takeLast(5)})"
            val customName = overridesState.names[unitId]
            units.add(PhysicalUnit(unitId, customName ?: autoName, customName != null, confidence, evidence, radios, vendor, model, emptyList()))
        }

        val unitsBySsid = LinkedHashMap<String, MutableList<String>>()
        units.flatMap { u -> u.members.filter { it.ssid.isNotEmpty() && it.ssid != HIDDEN_SSID }.map { it.ssid to u.id } }.distinct()
            .forEach { (s, id) -> unitsBySsid.getOrPut(s) { ArrayList() }.add(id) }
        unitList = units.map { u ->
            val others = u.members.map { it.ssid }.distinct().filter { unitsBySsid.containsKey(it) }
                .flatMap { unitsBySsid[it]!! }.filter { it != u.id }.distinct().sorted()
            if (others.isEmpty()) u else u.copy(sameSsidUnitIds = others,
                evidence = u.evidence + "Shares an SSID with ${others.size} other unit(s): mesh node or extender candidate.")
        }.sortedWith(compareByDescending<PhysicalUnit> { u -> u.members.maxOf { it.rssi } }.thenBy { it.id })
        changedListeners.forEach { it() }
    }

    private fun radios(members: List<AccessPoint>): List<UnitRadio> {
        val groups = ArrayList<MutableList<AccessPoint>>()
        for (m in members) {
            val g = groups.firstOrNull { g -> g[0].band == m.band && g.any { sameRadioClock(it.bssid, m.bssid) } }
                ?: groups.firstOrNull { g -> g[0].band == m.band && g[0].channel == m.channel && g.none { tsf.containsKey(pairKey(it.bssid, m.bssid)) } }
            if (g == null) groups.add(arrayListOf(m)) else g.add(m)
        }
        return groups.map { g ->
            val clock = g.size > 1 && g.drop(1).all { sameRadioClock(g[0].bssid, it.bssid) }
            val key = "${g[0].band}${if (clock) "#" else "@"}${g.map { it.bssid }.minBy { it.uppercase() }}"
            UnitRadio(key, g[0].band, g[0].channel, g.map { UnitMember(it.bssid, it.ssid, it.band, it.channel, it.rssi) })
        }.sortedBy { it.band }
    }

    companion object {
        const val SAME_RADIO_TOLERANCE_MS = 5.0
        val RETENTION: Duration = Duration.ofMinutes(10)
        const val HIDDEN_SSID = "<Hidden SSID>"

        fun octets(bssid: String): IntArray? {
            val parts = bssid.split(':', '-')
            if (parts.size != 6) return null
            return IntArray(6) { parts[it].toIntOrNull(16)?.takeIf { v -> v in 0..255 && parts[it].length in 1..2 } ?: return null }
        }

        fun isLocallyAdministered(bssid: String) = octets(bssid)?.let { (it[0] and 0x02) != 0 } ?: false

        fun sameFamily(a: String, b: String): Boolean {
            val x = octets(a) ?: return false
            val y = octets(b) ?: return false
            val core = (x[0] and 0x06.inv()) == (y[0] and 0x06.inv()) && x[1] == y[1] && x[2] == y[2] && x[4] == y[4] && (x[5] shr 4) == (y[5] shr 4)
            if (!core) return false
            if (x[3] == y[3]) return true
            return (x[0] and 0x02) != 0 && (y[0] and 0x02) != 0
        }

        private fun pairKey(a: String, b: String): Pair<String, String> {
            val x = a.uppercase(); val y = b.uppercase()
            return if (x <= y) x to y else y to x
        }

        private fun clone(o: UnitOverrides) = UnitOverrides(LinkedHashMap(o.assignments), LinkedHashMap(o.names))
    }
}
