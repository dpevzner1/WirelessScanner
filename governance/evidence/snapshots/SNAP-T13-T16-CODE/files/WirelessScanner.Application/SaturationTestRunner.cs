using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Runs a Signal Saturation Test (SPEC-SAT-001 §4): one fresh-scan cycle per interval on a background
/// task, until the configured duration of ACTIVE time (pauses excluded) elapses or the user stops it.
/// Scans come from the shared <see cref="IScanScheduler"/> (DR-0011, F18): the runner requests a cadence and
/// records the first cycle at or after each of its due times, so it never issues competing scans.
/// Every cycle is persisted in order through the shared OrderedPersistenceWriter; the report is always
/// re-derivable from stored cycles (analytics are pure, see SaturationAnalyzer).
/// </summary>
public sealed class SaturationTestRunner : ISaturationTestRunner, IAsyncDisposable
{
    private readonly IScanScheduler _scheduler;
    private readonly ScanScheduler? _ownedScheduler;
    private readonly ISaturationRepository _repository;
    private readonly ISaturationAnalyzer _analyzer;
    private readonly IAppLogger _logger;
    private readonly OrderedPersistenceWriter _writer;
    private readonly bool _ownsWriter;

    private readonly object _lock = new();
    private readonly List<SaturationCycle> _cycles = new();
    private readonly List<SaturationMarker> _markers = new();
    private readonly Stopwatch _active = new();
    private SaturationTestConfig? _config;
    private SaturationTargetTracker? _tracker;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _startUtc;
    private volatile bool _paused;
    private volatile bool _stopRequested;
    private SaturationReport? _finalReport;

    /// <summary>Standalone runner (tests, headless, hardware probes): owns a private scheduler for the provider.</summary>
    public SaturationTestRunner(INetworkAdapterProvider provider, ISaturationRepository repository, ISaturationAnalyzer analyzer,
        IAppLogger logger, OrderedPersistenceWriter? writer = null)
        : this(new ScanScheduler(provider, logger), repository, analyzer, logger, writer, ownsScheduler: true)
    {
    }

    /// <summary>App runner: shares the application's scan scheduler with the live view.</summary>
    public SaturationTestRunner(IScanScheduler scheduler, ISaturationRepository repository, ISaturationAnalyzer analyzer,
        IAppLogger logger, OrderedPersistenceWriter? writer = null)
        : this(scheduler, repository, analyzer, logger, writer, ownsScheduler: false)
    {
    }

    private SaturationTestRunner(IScanScheduler scheduler, ISaturationRepository repository, ISaturationAnalyzer analyzer,
        IAppLogger logger, OrderedPersistenceWriter? writer, bool ownsScheduler)
    {
        _scheduler = scheduler;
        _ownedScheduler = ownsScheduler ? scheduler as ScanScheduler : null;
        _repository = repository;
        _analyzer = analyzer;
        _logger = logger;
        _ownsWriter = writer == null;
        _writer = writer ?? new OrderedPersistenceWriter(logger);
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public SaturationTestStatus? Status { get; private set; }
    public SaturationTestConfig? Config => _config;

    public event Action<SaturationProgress>? Progress;
    public event Action<SaturationReport>? Completed;

    public Task StartAsync(SaturationTestConfig config, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("A saturation test is already running.");
        if (config.Interval <= TimeSpan.Zero) throw new ArgumentException("Interval must be positive.", nameof(config));
        if (config.Scope != SaturationScope.AllVisible && config.TargetBssids.Count == 0 && config.TargetSsids.Count == 0)
            throw new ArgumentException("Select at least one target.", nameof(config));

        lock (_lock)
        {
            _cycles.Clear();
            _markers.Clear();
        }
        _config = config;
        _tracker = new SaturationTargetTracker(config);
        _startUtc = DateTime.UtcNow;
        _paused = false;
        _stopRequested = false;
        _finalReport = null;
        _active.Reset();
        _active.Start();
        Status = SaturationTestStatus.Running;

        var cfg = config;
        var start = _startUtc;
        _writer.Enqueue($"saturation-test {cfg.TestId}", () => _repository.SaveTestAsync(cfg, start, SaturationTestStatus.Running));
        _logger.Info($"Saturation test '{cfg.Name}' started: scope {cfg.Scope}, interval {cfg.Interval.TotalSeconds:F0} s, duration {(cfg.Duration?.ToString() ?? "until stopped")}.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var cfg = _config!;
        long sequence = 0;
        var cycles = Channel.CreateUnbounded<ScheduledCycle>(new UnboundedChannelOptions { SingleReader = true });
        void OnCycle(ScheduledCycle c)
        {
            if (c.AdapterId == cfg.InterfaceId) cycles.Writer.TryWrite(c);
        }

        _scheduler.ScanTimeout = cfg.ScanTimeout;
        if (_ownedScheduler != null || _scheduler.AdapterId is null) _scheduler.SetAdapter(cfg.InterfaceId);
        _scheduler.CycleCompleted += OnCycle;
        using var cadence = _scheduler.RequestCadence(cfg.Interval);
        long nextDueMs = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_paused && cfg.Duration is TimeSpan d && _active.Elapsed >= d) break;

                // wait for the next scan, but never past the end of the planned duration
                TimeSpan waitLimit = cfg.Duration is TimeSpan dur && !_paused ? dur - _active.Elapsed : TimeSpan.FromSeconds(1);
                if (waitLimit <= TimeSpan.Zero) break;
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(waitLimit < TimeSpan.FromSeconds(1) ? waitLimit : TimeSpan.FromSeconds(1));
                ScheduledCycle scheduled;
                try
                {
                    scheduled = await cycles.Reader.ReadAsync(waitCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue; // re-check duration / pause
                }

                if (_paused) continue;                              // paused: scans are not recorded
                long nowMs = _active.ElapsedMilliseconds;
                long intervalMs = (long)cfg.Interval.TotalMilliseconds;
                // Decimate a faster shared cadence to this test's interval. Scan durations jitter, so accept a cycle
                // up to half a scheduler period early; skip missed slots instead of bursting to catch up.
                long toleranceMs = (long)((_scheduler.EffectiveInterval ?? cfg.Interval).TotalMilliseconds / 2);
                if (nowMs < nextDueMs - toleranceMs) continue;
                nextDueMs += intervalMs;
                if (nextDueMs < nowMs - toleranceMs) nextDueMs = nowMs + intervalMs;

                var result = scheduled.Result;
                sequence++;
                var observations = SaturationObservationBuilder.Build(cfg, _tracker!, result, sequence);
                var cycle = new SaturationCycle(sequence, result.CompletedUtc, nowMs, result.Status, result.DurationMs,
                    result.AccessPoints.Count, result.ErrorCode, result.ErrorMessage, observations);

                SaturationCycle[] cyclesSnapshot;
                SaturationMarker[] markersSnapshot;
                lock (_lock)
                {
                    _cycles.Add(cycle);
                    cyclesSnapshot = _cycles.ToArray();
                    markersSnapshot = _markers.ToArray();
                }
                long seq = sequence;
                _writer.Enqueue($"saturation-cycle {cfg.TestId}#{seq}", () => _repository.SaveCycleAsync(cfg.TestId, cycle));

                double active = _active.Elapsed.TotalSeconds;
                double? remaining = cfg.Duration is TimeSpan du ? Math.Max(0, du.TotalSeconds - active) : null;
                SafeInvoke(() => Progress?.Invoke(new SaturationProgress(cfg, Status ?? SaturationTestStatus.Running, _startUtc, active, remaining,
                    cycle, cyclesSnapshot, markersSnapshot)));
            }
        }
        catch (OperationCanceledException)
        {
            // stop requested
        }
        catch (Exception ex)
        {
            _logger.Error($"Saturation test loop failed: {ex.Message}", ex);
            Status = SaturationTestStatus.Failed;
        }
        finally
        {
            _scheduler.CycleCompleted -= OnCycle;
            _active.Stop();
            await FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        var cfg = _config!;
        var final = Status == SaturationTestStatus.Failed ? SaturationTestStatus.Failed
            : _stopRequested ? SaturationTestStatus.Stopped
            : SaturationTestStatus.Completed;
        Status = final;
        var end = DateTime.UtcNow;
        _writer.Enqueue($"saturation-status {cfg.TestId} {final}", () => _repository.UpdateTestStatusAsync(cfg.TestId, final, end));
        await _writer.FlushAsync();

        SaturationCycle[] cycles;
        SaturationMarker[] markers;
        lock (_lock)
        {
            cycles = _cycles.ToArray();
            markers = _markers.ToArray();
        }
        _finalReport = _analyzer.Analyze(cfg, cycles, markers, _startUtc, end, final);
        _logger.Info($"Saturation test '{cfg.Name}' {final}: {cycles.Length} cycles, {_finalReport.Events.Count} events.");
        SafeInvoke(() => Completed?.Invoke(_finalReport));
    }

    private void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { _logger.Error($"Saturation test subscriber error: {ex.Message}", ex); }
    }

    public void Pause()
    {
        if (!IsRunning || _paused) return;
        _paused = true;
        _active.Stop();
        Status = SaturationTestStatus.Paused;
        AddMarker("Paused");
        var id = _config!.TestId;
        _writer.Enqueue($"saturation-status {id} Paused", () => _repository.UpdateTestStatusAsync(id, SaturationTestStatus.Paused, null));
    }

    public void Resume()
    {
        if (!IsRunning || !_paused) return;
        AddMarker("Resumed");
        _paused = false;
        _active.Start();
        Status = SaturationTestStatus.Running;
        var id = _config!.TestId;
        _writer.Enqueue($"saturation-status {id} Running", () => _repository.UpdateTestStatusAsync(id, SaturationTestStatus.Running, null));
    }

    public async Task<SaturationReport?> StopAsync()
    {
        if (_loop == null) return _finalReport;
        _stopRequested = true;
        _cts?.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        return _finalReport;
    }

    /// <summary>Wait for a running test to finish on its own (duration elapsed) and return the report.</summary>
    public async Task<SaturationReport?> WaitForCompletionAsync()
    {
        if (_loop != null) await _loop;
        return _finalReport;
    }

    public void AddMarker(string text)
    {
        if (_config == null || string.IsNullOrWhiteSpace(text)) return;
        var marker = new SaturationMarker(DateTime.UtcNow, _active.ElapsedMilliseconds, text.Trim());
        lock (_lock) _markers.Add(marker);
        var id = _config.TestId;
        _writer.Enqueue($"saturation-marker {id}", () => _repository.SaveMarkerAsync(id, marker));
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        if (_ownsWriter) await _writer.DisposeAsync();
        if (_ownedScheduler != null) await _ownedScheduler.DisposeAsync();
    }
}
