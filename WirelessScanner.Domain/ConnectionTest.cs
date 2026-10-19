using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

// Connection test (DR-0014): quality of the connection this PC is ALREADY associated and authenticated to, measured end to end
// with small probes bound to the Wi-Fi interface. The connection itself is never changed (no connect/disconnect/roam).
// Stage A = segmented monitor; stage B = speed and responsiveness under load; stage C = two-way tests with a LAN responder.

/// <summary>Where along the path a probe measures. Order = upstream order used for fault attribution.</summary>
public enum ConnectionSegment
{
    /// <summary>The radio link itself (association, signal, PHY rate, frame retries).</summary>
    WifiLink,
    /// <summary>The default gateway (router): Wi-Fi + LAN.</summary>
    Gateway,
    /// <summary>The first ISP hop beyond the gateway (found by a TTL-limited probe at start).</summary>
    IspHop,
    /// <summary>Public anycast targets (TCP 443 connect and ICMP).</summary>
    Internet,
    /// <summary>Name resolution against the configured resolvers.</summary>
    Dns,
    /// <summary>A small HTTPS request (time to first byte).</summary>
    Web,
    /// <summary>Stage C: two-way UDP stream to a LAN responder (per-direction loss and delay variation).</summary>
    LanTwoWay
}

public enum ProbeMethod { Icmp, IcmpTtl, Tcp, Dns, Https, Udp }

/// <summary>Every probe ends in exactly one outcome; failures are data, never exceptions.</summary>
public enum ProbeOutcome { Ok, Timeout, Refused, Unreachable, TtlExpired, DnsError, HttpError, Error, Skipped }

public enum ConnectionIntensity { Light, Detailed }

public enum ConnectionTestStatus { Running, Paused, Completed, Stopped, Failed }

/// <summary>Result of one probe from the prober (no timing context yet).</summary>
public sealed record ProbeReply(ProbeOutcome Outcome, double? RttMs, string? From = null, string? Detail = null)
{
    public static ProbeReply Fail(ProbeOutcome outcome, string? detail = null) => new(outcome, null, null, detail);
}

/// <summary>One recorded probe. RttMs only when Outcome = Ok.</summary>
public sealed record ConnectionProbeResult(
    long Sequence,
    DateTime WallUtc,
    long ElapsedMs,
    ConnectionSegment Segment,
    string Target,
    ProbeMethod Method,
    ProbeOutcome Outcome,
    double? RttMs,
    string? Detail = null);

/// <summary>Cumulative 802.11 frame counters reported by the driver (WLAN statistics); many drivers do not report them.</summary>
public sealed record WlanFrameCounters(ulong TxFrames, ulong TxRetries, ulong TxFailed, ulong AckFailures, ulong RxFrames, ulong FcsErrors);

/// <summary>Cheap read of the current association (no scan, no BSS list).</summary>
public sealed record WlanLinkReading
{
    public bool Connected { get; init; }
    public string State { get; init; } = "";
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public string? PhyType { get; init; }
    public int? Rssi { get; init; }
    public int? SignalQualityPct { get; init; }
    public double? RxRateMbps { get; init; }
    public double? TxRateMbps { get; init; }
    public int? Channel { get; init; }
    public WlanFrameCounters? Counters { get; init; }
}

/// <summary>One link sample per tick: the radio link plus this PC's own traffic on the interface.</summary>
public sealed record ConnectionLinkSample(
    long Sequence,
    DateTime WallUtc,
    long ElapsedMs,
    bool Connected,
    string? Ssid,
    string? Bssid,
    int? Channel,
    int? Rssi,
    int? SignalQualityPct,
    double? RxRateMbps,
    double? TxRateMbps,
    string? LocalIp,
    string? Gateway,
    long? IfBytesIn,
    long? IfBytesOut,
    WlanFrameCounters? Counters,
    /// <summary>Set when the loop woke this much later than planned (sleep/resume, CPU stall): the gap is unmeasured.</summary>
    long? GapMs = null);

/// <summary>What this PC is connected to when the test starts (provenance; also the "What is tested" card).</summary>
public sealed record ConnectionContext
{
    public bool WifiConnected { get; init; }
    public Guid InterfaceId { get; init; }
    public string InterfaceName { get; init; } = "";
    public int? InterfaceIndex { get; init; }
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public int? Channel { get; init; }
    public string? Band { get; init; }
    public string? PhyType { get; init; }
    public int? Rssi { get; init; }
    public double? RxRateMbps { get; init; }
    public double? TxRateMbps { get; init; }
    public string? LocalIPv4 { get; init; }
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    /// <summary>Other interfaces that are up with a default gateway (e.g. Ethernet): probes are bound to Wi-Fi anyway.</summary>
    public IReadOnlyList<string> OtherActiveInterfaces { get; init; } = Array.Empty<string>();
    /// <summary>Path found at start: hop IPs in order (gateway first); empty when discovery failed.</summary>
    public IReadOnlyList<string> PathHops { get; init; } = Array.Empty<string>();
    public string? IspHop { get; init; }
    public int? IspHopTtl { get; init; }
    /// <summary>True when the ISP hop answers echo requests; otherwise it is probed with TTL-limited packets.</summary>
    public bool IspHopAnswersEcho { get; init; }
    public string? Problem { get; init; }
}

/// <summary>Stage B: what to test against (LAN responder or a custom HTTP server). No public provider is preset (DR-0014).</summary>
public enum SpeedServerKind { None, Responder, Http }

public sealed record SpeedServerSettings
{
    public SpeedServerKind Kind { get; init; } = SpeedServerKind.None;
    public string HttpDownloadUrl { get; init; } = "";
    public string HttpUploadUrl { get; init; } = "";
    public int Streams { get; init; } = 4;
    public int SecondsPerDirection { get; init; } = 10;
}

/// <summary>Stage C: the paired LAN responder. The key is stored protected (DPAPI) and never written to reports.</summary>
public sealed record ResponderSettings
{
    public string Address { get; init; } = "";
    public int Port { get; init; } = 5410;
    public string Key { get; init; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(Key);
}

public sealed record ConnectionTestConfig
{
    public Guid TestId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    /// <summary>Null = until stopped.</summary>
    public TimeSpan? Duration { get; init; } = TimeSpan.FromMinutes(15);
    public ConnectionIntensity Intensity { get; init; } = ConnectionIntensity.Light;
    public Guid InterfaceId { get; init; }
    public string InterfaceName { get; init; } = "";

    public bool ProbeWifiLink { get; init; } = true;
    public bool ProbeGateway { get; init; } = true;
    public bool ProbeIsp { get; init; } = true;
    public bool ProbeInternet { get; init; } = true;
    public bool ProbeDns { get; init; } = true;
    public bool ProbeWeb { get; init; } = true;

    /// <summary>Public anycast targets (owner-approved defaults: Cloudflare, Google, Quad9). Probed by TCP 443 + ICMP.</summary>
    public IReadOnlyList<string> InternetTargets { get; init; } = DefaultInternetTargets;
    public int InternetTcpPort { get; init; } = 443;
    /// <summary>Name resolved on each configured resolver (IANA-reserved, stable).</summary>
    public string DnsQueryName { get; init; } = "example.com";
    public string WebUrl { get; init; } = "https://one.one.one.one/cdn-cgi/trace";

    public string Location { get; init; } = "";
    public string Facility { get; init; } = "";
    public string Operator { get; init; } = "";
    public string Client { get; init; } = "";
    public string Notes { get; init; } = "";
    public GeoTag Geo { get; init; } = new();
    public bool KeepAwake { get; init; } = true;
    public bool StopOnNetworkChange { get; init; }
    /// <summary>Saturation test started together with this one (one timeline); null when none.</summary>
    public Guid? LinkedSaturationTestId { get; init; }

    /// <summary>Stage B: run a speed test at start and/or every N minutes (0 = not scheduled).</summary>
    public bool SpeedTestAtStart { get; init; }
    public int SpeedTestEveryMinutes { get; init; }
    /// <summary>Stage C: run a two-way responder test at start and/or every N minutes.</summary>
    public bool TwoWayAtStart { get; init; }
    public int TwoWayEveryMinutes { get; init; }

    public ConnectionContext? ContextAtStart { get; init; }
    public SaturationEnvironment? Environment { get; init; }

    /// <summary>Base tick. Light = 1 s, Detailed = 0.5 s. Tests may shorten it.</summary>
    public int TickMilliseconds { get; init; } = 1000;

    public static readonly IReadOnlyList<string> DefaultInternetTargets = new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

    // Probe periods in ticks (Light): gateway 1 s, ISP 2 s, internet 2 s, DNS 10 s, web 30 s. Detailed halves the tick.
    public const int GatewayEveryTicks = 1, IspEveryTicks = 2, InternetEveryTicks = 2, DnsEveryTicks = 10, WebEveryTicks = 30;
    public const int GatewayTimeoutMs = 1000, IspTimeoutMs = 1500, InternetTimeoutMs = 2000, DnsTimeoutMs = 2000, WebTimeoutMs = 5000;

    /// <summary>Approximate probe traffic in kbit/s (both directions, IP headers included).</summary>
    public double EstimatedKbps()
    {
        double perSec = 1000.0 / TickMilliseconds;
        double bytes = 0;
        if (ProbeGateway) bytes += 2 * 74 * perSec / GatewayEveryTicks;                              // ICMP echo + reply
        if (ProbeIsp) bytes += 2 * 74 * perSec / IspEveryTicks;
        if (ProbeInternet) bytes += InternetTargets.Count * (2 * 74 + 4 * 60) * perSec / InternetEveryTicks; // ICMP + TCP SYN/SYN-ACK/ACK/RST
        if (ProbeDns) bytes += Math.Max(1, ContextAtStart?.DnsServers.Count(s => System.Net.IPAddress.TryParse(s, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? 1) * 2 * 90 * perSec / DnsEveryTicks;
        if (ProbeWeb) bytes += 9000 * perSec / WebEveryTicks;                                          // TLS handshake + tiny response
        return Math.Round(bytes * 8 / 1000, 1);
    }
}

public sealed record ConnectionMarker(DateTime WallUtc, long ElapsedMs, string Text);

public enum ConnectionEventType
{
    Outage, LatencySpike, LossBurst, Roam, Disconnect, Reconnect, IpChange, NetworkChange, SleepGap, SpeedTest, TwoWayTest
}

public sealed record ConnectionEvent(
    ConnectionEventType Type,
    ConnectionSegment? Segment,
    /// <summary>Where the problem most likely is (first failing segment upstream); null for informational events.</summary>
    ConnectionSegment? AttributedTo,
    DateTime StartUtc,
    DateTime? EndUtc,
    long StartElapsedMs,
    long? EndElapsedMs,
    double? DurationSeconds,
    int? Probes,
    double? PeakRttMs,
    string Detail);

/// <summary>Statistics for one target (or, in Segments, for a whole segment pooled across its targets).</summary>
public sealed record ConnectionSegmentSummary
{
    public ConnectionSegment Segment { get; init; }
    public string Target { get; init; } = "";
    public ProbeMethod Method { get; init; }
    public int Sent { get; init; }
    public int Ok { get; init; }
    public int Failed { get; init; }
    public IReadOnlyDictionary<string, int> FailureReasons { get; init; } = new Dictionary<string, int>();
    public double? LossPct { get; init; }
    public double? AvailabilityPct { get; init; }
    public double? RttMin { get; init; }
    public double? RttP50 { get; init; }
    public double? RttP95 { get; init; }
    public double? RttP99 { get; init; }
    public double? RttMax { get; init; }
    public double? RttMean { get; init; }
    /// <summary>Jitter: mean |RTT(n) − RTT(n−1)| over consecutive successful probes of one target (ms). Per-direction RFC 3550 jitter is in stage C.</summary>
    public double? JitterMs { get; init; }
    /// <summary>95th percentile of |RTT(n) − RTT(n−1)| over consecutive successful probes (round-trip delay variation, RFC 3393 style).</summary>
    public double? DelayVariationP95Ms { get; init; }
    public int Spikes { get; init; }
    public int Outages { get; init; }
    public double? LongestOutageSeconds { get; init; }
}

public sealed record UseVerdict(string Use, string Rating, string Reason);

public sealed record ConnectionLinkSummary
{
    public int Samples { get; init; }
    public int Roams { get; init; }
    public int Disconnects { get; init; }
    public IReadOnlyList<string> Bssids { get; init; } = Array.Empty<string>();
    public double? RssiMean { get; init; }
    public int? RssiMin { get; init; }
    public double? RxRateMean { get; init; }
    public double? TxRateMean { get; init; }
    public bool CountersAvailable { get; init; }
    public double? RetryPct { get; init; }
    public double? TxFailedPct { get; init; }
    public double? FcsErrorPct { get; init; }
    /// <summary>This PC's own traffic on the interface (kbit/s, both directions): context for spikes.</summary>
    public double? PcTrafficMeanKbps { get; init; }
    public double? PcTrafficMaxKbps { get; init; }
}

/// <summary>Stage B result.</summary>
public sealed record SpeedTestResult
{
    public DateTime StartUtc { get; init; }
    public long ElapsedMs { get; init; }
    public string Provider { get; init; } = "";
    public string Server { get; init; } = "";
    public int Streams { get; init; }
    public double SecondsPerDirection { get; init; }
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
    public long BytesDown { get; init; }
    public long BytesUp { get; init; }
    public double? IdleLatencyMs { get; init; }
    public double? LoadedLatencyDownMs { get; init; }
    public double? LoadedLatencyUpMs { get; init; }
    public double? LoadedLatencyP95Ms { get; init; }
    /// <summary>Median latency increase under load, worst direction (ms).</summary>
    public double? LatencyIncreaseMs { get; init; }
    public string BufferbloatGrade { get; init; } = "";
    /// <summary>Approximate responsiveness: 60 000 / median loaded round-trip (round trips per minute). Not the IETF RPM algorithm.</summary>
    public double? ResponsivenessRpm { get; init; }
    public string LatencyTarget { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>Stage C result: per-direction quality of the Wi-Fi/LAN segment measured against a wired responder.</summary>
public sealed record TwoWayResult
{
    public DateTime StartUtc { get; init; }
    public long ElapsedMs { get; init; }
    public string Responder { get; init; } = "";
    public double Seconds { get; init; }
    public int PacketsPerSecond { get; init; }
    public int PayloadBytes { get; init; }
    public int Sent { get; init; }
    public int ReceivedByResponder { get; init; }
    public int RepliesSent { get; init; }
    public int ReceivedBack { get; init; }
    public double? UpLossPct { get; init; }
    public double? DownLossPct { get; init; }
    public double? RttP50Ms { get; init; }
    public double? RttP95Ms { get; init; }
    public double? UpJitterMs { get; init; }
    public double? DownJitterMs { get; init; }
    public int UpReordered { get; init; }
    public int DownReordered { get; init; }
    public int Duplicates { get; init; }
    public double? MosUp { get; init; }
    public double? MosDown { get; init; }
    public double? LanDownMbps { get; init; }
    public double? LanUpMbps { get; init; }
    public string? Error { get; init; }
}

public sealed record ConnectionTotals(int ProbesSent, int ProbesOk, int ProbesFailed, int LinkSamples, int SleepGaps, double UnmeasuredSeconds);

public sealed record ConnectionReport(
    ConnectionTestConfig Config,
    string AnalyticsVersion,
    DateTime StartUtc,
    DateTime? EndUtc,
    double ActiveSeconds,
    ConnectionTestStatus Status,
    IReadOnlyList<ConnectionSegmentSummary> Segments,
    IReadOnlyList<ConnectionSegmentSummary> Targets,
    IReadOnlyList<ConnectionEvent> Events,
    IReadOnlyDictionary<ConnectionSegment, int> ProblemsBySegment,
    IReadOnlyList<UseVerdict> Verdicts,
    double? VoiceRFactor,
    double? VoiceMos,
    ConnectionLinkSummary Link,
    IReadOnlyList<SpeedTestResult> SpeedTests,
    IReadOnlyList<TwoWayResult> TwoWayTests,
    IReadOnlyList<ConnectionMarker> Markers,
    ConnectionTotals Totals,
    IReadOnlyList<ConnectionProbeResult> Probes,
    IReadOnlyList<ConnectionLinkSample> LinkSamples);

public sealed record ConnectionTestRecord(ConnectionTestConfig Config, DateTime StartUtc, DateTime? EndUtc, ConnectionTestStatus Status, int ProbeCount, double? ActiveSeconds);

public sealed record ConnectionProgress(
    ConnectionTestConfig Config,
    ConnectionTestStatus Status,
    DateTime StartUtc,
    double ActiveSeconds,
    double? RemainingSeconds,
    long Tick,
    IReadOnlyList<ConnectionProbeResult> LastProbes,
    ConnectionLinkSample? LastLink,
    string? Activity);

// ------------------------------------------------------------------ services

/// <summary>Sends probes bound to the Wi-Fi interface's address. Implementations never throw for network failures.</summary>
public interface IConnectionProber
{
    Task<ConnectionContext> GetContextAsync(Guid interfaceId, CancellationToken ct = default);
    Task<WlanLinkReading?> ReadLinkAsync(Guid interfaceId);
    (long BytesIn, long BytesOut)? InterfaceBytes(Guid interfaceId);
    Task<ProbeReply> IcmpAsync(string target, string source, int ttl, int timeoutMs, CancellationToken ct = default);
    Task<ProbeReply> TcpConnectAsync(string target, int port, string source, int timeoutMs, CancellationToken ct = default);
    Task<ProbeReply> DnsQueryAsync(string server, string name, string source, int timeoutMs, CancellationToken ct = default);
    Task<ProbeReply> HttpsAsync(string url, string source, int timeoutMs, CancellationToken ct = default);
}

/// <summary>Stage B: capacity in one direction for a fixed time. Returns bytes and the measured steady-state seconds.</summary>
public interface ISpeedTestClient
{
    string Provider { get; }
    string Server { get; }
    Task<(long Bytes, double Seconds, string? Error)> DownloadAsync(string source, int streams, TimeSpan duration, CancellationToken ct);
    Task<(long Bytes, double Seconds, string? Error)> UploadAsync(string source, int streams, TimeSpan duration, CancellationToken ct);
}

/// <summary>Stage C: two-way stream and LAN throughput against a paired responder.</summary>
public interface ITwoWayClient
{
    string Responder { get; }
    Task<TwoWayResult> RunStreamAsync(string source, TimeSpan duration, int packetsPerSecond, int payloadBytes, CancellationToken ct);
    Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct);
}

public interface IConnectionAnalyzer
{
    string Version { get; }
    ConnectionReport Analyze(ConnectionTestConfig config, IReadOnlyList<ConnectionProbeResult> probes, IReadOnlyList<ConnectionLinkSample> link,
        IReadOnlyList<SpeedTestResult> speed, IReadOnlyList<TwoWayResult> twoWay, IReadOnlyList<ConnectionMarker> markers,
        DateTime startUtc, DateTime? endUtc, ConnectionTestStatus status, double activeSeconds);
}

public interface IConnectionRepository
{
    Task SaveTestAsync(ConnectionTestConfig config, DateTime startUtc, ConnectionTestStatus status);
    Task UpdateTestStatusAsync(Guid testId, ConnectionTestStatus status, DateTime? endUtc, double? activeSeconds = null);
    Task SaveProbesAsync(Guid testId, IReadOnlyList<ConnectionProbeResult> probes);
    Task SaveLinkSamplesAsync(Guid testId, IReadOnlyList<ConnectionLinkSample> samples);
    Task SaveMarkerAsync(Guid testId, ConnectionMarker marker);
    Task SaveSpeedTestAsync(Guid testId, SpeedTestResult result);
    Task SaveTwoWayAsync(Guid testId, TwoWayResult result);
    Task<IReadOnlyList<ConnectionTestRecord>> GetTestsAsync();
    Task<(IReadOnlyList<ConnectionProbeResult> Probes, IReadOnlyList<ConnectionLinkSample> Link, IReadOnlyList<SpeedTestResult> Speed,
        IReadOnlyList<TwoWayResult> TwoWay, IReadOnlyList<ConnectionMarker> Markers)> GetDataAsync(Guid testId);
    Task DeleteTestAsync(Guid testId);
}

public interface IConnectionExportService
{
    Task ExportPdfAsync(string filePath, ConnectionReport report);
    Task<IReadOnlyList<string>> ExportCsvBundleAsync(string directory, string filePrefix, ConnectionReport report);
    Task ExportJsonAsync(string filePath, ConnectionReport report);
}

public interface IConnectionTestRunner
{
    bool IsRunning { get; }
    ConnectionTestStatus? Status { get; }
    ConnectionTestConfig? Config { get; }
    event Action<ConnectionProgress>? Progress;
    event Action<ConnectionReport>? Completed;
    Task StartAsync(ConnectionTestConfig config, CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    Task<ConnectionReport?> StopAsync();
    void AddMarker(string text);
    /// <summary>Snapshot of everything recorded so far (for the live view and the REST API).</summary>
    (IReadOnlyList<ConnectionProbeResult> Probes, IReadOnlyList<ConnectionLinkSample> Link, IReadOnlyList<SpeedTestResult> Speed,
        IReadOnlyList<TwoWayResult> TwoWay, IReadOnlyList<ConnectionMarker> Markers, DateTime StartUtc, double ActiveSeconds) Snapshot();
    Task<SpeedTestResult?> RunSpeedTestAsync(ISpeedTestClient client, SpeedServerSettings settings, CancellationToken ct = default);
    Task<TwoWayResult?> RunTwoWayAsync(ITwoWayClient client, CancellationToken ct = default);
}
