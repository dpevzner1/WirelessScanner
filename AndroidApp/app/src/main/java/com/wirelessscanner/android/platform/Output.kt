package com.wirelessscanner.android.platform

import android.annotation.SuppressLint
import android.app.*
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.*
import android.graphics.pdf.PdfDocument
import android.location.Location
import android.location.LocationManager
import android.net.Uri
import android.os.Build
import android.os.CancellationSignal
import android.os.IBinder
import android.os.PowerManager
import android.text.Layout
import android.text.StaticLayout
import android.text.TextPaint
import androidx.core.content.FileProvider
import com.wirelessscanner.android.MainActivity
import com.wirelessscanner.android.R
import com.wirelessscanner.core.domain.GeoLocationProvider
import com.wirelessscanner.core.domain.GeoTag
import com.wirelessscanner.core.report.*
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import java.io.ByteArrayOutputStream
import java.io.File
import java.time.Instant
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.coroutines.resume
import kotlin.math.max
import kotlin.math.min

/** Current position from the platform LocationManager (no Play services). Windows: WindowsGeoLocationProvider. */
class AndroidGeo(private val context: Context) : GeoLocationProvider {
    @SuppressLint("MissingPermission")
    override suspend fun current(timeoutMs: Long): Pair<GeoTag?, String?> {
        if (!Permissions.hasLocation(context)) return null to "Location permission is not granted."
        val lm = context.getSystemService(Context.LOCATION_SERVICE) as LocationManager
        if (!lm.isLocationEnabled) return null to "Location is off. Turn it on in the quick settings."
        val provider = listOf(LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER, LocationManager.FUSED_PROVIDER).firstOrNull { runCatching { lm.isProviderEnabled(it) }.getOrDefault(false) }
            ?: return null to "No location provider is available."
        val loc: Location? = withTimeoutOrNull(timeoutMs) {
            suspendCancellableCoroutine { cont ->
                val signal = CancellationSignal()
                cont.invokeOnCancellation { signal.cancel() }
                lm.getCurrentLocation(provider, signal, context.mainExecutor) { cont.resume(it) }
            }
        } ?: lm.getLastKnownLocation(provider)
        loc ?: return null to "No position within ${timeoutMs / 1000} s. Move near a window or enter coordinates."
        return GeoTag(loc.latitude, loc.longitude, if (loc.hasAccuracy()) loc.accuracy.toDouble() else null, "android-location", Instant.ofEpochMilli(loc.time)) to null
    }
}

/**
 * Renders a core [ReportDocument] to PDF with android.graphics.pdf.PdfDocument: A4, 1.4 cm margins, the same sections, tables and
 * charts as the Windows QuestPDF reports. Two passes: layout into pages, then draw with "Page n of N".
 */
object PdfRenderer {
    private const val W = 595f; private const val H = 842f; private const val M = 40f
    private const val CW = W - 2 * M
    private const val TOP = M + 44f; private const val BOTTOM = H - M - 18f
    private val head = Color.rgb(0x1F, 0x29, 0x37)
    private val grid = Color.rgb(0xE5, 0xE7, 0xEB)

    private fun paint(size: Float, bold: Boolean = false, italic: Boolean = false, color: Int = Color.rgb(0x11, 0x18, 0x27)) = TextPaint(Paint.ANTI_ALIAS_FLAG).apply {
        textSize = size; this.color = color
        typeface = Typeface.create(Typeface.SANS_SERIF, when { bold && italic -> Typeface.BOLD_ITALIC; bold -> Typeface.BOLD; italic -> Typeface.ITALIC; else -> Typeface.NORMAL })
    }
    private fun layout(text: String, p: TextPaint, width: Float): StaticLayout =
        StaticLayout.Builder.obtain(text, 0, text.length, p, max(1, width.toInt())).setAlignment(Layout.Alignment.ALIGN_NORMAL).setIncludePad(false).build()

    private class Page { val ops = ArrayList<(Canvas) -> Unit>() }

    fun render(doc: ReportDocument): ByteArray {
        val pages = ArrayList<Page>()
        var page = Page().also { pages.add(it) }
        var y = TOP
        fun newPage() { page = Page().also { pages.add(it) }; y = TOP }
        fun ensure(h: Float) { if (y + h > BOTTOM && y > TOP) newPage() }
        fun text(t: String, p: TextPaint, x: Float = M, width: Float = CW, gapAfter: Float = 2f) {
            val l = layout(t, p, width)
            ensure(l.height.toFloat())
            val yy = y; page.ops.add { c -> c.save(); c.translate(x, yy); l.draw(c); c.restore() }
            y += l.height + gapAfter
        }
        fun tableRows(widths: List<Float>, header: List<String>?, rows: List<List<String>>, size: Float, firstBold: Boolean) {
            val total = widths.sum()
            val cols = widths.map { it / total * CW }
            fun rowLayouts(cells: List<String>, p: (Int) -> TextPaint) = cells.mapIndexed { i, s -> layout(s.ifEmpty { "" }, p(i), cols[i] - 4) }
            val headPaint = paint(size, bold = true, color = Color.WHITE)
            val headLayouts = header?.let { rowLayouts(it) { headPaint } }
            val headH = headLayouts?.let { ls -> ls.maxOf { it.height } + 4f } ?: 0f
            fun drawHeader() {
                if (headLayouts == null) return
                val yy = y
                page.ops.add { c ->
                    c.drawRect(M, yy, M + CW, yy + headH, Paint().apply { color = head })
                    var x = M
                    headLayouts.forEachIndexed { i, l -> c.save(); c.translate(x + 2, yy + 2); l.draw(c); c.restore(); x += cols[i] }
                }
                y += headH
            }
            ensure(headH + 14f); drawHeader()
            val body = paint(size)
            val bold = paint(size, bold = true)
            for (r in rows) {
                val ls = rowLayouts(r) { i -> if (firstBold && i == 0) bold else body }
                val h = ls.maxOf { it.height } + 4f
                if (y + h > BOTTOM) { newPage(); drawHeader() }
                val yy = y
                page.ops.add { c ->
                    var x = M
                    ls.forEachIndexed { i, l -> c.save(); c.translate(x + 2, yy + 2); l.draw(c); c.restore(); x += cols[i] }
                    c.drawLine(M, yy + h, M + CW, yy + h, Paint().apply { color = grid; strokeWidth = 0.5f })
                }
                y += h
            }
            y += 4f
        }

        for (b in doc.blocks) when (b) {
            is Block.Heading -> { ensure(40f); y += 8f; text(b.text, paint(12f, bold = true, color = doc.accent), gapAfter = 4f) }
            is Block.SubHeading -> { y += 4f; text(b.text, paint(b.size, bold = true, color = head)) }
            is Block.Paragraph -> text(b.text, paint(b.size, b.bold, b.italic), M + b.indent, CW - b.indent)
            is Block.KeyValues -> tableRows(listOf(b.keyWidth, CW - b.keyWidth), null, b.rows.map { listOf(it.first, it.second.ifBlank { "—" }) }, 8.5f, true)
            is Block.Grid -> tableRows(b.widths ?: b.headers.map { 1f }, b.headers, b.rows, 7.5f, false)
            is Block.Chart -> {
                val titleL = if (b.title.isNotEmpty()) layout(b.title, paint(7.5f, italic = true), CW) else null
                val h = (titleL?.height ?: 0) + b.heightPt + 6f
                ensure(h)
                val yy = y; val th = (titleL?.height ?: 0).toFloat()
                page.ops.add { c ->
                    titleL?.let { c.save(); c.translate(M, yy); it.draw(c); c.restore() }
                    c.save(); c.translate(M, yy + th + 2); Charts.draw(c, b.spec, CW, b.heightPt); c.restore()
                }
                y += h
            }
            is Block.Rule -> { ensure(10f); val yy = y + 4; page.ops.add { c -> c.drawLine(M, yy, M + CW, yy, Paint().apply { color = b.color; strokeWidth = 0.6f }) }; y += 10f }
            Block.PageBreak -> newPage()
        }

        val pdf = PdfDocument()
        val titleP = paint(16f, bold = true, color = doc.accent); val subP = paint(9f, color = Color.rgb(0x4B, 0x55, 0x63))
        val brandP = paint(11f, bold = true, color = head).apply { textAlign = Paint.Align.RIGHT }
        val footP = paint(7f, color = Color.rgb(0x6B, 0x72, 0x80)).apply { textAlign = Paint.Align.CENTER }
        pages.forEachIndexed { i, pg ->
            val p = pdf.startPage(PdfDocument.PageInfo.Builder(W.toInt(), H.toInt(), i + 1).create())
            val c = p.canvas
            c.drawColor(Color.WHITE)
            c.drawText(doc.title, M, M + 14, titleP)
            val sub = layout(doc.subtitle, subP, CW - 120); c.save(); c.translate(M, M + 20); sub.draw(c); c.restore()
            c.drawText("WirelessScanner", W - M, M + 14, brandP)
            c.drawText("${doc.footer} | Page ${i + 1} of ${pages.size}", W / 2, H - M + 4, footP)
            pg.ops.forEach { it(c) }
            pdf.finishPage(p)
        }
        val out = ByteArrayOutputStream()
        pdf.writeTo(out); pdf.close()
        return out.toByteArray()
    }
}

/** Chart drawing shared by the PDF (white background, print colours). Values are also tabulated in the reports. */
object Charts {
    private fun stroke(c: Int, w: Float, dashed: Boolean = false) = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = c; strokeWidth = w; style = Paint.Style.STROKE; if (dashed) pathEffect = DashPathEffect(floatArrayOf(3f, 3f), 0f) }
    private fun fill(c: Int) = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = c; style = Paint.Style.FILL }
    private val ink = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(0x37, 0x41, 0x51); textSize = 7.5f }
    private fun hms(s: Double) = ReportText.dur(s)

    fun draw(c: Canvas, spec: ChartSpec, w: Float, h: Float) {
        when (spec) {
            is ChartSpec.RssiTimeline -> timeline(c, spec, w, h)
            is ChartSpec.Histogram -> histogram(c, spec, w, h)
            is ChartSpec.ProbeTimeline -> probes(c, spec, w, h)
            is ChartSpec.SignalTimeline -> signal(c, spec, w, h)
            is ChartSpec.SessionTimeline -> session(c, spec, w, h)
        }
    }

    private fun timeline(c: Canvas, s: ChartSpec.RssiTimeline, w: Float, h: Float) {
        val left = 40f; val top = 6f; val pw = w - left - 6f; val ph = h - top - 20f
        fun x(t: Double) = left + (t / s.maxSeconds).toFloat() * pw
        fun y(d: Double) = top + ((s.yMax - d) / (s.yMax - s.yMin)).toFloat() * ph
        c.drawRect(left, y(s.goodDbm.toDouble()), left + pw, y(s.fairDbm.toDouble()), fill(Color.argb(0x18, 0xF5, 0x9E, 0x0B)))
        c.drawRect(left, y(s.fairDbm.toDouble()), left + pw, y(s.unusableDbm.toDouble()), fill(Color.argb(0x22, 0xF9, 0x73, 0x16)))
        c.drawRect(left, y(s.unusableDbm.toDouble()), left + pw, top + ph, fill(Color.argb(0x22, 0xDC, 0x26, 0x26)))
        var db = (s.yMax / 10) * 10
        while (db >= s.yMin) { c.drawLine(left, y(db.toDouble()), left + pw, y(db.toDouble()), stroke(Color.rgb(0xE5, 0xE7, 0xEB), 0.5f)); if (y(db.toDouble()) >= top + 3) c.drawText("$db dBm", 2f, y(db.toDouble()) + 2.5f, ink); db -= 10 }
        for (i in 0..4) c.drawText(hms(s.maxSeconds * i / 4), x(s.maxSeconds * i / 4) - (if (i == 4) 30 else if (i == 0) 0 else 15), h - 4, ink)
        s.drops.forEach { (a, b) -> c.drawRect(x(a), top, max(x(a) + 1.5f, x(b)), top + ph, fill(Color.argb(0x30, 0xEA, 0x58, 0x0C))) }
        s.missing.forEach { t -> c.drawLine(x(t), top + ph - 6, x(t), top + ph, stroke(Color.rgb(0xDC, 0x26, 0x26), 1.2f)) }
        s.markers.forEach { (t, label) -> c.drawLine(x(t), top, x(t), top + ph, stroke(Color.rgb(0x37, 0x41, 0x51), 0.8f, true)); c.drawText(if (label.length > 18) label.take(18) + "…" else label, x(t) + 2, top + 8, ink) }
        var prev: PointF? = null
        val line = stroke(Color.rgb(0x1D, 0x4E, 0xD8), 1.2f)
        for ((t, r) in s.points) {
            if (r == null) { prev = null; continue }
            val pt = PointF(x(t), y(r.toDouble()))
            prev?.let { c.drawLine(it.x, it.y, pt.x, pt.y, line) } ?: c.drawCircle(pt.x, pt.y, 1f, fill(line.color))
            prev = pt
        }
        c.drawRect(left, top, left + pw, top + ph, stroke(Color.rgb(0x9C, 0xA3, 0xAF), 0.6f))
    }

    private fun histogram(c: Canvas, s: ChartSpec.Histogram, w: Float, h: Float) {
        val left = 40f; val top = 6f; val pw = w - left - 6f; val ph = h - top - 18f
        if (s.bins.isEmpty()) { c.drawText("No observed samples", w / 2 - 35, h / 2, ink); return }
        val maxN = s.bins.maxOf { it.second }; val bw = pw / s.bins.size
        s.bins.forEachIndexed { i, (v, n) ->
            val bh = ph * n / maxN
            val col = if (v >= -67) Color.rgb(0x05, 0x96, 0x69) else if (v >= -70) Color.rgb(0xD9, 0x77, 0x06) else if (v >= -80) Color.rgb(0xEA, 0x58, 0x0C) else Color.rgb(0xDC, 0x26, 0x26)
            c.drawRect(left + i * bw + 0.5f, top + ph - bh, left + i * bw + max(0.8f, bw - 1), top + ph, fill(col))
        }
        val step = max(1, Math.ceil(s.bins.size / 12.0).toInt())
        for (i in s.bins.indices step step) c.drawText("${s.bins[i].first}", left + i * bw + bw / 2 - 6, h - 5, ink)
        c.drawText("$maxN", 4f, top + 7, ink); c.drawText("0", 4f, top + ph, ink); c.drawText("n = ${s.total}", left + pw - 40, top + 8, ink)
    }

    private fun axes(c: Canvas, w: Float, h: Float, xMax: Double, yMin: Double, yMax: Double, unit: String): Pair<(Double) -> Float, (Double) -> Float> {
        val left = 36f; val top = 4f; val pw = w - left - 6f; val ph = h - top - 14f
        val span = max(1e-6, yMax - yMin)
        val x = { s: Double -> left + (if (xMax <= 0) 0.0 else s / xMax).toFloat() * pw }
        val y = { v: Double -> top + ph - ((v - yMin) / span).toFloat() * ph }
        val ax = stroke(Color.rgb(0x9C, 0xA3, 0xAF), 0.5f)
        c.drawLine(left, top, left, top + ph, ax); c.drawLine(left, top + ph, left + pw, top + ph, ax)
        for (i in 0..2) { val v = yMin + span * i / 2; c.drawText("${ReportText.fmt(v, "0")} $unit", 2f, y(v) + 2, ink) }
        for (i in 0..4) c.drawText(hms(xMax * i / 4), x(xMax * i / 4) - 12, h - 3, ink)
        return x to y
    }

    private fun probes(c: Canvas, s: ChartSpec.ProbeTimeline, w: Float, h: Float) {
        val (x, y) = axes(c, w, h, s.maxSeconds, 0.0, s.yMax, "ms")
        val line = stroke(Color.rgb(0x1D, 0x4E, 0xD8), 0.7f)
        s.series.forEach { pts -> val p = Path(); pts.forEachIndexed { i, (t, v) -> if (i == 0) p.moveTo(x(t), y(v)) else p.lineTo(x(t), y(v)) }; c.drawPath(p, line) }
        val fail = stroke(Color.rgb(0xDC, 0x26, 0x26), 0.8f)
        s.failures.forEach { t -> c.drawLine(x(t), y(s.yMax), x(t), y(s.yMax * 0.85), fail) }
    }

    private fun signal(c: Canvas, s: ChartSpec.SignalTimeline, w: Float, h: Float) {
        val (x, y) = axes(c, w, h, s.maxSeconds, s.yMin, s.yMax, "dBm")
        val p = Path(); s.points.forEachIndexed { i, (t, v) -> if (i == 0) p.moveTo(x(t), y(v.toDouble())) else p.lineTo(x(t), y(v.toDouble())) }
        c.drawPath(p, stroke(Color.rgb(0x05, 0x96, 0x69), 0.8f))
        s.roams.forEach { t -> c.drawLine(x(t), y(s.yMax), x(t), y(s.yMin), stroke(Color.rgb(0x6B, 0x72, 0x80), 0.5f)) }
    }

    private fun session(c: Canvas, s: ChartSpec.SessionTimeline, w: Float, h: Float) {
        val left = 40f; val top = 6f; val pw = w - left - 6f; val ph = h - top - 40f
        val span = max(1L, s.endMs - s.startMs).toDouble()
        fun x(t: Long) = left + ((t - s.startMs) / span).toFloat() * pw
        fun y(r: Int) = top + ((-30 - r) / 70f) * ph
        for (db in listOf(-30, -50, -70, -90)) { c.drawLine(left, y(db), left + pw, y(db), stroke(Color.rgb(0xE5, 0xE7, 0xEB), 0.5f)); c.drawText("$db", 8f, y(db) + 3, ink) }
        s.series.forEachIndexed { k, (label, color, pts) ->
            val p = Path(); pts.forEachIndexed { i, (t, r) -> if (i == 0) p.moveTo(x(t), y(r)) else p.lineTo(x(t), y(r)) }
            c.drawPath(p, stroke(color, 1.2f))
            c.drawRect(left + (k % 2) * pw / 2, top + ph + 8 + (k / 2) * 10, left + (k % 2) * pw / 2 + 7, top + ph + 15 + (k / 2) * 10, fill(color))
            c.drawText(label.take(48), left + (k % 2) * pw / 2 + 10, top + ph + 15 + (k / 2) * 10, ink)
        }
    }
}

/**
 * Exports (DR-0015): files are written to the app cache and offered through
 *  - Share or send: the Android Sharesheet (email, chat, Drive, OneDrive, …) via FileProvider, no cloud SDKs;
 *  - Save a copy: ACTION_CREATE_DOCUMENT (any installed document provider). A CSV bundle is saved as one ZIP.
 */
object Exporter {
    fun dir(context: Context, name: String): File = File(context.cacheDir, "exports/$name").also { it.deleteRecursively(); it.mkdirs() }

    fun write(context: Context, folder: String, files: List<ExportFile>): List<File> {
        val d = dir(context, folder)
        return files.map { f -> File(d, f.name).also { it.writeBytes(f.bytes) } }
    }

    fun shareIntent(context: Context, files: List<File>, subject: String): Intent {
        val uris = ArrayList(files.map { FileProvider.getUriForFile(context, "${context.packageName}.files", it) })
        val type = files.map { mime(it.name) }.distinct().singleOrNull() ?: "*/*"
        val send = if (uris.size == 1) Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        else Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
        send.type = type
        send.putExtra(Intent.EXTRA_SUBJECT, subject)
        send.putExtra(Intent.EXTRA_TEXT, "$subject — generated by WirelessScanner.")
        send.clipData = ClipData.newRawUri(subject, uris[0]).also { cd -> uris.drop(1).forEach { cd.addItem(ClipData.Item(it)) } }
        send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        return Intent.createChooser(send, "Share or send: $subject")
    }

    /** One file for "Save a copy": the file itself, or a ZIP when there are several (CSV bundle). */
    fun saveable(name: String, files: List<ExportFile>): ExportFile {
        if (files.size == 1) return files[0]
        val out = ByteArrayOutputStream()
        ZipOutputStream(out).use { z -> files.forEach { f -> z.putNextEntry(ZipEntry(f.name)); z.write(f.bytes); z.closeEntry() } }
        return ExportFile("$name.zip", "application/zip", out.toByteArray())
    }

    fun writeTo(context: Context, uri: Uri, bytes: ByteArray) {
        context.contentResolver.openOutputStream(uri, "wt")?.use { it.write(bytes) } ?: throw java.io.IOException("The selected location cannot be written.")
    }

    fun mime(name: String) = when {
        name.endsWith(".pdf") -> "application/pdf"; name.endsWith(".csv") -> "text/csv"; name.endsWith(".json") -> "application/json"
        name.endsWith(".zip") -> "application/zip"; name.endsWith(".txt") || name.endsWith(".log") -> "text/plain"; else -> "application/octet-stream"
    }
}

/**
 * Keeps long captures and tests running with the screen off (foreground service, type location, partial wake lock).
 * The measurement logic lives in AppGraph; this service only keeps the process in the foreground and shows progress.
 */
class TestService : Service() {
    private var wake: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelf(); return START_NOT_STICKY }
        val text = intent?.getStringExtra(EXTRA_TEXT) ?: "Measuring"
        val n = notification(this, text)
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION) else startForeground(NOTIFICATION_ID, n)
        if (wake == null) wake = (getSystemService(POWER_SERVICE) as PowerManager).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "WirelessScanner:test").apply { setReferenceCounted(false); acquire(26 * 3600 * 1000L) }
        return START_NOT_STICKY
    }

    override fun onDestroy() { wake?.let { if (it.isHeld) it.release() }; wake = null; super.onDestroy() }

    companion object {
        const val CHANNEL = "tests"; const val NOTIFICATION_ID = 7; const val EXTRA_TEXT = "text"; const val ACTION_STOP = "stop"

        fun notification(context: Context, text: String): Notification {
            val nm = context.getSystemService(NOTIFICATION_SERVICE) as NotificationManager
            if (nm.getNotificationChannel(CHANNEL) == null) nm.createNotificationChannel(NotificationChannel(CHANNEL, "Captures and tests", NotificationManager.IMPORTANCE_LOW))
            val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)
            return Notification.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_stat_wifi).setContentTitle("WirelessScanner").setContentText(text)
                .setOngoing(true).setContentIntent(open).setOnlyAlertOnce(true).build()
        }

        fun update(context: Context, text: String?) {
            val i = Intent(context, TestService::class.java)
            if (text == null) { context.stopService(i); return }
            i.putExtra(EXTRA_TEXT, text)
            try { context.startForegroundService(i) } catch (e: Exception) { /* app in background without permission: the notification is skipped */ }
        }
    }
}
