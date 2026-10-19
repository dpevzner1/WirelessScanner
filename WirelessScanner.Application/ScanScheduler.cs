using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>A completed acquisition cycle with a scheduler-wide sequence number.</summary>
public sealed record ScheduledCycle(long Sequence, Guid AdapterId, ScanCycleResult Result);

/// <summary>
/// The single owner of Wi-Fi acquisition for one adapter (DR-0011, finding F18).
/// Every view (live survey, capture, saturation test, discovery, FORCE SCAN) is served by the same scans:
/// at most one scan is in flight per adapter, concurrent requests share it, and the loop runs at the
/// shortest interval any consumer has requested.
/// </summary>
public interface IScanScheduler
{
    Guid? AdapterId { get; }
    bool IsRunning { get; }
    TimeSpan ScanTimeout { get; set; }
    long CyclesCompleted { get; }

    /// <summary>Raised on a background thread after every scan, whoever triggered it.</summary>
    event Action<ScheduledCycle>? CycleCompleted;

    /// <summary>Select the adapter to scan (null stops scanning).</summary>
    void SetAdapter(Guid? adapterId);

    /// <summary>Ask for periodic scans at (at most) this interval. Dispose the handle to release the request.</summary>
    IDisposable RequestCadence(TimeSpan interval);

    /// <summary>Shortest requested interval (the loop cadence), or null when nobody needs periodic scans.</summary>
    TimeSpan? EffectiveInterval { get; }

    /// <summary>Scan now, or join the scan already in flight. Returns null when no adapter is selected.</summary>
    Task<ScheduledCycle?> ScanNowAsync(CancellationToken cancellationToken = default);
}

public sealed class ScanScheduler : IScanScheduler, IAsyncDisposable
{
    private readonly INetworkAdapterProvider _provider;
    private readonly IAppLogger? _logger;
    private readonly object _lock = new();
    private readonly List<CadenceRequest> _cadences = new();
    private readonly CancellationTokenSource _life = new();
    private Task<ScheduledCycle?>? _inflight;
    private Guid? _adapterId;
    private Task? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private long _providerCalls;

    public ScanScheduler(INetworkAdapterProvider provider, IAppLogger? logger = null)
    {
        _provider = provider;
        _logger = logger;
    }

    public Guid? AdapterId { get { lock (_lock) return _adapterId; } }
    public bool IsRunning => _loop is { IsCompleted: false };
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromSeconds(6);
    public long CyclesCompleted => Interlocked.Read(ref _sequence);

    /// <summary>
    /// When false, cadence requests are recorded but no background loop runs: scans happen only through
    /// <see cref="ScanNowAsync"/> (deterministic tests and headless callers). The application uses true.
    /// </summary>
    public bool AutoScan { get; init; } = true;

    /// <summary>Number of times the provider was actually asked to scan (diagnostics / tests).</summary>
    public long ProviderCalls => Interlocked.Read(ref _providerCalls);

    public event Action<ScheduledCycle>? CycleCompleted;

    private sealed class CadenceRequest : IDisposable
    {
        private readonly ScanScheduler _owner;
        public TimeSpan Interval { get; }
        public CadenceRequest(ScanScheduler owner, TimeSpan interval) { _owner = owner; Interval = interval; }
        public void Dispose() => _owner.Release(this);
    }

    public void SetAdapter(Guid? adapterId)
    {
        lock (_lock)
        {
            if (_adapterId == adapterId) return;
            _adapterId = adapterId;
        }
        Wake();
        EnsureLoop();
    }

    public IDisposable RequestCadence(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var request = new CadenceRequest(this, interval);
        lock (_lock) _cadences.Add(request);
        Wake();
        EnsureLoop();
        return request;
    }

    private void Release(CadenceRequest request)
    {
        lock (_lock) _cadences.Remove(request);
        Wake();
    }

    /// <summary>Shortest requested interval, or null when nobody needs periodic scans.</summary>
    public TimeSpan? EffectiveInterval
    {
        get { lock (_lock) return _cadences.Count == 0 ? null : _cadences.Min(c => c.Interval); }
    }

    public Task<ScheduledCycle?> ScanNowAsync(CancellationToken cancellationToken = default)
    {
        Task<ScheduledCycle?> task;
        lock (_lock)
        {
            if (_adapterId is not Guid id) return Task.FromResult<ScheduledCycle?>(null);
            _inflight ??= RunScanAsync(id);   // single flight: join the scan already running
            task = _inflight;
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ScheduledCycle?> RunScanAsync(Guid adapterId)
    {
        try
        {
            await Task.Yield();
            Interlocked.Increment(ref _providerCalls);
            ScanCycleResult result;
            try
            {
                result = await _provider.ScanAsync(adapterId, ScanTimeout, _life.Token);
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Scan failed: {ex.Message}", ex);
                result = ScanCycleResult.Failure(DateTime.UtcNow, "provider_exception", ex.Message);
            }

            var cycle = new ScheduledCycle(Interlocked.Increment(ref _sequence), adapterId, result);
            try
            {
                CycleCompleted?.Invoke(cycle);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Scan subscriber failed: {ex.Message}", ex);
            }
            return cycle;
        }
        finally
        {
            lock (_lock) _inflight = null;
        }
    }

    private void Wake()
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            old = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private void EnsureLoop()
    {
        lock (_lock)
        {
            if (!AutoScan || _loop is { IsCompleted: false } || _life.IsCancellationRequested) return;
            _loop = Task.Run(LoopAsync);
        }
    }

    private async Task LoopAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_life.IsCancellationRequested)
        {
            TimeSpan? interval = EffectiveInterval;
            Guid? adapter = AdapterId;
            Task wake;
            lock (_lock) wake = _wake.Task;

            if (interval is not TimeSpan iv || adapter is null)
            {
                // idle until someone sets an adapter or requests a cadence
                await Task.WhenAny(wake, Task.Delay(Timeout.Infinite, _life.Token)).ConfigureAwait(false);
                continue;
            }

            var started = clock.Elapsed;
            try
            {
                await ScanNowAsync(_life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var wait = started + iv - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    // a shorter cadence, adapter change or release wakes the loop early
                    await Task.WhenAny(Task.Delay(wait, _life.Token), wake).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        Wake();
        if (_loop != null)
        {
            try { await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(5))); } catch { }
        }
    }
}
