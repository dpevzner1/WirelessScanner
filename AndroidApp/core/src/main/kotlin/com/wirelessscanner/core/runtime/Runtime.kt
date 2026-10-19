package com.wirelessscanner.core.runtime

import com.wirelessscanner.core.domain.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.Closeable
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.abs

/**
 * Port of OrderedPersistenceWriter.cs (DR-0003, F02/F03): single-consumer FIFO queue; a parent row enqueued before its
 * children is always written first, and writers never block the UI or the scan loop.
 */
class OrderedPersistenceWriter(private val logger: AppLogger, scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)) {
    private class WorkItem(val name: String, val work: suspend () -> Unit, val done: CompletableDeferred<Unit>?)
    private val channel = Channel<WorkItem>(Channel.UNLIMITED)
    private val failuresCount = AtomicLong()
    private val completedCount = AtomicLong()
    val failures get() = failuresCount.get()
    val completed get() = completedCount.get()

    private val consumer = scope.launch {
        for (item in channel) {
            try { item.work(); completedCount.incrementAndGet() }
            catch (e: CancellationException) { throw e }
            catch (e: Exception) { failuresCount.incrementAndGet(); logger.error("Persistence work '${item.name}' failed: ${e.message}", e) }
            finally { item.done?.complete(Unit) }
        }
    }

    fun enqueue(name: String, work: suspend () -> Unit): Boolean = channel.trySend(WorkItem(name, work, null)).isSuccess

    suspend fun flush() {
        val d = CompletableDeferred<Unit>()
        if (!channel.trySend(WorkItem("flush", {}, d)).isSuccess) return
        d.await()
    }

    suspend fun close() { channel.close(); withTimeoutOrNull(10_000) { consumer.join() } }
}

data class ScheduledCycle(val sequence: Long, val adapterId: String, val result: ScanCycleResult)

/**
 * Port of ScanScheduler.cs (DR-0011, F18): the single owner of Wi-Fi acquisition. At most one scan in flight, concurrent
 * requests share it, and the loop runs at the shortest interval any consumer requested.
 * Android: the provider reports throttled scans as Stale; the scheduler itself is platform-neutral.
 */
class ScanScheduler(
    private val provider: NetworkAdapterProvider,
    private val logger: AppLogger = NullLogger,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default),
    private val autoScan: Boolean = true,
) {
    private val lock = Any()
    private val cadences = ArrayList<CadenceRequest>()
    private var inflight: Deferred<ScheduledCycle?>? = null
    @Volatile private var adapter: String? = null
    private var loop: Job? = null
    private var wake = CompletableDeferred<Unit>()
    private val sequence = AtomicLong()
    private val providerCallCount = AtomicLong()
    private val _cycles = MutableSharedFlow<ScheduledCycle>(extraBufferCapacity = 64)

    val cycles: SharedFlow<ScheduledCycle> = _cycles
    val adapterId get() = adapter
    val isRunning get() = loop?.isActive == true
    var scanTimeoutMs: Long = 6000
    val cyclesCompleted get() = sequence.get()
    val providerCalls get() = providerCallCount.get()
    private val listeners = ConcurrentHashMap.newKeySet<(ScheduledCycle) -> Unit>()

    /** Synchronous subscription (called on the scan coroutine), mirroring the C# CycleCompleted event. */
    fun addCycleListener(l: (ScheduledCycle) -> Unit) { listeners.add(l) }
    fun removeCycleListener(l: (ScheduledCycle) -> Unit) { listeners.remove(l) }

    inner class CadenceRequest(val intervalMs: Long) : Closeable {
        override fun close() { synchronized(lock) { cadences.remove(this) }; wakeUp() }
    }

    fun setAdapter(id: String?) {
        synchronized(lock) { if (adapter == id) return; adapter = id }
        wakeUp(); ensureLoop()
    }

    fun requestCadence(intervalMs: Long): Closeable {
        require(intervalMs > 0) { "interval" }
        val r = CadenceRequest(intervalMs)
        synchronized(lock) { cadences.add(r) }
        wakeUp(); ensureLoop()
        return r
    }

    val effectiveIntervalMs: Long? get() = synchronized(lock) { cadences.minOfOrNull { it.intervalMs } }

    suspend fun scanNow(): ScheduledCycle? {
        val d = synchronized(lock) {
            val id = adapter ?: return null
            inflight ?: scope.async { runScan(id) }.also { inflight = it }
        }
        return d.await()
    }

    private suspend fun runScan(adapterId: String): ScheduledCycle? {
        try {
            yield()
            providerCallCount.incrementAndGet()
            val result = try { provider.scan(adapterId, scanTimeoutMs) }
            catch (e: CancellationException) { return null }
            catch (e: Exception) {
                logger.error("Scan failed: ${e.message}", e)
                ScanCycleResult.failure(Instant.now(), "provider_exception", e.message ?: "error")
            }
            val cycle = ScheduledCycle(sequence.incrementAndGet(), adapterId, result)
            for (l in listeners) try { l(cycle) } catch (e: Exception) { logger.error("Scan subscriber failed: ${e.message}", e) }
            _cycles.tryEmit(cycle)
            return cycle
        } finally {
            synchronized(lock) { inflight = null }
        }
    }

    private fun wakeUp() {
        val old = synchronized(lock) { val o = wake; wake = CompletableDeferred(); o }
        old.complete(Unit)
    }

    private fun ensureLoop() {
        synchronized(lock) {
            if (!autoScan || loop?.isActive == true || !scope.isActive) return
            loop = scope.launch { loopBody() }
        }
    }

    private suspend fun loopBody() {
        val t0 = System.nanoTime()
        fun now() = (System.nanoTime() - t0) / 1_000_000
        while (currentCoroutineContext().isActive) {
            val iv = effectiveIntervalMs
            val a = adapter
            val w = synchronized(lock) { wake }
            if (iv == null || a == null) { w.await(); continue }
            val started = now()
            scanNow()
            val wait = started + iv - now()
            if (wait > 0) withTimeoutOrNull(wait) { w.await() }
        }
    }

    fun dispose() { scope.cancel() }
}

/** Port of SessionManager.cs: capture sessions, ordered persistence, scan-to-scan RSSI variation. */
class SessionManager(private val repository: SessionRepository, private val logger: AppLogger, private val writer: OrderedPersistenceWriter) {
    private val lastRssi = ConcurrentHashMap<String, Int>()
    var activeSession: CaptureSession? = null
        private set
    var activeSurveyPoint: String = "Default"
        set(v) { field = if (v.isBlank()) "Default" else v.trim() }
    private val stateListeners = ArrayList<(CaptureSession) -> Unit>()
    private val sampleListeners = ArrayList<(TelemetrySample) -> Unit>()
    fun addStateListener(l: (CaptureSession) -> Unit) { stateListeners.add(l) }
    fun addSampleListener(l: (TelemetrySample) -> Unit) { sampleListeners.add(l) }

    fun startSession(name: String, scope: String, facility: String, device: NetworkInterfaceInfo, mode: CaptureSessionMode): CaptureSession {
        val a = activeSession
        check(a == null || (a.status != CaptureSessionStatus.Active && a.status != CaptureSessionStatus.Paused)) { "A session is already running. Stop it before starting a new one." }
        lastRssi.clear()
        val now = Instant.now()
        val local = java.time.LocalDateTime.now()
        val session = CaptureSession(
            UUID.randomUUID().toString(),
            if (name.isBlank()) "Session_" + local.format(java.time.format.DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss")) else name.trim(),
            if (scope.isBlank()) "All SSIDs" else scope.trim(),
            if (facility.isBlank()) "General Facility" else facility.trim(),
            device.id, device.name, now, null, mode, CaptureSessionStatus.Active,
        )
        activeSession = session
        writer.enqueue("save-session ${session.sessionId}") { repository.saveSession(session) }
        stateListeners.forEach { it(session) }
        return session
    }

    fun pauseSession() = transition(CaptureSessionStatus.Active, CaptureSessionStatus.Paused, null)
    fun resumeSession() = transition(CaptureSessionStatus.Paused, CaptureSessionStatus.Active, null)
    fun stopSession() {
        val s = activeSession ?: return
        if (s.status == CaptureSessionStatus.Completed) return
        transition(s.status, CaptureSessionStatus.Completed, Instant.now())
    }

    private fun transition(from: CaptureSessionStatus, to: CaptureSessionStatus, endTime: Instant?) {
        val s = activeSession ?: return
        if (s.status != from) return
        val updated = s.copy(status = to, endTime = endTime ?: s.endTime)
        activeSession = updated
        writer.enqueue("status ${updated.sessionId} $to") { repository.updateSessionStatus(updated.sessionId, updated.status, endTime) }
        stateListeners.forEach { it(updated) }
    }

    fun calculateJitterFor(bssid: String, rssi: Int): Double {
        if (bssid.isBlank()) return 0.0
        val last = lastRssi[bssid]
        lastRssi[bssid] = rssi
        return if (last != null) abs(rssi - last).toDouble() else 0.0
    }

    fun saveSamples(samples: List<TelemetrySample>) {
        val session = activeSession
        if (session == null || session.status != CaptureSessionStatus.Active || samples.isEmpty()) return
        val batch = samples.toList()
        writer.enqueue("samples ${session.sessionId} x${batch.size}") {
            repository.saveSamples(batch, session.sessionId)
            batch.forEach { s -> sampleListeners.forEach { it(s) } }
        }
    }

    suspend fun flush() = writer.flush()
}

object StatisticsCalculator {
    fun calculate(rssi: List<Int>, jitter: List<Double>): TelemetryStats {
        if (rssi.isEmpty()) return TelemetryStats(0, 0, 0.0, 0.0, 0.0)
        val mean = rssi.average()
        val sd = kotlin.math.sqrt(rssi.sumOf { (it - mean) * (it - mean) } / rssi.size)
        return TelemetryStats(rssi.min(), rssi.max(), mean, sd, if (jitter.isNotEmpty()) jitter.average() else 0.0)
    }
}
