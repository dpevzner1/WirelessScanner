using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>Finds the path at start: TTL-limited probes towards an internet target; the first public hop after the gateway is the ISP hop.</summary>
public static class PathDiscovery
{
    public const int MaxTtl = 10;

    /// <summary>RFC 1918, link-local and loopback are "local". Carrier-grade NAT (100.64/10) is the ISP's side, so it counts as ISP.</summary>
    public static bool IsLocalAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static async Task<ConnectionContext> DiscoverAsync(IConnectionProber prober, ConnectionContext ctx, string target, CancellationToken ct = default)
    {
        if (ctx.LocalIPv4 == null) return ctx;
        var hops = new List<string>();
        string? isp = null;
        int? ispTtl = null;
        for (int ttl = 1; ttl <= MaxTtl && !ct.IsCancellationRequested; ttl++)
        {
            var r = await prober.IcmpAsync(target, ctx.LocalIPv4, ttl, 1000, ct);
            if (r.Outcome == ProbeOutcome.Timeout) r = await prober.IcmpAsync(target, ctx.LocalIPv4, ttl, 1000, ct);   // one retry: routers rate-limit
            if (r.Outcome == ProbeOutcome.TtlExpired && r.From != null)
            {
                hops.Add(r.From);
                if (isp == null && !IsLocalAddress(r.From) && !ctx.Gateways.Contains(r.From)) { isp = r.From; ispTtl = ttl; break; }
            }
            else if (r.Outcome == ProbeOutcome.Ok) { hops.Add(r.From ?? target); break; }
            else hops.Add("*");
        }
        bool echo = false;
        if (isp != null)
        {
            for (int i = 0; i < 2 && !echo; i++)
                echo = (await prober.IcmpAsync(isp, ctx.LocalIPv4, 64, 1000, ct)).Outcome == ProbeOutcome.Ok;
        }
        return ctx with { PathHops = hops, IspHop = isp, IspHopTtl = ispTtl, IspHopAnswersEcho = echo };
    }
}

/// <summary>
/// Runs a connection test (DR-0014). One loop tick per TickMilliseconds of ACTIVE time; each tick takes a link sample and launches the
/// probes that are due (gateway every tick, ISP and internet every 2, DNS every 10, web every 30). Probes run concurrently and never
/// block the next tick. A late wake-up (sleep, stall) is recorded as a gap, never invented data. Speed and two-way runs pause the
/// regular probes so the monitoring statistics stay clean. Every result is persisted in order through the shared writer.
/// </summary>
public sealed class ConnectionTestRunner : IConnectionTestRunner, IAsyncDisposable
{
    private readonly IConnectionProber _prober;
    private readonly IConnectionRepository _repository;
    private readonly IConnectionAnalyzer _analyzer;
    private readonly IAppLogger _logger;
    private readonly OrderedPersistenceWriter _writer;
    private readonly bool _ownsWriter;

    private readonly object _lock = new();
    private readonly List<ConnectionProbeResult> _probes = new();
    private readonly List<ConnectionLinkSample> _link = new();
    private readonly List<SpeedTestResult> _speed = new();
    private readonly List<TwoWayResult> _twoWay = new();
    private readonly List<ConnectionMarker> _markers = new();
    private readonly List<ConnectionProbeResult> _pendingProbes = new();
    private readonly List<ConnectionLinkSample> _pendingLink = new();
    private readonly List<ConnectionProbeResult> _sinceProgress = new();
    private readonly List<Task> _inflight = new();
    private readonly Stopwatch _active = new();
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    private ConnectionTestConfig? _config;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _startUtc;
    private volatile bool _paused;
    private volatile bool _capacityRunning;
    private volatile bool _stopRequested;
    private ConnectionReport? _finalReport;
    private string? _currentIp, _currentGateway;
    private string? _lastSsid;
    private string? _activity;

    /// <summary>Clients for scheduled stage B / C runs (set by the view model from the saved test-server settings).</summary>
    public Func<(ISpeedTestClient Client, SpeedServerSettings Settings)?>? SpeedClientFactory { get; set; }
    public Func<ITwoWayClient?>? TwoWayClientFactory { get; set; }

    public ConnectionTestRunner(IConnectionProber prober, IConnectionRepository repository, IConnectionAnalyzer analyzer, IAppLogger logger,
        OrderedPersistenceWriter? writer = null)
    {
        _prober = prober;
        _repository = repository;
        _analyzer = analyzer;
        _logger = logger;
        _ownsWriter = writer == null;
        _writer = writer ?? new OrderedPersistenceWriter(logger);
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public ConnectionTestStatus? Status { get; private set; }
    public ConnectionTestConfig? Config => _config;
    public event Action<ConnectionProgress>? Progress;
    public event Action<ConnectionReport>? Completed;

    public async Task StartAsync(ConnectionTestConfig config, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("A connection test is already running.");
        var ctx = config.ContextAtStart ?? throw new ArgumentException("The connection was not read before starting.", nameof(config));
        if (!ctx.WifiConnected || string.IsNullOrEmpty(ctx.LocalIPv4))
            throw new ArgumentException("This PC is not connected over Wi-Fi (no Wi-Fi IPv4 address). Connect first; the test never connects for you.", nameof(config));
        if (config.TickMilliseconds < 20) throw new ArgumentException("Tick too short.", nameof(config));

        // Path discovery for the ISP segment (TTL-limited probes towards the first internet target).
        if (config.ProbeIsp && ctx.IspHop == null && config.InternetTargets.Count > 0)
        {
            _activity = "Finding the ISP hop…";
            try
            {
                ctx = await PathDiscovery.DiscoverAsync(_prober, ctx, config.InternetTargets[0], cancellationToken);
                _logger.Info($"Path discovery: {string.Join(",", ctx.PathHops.Select(h => h == "*" ? "*" : PathDiscovery.IsLocalAddress(h) ? "local" : "public"))}; ISP hop {(ctx.IspHop != null ? $"at TTL {ctx.IspHopTtl}" : "not found")}.");
            }
            catch (Exception ex) { _logger.Warn($"Path discovery failed: {ex.GetType().Name}: {ex.Message}"); }
            config = config with { ContextAtStart = ctx };
        }

        lock (_lock)
        {
            _probes.Clear(); _link.Clear(); _speed.Clear(); _twoWay.Clear(); _markers.Clear();
            _pendingProbes.Clear(); _pendingLink.Clear(); _sinceProgress.Clear(); _inflight.Clear();
        }
        _config = config;
        _currentIp = ctx.LocalIPv4;
        _currentGateway = ctx.Gateways.FirstOrDefault();
        _lastSsid = ctx.Ssid;
        _startUtc = DateTime.UtcNow;
        _paused = false;
        _stopRequested = false;
        _capacityRunning = false;
        _finalReport = null;
        _activity = null;
        _active.Reset();
        _active.Start();
        Status = ConnectionTestStatus.Running;

        var cfg = config;
        var start = _startUtc;
        _writer.Enqueue($"connection-test {cfg.TestId}", () => _repository.SaveTestAsync(cfg, start, ConnectionTestStatus.Running));
        _logger.Info($"Connection test '{cfg.Name}' started on {ctx.Ssid} ({ctx.Bssid}) from {ctx.LocalIPv4}; tick {cfg.TickMilliseconds} ms; duration {(cfg.Duration?.ToString() ?? "until stopped")}.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var cfg = _config!;
        long tickMs = cfg.TickMilliseconds;
        long tick = 0;
        long lastSpeedMinute = cfg.SpeedTestAtStart ? -1 : 0, lastTwoWayMinute = cfg.TwoWayAtStart ? -1 : 0;
        try
        {
            while (!ct.IsCancellationRequested && !_stopRequested)
            {
                if (cfg.Duration is TimeSpan d && _active.Elapsed >= d) break;
                long due = tick * tickMs;
                long now = _active.ElapsedMilliseconds;
                if (now < due)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(due - now, 250)), ct);
                    continue;
                }
                if (_paused || _capacityRunning)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(tickMs, 100)), ct);
                    tick = _active.ElapsedMilliseconds / tickMs + 1;   // no catch-up burst after a pause or capacity run
                    continue;
                }
                long late = now - due;
                long? gap = late >= 3 * tickMs ? late : null;
                if (gap != null) tick = now / tickMs;                 // skip missed ticks; the gap is recorded on the link sample

                await RunTickAsync(_config!, tick, gap, ct);

                // ISP hop not found at start (routers intermittently ignore the probes): retry after 30 ticks, then every 300 ticks
                if ((tick == 30 || (tick > 30 && tick % 300 == 0)) && _config!.ProbeIsp && _config.ContextAtStart?.IspHop == null && _config.InternetTargets.Count > 0)
                {
                    try
                    {
                        var again = await PathDiscovery.DiscoverAsync(_prober, _config.ContextAtStart! with { LocalIPv4 = _currentIp ?? _config.ContextAtStart!.LocalIPv4 }, _config.InternetTargets[0], ct);
                        if (again.IspHop != null)
                        {
                            _config = _config with { ContextAtStart = again };
                            AddMarker($"ISP hop found on a later attempt (TTL {again.IspHopTtl}); ISP probing starts now");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"Second path discovery failed: {ex.Message}"); }
                }

                // scheduled stage B / C runs (first at start when requested, then every N minutes)
                long minute = (long)(_active.Elapsed.TotalMinutes);
                if (SpeedClientFactory != null && ((lastSpeedMinute < 0 && tick >= 2) || (cfg.SpeedTestEveryMinutes > 0 && minute > 0 && minute % cfg.SpeedTestEveryMinutes == 0 && minute != lastSpeedMinute && lastSpeedMinute >= 0)))
                {
                    lastSpeedMinute = Math.Max(minute, 0);
                    if (SpeedClientFactory() is { } s) _ = RunSpeedTestAsync(s.Client, s.Settings, ct);
                }
                if (TwoWayClientFactory != null && ((lastTwoWayMinute < 0 && tick >= 2) || (cfg.TwoWayEveryMinutes > 0 && minute > 0 && minute % cfg.TwoWayEveryMinutes == 0 && minute != lastTwoWayMinute && lastTwoWayMinute >= 0)))
                {
                    lastTwoWayMinute = Math.Max(minute, 0);
                    if (TwoWayClientFactory() is { } c) _ = RunTwoWayAsync(c, ct);
                }
                tick++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"Connection test loop failed: {ex.Message}", ex);
            Status = ConnectionTestStatus.Failed;
        }
        finally
        {
            _active.Stop();
            await DrainInflightAsync();
            await FinishAsync();
        }
    }

    private async Task RunTickAsync(ConnectionTestConfig cfg, long tick, long? gapMs, CancellationToken ct)
    {
        var wall = DateTime.UtcNow;
        long elapsed = _active.ElapsedMilliseconds;
        var ctx = cfg.ContextAtStart!;

        // refresh address / gateway every 10 ticks (DHCP renewals, roams to another subnet)
        if (tick % 10 == 0 && tick > 0)
        {
            try
            {
                var fresh = await _prober.GetContextAsync(cfg.InterfaceId, ct);
                _currentIp = fresh.LocalIPv4;
                _currentGateway = fresh.Gateways.FirstOrDefault() ?? _currentGateway;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Warn($"Context refresh failed: {ex.Message}"); }
        }

        ConnectionLinkSample? sample = null;
        if (cfg.ProbeWifiLink)
        {
            WlanLinkReading? r = null;
            try { r = await _prober.ReadLinkAsync(cfg.InterfaceId); } catch (Exception ex) { _logger.Warn($"Link read failed: {ex.Message}"); }
            var bytes = _prober.InterfaceBytes(cfg.InterfaceId);
            sample = new ConnectionLinkSample(tick, wall, elapsed, r?.Connected ?? false, r?.Ssid, r?.Bssid, r?.Channel, r?.Rssi, r?.SignalQualityPct,
                r?.RxRateMbps, r?.TxRateMbps, _currentIp, _currentGateway, bytes?.BytesIn, bytes?.BytesOut, r?.Counters, gapMs);
            lock (_lock) { _link.Add(sample); _pendingLink.Add(sample); }
            if (r?.Connected == true && r.Ssid != null && _lastSsid != null && r.Ssid != _lastSsid && cfg.StopOnNetworkChange)
            {
                _logger.Info($"Connection test stopping: network changed {_lastSsid} → {r.Ssid}.");
                AddMarker($"Stopped: network changed to {r.Ssid}");
                _stopRequested = true;
            }
            if (r?.Connected == true && r.Ssid != null) _lastSsid = r.Ssid;
        }

        string? src = _currentIp;
        void Launch(ConnectionSegment seg, string target, ProbeMethod method, Func<string, Task<ProbeReply>> probe, Func<ProbeReply, ProbeReply>? map = null)
        {
            var task = Task.Run(async () =>
            {
                ProbeReply reply;
                if (src == null) reply = ProbeReply.Fail(ProbeOutcome.Unreachable, "no Wi-Fi IPv4 address");
                else
                {
                    try { reply = await probe(src); }
                    catch (OperationCanceledException) { reply = ProbeReply.Fail(ProbeOutcome.Skipped, "test stopped"); }
                    catch (Exception ex) { reply = ProbeReply.Fail(ProbeOutcome.Error, ex.GetType().Name + ": " + ex.Message); }
                }
                if (map != null) reply = map(reply);
                var result = new ConnectionProbeResult(tick, wall, elapsed, seg, target, method, reply.Outcome, reply.Outcome == ProbeOutcome.Ok ? reply.RttMs : null, reply.Detail);
                lock (_lock) { _probes.Add(result); _pendingProbes.Add(result); _sinceProgress.Add(result); }
            });
            lock (_lock) _inflight.Add(task);
        }

        if (cfg.ProbeGateway && tick % ConnectionTestConfig.GatewayEveryTicks == 0 && _currentGateway is { } gw)
            Launch(ConnectionSegment.Gateway, gw, ProbeMethod.Icmp, s => _prober.IcmpAsync(gw, s, 64, ConnectionTestConfig.GatewayTimeoutMs, ct));

        if (cfg.ProbeIsp && tick % ConnectionTestConfig.IspEveryTicks == 0 && ctx.IspHop is { } isp)
        {
            if (ctx.IspHopAnswersEcho)
                Launch(ConnectionSegment.IspHop, isp, ProbeMethod.Icmp, s => _prober.IcmpAsync(isp, s, 64, ConnectionTestConfig.IspTimeoutMs, ct));
            else if (ctx.IspHopTtl is int ttl && cfg.InternetTargets.Count > 0)
            {
                string via = cfg.InternetTargets[0];
                Launch(ConnectionSegment.IspHop, isp, ProbeMethod.IcmpTtl, s => _prober.IcmpAsync(via, s, ttl, ConnectionTestConfig.IspTimeoutMs, ct),
                    r => r.Outcome == ProbeOutcome.TtlExpired && r.From == isp ? r with { Outcome = ProbeOutcome.Ok }
                       : r.Outcome == ProbeOutcome.TtlExpired ? r with { Outcome = ProbeOutcome.Error, Detail = $"answer from {r.From} (path changed)" } : r);
            }
        }

        if (cfg.ProbeInternet && tick % ConnectionTestConfig.InternetEveryTicks == 0)
        {
            foreach (var t in cfg.InternetTargets)
            {
                Launch(ConnectionSegment.Internet, t, ProbeMethod.Tcp, s => _prober.TcpConnectAsync(t, cfg.InternetTcpPort, s, ConnectionTestConfig.InternetTimeoutMs, ct));
                Launch(ConnectionSegment.Internet, t, ProbeMethod.Icmp, s => _prober.IcmpAsync(t, s, 64, ConnectionTestConfig.InternetTimeoutMs, ct));
            }
        }

        if (cfg.ProbeDns && tick % ConnectionTestConfig.DnsEveryTicks == 0)
            foreach (var server in ctx.DnsServers.Where(IsIPv4))
                Launch(ConnectionSegment.Dns, server, ProbeMethod.Dns, s => _prober.DnsQueryAsync(server, cfg.DnsQueryName, s, ConnectionTestConfig.DnsTimeoutMs, ct));

        if (cfg.ProbeWeb && tick % ConnectionTestConfig.WebEveryTicks == 0 && !string.IsNullOrWhiteSpace(cfg.WebUrl))
            Launch(ConnectionSegment.Web, cfg.WebUrl, ProbeMethod.Https, s => _prober.HttpsAsync(cfg.WebUrl, s, ConnectionTestConfig.WebTimeoutMs, ct));

        FlushPending(cfg.TestId);
        PublishProgress(cfg, tick, sample);
        lock (_lock) _inflight.RemoveAll(t => t.IsCompleted);
    }

    private static bool IsIPv4(string s) => IPAddress.TryParse(s, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private void FlushPending(Guid testId)
    {
        ConnectionProbeResult[] probes;
        ConnectionLinkSample[] link;
        lock (_lock)
        {
            probes = _pendingProbes.ToArray(); _pendingProbes.Clear();
            link = _pendingLink.ToArray(); _pendingLink.Clear();
        }
        if (probes.Length > 0) _writer.Enqueue($"connection-probes {testId}", () => _repository.SaveProbesAsync(testId, probes));
        if (link.Length > 0) _writer.Enqueue($"connection-link {testId}", () => _repository.SaveLinkSamplesAsync(testId, link));
    }

    private void PublishProgress(ConnectionTestConfig cfg, long tick, ConnectionLinkSample? sample)
    {
        ConnectionProbeResult[] recent;
        lock (_lock) { recent = _sinceProgress.ToArray(); _sinceProgress.Clear(); }
        double active = _active.Elapsed.TotalSeconds;
        double? remaining = cfg.Duration is TimeSpan d ? Math.Max(0, d.TotalSeconds - active) : null;
        SafeInvoke(() => Progress?.Invoke(new ConnectionProgress(cfg, Status ?? ConnectionTestStatus.Running, _startUtc, active, remaining, tick, recent, sample, _activity)));
    }

    private async Task DrainInflightAsync()
    {
        Task[] pending;
        lock (_lock) pending = _inflight.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { _logger.Warn("Some probes were still running when the test ended."); }
    }

    private async Task FinishAsync()
    {
        var cfg = _config!;
        var final = Status == ConnectionTestStatus.Failed ? ConnectionTestStatus.Failed
            : _stopRequested ? ConnectionTestStatus.Stopped : ConnectionTestStatus.Completed;
        Status = final;
        var end = DateTime.UtcNow;
        double active = cfg.Duration is TimeSpan planned && final == ConnectionTestStatus.Completed
            ? Math.Min(_active.Elapsed.TotalSeconds, planned.TotalSeconds) : _active.Elapsed.TotalSeconds;
        FlushPending(cfg.TestId);
        _writer.Enqueue($"connection-status {cfg.TestId} {final}", () => _repository.UpdateTestStatusAsync(cfg.TestId, final, end, active));
        await _writer.FlushAsync();
        var snap = Snapshot();
        _finalReport = _analyzer.Analyze(cfg, snap.Probes, snap.Link, snap.Speed, snap.TwoWay, snap.Markers, _startUtc, end, final, active);
        _logger.Info($"Connection test '{cfg.Name}' {final}: {_finalReport.Totals.ProbesSent} probes, {_finalReport.Events.Count} events.");
        SafeInvoke(() => Completed?.Invoke(_finalReport));
    }

    private void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { _logger.Error($"Connection test subscriber error: {ex.Message}", ex); }
    }

    public void Pause()
    {
        if (!IsRunning || _paused) return;
        _paused = true;
        _active.Stop();
        Status = ConnectionTestStatus.Paused;
        AddMarker("Paused");
        var id = _config!.TestId;
        _writer.Enqueue($"connection-status {id} Paused", () => _repository.UpdateTestStatusAsync(id, ConnectionTestStatus.Paused, null));
    }

    public void Resume()
    {
        if (!IsRunning || !_paused) return;
        AddMarker("Resumed");
        _paused = false;
        _active.Start();
        Status = ConnectionTestStatus.Running;
        var id = _config!.TestId;
        _writer.Enqueue($"connection-status {id} Running", () => _repository.UpdateTestStatusAsync(id, ConnectionTestStatus.Running, null));
    }

    public async Task<ConnectionReport?> StopAsync()
    {
        if (_loop == null) return _finalReport;
        _stopRequested = true;
        _cts?.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        return _finalReport;
    }

    public async Task<ConnectionReport?> WaitForCompletionAsync()
    {
        if (_loop != null) await _loop;
        return _finalReport;
    }

    public void AddMarker(string text)
    {
        if (_config == null || string.IsNullOrWhiteSpace(text)) return;
        var marker = new ConnectionMarker(DateTime.UtcNow, _active.ElapsedMilliseconds, text.Trim());
        lock (_lock) _markers.Add(marker);
        var id = _config.TestId;
        _writer.Enqueue($"connection-marker {id}", () => _repository.SaveMarkerAsync(id, marker));
    }

    public (IReadOnlyList<ConnectionProbeResult> Probes, IReadOnlyList<ConnectionLinkSample> Link, IReadOnlyList<SpeedTestResult> Speed,
        IReadOnlyList<TwoWayResult> TwoWay, IReadOnlyList<ConnectionMarker> Markers, DateTime StartUtc, double ActiveSeconds) Snapshot()
    {
        lock (_lock)
            return (_probes.ToArray(), _link.ToArray(), _speed.ToArray(), _twoWay.ToArray(), _markers.ToArray(), _startUtc, _active.Elapsed.TotalSeconds);
    }

    // ------------------------------------------------------------------ stage B

    /// <summary>Bufferbloat grade from the median latency increase under load (ms).</summary>
    public static string BufferbloatGrade(double? increaseMs) => increaseMs switch
    {
        null => "",
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F"
    };

    public async Task<SpeedTestResult?> RunSpeedTestAsync(ISpeedTestClient client, SpeedServerSettings settings, CancellationToken ct = default)
    {
        var cfg = _config;
        if (!IsRunning || cfg == null || _currentIp is not { } src) return null;
        if (!await _capacityGate.WaitAsync(0, ct)) return null;          // one capacity run at a time
        _capacityRunning = true;
        var startWall = DateTime.UtcNow;
        long startMs = _active.ElapsedMilliseconds;
        SpeedTestResult result;
        try
        {
            _activity = $"Speed test against {client.Server}…";
            await DrainInflightAsync();
            // Latency target: the router for a LAN server (Wi-Fi/router queueing), the first internet target (TCP) for an internet server.
            bool lan = settings.Kind == SpeedServerKind.Responder;
            string latencyTarget = lan ? _currentGateway ?? "" : cfg.InternetTargets.FirstOrDefault() ?? "";
            async Task<double?> One()
            {
                var r = lan ? await _prober.IcmpAsync(latencyTarget, src, 64, 1000, ct) : await _prober.TcpConnectAsync(latencyTarget, cfg.InternetTcpPort, src, 1500, ct);
                return r.Outcome == ProbeOutcome.Ok ? r.RttMs : null;
            }
            async Task<List<double>> Sample(Func<bool> keepGoing, int max)
            {
                var list = new List<double>();
                for (int i = 0; i < max && keepGoing() && !ct.IsCancellationRequested; i++)
                {
                    if (await One() is double v) list.Add(v);
                    await Task.Delay(250, ct);
                }
                return list;
            }
            var idle = await Sample(() => true, 12);
            var duration = TimeSpan.FromSeconds(Math.Clamp(settings.SecondsPerDirection, 3, 30));
            var downTask = client.DownloadAsync(src, Math.Clamp(settings.Streams, 1, 16), duration, ct);
            var loadedDown = await Sample(() => !downTask.IsCompleted, 400);
            var down = await downTask;
            var upTask = client.UploadAsync(src, Math.Clamp(settings.Streams, 1, 16), duration, ct);
            var loadedUp = await Sample(() => !upTask.IsCompleted, 400);
            var up = await upTask;

            static double? Median(List<double> v) => v.Count == 0 ? null : ConnectionAnalyzer.Percentile(v.OrderBy(x => x).ToList(), 50);
            double? idleMed = Median(idle), downMed = Median(loadedDown), upMed = Median(loadedUp);
            double? worstLoaded = downMed.HasValue || upMed.HasValue ? Math.Max(downMed ?? 0, upMed ?? 0) : null;
            double? increase = idleMed.HasValue && worstLoaded.HasValue ? Math.Max(0, worstLoaded.Value - idleMed.Value) : null;
            var allLoaded = loadedDown.Concat(loadedUp).OrderBy(x => x).ToList();
            string? error = down.Error ?? up.Error;
            result = new SpeedTestResult
            {
                StartUtc = startWall,
                ElapsedMs = startMs,
                Provider = client.Provider,
                Server = client.Server,
                Streams = Math.Clamp(settings.Streams, 1, 16),
                SecondsPerDirection = duration.TotalSeconds,
                DownloadMbps = down.Seconds > 0 && down.Bytes > 0 ? Math.Round(down.Bytes * 8 / down.Seconds / 1e6, 2) : null,
                UploadMbps = up.Seconds > 0 && up.Bytes > 0 ? Math.Round(up.Bytes * 8 / up.Seconds / 1e6, 2) : null,
                BytesDown = down.Bytes,
                BytesUp = up.Bytes,
                IdleLatencyMs = idleMed,
                LoadedLatencyDownMs = downMed,
                LoadedLatencyUpMs = upMed,
                LoadedLatencyP95Ms = ConnectionAnalyzer.Percentile(allLoaded, 95),
                LatencyIncreaseMs = increase.HasValue ? Math.Round(increase.Value, 3) : null,
                BufferbloatGrade = BufferbloatGrade(increase),
                ResponsivenessRpm = worstLoaded is double wl && wl > 0 ? Math.Round(60000 / wl) : null,
                LatencyTarget = (lan ? "router " : "internet ") + latencyTarget,
                Error = error
            };
        }
        catch (OperationCanceledException)
        {
            result = new SpeedTestResult { StartUtc = startWall, ElapsedMs = startMs, Provider = client.Provider, Server = client.Server, Error = "cancelled" };
        }
        catch (Exception ex)
        {
            result = new SpeedTestResult { StartUtc = startWall, ElapsedMs = startMs, Provider = client.Provider, Server = client.Server, Error = ex.Message };
        }
        finally
        {
            _activity = null;
            _capacityRunning = false;
            _capacityGate.Release();
        }
        lock (_lock) _speed.Add(result);
        var id = cfg.TestId;
        _writer.Enqueue($"connection-speed {id}", () => _repository.SaveSpeedTestAsync(id, result));
        return result;
    }

    // ------------------------------------------------------------------ stage C

    public async Task<TwoWayResult?> RunTwoWayAsync(ITwoWayClient client, CancellationToken ct = default)
    {
        var cfg = _config;
        if (!IsRunning || cfg == null || _currentIp is not { } src) return null;
        if (!await _capacityGate.WaitAsync(0, ct)) return null;
        _capacityRunning = true;
        var startWall = DateTime.UtcNow;
        long startMs = _active.ElapsedMilliseconds;
        TwoWayResult result;
        try
        {
            _activity = $"Two-way test with {client.Responder}…";
            await DrainInflightAsync();
            result = await client.RunStreamAsync(src, TimeSpan.FromSeconds(10), 50, 172, ct);
            if (client is ISpeedTestClient lanSpeed && result.Error == null)
            {
                var d = await lanSpeed.DownloadAsync(src, 4, TimeSpan.FromSeconds(5), ct);
                var u = await lanSpeed.UploadAsync(src, 4, TimeSpan.FromSeconds(5), ct);
                result = result with
                {
                    LanDownMbps = d.Seconds > 0 && d.Bytes > 0 ? Math.Round(d.Bytes * 8 / d.Seconds / 1e6, 2) : null,
                    LanUpMbps = u.Seconds > 0 && u.Bytes > 0 ? Math.Round(u.Bytes * 8 / u.Seconds / 1e6, 2) : null
                };
            }
            result = result with { StartUtc = startWall, ElapsedMs = startMs };
        }
        catch (OperationCanceledException)
        {
            result = new TwoWayResult { StartUtc = startWall, ElapsedMs = startMs, Responder = client.Responder, Error = "cancelled" };
        }
        catch (Exception ex)
        {
            result = new TwoWayResult { StartUtc = startWall, ElapsedMs = startMs, Responder = client.Responder, Error = ex.Message };
        }
        finally
        {
            _activity = null;
            _capacityRunning = false;
            _capacityGate.Release();
        }
        lock (_lock) _twoWay.Add(result);
        var id = cfg.TestId;
        _writer.Enqueue($"connection-twoway {id}", () => _repository.SaveTwoWayAsync(id, result));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        if (_ownsWriter) await _writer.DisposeAsync();
        _capacityGate.Dispose();
    }
}
