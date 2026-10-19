package com.wirelessscanner.core.spectrum

import com.wirelessscanner.core.domain.AccessPoint
import com.wirelessscanner.core.domain.ChannelMath

/** Port of WirelessScanner.Presentation/SpectrumLayout.cs (DR-0010): pure spectrum geometry, channel load and waterfall bins. */
object SpectrumLayout {
    data class BandAxis(
        val band: String, val lowMhz: Double, val highMhz: Double, val tickChannels: List<Int>, val channels: List<Int>,
        val dfsLowMhz: Double?, val dfsHighMhz: Double?, val preferredChannels: List<Int>,
    )

    val BAND_24 = BandAxis("2.4 GHz", 2400.0, 2485.0, (1..14).toList(), (1..13).toList(), null, null, listOf(1, 6, 11))
    val BAND_5 = BandAxis("5 GHz", 5150.0, 5850.0, listOf(36, 48, 64, 100, 116, 132, 149, 165),
        listOf(36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165),
        5250.0, 5730.0, listOf(36, 40, 44, 48, 149, 153, 157, 161, 165))
    val BAND_6 = BandAxis("6 GHz", 5925.0, 7125.0, listOf(1, 33, 65, 97, 129, 161, 193, 225),
        (0 until 59).map { 1 + 4 * it }, null, null, (0 until 15).map { 5 + 16 * it })

    fun axisFor(band: String) = when (band) { "2.4 GHz" -> BAND_24; "5 GHz" -> BAND_5; "6 GHz" -> BAND_6; else -> null }

    fun channelFrequencyMhz(band: String, channel: Int) = ChannelMath.channelFrequencyMhz(band, channel)
    fun blockCenterMhz(band: String, primaryChannel: Int, widthMhz: Int?) = ChannelMath.blockCenterMhz(band, primaryChannel, widthMhz)
    fun occupancy(ap: AccessPoint) = ChannelMath.occupancy(ap)

    /** Relative load per channel: sum of (RSSI + 95) / 45 (clamped 0..1) over APs overlapping the channel's 20 MHz slot. */
    fun channelLoad(axis: BandAxis, aps: Iterable<AccessPoint>): Map<Int, Double> {
        val list = aps.filter { it.band == axis.band && it.channel > 0 }
        val load = LinkedHashMap<Int, Double>()
        for (ch in axis.channels) {
            val f = channelFrequencyMhz(axis.band, ch)
            val lo = f - 10; val hi = f + 10
            load[ch] = list.filter { val (l, h) = occupancy(it); l < hi && h > lo }.sumOf { ((it.rssi + 95) / 45.0).coerceIn(0.0, 1.0) }
        }
        return load
    }

    /** Least-loaded preferred channel (2.4 GHz: 1/6/11; 5 GHz non-DFS first). Ties go to the lower channel. */
    fun leastBusyChannel(axis: BandAxis, load: Map<Int, Double>): Int =
        axis.preferredChannels.filter { load.containsKey(it) }.sortedWith(compareBy<Int> { load[it]!! }.thenBy { it }).firstOrNull() ?: axis.channels[0]

    fun strongestPerChannel(axis: BandAxis, aps: Iterable<AccessPoint>): Map<Int, Int?> {
        val list = aps.filter { it.band == axis.band && it.channel > 0 }
        val result = LinkedHashMap<Int, Int?>()
        for (ch in axis.channels) {
            val f = channelFrequencyMhz(axis.band, ch)
            result[ch] = list.filter { val (l, h) = occupancy(it); l < f + 10 && h > f - 10 }.maxOfOrNull { it.rssi }
        }
        return result
    }

    /** Waterfall intensity 0..1 (−90 dBm → 0, −45 dBm → 1). */
    fun intensity(rssi: Int) = ((rssi + 90) / 45.0).coerceIn(0.0, 1.0)

    fun isDfs(axis: BandAxis, mhz: Double) = axis.dfsLowMhz != null && axis.dfsHighMhz != null && mhz >= axis.dfsLowMhz && mhz <= axis.dfsHighMhz
}
