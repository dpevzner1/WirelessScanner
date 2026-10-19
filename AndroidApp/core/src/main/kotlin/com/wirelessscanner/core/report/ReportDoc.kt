package com.wirelessscanner.core.report

/**
 * Platform-neutral report layout (the same sections as the Windows QuestPDF documents). The Android app renders it to PDF with
 * android.graphics.pdf.PdfDocument; charts are described as data and drawn by the renderer.
 */
sealed interface Block {
    data class Heading(val text: String) : Block
    data class SubHeading(val text: String, val size: Float = 11f) : Block
    data class Paragraph(val text: String, val size: Float = 8.5f, val bold: Boolean = false, val italic: Boolean = false, val indent: Float = 0f) : Block
    data class KeyValues(val rows: List<Pair<String, String>>, val keyWidth: Float = 140f) : Block
    data class Grid(val headers: List<String>, val rows: List<List<String>>, val widths: List<Float>? = null) : Block
    data class Chart(val title: String, val heightPt: Float, val spec: ChartSpec) : Block
    data class Rule(val color: Int = 0xFFD1D5DB.toInt()) : Block
    data object PageBreak : Block
}

data class ReportDocument(
    val title: String,
    val subtitle: String,
    val metadataTitle: String,
    val author: String,
    val subject: String,
    val footer: String,
    val accent: Int,
    val blocks: List<Block>,
)

/** Chart data; the renderer owns pixels, the core owns what is drawn (identical to the Windows chart rules). */
sealed interface ChartSpec {
    /** Saturation RSSI timeline: observed series with breaks, missing ticks, drop spans, markers, threshold bands. */
    data class RssiTimeline(
        val maxSeconds: Double, val yMin: Int, val yMax: Int, val goodDbm: Int, val fairDbm: Int, val unusableDbm: Int,
        val points: List<Pair<Double, Int?>>, val missing: List<Double>, val drops: List<Pair<Double, Double>>, val markers: List<Pair<Double, String>>,
    ) : ChartSpec
    /** 1 dB bins of observed RSSI. */
    data class Histogram(val bins: List<Pair<Int, Int>>, val total: Int) : ChartSpec
    /** Connection test: RTT series per target (ms) with failure ticks, time axis = active seconds. */
    data class ProbeTimeline(val maxSeconds: Double, val yMax: Double, val series: List<List<Pair<Double, Double>>>, val failures: List<Double>) : ChartSpec
    /** Connection test: Wi-Fi signal with roam lines. */
    data class SignalTimeline(val maxSeconds: Double, val yMin: Double, val yMax: Double, val points: List<Pair<Double, Int>>, val roams: List<Double>) : ChartSpec
    /** Capture session: RSSI over time for the top APs (legend colours by mean tier). */
    data class SessionTimeline(val startMs: Long, val endMs: Long, val series: List<Triple<String, Int, List<Pair<Long, Int>>>>) : ChartSpec
}
