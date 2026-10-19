package com.wirelessscanner.core.runtime

import com.wirelessscanner.core.analytics.SaturationAnalyzer
import com.wirelessscanner.core.analytics.SaturationObservationBuilder
import com.wirelessscanner.core.analytics.SaturationTargetTracker
import com.wirelessscanner.core.domain.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import java.time.Instant

/** Stopwatch of active (unpaused) time, like System.Diagnostics.Stopwatch. */
class ActiveClock {
    private var accumulatedNs = 0L
    private var startedAt: Long? = null
    @Synchronized fun start() { if (startedAt == null) startedAt = System.nanoTime() }
    @Synchronized fun stop() { startedAt?.let { accumulatedNs += System.nanoTime() - it }; startedAt = null }
    @Synchronized fun reset() { accumulatedNs = 0; startedAt = null }
    val elapsedMs: Long @Synchronized get() = (accumulatedNs + (startedAt?.let { System.nanoTime() - it } ?: 0)) / 1_000_000
    val elapsedSeconds: Double get() = elapsedMs / 1000.0
}

/**
 * Port of SaturationTestRunner.cs (SPEC-SAT-001 §4): one fresh-scan cycle per interval from the shared scheduler, until
 * the configured ACTIVE duration elapses or the user stops it. Every cycle is persisted in order.
 */
class SaturationTestRunner(
    private val scheduler: ScanScheduler,
    private val repository: SaturationRepository,
    private val analyzer: SaturationAnalyzer,
    private val logger: AppLogger,
    private val writer: OrderedPersistenceWriter,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default),
) {
    private val lock = Any()
    private val cycles = ArrayList<SaturationCycle>()
    private val markers = ArrayList<SaturationMarker>()
    private val active = ActiveClock()
    var config: SaturationTestConfig? = null
        private set
    private var tracker: SaturationTargetTracker? = null
    private var loop: Job? = null
    private var startUtc: Instant = Instant.EPOCH
    @Volatile private var paused = false
    @Volatile private var stopRequested = false
    @Volatile private var finalReport: SaturationReport? = null
    @Volatile var status: SaturationTestStatus? = null
        private set

    private val progressListeners = ArrayList<(SaturationProgress) -> Unit>()
    private val completedListeners = ArrayList<(SaturationReport) -> Unit>()
    fun addProgressListener(l: (SaturationProgress) -> Unit) { progressListeners.add(l) }
    fun addCompletedListener(l: (SaturationReport) -> Unit) { completedListeners.add(l) }

    val isRunning get() = loop?.isActive == true
    val activeSeconds get() = active.elapsedSeconds

    fun start(cfg: SaturationTestConfig) {
        check(!isRunning) { "A saturation test is already running." }
        require(!cfg.interval.isZero && !cfg.interval.isNegative) { "Interval must be positive." }
        require(cfg.scope == SaturationScope.AllVisible || cfg.targetBssids.isNotEmpty() || cfg.targetSsids.isNotEmpty()) { "Select at least one target." }
        synchronized(lock) { cycles.clear(); markers.clear() }
        config = cfg
        tracker = SaturationTargetTracker(cfg)
        startUtc = Instant.now()
        paused = false; stopRequested = false; finalReport = null
        active.reset(); active.start()
        status = SaturationTestStatus.Running
        val start = startUtc
        writer.enqueue("saturation-test ${cfg.testId}") { repository.saveTest(cfg, start, SaturationTestStatus.Running) }
        logger.info("Saturation test '${cfg.name}' started: scope ${cfg.scope}, interval ${cfg.interval.seconds} s, duration ${cfg.duration?.toString() ?: "until stopped"}.")
        loop = scope.launch { loopBody(cfg) }
    }

    private suspend fun loopBody(cfg: SaturationTestConfig) {
        var sequence = 0L
        val incoming = Channel<ScheduledCycle>(Channel.UNLIMITED)
        val onCycle: (ScheduledCycle) -> Unit = { if (it.adapterId == cfg.interfaceId) incoming.trySend(it) }
        scheduler.scanTimeoutMs = cfg.scanTimeout.toMillis()
        if (scheduler.adapterId == null) scheduler.setAdapter(cfg.interfaceId)
        scheduler.addCycleListener(onCycle)
        val cadence = scheduler.requestCadence(cfg.interval.toMillis())
        var nextDueMs = 0L
        try {
            while (currentCoroutineContext().isActive) {
                val dMs = cfg.duration?.toMillis()
                if (!paused && dMs != null && active.elapsedMs >= dMs) break
                val waitLimit = if (dMs != null && !paused) dMs - active.elapsedMs else 1000L
                if (waitLimit <= 0) break
                val scheduled = withTimeoutOrNull(minOf(waitLimit, 1000L)) { incoming.receive() } ?: continue
                if (paused) continue
                val nowMs = active.elapsedMs
                val intervalMs = cfg.interval.toMillis()
                val toleranceMs = (scheduler.effectiveIntervalMs ?: intervalMs) / 2
                if (nowMs < nextDueMs - toleranceMs) continue
                nextDueMs += intervalMs
                if (nextDueMs < nowMs - toleranceMs) nextDueMs = nowMs + intervalMs

                val result = scheduled.result
                sequence++
                val observations = SaturationObservationBuilder.build(cfg, tracker!!, result, sequence)
                val cycle = SaturationCycle(sequence, result.completedUtc, nowMs, result.status, result.durationMs,
                    result.accessPoints.size, result.errorCode, result.errorMessage, observations)
                val (cs, ms) = synchronized(lock) { cycles.add(cycle); cycles.toList() to markers.toList() }
                writer.enqueue("saturation-cycle ${cfg.testId}#$sequence") { repository.saveCycle(cfg.testId, cycle) }
                val act = active.elapsedSeconds
                val remaining = cfg.duration?.let { maxOf(0.0, it.toMillis() / 1000.0 - act) }
                val p = SaturationProgress(cfg, status ?: SaturationTestStatus.Running, startUtc, act, remaining, cycle, cs, ms)
                progressListeners.forEach { safe { it(p) } }
            }
        } catch (e: CancellationException) {
            // stop requested
        } catch (e: Exception) {
            logger.error("Saturation test loop failed: ${e.message}", e)
            status = SaturationTestStatus.Failed
        } finally {
            scheduler.removeCycleListener(onCycle)
            cadence.close()
            active.stop()
            withContext(NonCancellable) { finish(cfg) }
        }
    }

    private suspend fun finish(cfg: SaturationTestConfig) {
        val final = when {
            status == SaturationTestStatus.Failed -> SaturationTestStatus.Failed
            stopRequested -> SaturationTestStatus.Stopped
            else -> SaturationTestStatus.Completed
        }
        status = final
        val end = Instant.now()
        val planned = cfg.duration
        val act = if (planned != null && final == SaturationTestStatus.Completed) minOf(active.elapsedSeconds, planned.toMillis() / 1000.0) else active.elapsedSeconds
        writer.enqueue("saturation-status ${cfg.testId} $final") { repository.updateTestStatus(cfg.testId, final, end, act) }
        writer.flush()
        val (cs, ms) = synchronized(lock) { cycles.toList() to markers.toList() }
        val report = analyzer.analyze(cfg, cs, ms, startUtc, end, final, act)
        finalReport = report
        logger.info("Saturation test '${cfg.name}' $final: ${cs.size} cycles, ${report.events.size} events.")
        completedListeners.forEach { safe { it(report) } }
    }

    private inline fun safe(a: () -> Unit) { try { a() } catch (e: Exception) { logger.error("Saturation test subscriber error: ${e.message}", e) } }

    fun pause() {
        if (!isRunning || paused) return
        paused = true; active.stop(); status = SaturationTestStatus.Paused
        addMarker("Paused")
        val id = config!!.testId
        writer.enqueue("saturation-status $id Paused") { repository.updateTestStatus(id, SaturationTestStatus.Paused, null) }
    }

    fun resume() {
        if (!isRunning || !paused) return
        addMarker("Resumed")
        paused = false; active.start(); status = SaturationTestStatus.Running
        val id = config!!.testId
        writer.enqueue("saturation-status $id Running") { repository.updateTestStatus(id, SaturationTestStatus.Running, null) }
    }

    suspend fun stop(): SaturationReport? {
        val l = loop ?: return finalReport
        stopRequested = true
        l.cancel(); l.join()
        return finalReport
    }

    suspend fun waitForCompletion(): SaturationReport? { loop?.join(); return finalReport }

    fun addMarker(text: String) {
        val cfg = config ?: return
        if (text.isBlank()) return
        val m = SaturationMarker(Instant.now(), active.elapsedMs, text.trim())
        synchronized(lock) { markers.add(m) }
        writer.enqueue("saturation-marker ${cfg.testId}") { repository.saveMarker(cfg.testId, m) }
    }

    fun snapshot(): Pair<List<SaturationCycle>, List<SaturationMarker>> = synchronized(lock) { cycles.toList() to markers.toList() }
}
