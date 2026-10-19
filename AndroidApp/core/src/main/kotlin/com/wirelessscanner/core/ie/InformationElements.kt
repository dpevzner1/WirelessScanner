package com.wirelessscanner.core.ie

import java.nio.ByteBuffer
import java.util.Locale
import java.util.UUID
import kotlin.math.abs

/**
 * Port of the 802.11 element parsers in WlanAdapter.cs (F15–F17, DR-0012, DR-0013). Android's ScanResult.getInformationElements()
 * (API 30+) is re-assembled into the raw "id, length, body" stream so the same parsers produce the same labels as Windows.
 */
object InformationElements {
    /** One element as Android reports it (body without id/length; extension elements without the extension id). */
    data class Element(val id: Int, val idExt: Int, val body: ByteArray)

    fun assemble(elements: List<Element>): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        for (e in elements) {
            if (e.id == 255) {
                val len = minOf(255, e.body.size + 1)
                out.write(255); out.write(len); out.write(e.idExt); out.write(e.body, 0, len - 1)
            } else {
                val len = minOf(255, e.body.size)
                out.write(e.id); out.write(len); out.write(e.body, 0, len)
            }
        }
        return out.toByteArray()
    }

    private inline fun walk(ie: ByteArray, f: (id: Int, body: Int, len: Int) -> Boolean) {
        var offset = 0
        while (offset + 2 <= ie.size) {
            val id = ie[offset].toInt() and 0xFF
            val len = ie[offset + 1].toInt() and 0xFF
            val body = offset + 2
            if (body + len > ie.size) break
            if (!f(id, body, len)) return
            offset = body + len
        }
    }
    private fun u(b: ByteArray, i: Int) = b[i].toInt() and 0xFF

    data class Wps(val manufacturer: String?, val model: String?, val modelNumber: String?, val deviceName: String?, val uuid: String?)

    fun parseWps(ie: ByteArray): Wps? {
        var result: Wps? = null
        walk(ie) { id, body, len ->
            if (id == 221 && len >= 4 && u(ie, body) == 0x00 && u(ie, body + 1) == 0x50 && u(ie, body + 2) == 0xF2 && u(ie, body + 3) == 0x04) {
                var mfr: String? = null; var model: String? = null; var number: String? = null; var name: String? = null; var uuid: String? = null
                var t = body + 4
                while (t + 4 <= body + len) {
                    val type = (u(ie, t) shl 8) or u(ie, t + 1)
                    val l = (u(ie, t + 2) shl 8) or u(ie, t + 3)
                    if (t + 4 + l > body + len) break
                    fun text() = String(ie, t + 4, l, Charsets.UTF_8).trim('\u0000', ' ')
                    when (type) {
                        0x1021 -> mfr = text()
                        0x1023 -> model = text()
                        0x1024 -> number = text()
                        0x1011 -> name = text()
                        0x1047 -> if (l == 16) { val bb = ByteBuffer.wrap(ie, t + 4, 16); uuid = UUID(bb.long, bb.long).toString() }
                    }
                    t += 4 + l
                }
                fun n(v: String?) = if (v.isNullOrBlank()) null else v
                result = Wps(n(mfr), n(model), n(number), n(name), uuid)
                false
            } else true
        }
        return result
    }

    data class BssLoad(val stations: Int, val utilizationPct: Double, val admissionCapacity: Int)

    fun parseBssLoad(ie: ByteArray): BssLoad? {
        var r: BssLoad? = null
        walk(ie) { id, body, len ->
            if (id == 11 && len >= 5) {
                r = BssLoad(u(ie, body) or (u(ie, body + 1) shl 8), Math.rint(u(ie, body + 2) * 100.0 / 255.0 * 10) / 10, u(ie, body + 3) or (u(ie, body + 4) shl 8))
                false
            } else true
        }
        return r
    }

    /** Operating width from HT (61), VHT (192) and HE 6 GHz (255/36) operation elements; null = unknown, never guessed. */
    fun parseChannelWidth(ie: ByteArray, band: String): Int? {
        var width: Int? = null; var vht: Int? = null; var he6: Int? = null
        walk(ie) { id, body, len ->
            if (id == 61 && len >= 2) {
                val info = u(ie, body + 1)
                val forty = (info and 0x03) != 0 && (info and 0x04) != 0
                width = maxOf(width ?: 0, if (forty) 40 else 20)
            } else if (id == 192 && len >= 3) {
                val w = u(ie, body); val c0 = u(ie, body + 1); val c1 = u(ie, body + 2)
                vht = when {
                    w == 1 && c1 != 0 && abs(c1 - c0) == 8 -> 160
                    w == 1 && c1 != 0 && abs(c1 - c0) > 16 -> 160
                    w == 1 -> 80
                    w == 2 || w == 3 -> 160
                    else -> null
                }
            } else if (id == 255 && len >= 7 && u(ie, body) == 36) {
                val p = body + 1
                val params = u(ie, p) or (u(ie, p + 1) shl 8) or (u(ie, p + 2) shl 16)
                var cursor = p + 3 + 1 + 2
                if ((params and (1 shl 14)) != 0) cursor += 3
                if ((params and (1 shl 15)) != 0) cursor += 1
                if ((params and (1 shl 17)) != 0 && cursor + 5 <= body + len) {
                    he6 = when (u(ie, cursor + 1) and 0x03) { 0 -> 20; 1 -> 40; 2 -> 80; else -> 160 }
                }
            }
            true
        }
        if (band == "6 GHz") return he6
        val v = vht
        return if (v != null && v > (width ?: 0)) v else width
    }

    /** Security label from the privacy capability plus RSN (48) / WPA (221) elements and their AKM suites (F17). */
    fun parseSecurity(ie: ByteArray, privacy: Boolean): String {
        val akms = HashSet<Int>()
        var hasRsn = false; var hasWpa = false
        walk(ie) { id, body, len ->
            if (id == 48 && len >= 2) {
                hasRsn = true
                var c = body + 2 + 4
                if (c + 2 <= body + len) {
                    val pairwise = u(ie, c) or (u(ie, c + 1) shl 8)
                    c += 2 + 4 * pairwise
                    if (c + 2 <= body + len) {
                        val akmCount = u(ie, c) or (u(ie, c + 1) shl 8)
                        c += 2
                        var i = 0
                        while (i < akmCount && c + 4 <= body + len) {
                            if (u(ie, c) == 0x00 && u(ie, c + 1) == 0x0F && u(ie, c + 2) == 0xAC) akms.add(u(ie, c + 3))
                            i++; c += 4
                        }
                    }
                }
            } else if (id == 221 && len >= 4 && u(ie, body) == 0x00 && u(ie, body + 1) == 0x50 && u(ie, body + 2) == 0xF2 && u(ie, body + 3) == 0x01) hasWpa = true
            true
        }
        return label(akms, hasRsn, hasWpa, privacy)
    }

    private fun label(akms: Set<Int>, hasRsn: Boolean, hasWpa: Boolean, privacy: Boolean): String {
        val sae = akms.any { it in setOf(8, 9, 24, 25) }
        val psk = akms.any { it in setOf(2, 4, 6) }
        val dot1x = akms.any { it in setOf(1, 3, 5, 13) }
        return when {
            18 in akms -> "OWE (Enhanced Open)"
            !privacy && !hasRsn && !hasWpa -> "Open"
            12 in akms || 11 in akms -> "WPA3-Enterprise-192"
            sae && psk -> "WPA2/WPA3-Personal"
            sae -> "WPA3-Personal"
            dot1x -> if (hasRsn) "WPA2-Enterprise" else "WPA-Enterprise"
            psk -> "WPA2-Personal"
            hasRsn -> "WPA2 (unknown AKM)"
            hasWpa -> "WPA-Personal"
            privacy -> "WEP"
            else -> "Open"
        }
    }

    /**
     * Fallback for Android 8–10 (no information elements): ScanResult.capabilities, e.g. "[WPA2-PSK-CCMP][RSN-SAE+PSK-CCMP][ESS]".
     * Mapped to the same labels.
     */
    fun securityFromCapabilities(caps: String): String {
        val c = caps.uppercase(Locale.ROOT)
        val akms = HashSet<Int>()
        if ("SAE" in c) akms.add(8)
        if ("PSK" in c) akms.add(2)
        if ("EAP" in c || "802.1X" in c) akms.add(1)
        if ("SUITE_B_192" in c || "EAP_SUITE_B" in c) akms.add(12)
        if ("OWE" in c) akms.add(18)
        val hasRsn = "RSN" in c || "WPA2" in c || "WPA3" in c
        val hasWpa = "[WPA-" in c
        val privacy = hasRsn || hasWpa || "WEP" in c
        return label(akms, hasRsn, hasWpa, privacy)
    }

    fun parseRates(ie: ByteArray): List<String> {
        val rates = ArrayList<String>()
        walk(ie) { id, body, len ->
            if (id == 1 || id == 50) for (i in 0 until len) rates.add(String.format(Locale.ROOT, "%.1f", (u(ie, body + i) and 0x7F) * 0.5))
            true
        }
        return rates
    }

    /** Android ScanResult.getWifiStandard() → the 802.11 names used on Windows. */
    fun standardName(wifiStandard: Int): String? = when (wifiStandard) {
        1 -> "802.11a/b/g"   // WIFI_STANDARD_LEGACY
        4 -> "802.11n"
        5 -> "802.11ac"
        6 -> "802.11ax"
        7 -> "802.11ad"
        8 -> "802.11be"
        else -> null
    }

    private val vendorMap = mapOf(
        "00:00:0C" to "Cisco Systems", "00:03:7F" to "Atheros", "00:0D:97" to "Intel", "00:14:22" to "Dell", "00:15:6D" to "Ubiquiti Networks",
        "00:16:6C" to "Intel", "00:17:88" to "Philips Lighting", "00:1A:11" to "Google", "00:1C:C2" to "Hewlett Packard", "00:24:B2" to "Apple",
        "00:26:86" to "Intel", "00:27:0D" to "Cisco Systems", "00:50:F2" to "Microsoft", "04:18:D6" to "Ubiquiti Networks", "18:E8:29" to "Netgear",
        "24:5A:4C" to "TP-Link", "3C:A8:2A" to "HP", "50:D4:F7" to "TP-Link", "70:8B:CD" to "ASUSTek", "74:83:C2" to "Apple", "8C:5A:25" to "Cisco Systems",
        "A0:04:60" to "ASUSTek", "A0:B1:C2" to "HPE", "C0:25:E9" to "TP-Link", "C8:D7:19" to "Cisco Systems", "D8:EC:5E" to "Cisco Systems", "E8:9F:80" to "TP-Link",
    )

    fun resolveVendor(bssid: String): String =
        if (bssid.length >= 8) vendorMap[bssid.substring(0, 8).replace('-', ':').uppercase(Locale.ROOT)] ?: "Unknown Vendor" else "Unknown Vendor"

    /** Link quality % from RSSI, the Windows mapping (2 × (RSSI + 100), clamped). */
    fun qualityFromRssi(rssi: Int) = (2 * (rssi + 100)).coerceIn(0, 100)
}
