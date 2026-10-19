using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Responder;

namespace WirelessScanner.Tests;

/// <summary>Synthetic probe series with known answers for the connection analytics (CONN-ANALYTICS-1, DR-0014).</summary>
internal static class Conn
{
    public static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    public static ConnectionTestConfig Config() => new()
    {
        Name = "Synthetic", TickMilliseconds = 1000, InternetTargets = new[] { "1.1.1.1", "8.8.8.8" },
        ContextAtStart = new ConnectionContext { WifiConnected = true, LocalIPv4 = "192.168.1.100", Gateways = new[] { "192.168.1.1" }, DnsServers = new[] { "192.168.1.1" },
            Ssid = "Home", Bssid = "9A:3C:5E:26:42:70", IspHop = "203.0.113.1", IspHopTtl = 2, IspHopAnswersEcho = true }
    };

    public static ConnectionProbeResult P(long tick, ConnectionSegment seg, string target, double? rtt, ProbeMethod m = ProbeMethod.Icmp, ProbeOutcome? outcome = null) =>
        new(tick, T0.AddSeconds(tick), tick * 1000, seg, target, m, outcome ?? (rtt.HasValue ? ProbeOutcome.Ok : ProbeOutcome.Timeout), rtt);

    public static ConnectionLinkSample L(long tick, bool connected = true, string bssid = "9A:3C:5E:26:42:70", int rssi = -50, long? gap = null, string ssid = "Home",
        WlanFrameCounters? counters = null, long? bytesIn = null) =>
        new(tick, T0.AddSeconds(tick), tick * 1000, connected, connected ? ssid : null, connected ? bssid : null, 149, connected ? rssi : null, 90, 866, 780,
            "192.168.1.100", "192.168.1.1", bytesIn, bytesIn, counters, gap);

    /// <summary>A healthy path: gateway 2 ms, ISP 10 ms, internet 20 ms (TCP) for n ticks.</summary>
    public static List<ConnectionProbeResult> Healthy(int n, Func<long, ConnectionSegment, double?>? overrideRtt = null)
    {
        var list = new List<ConnectionProbeResult>();
        for (long t = 0; t < n; t++)
        {
            double? R(ConnectionSegment s, double v) => overrideRtt?.Invoke(t, s) is double o ? (double.IsNaN(o) ? null : o) : v;
            list.Add(P(t, ConnectionSegment.Gateway, "192.168.1.1", R(ConnectionSegment.Gateway, 2 + t % 2)));
            list.Add(P(t, ConnectionSegment.IspHop, "203.0.113.1", R(ConnectionSegment.IspHop, 10)));
            list.Add(P(t, ConnectionSegment.Internet, "1.1.1.1", R(ConnectionSegment.Internet, 20 + t % 3), ProbeMethod.Tcp));
            list.Add(P(t, ConnectionSegment.Internet, "8.8.8.8", R(ConnectionSegment.Internet, 22), ProbeMethod.Tcp));
        }
        return list;
    }

    public static ConnectionReport Analyze(List<ConnectionProbeResult> probes, List<ConnectionLinkSample>? link = null) =>
        new ConnectionAnalyzer().Analyze(Config(), probes, link ?? new List<ConnectionLinkSample>(), Array.Empty<SpeedTestResult>(), Array.Empty<TwoWayResult>(),
            Array.Empty<ConnectionMarker>(), T0, T0.AddSeconds(probes.Max(p => p.ElapsedMs) / 1000.0 + 1), ConnectionTestStatus.Completed, probes.Max(p => p.ElapsedMs) / 1000.0 + 1);
}

[TestFixture]
public class ConnectionAnalyzerTests
{
    private static readonly double? Fail = double.NaN;

    [Test]
    public void Statistics_MatchHandComputedValues()
    {
        var probes = new List<ConnectionProbeResult>();
        double[] rtts = { 10, 12, 11, 30, 13 };
        for (int i = 0; i < rtts.Length; i++) probes.Add(Conn.P(i, ConnectionSegment.Gateway, "gw", rtts[i]));
        probes.Add(Conn.P(5, ConnectionSegment.Gateway, "gw", null));
        var s = Conn.Analyze(probes).Segments.Single(x => x.Segment == ConnectionSegment.Gateway);
        Assert.Multiple(() =>
        {
            Assert.That(s.Sent, Is.EqualTo(6));
            Assert.That(s.Ok, Is.EqualTo(5));
            Assert.That(s.LossPct, Is.EqualTo(Math.Round(100.0 / 6, 3)));
            Assert.That(s.RttMin, Is.EqualTo(10));
            Assert.That(s.RttP50, Is.EqualTo(12), "nearest rank: ceil(0.5·5) = 3rd of 10,11,12,13,30");
            Assert.That(s.RttP95, Is.EqualTo(30));
            Assert.That(s.RttMean, Is.EqualTo(15.2));
            Assert.That(s.JitterMs, Is.EqualTo((2 + 1 + 19 + 17) / 4.0), "mean |ΔRTT| = (2+1+19+17)/4");
            Assert.That(s.FailureReasons["Timeout"], Is.EqualTo(1));
        });
    }

    [Test]
    public void InternetAvailability_CountsRoundsWithAnyTargetUp()
    {
        // tick 1: one target fails, the other answers -> the round is available; tick 2: both fail
        var probes = Conn.Healthy(4, (t, s) => s != ConnectionSegment.Internet ? null : t == 2 ? Fail : null);
        probes.RemoveAll(p => p.Sequence == 1 && p.Target == "8.8.8.8");
        probes.Add(Conn.P(1, ConnectionSegment.Internet, "8.8.8.8", null, ProbeMethod.Tcp));
        var inet = Conn.Analyze(probes).Segments.Single(x => x.Segment == ConnectionSegment.Internet);
        Assert.That(inet.AvailabilityPct, Is.EqualTo(75), "3 of 4 rounds had a successful probe");
        Assert.That(inet.LossPct, Is.EqualTo(Math.Round(100.0 * 3 / 8, 3)));
    }

    [Test]
    public void Outage_AtRouter_IsAttributedToRouter_AndInternetOutageFollows()
    {
        var probes = Conn.Healthy(20, (t, s) => t is >= 5 and <= 9 && s is ConnectionSegment.Gateway or ConnectionSegment.IspHop or ConnectionSegment.Internet ? Fail : null);
        var r = Conn.Analyze(probes, Enumerable.Range(0, 20).Select(t => Conn.L(t)).ToList());
        var outages = r.Events.Where(e => e.Type == ConnectionEventType.Outage).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(outages.Select(o => o.Segment), Is.EquivalentTo(new ConnectionSegment?[] { ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet }));
            Assert.That(outages.All(o => o.AttributedTo == ConnectionSegment.Gateway), Is.True, "everything downstream of a dead router is the router's fault");
            Assert.That(outages.First().DurationSeconds, Is.EqualTo(5));
            Assert.That(r.ProblemsBySegment[ConnectionSegment.Gateway], Is.EqualTo(3));
        });
    }

    [Test]
    public void Outage_BeyondHealthyRouterAndIsp_IsAttributedToInternet()
    {
        var probes = Conn.Healthy(20, (t, s) => t is >= 8 and <= 11 && s == ConnectionSegment.Internet ? Fail : null);
        var o = Conn.Analyze(probes).Events.Single(e => e.Type == ConnectionEventType.Outage);
        Assert.That(o.AttributedTo, Is.EqualTo(ConnectionSegment.Internet));
        Assert.That(o.Detail, Does.Contain("beyond"));
    }

    [Test]
    public void Outage_WhenWifiDisconnected_IsAttributedToWifiLink()
    {
        var probes = Conn.Healthy(20, (t, s) => t is >= 5 and <= 8 ? Fail : null);
        var link = Enumerable.Range(0, 20).Select(t => Conn.L(t, connected: t is < 5 or > 8)).ToList();
        var r = Conn.Analyze(probes, link);
        Assert.Multiple(() =>
        {
            Assert.That(r.Events.Where(e => e.Type == ConnectionEventType.Outage).All(e => e.AttributedTo == ConnectionSegment.WifiLink), Is.True);
            Assert.That(r.Events.Single(e => e.Type == ConnectionEventType.Disconnect).DurationSeconds, Is.EqualTo(4));
            Assert.That(r.Link.Disconnects, Is.EqualTo(1));
        });
    }

    [Test]
    public void IspHopSilentWhileInternetFine_IsNotAProblem()
    {
        var probes = Conn.Healthy(20, (t, s) => t is >= 3 and <= 12 && s == ConnectionSegment.IspHop ? Fail : null);
        var r = Conn.Analyze(probes);
        var o = r.Events.Single(e => e.Type == ConnectionEventType.Outage);
        Assert.Multiple(() =>
        {
            Assert.That(o.Segment, Is.EqualTo(ConnectionSegment.IspHop));
            Assert.That(o.AttributedTo, Is.Null, "rate-limited ICMP at the ISP router is not a fault");
            Assert.That(r.ProblemsBySegment, Is.Empty);
        });
    }

    [Test]
    public void TwoFailedRounds_AreNotAnOutage()
    {
        var probes = Conn.Healthy(20, (t, s) => t is 5 or 6 && s == ConnectionSegment.Gateway ? Fail : null);
        Assert.That(Conn.Analyze(probes).Events.Any(e => e.Type == ConnectionEventType.Outage), Is.False);
    }

    [Test]
    public void LatencySpike_DetectedAboveBaseline_AndAttributedLocallyWhenRouterSlowToo()
    {
        var probes = Conn.Healthy(40, (t, s) => t is 30 or 31 ? (s == ConnectionSegment.Gateway ? 150 : s == ConnectionSegment.Internet ? 180 : null) : null);
        var r = Conn.Analyze(probes);
        var spikes = r.Events.Where(e => e.Type == ConnectionEventType.LatencySpike).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(spikes.Select(s => s.Segment).Distinct(), Is.EquivalentTo(new ConnectionSegment?[] { ConnectionSegment.Gateway, ConnectionSegment.Internet }));
            Assert.That(spikes.Where(s => s.Segment == ConnectionSegment.Internet).All(s => s.AttributedTo == ConnectionSegment.Gateway), Is.True);
            Assert.That(spikes.First(s => s.Segment == ConnectionSegment.Gateway).PeakRttMs, Is.EqualTo(150));
        });
    }

    [Test]
    public void SmallVariation_IsNotASpike()
    {
        var probes = Conn.Healthy(40, (t, s) => t == 30 && s == ConnectionSegment.Internet ? 60 : null);   // 3x baseline but < baseline + 100 ms
        Assert.That(Conn.Analyze(probes).Events.Any(e => e.Type == ConnectionEventType.LatencySpike), Is.False);
    }

    [Test]
    public void LinkEvents_RoamNetworkChangeAndSleepGap()
    {
        var link = new List<ConnectionLinkSample>
        {
            Conn.L(0), Conn.L(1), Conn.L(2, bssid: "9A:3C:5E:1F:42:70", rssi: -60), Conn.L(3, bssid: "9A:3C:5E:1F:42:70", ssid: "Office", rssi: -55),
            Conn.L(10, bssid: "9A:3C:5E:1F:42:70", ssid: "Office", gap: 7000)
        };
        var r = Conn.Analyze(Conn.Healthy(11), link);
        Assert.Multiple(() =>
        {
            Assert.That(r.Events.Count(e => e.Type == ConnectionEventType.Roam), Is.EqualTo(1));
            Assert.That(r.Events.Single(e => e.Type == ConnectionEventType.Roam).Detail, Does.Contain("-50 → -60 dBm"));
            Assert.That(r.Events.Count(e => e.Type == ConnectionEventType.NetworkChange), Is.EqualTo(1), "an SSID change is a network change, not a roam");
            Assert.That(r.Events.Single(e => e.Type == ConnectionEventType.SleepGap).DurationSeconds, Is.EqualTo(7));
            Assert.That(r.Totals.SleepGaps, Is.EqualTo(1));
            Assert.That(r.Totals.UnmeasuredSeconds, Is.EqualTo(7));
        });
    }

    [Test]
    public void FrameCounters_RetryPercentFromDeltas_AndResetIgnored()
    {
        var link = new List<ConnectionLinkSample>
        {
            Conn.L(0, counters: new WlanFrameCounters(1000, 100, 1, 0, 5000, 10)),
            Conn.L(1, counters: new WlanFrameCounters(2000, 150, 3, 0, 7000, 30)),
            Conn.L(2, counters: new WlanFrameCounters(10, 1, 0, 0, 20, 0)),                // driver reset
            Conn.L(3, counters: new WlanFrameCounters(1010, 51, 2, 0, 1020, 0))
        };
        var l = Conn.Analyze(Conn.Healthy(4), link).Link;
        Assert.Multiple(() =>
        {
            Assert.That(l.CountersAvailable, Is.True);
            Assert.That(l.RetryPct, Is.EqualTo(Math.Round(100.0 * (50 + 50) / (1000 + 1000), 3)));
            Assert.That(l.TxFailedPct, Is.EqualTo(Math.Round(100.0 * (2 + 2) / 2000, 3)));
            Assert.That(l.FcsErrorPct, Is.EqualTo(Math.Round(100.0 * 20 / (3000 + 20), 3)));
        });
    }

    [Test]
    public void Verdicts_AndVoiceEstimate_FromInternetSegment()
    {
        var good = Conn.Analyze(Conn.Healthy(30));
        var lossy = Conn.Analyze(Conn.Healthy(30, (t, s) => s == ConnectionSegment.Internet && t % 10 == 0 ? Fail : s == ConnectionSegment.Internet ? 120 + (t % 2) * 60 : null));
        Assert.Multiple(() =>
        {
            Assert.That(good.Verdicts.All(v => v.Rating == "GOOD"), Is.True, string.Join("; ", good.Verdicts.Select(v => v.Use + " " + v.Rating + " " + v.Reason)));
            Assert.That(good.VoiceMos, Is.GreaterThan(4.3));
            Assert.That(lossy.Verdicts.Single(v => v.Use == "Online gaming").Rating, Is.EqualTo("POOR"));
            Assert.That(lossy.VoiceMos, Is.LessThan(good.VoiceMos));
            Assert.That(good.Verdicts.First().Reason, Does.Contain("p95"));
        });
    }

    [Test]
    public void Totals_Reconcile_AndSkippedProbesAreNotSent()
    {
        var probes = Conn.Healthy(10);
        probes.Add(Conn.P(10, ConnectionSegment.Web, "https://x", null, ProbeMethod.Https, ProbeOutcome.Skipped));
        var r = Conn.Analyze(probes);
        Assert.That(r.Totals.ProbesSent, Is.EqualTo(40));
        Assert.That(r.Totals.ProbesOk + r.Totals.ProbesFailed, Is.EqualTo(r.Totals.ProbesSent));
        Assert.That(r.Segments.Any(s => s.Segment == ConnectionSegment.Web), Is.False);
    }

    [Test]
    public void Bufferbloat_Grades()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConnectionTestRunner.BufferbloatGrade(3), Is.EqualTo("A+"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(29), Is.EqualTo("A"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(59), Is.EqualTo("B"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(199), Is.EqualTo("C"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(399), Is.EqualTo("D"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(400), Is.EqualTo("F"));
            Assert.That(ConnectionTestRunner.BufferbloatGrade(null), Is.EqualTo(""));
        });
    }

    [Test]
    public void LocalAddresses_ForIspHopDiscovery()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PathDiscovery.IsLocalAddress("192.168.1.1"), Is.True);
            Assert.That(PathDiscovery.IsLocalAddress("10.0.0.1"), Is.True);
            Assert.That(PathDiscovery.IsLocalAddress("172.20.1.1"), Is.True);
            Assert.That(PathDiscovery.IsLocalAddress("172.32.1.1"), Is.False);
            Assert.That(PathDiscovery.IsLocalAddress("100.64.0.1"), Is.False, "carrier-grade NAT is the ISP's side");
            Assert.That(PathDiscovery.IsLocalAddress("203.0.113.1"), Is.False);
        });
    }
}

// ------------------------------------------------------------------ runner with a scripted prober

internal sealed class FakeProber : IConnectionProber
{
    public Func<string, int, ProbeReply> Icmp { get; set; } = (t, ttl) => ttl == 1 ? new ProbeReply(ProbeOutcome.TtlExpired, 1, "192.168.1.1")
        : ttl == 2 ? new ProbeReply(ProbeOutcome.TtlExpired, 8, "203.0.113.1") : new ProbeReply(ProbeOutcome.Ok, t == "192.168.1.1" ? 2 : t == "203.0.113.1" ? 9 : 20, t);
    public Func<string, ProbeReply> Tcp { get; set; } = t => new ProbeReply(ProbeOutcome.Ok, 21, t);
    public bool Connected { get; set; } = true;
    public string Bssid { get; set; } = "9A:3C:5E:26:42:70";
    public string Ssid { get; set; } = "Home";
    public readonly List<string> Sources = new();
    public int IcmpCalls, TcpCalls, DnsCalls, WebCalls;

    public ConnectionContext Context => new()
    {
        WifiConnected = Connected, LocalIPv4 = Connected ? "192.168.1.100" : null, Gateways = new[] { "192.168.1.1" }, DnsServers = new[] { "192.168.1.1", "fe80::1" },
        Ssid = Ssid, Bssid = Bssid, InterfaceName = "Fake Wi-Fi"
    };
    public Task<ConnectionContext> GetContextAsync(Guid interfaceId, CancellationToken ct = default) => Task.FromResult(Context);
    public Task<WlanLinkReading?> ReadLinkAsync(Guid interfaceId) => Task.FromResult<WlanLinkReading?>(new WlanLinkReading { Connected = Connected, Ssid = Ssid, Bssid = Bssid, Rssi = -50, Channel = 149 });
    public (long BytesIn, long BytesOut)? InterfaceBytes(Guid interfaceId) => (1000, 1000);
    public Task<ProbeReply> IcmpAsync(string target, string source, int ttl, int timeoutMs, CancellationToken ct = default)
    { lock (Sources) { Sources.Add(source); IcmpCalls++; } return Task.FromResult(Icmp(target, ttl)); }
    public Task<ProbeReply> TcpConnectAsync(string target, int port, string source, int timeoutMs, CancellationToken ct = default)
    { lock (Sources) { Sources.Add(source); TcpCalls++; } return Task.FromResult(Tcp(target)); }
    public Task<ProbeReply> DnsQueryAsync(string server, string name, string source, int timeoutMs, CancellationToken ct = default)
    { lock (Sources) { Sources.Add(source); DnsCalls++; } return Task.FromResult(new ProbeReply(ProbeOutcome.Ok, 12, server)); }
    public Task<ProbeReply> HttpsAsync(string url, string source, int timeoutMs, CancellationToken ct = default)
    { lock (Sources) { Sources.Add(source); WebCalls++; } return Task.FromResult(new ProbeReply(ProbeOutcome.Ok, 150, url)); }
}

internal sealed class MemoryConnectionRepository : IConnectionRepository
{
    public readonly List<ConnectionProbeResult> Probes = new();
    public readonly List<ConnectionLinkSample> Link = new();
    public readonly List<SpeedTestResult> Speed = new();
    public readonly List<TwoWayResult> TwoWay = new();
    public readonly List<ConnectionMarker> Markers = new();
    public readonly List<ConnectionTestRecord> Tests = new();
    public Task SaveTestAsync(ConnectionTestConfig config, DateTime startUtc, ConnectionTestStatus status) { lock (Tests) Tests.Add(new(config, startUtc, null, status, 0, null)); return Task.CompletedTask; }
    public Task UpdateTestStatusAsync(Guid testId, ConnectionTestStatus status, DateTime? endUtc, double? activeSeconds = null)
    {
        lock (Tests)
        {
            int i = Tests.FindIndex(t => t.Config.TestId == testId);
            if (i >= 0) Tests[i] = Tests[i] with { Status = status, EndUtc = endUtc ?? Tests[i].EndUtc, ActiveSeconds = activeSeconds ?? Tests[i].ActiveSeconds };
        }
        return Task.CompletedTask;
    }
    public Task SaveProbesAsync(Guid testId, IReadOnlyList<ConnectionProbeResult> probes) { lock (Probes) Probes.AddRange(probes); return Task.CompletedTask; }
    public Task SaveLinkSamplesAsync(Guid testId, IReadOnlyList<ConnectionLinkSample> samples) { lock (Link) Link.AddRange(samples); return Task.CompletedTask; }
    public Task SaveMarkerAsync(Guid testId, ConnectionMarker marker) { lock (Markers) Markers.Add(marker); return Task.CompletedTask; }
    public Task SaveSpeedTestAsync(Guid testId, SpeedTestResult result) { lock (Speed) Speed.Add(result); return Task.CompletedTask; }
    public Task SaveTwoWayAsync(Guid testId, TwoWayResult result) { lock (TwoWay) TwoWay.Add(result); return Task.CompletedTask; }
    public Task<IReadOnlyList<ConnectionTestRecord>> GetTestsAsync() { lock (Tests) return Task.FromResult<IReadOnlyList<ConnectionTestRecord>>(Tests.Select(t => t with { ProbeCount = Probes.Count }).ToList()); }
    public Task<(IReadOnlyList<ConnectionProbeResult> Probes, IReadOnlyList<ConnectionLinkSample> Link, IReadOnlyList<SpeedTestResult> Speed, IReadOnlyList<TwoWayResult> TwoWay, IReadOnlyList<ConnectionMarker> Markers)> GetDataAsync(Guid testId)
        => Task.FromResult<(IReadOnlyList<ConnectionProbeResult>, IReadOnlyList<ConnectionLinkSample>, IReadOnlyList<SpeedTestResult>, IReadOnlyList<TwoWayResult>, IReadOnlyList<ConnectionMarker>)>(
            (Probes.ToList(), Link.ToList(), Speed.ToList(), TwoWay.ToList(), Markers.ToList()));
    public Task DeleteTestAsync(Guid testId) { lock (Tests) Tests.RemoveAll(t => t.Config.TestId == testId); return Task.CompletedTask; }
}

internal sealed class FakeSpeed : ISpeedTestClient
{
    public string Provider => "Fake";
    public string Server => "fake:1";
    public async Task<(long Bytes, double Seconds, string? Error)> DownloadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    { await Task.Delay(300, ct); return (12_500_000, 1.0, null); }
    public async Task<(long Bytes, double Seconds, string? Error)> UploadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    { await Task.Delay(300, ct); return (2_500_000, 1.0, null); }
}

[TestFixture]
public class ConnectionRunnerTests
{
    private static ConnectionTestConfig Cfg(FakeProber p, int seconds = 1) => Conn.Config() with
    {
        TickMilliseconds = 50, Duration = TimeSpan.FromMilliseconds(seconds * 1000), ContextAtStart = p.Context, InternetTargets = new[] { "1.1.1.1", "8.8.8.8" }
    };

    [Test]
    public async Task Runs_Ticks_WithDueProbes_BoundToWifiAddress_AndPersistsEverything()
    {
        var prober = new FakeProber();
        var repo = new MemoryConnectionRepository();
        await using var runner = new ConnectionTestRunner(prober, repo, new ConnectionAnalyzer(), new NullLogger());
        await runner.StartAsync(Cfg(prober, 2));
        var report = await runner.WaitForCompletionAsync();
        Assert.That(report, Is.Not.Null);
        var r = report!;
        Assert.Multiple(() =>
        {
            Assert.That(r.Status, Is.EqualTo(ConnectionTestStatus.Completed));
            Assert.That(r.Config.ContextAtStart!.IspHop, Is.EqualTo("203.0.113.1"), "ISP hop found by TTL-limited discovery");
            Assert.That(r.Config.ContextAtStart.IspHopAnswersEcho, Is.True);
            Assert.That(prober.Sources.Distinct(), Is.EqualTo(new[] { "192.168.1.100" }), "every probe bound to the Wi-Fi address");
            Assert.That(r.Probes.Count(p => p.Segment == ConnectionSegment.Gateway), Is.GreaterThan(r.Probes.Count(p => p.Segment == ConnectionSegment.Internet && p.Method == ProbeMethod.Tcp) / 2 * 1.5),
                "gateway every tick, each internet target every 2 ticks");
            Assert.That(r.Probes.Where(p => p.Segment == ConnectionSegment.Dns).Select(p => p.Target).Distinct(), Is.EqualTo(new[] { "192.168.1.1" }), "IPv6 resolvers skipped");
            Assert.That(r.Probes.Any(p => p.Segment == ConnectionSegment.Web), Is.True);
            Assert.That(r.LinkSamples.Count, Is.GreaterThan(10));
            Assert.That(repo.Probes.Count, Is.EqualTo(r.Probes.Count), "every probe persisted");
            Assert.That(repo.Link.Count, Is.EqualTo(r.LinkSamples.Count));
            Assert.That(repo.Tests.Single().Status, Is.EqualTo(ConnectionTestStatus.Completed));
            Assert.That(r.ActiveSeconds, Is.EqualTo(2).Within(0.001));
            Assert.That(r.Segments.Single(s => s.Segment == ConnectionSegment.Internet).Method, Is.EqualTo(ProbeMethod.Tcp));
        });
    }

    [Test]
    public void RefusesToStart_WithoutWifiConnection()
    {
        var prober = new FakeProber { Connected = false };
        var runner = new ConnectionTestRunner(prober, new MemoryConnectionRepository(), new ConnectionAnalyzer(), new NullLogger());
        var ex = Assert.ThrowsAsync<ArgumentException>(() => runner.StartAsync(Cfg(prober)));
        Assert.That(ex!.Message, Does.Contain("not connected over Wi-Fi"));
    }

    [Test]
    public async Task IspHopWithoutEcho_IsProbedWithTtlLimitedPackets()
    {
        var prober = new FakeProber();
        prober.Icmp = (t, ttl) => ttl == 1 ? new ProbeReply(ProbeOutcome.TtlExpired, 1, "192.168.1.1")
            : ttl == 2 ? new ProbeReply(ProbeOutcome.TtlExpired, 8, "203.0.113.1")
            : t == "203.0.113.1" ? ProbeReply.Fail(ProbeOutcome.Timeout) : new ProbeReply(ProbeOutcome.Ok, 20, t);
        await using var runner = new ConnectionTestRunner(prober, new MemoryConnectionRepository(), new ConnectionAnalyzer(), new NullLogger());
        await runner.StartAsync(Cfg(prober));
        var r = (await runner.WaitForCompletionAsync())!;
        var isp = r.Probes.Where(p => p.Segment == ConnectionSegment.IspHop).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(r.Config.ContextAtStart!.IspHopAnswersEcho, Is.False);
            Assert.That(isp, Is.Not.Empty);
            Assert.That(isp.All(p => p.Method == ProbeMethod.IcmpTtl && p.Outcome == ProbeOutcome.Ok), Is.True, "time-exceeded from the ISP hop = success");
        });
    }

    [Test]
    public async Task StopOnNetworkChange_StopsAndMarks()
    {
        var prober = new FakeProber();
        await using var runner = new ConnectionTestRunner(prober, new MemoryConnectionRepository(), new ConnectionAnalyzer(), new NullLogger());
        await runner.StartAsync(Cfg(prober, 30) with { StopOnNetworkChange = true });
        await Task.Delay(300);
        prober.Ssid = "Neighbour";
        var r = (await runner.WaitForCompletionAsync())!;
        Assert.That(r.Status, Is.EqualTo(ConnectionTestStatus.Stopped));
        Assert.That(r.Markers.Single().Text, Does.Contain("Neighbour"));
        Assert.That(r.ActiveSeconds, Is.LessThan(10));
    }

    [Test]
    public async Task SpeedTest_PausesRegularProbes_AndRecordsLatencyUnderLoad()
    {
        var prober = new FakeProber();
        int calls = 0;
        prober.Tcp = t => new ProbeReply(ProbeOutcome.Ok, Interlocked.Increment(ref calls) > 40 ? 120 : 20, t);   // later probes slower (under load)
        var repo = new MemoryConnectionRepository();
        await using var runner = new ConnectionTestRunner(prober, repo, new ConnectionAnalyzer(), new NullLogger());
        await runner.StartAsync(Cfg(prober, 30));
        await Task.Delay(200);
        var s = await runner.RunSpeedTestAsync(new FakeSpeed(), new SpeedServerSettings { Kind = SpeedServerKind.Http, Streams = 2, SecondsPerDirection = 3 });
        await runner.StopAsync();
        Assert.That(s, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(s!.DownloadMbps, Is.EqualTo(100), "12.5 MB in 1 s = 100 Mbps");
            Assert.That(s.UploadMbps, Is.EqualTo(20));
            Assert.That(s.IdleLatencyMs, Is.Not.Null);
            Assert.That(s.LatencyTarget, Does.StartWith("internet 1.1.1.1"));
            Assert.That(s.BufferbloatGrade, Is.Not.Empty);
            Assert.That(repo.Speed, Has.Count.EqualTo(1));
        });
    }
}

// ------------------------------------------------------------------ responder end to end (loopback)

[TestFixture]
public class ResponderTests
{
    private static readonly byte[] Key = ResponderProtocol.NewKey();

    [Test]
    public async Task TwoWayStream_OverLoopback_HasNoLoss_AndSaneTimings()
    {
        await using var server = new ResponderServer(Key, 0, IPAddress.Loopback, "Test responder");
        server.Start();
        var client = new ResponderClient(new ResponderSettings { Address = "127.0.0.1", Port = server.Port, Key = ResponderProtocol.KeyToText(Key) });
        var check = await client.CheckAsync(CancellationToken.None);
        Assert.That(check.Ok, Is.True, check.Message);
        Assert.That(check.Message, Does.Contain("Test responder"));
        var r = await client.RunStreamAsync("127.0.0.1", TimeSpan.FromSeconds(2), 50, 172, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(r.Error, Is.Null);
            Assert.That(r.Sent, Is.EqualTo(100));
            Assert.That(r.ReceivedByResponder, Is.EqualTo(100));
            Assert.That(r.ReceivedBack, Is.EqualTo(100));
            Assert.That(r.UpLossPct, Is.EqualTo(0));
            Assert.That(r.DownLossPct, Is.EqualTo(0));
            Assert.That(r.RttP50Ms, Is.GreaterThanOrEqualTo(0).And.LessThan(50));
            Assert.That(r.UpJitterMs, Is.Not.Null);
            Assert.That(r.MosUp, Is.GreaterThan(4));
        });
    }

    [Test]
    public async Task Throughput_OverLoopback_MeasuresBothDirections()
    {
        await using var server = new ResponderServer(Key, 0, IPAddress.Loopback);
        server.Start();
        var client = new ResponderClient(new ResponderSettings { Address = "127.0.0.1", Port = server.Port, Key = ResponderProtocol.KeyToText(Key) });
        var down = await client.DownloadAsync("127.0.0.1", 2, TimeSpan.FromSeconds(1.5), CancellationToken.None);
        var up = await client.UploadAsync("127.0.0.1", 2, TimeSpan.FromSeconds(1.5), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(down.Error, Is.Null);
            Assert.That(down.Bytes, Is.GreaterThan(1_000_000));
            Assert.That(down.Seconds, Is.GreaterThan(0.5).And.LessThan(3));
            Assert.That(up.Error, Is.Null);
            Assert.That(up.Bytes, Is.GreaterThan(1_000_000));
        });
    }

    [Test]
    public async Task WrongKey_IsRejected_AndForgedUdpIsNotAnswered()
    {
        await using var server = new ResponderServer(Key, 0, IPAddress.Loopback);
        server.Start();
        var bad = new ResponderClient(new ResponderSettings { Address = "127.0.0.1", Port = server.Port, Key = ResponderProtocol.KeyToText(ResponderProtocol.NewKey()) });
        var check = await bad.CheckAsync(CancellationToken.None);
        Assert.That(check.Ok, Is.False);
        Assert.That(check.Message, Does.Contain("rejected the key"));

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var forged = ResponderProtocol.BuildUdp(ResponderProtocol.NewKey(), new ResponderProtocol.UdpPacket(1, 1234, 0, 1, 0, 0, 0), 172);
        await udp.SendAsync(forged, new IPEndPoint(IPAddress.Loopback, server.Port));
        using var cts = new CancellationTokenSource(500);
        Assert.ThrowsAsync<OperationCanceledException>(async () => await udp.ReceiveAsync(cts.Token), "no reply to unauthenticated packets");
    }

    [Test]
    public void UdpPacket_RoundTrips_AndTamperingIsDetected()
    {
        var p = new ResponderProtocol.UdpPacket(1, 42, 7, 100, 200, 300, 5);
        var buf = ResponderProtocol.BuildUdp(Key, p, 172);
        Assert.That(ResponderProtocol.TryParseUdp(Key, buf, out var back), Is.True);
        Assert.That(back, Is.EqualTo(p));
        buf[20] ^= 1;
        Assert.That(ResponderProtocol.TryParseUdp(Key, buf, out _), Is.False);
    }

    [Test]
    public void TwoWayCompute_PerDirectionLossJitterAndRtt()
    {
        // 4 packets sent, responder got 3 (seq 0,1,3), replies 3, client got 2 back (0 and 3)
        var received = new Dictionary<int, (long, long, long, long)>
        {
            [0] = (0, 5_000_000, 5_000_100, 10_100),          // clock offset 5 s; up 0, down 10.0 ms; rtt = 10.1 − 0.1 = 10 ms
            [3] = (60_000, 5_062_000, 5_062_100, 73_100)      // up delay +2 ms vs packet 0; down 11 ms
        };
        var r = ResponderClient.Compute(new TwoWayResult(), 4, 3, 0, 1, 3, received, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(r.UpLossPct, Is.EqualTo(25));
            Assert.That(r.DownLossPct, Is.EqualTo(Math.Round(100.0 / 3, 3)));
            Assert.That(r.RttP50Ms, Is.EqualTo(10));
            Assert.That(r.UpJitterMs, Is.EqualTo(2), "the 5 s clock offset cancels");
            Assert.That(r.DownJitterMs, Is.EqualTo(1));
            Assert.That(r.UpReordered, Is.EqualTo(1));
        });
    }
}

// ------------------------------------------------------------------ Windows prober against local endpoints, parsers, storage, exports

[TestFixture]
public class ConnectionProberTests
{
    private static WindowsConnectionProber Prober() => new(new MockNetworkAdapterProvider(), new NullLogger());

    [Test]
    public async Task Tcp_OpenPort_IsOk_ClosedPort_IsRefusedWithoutRtt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var open = await Prober().TcpConnectAsync("127.0.0.1", port, "127.0.0.1", 2000);
        listener.Stop();
        var closed = await Prober().TcpConnectAsync("127.0.0.1", port, "127.0.0.1", 6000);
        Assert.Multiple(() =>
        {
            Assert.That(open.Outcome, Is.EqualTo(ProbeOutcome.Ok));
            Assert.That(open.RttMs, Is.GreaterThan(0).And.LessThan(1000));
            Assert.That(closed.Outcome, Is.EqualTo(ProbeOutcome.Refused), "host reachable, port closed; no valid round trip on Windows");
            Assert.That(closed.RttMs, Is.Null);
            Assert.That(closed.Detail, Does.Contain("port").And.Contain("closed"));
        });
    }

    [Test]
    public async Task Tcp_UnavailableSourceAddress_IsUnreachable()
    {
        var r = await Prober().TcpConnectAsync("127.0.0.1", 9, "203.0.113.77", 1000);
        Assert.That(r.Outcome, Is.EqualTo(ProbeOutcome.Unreachable));
    }

    [Test]
    public async Task Dns_AgainstLocalFakeServer_OkAndNxDomainAndTimeout()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        Assert.That(port, Is.GreaterThan(0));
        // The prober always queries port 53; test the codec directly and the timeout path with a silent server.
        var q = WindowsConnectionProber.BuildDnsQuery(0x1234, "example.com");
        Assert.That(q.Length, Is.EqualTo(12 + 1 + 7 + 1 + 3 + 1 + 4));
        var resp = (byte[])q.Clone();
        resp[2] |= 0x80; resp[3] = 0x80; resp[7] = 1;
        Assert.That(WindowsConnectionProber.ParseDnsResponse(resp, resp.Length, 0x1234), Is.EqualTo((0, 1)));
        resp[3] = 0x83;
        Assert.That(WindowsConnectionProber.ParseDnsResponse(resp, resp.Length, 0x1234)!.Value.RCode, Is.EqualTo(3));
        Assert.That(WindowsConnectionProber.ParseDnsResponse(resp, resp.Length, 0x9999), Is.Null, "wrong id ignored");
        var timeout = await Prober().DnsQueryAsync("127.0.0.1", "example.com", "127.0.0.1", 300);
        Assert.That(timeout.Outcome, Is.AnyOf(ProbeOutcome.Timeout, ProbeOutcome.Unreachable), "nothing listens on 127.0.0.1:53");
    }

    [Test]
    public async Task Icmp_ToLoopback_WithSourceAddress()
    {
        var r = await Prober().IcmpAsync("127.0.0.1", "127.0.0.1", 64, 1000);
        Assert.That(r.Outcome, Is.EqualTo(ProbeOutcome.Ok));
        Assert.That(r.RttMs, Is.LessThan(100));
    }

    [Test]
    public void WlanStatistics_ParsedAndSummedOverPhys()
    {
        var b = new byte[224 + 2 * 144];
        BitConverter.GetBytes(2u).CopyTo(b, 216);
        void Phy(int i, ulong tx, ulong failed, ulong retry, ulong ack, ulong rx, ulong fcs)
        {
            int o = 224 + i * 144;
            BitConverter.GetBytes(tx).CopyTo(b, o); BitConverter.GetBytes(failed).CopyTo(b, o + 16); BitConverter.GetBytes(retry).CopyTo(b, o + 24);
            BitConverter.GetBytes(ack).CopyTo(b, o + 72); BitConverter.GetBytes(rx).CopyTo(b, o + 80); BitConverter.GetBytes(fcs).CopyTo(b, o + 136);
        }
        Phy(0, 100, 1, 10, 2, 500, 3);
        Phy(1, 50, 0, 5, 1, 100, 1);
        var c = WlanAdapter.ParseWlanStatistics(b);
        Assert.That(c, Is.EqualTo(new WlanFrameCounters(150, 15, 1, 3, 600, 4)));
        Assert.That(WlanAdapter.ParseWlanStatistics(new byte[100]), Is.Null);
    }

    [Test]
    public void SecretProtector_RoundTrip_AndCiphertextDiffersFromPlain()
    {
        var p = SecretProtector.Protect("secret-key");
        Assert.That(p, Does.StartWith("dpapi:"));
        Assert.That(p, Does.Not.Contain("secret-key"));
        Assert.That(SecretProtector.Unprotect(p), Is.EqualTo("secret-key"));
        Assert.That(SecretProtector.Unprotect("plain"), Is.EqualTo("plain"));
    }

    [Test]
    public async Task Repository_RoundTrip_AndExports()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ws_conn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<WlanDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "t.db")}").Options;
            var repo = new ConnectionRepository(new TestDbFactory(options));
            var cfg = Conn.Config() with { Location = "Office" };
            await repo.SaveTestAsync(cfg, Conn.T0, ConnectionTestStatus.Running);
            var probes = Conn.Healthy(20, (t, s) => t is >= 5 and <= 8 && s == ConnectionSegment.Internet ? double.NaN : null);
            await repo.SaveProbesAsync(cfg.TestId, probes);
            var link = Enumerable.Range(0, 20).Select(t => Conn.L(t, counters: new WlanFrameCounters((ulong)t * 100, (ulong)t * 5, 0, 0, (ulong)t * 300, 0), bytesIn: t * 1000)).ToList();
            await repo.SaveLinkSamplesAsync(cfg.TestId, link);
            await repo.SaveSpeedTestAsync(cfg.TestId, new SpeedTestResult { StartUtc = Conn.T0, Provider = "LAN responder", Server = "x", DownloadMbps = 300, UploadMbps = 90, BufferbloatGrade = "A" });
            await repo.SaveTwoWayAsync(cfg.TestId, new TwoWayResult { StartUtc = Conn.T0, Responder = "pi", UpLossPct = 0, DownLossPct = 0.5, MosUp = 4.4 });
            await repo.SaveMarkerAsync(cfg.TestId, new ConnectionMarker(Conn.T0, 1000, "door closed"));
            await repo.UpdateTestStatusAsync(cfg.TestId, ConnectionTestStatus.Completed, Conn.T0.AddSeconds(20), 20);

            var rec = (await repo.GetTestsAsync()).Single();
            var data = await repo.GetDataAsync(cfg.TestId);
            Assert.Multiple(() =>
            {
                Assert.That(rec.Config.Location, Is.EqualTo("Office"));
                Assert.That(rec.ProbeCount, Is.EqualTo(80));
                Assert.That(rec.Status, Is.EqualTo(ConnectionTestStatus.Completed));
                Assert.That(data.Probes, Has.Count.EqualTo(80));
                Assert.That(data.Link[5].Counters!.TxRetries, Is.EqualTo(25));
                Assert.That(data.Speed.Single().DownloadMbps, Is.EqualTo(300));
                Assert.That(data.TwoWay.Single().DownLossPct, Is.EqualTo(0.5));
                Assert.That(data.Markers.Single().Text, Is.EqualTo("door closed"));
            });

            var report = new ConnectionAnalyzer().Analyze(rec.Config, data.Probes, data.Link, data.Speed, data.TwoWay, data.Markers, rec.StartUtc, rec.EndUtc, rec.Status, 20);
            var export = new ConnectionExportService();
            var files = await export.ExportCsvBundleAsync(dir, "c", report);
            await export.ExportJsonAsync(Path.Combine(dir, "c.json"), report);
            await export.ExportPdfAsync(Path.Combine(dir, "c.pdf"), report);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "c.json")));
            var info = File.ReadAllText(files.Single(f => f.EndsWith("_info.csv", StringComparison.Ordinal)));
            Assert.Multiple(() =>
            {
                Assert.That(files.Select(Path.GetFileName), Is.EquivalentTo(new[] { "c_info.csv", "c_probes.csv", "c_link.csv", "c_segments.csv", "c_events.csv", "c_speed.csv", "c_twoway.csv", "c_markers.csv" }));
                Assert.That(File.ReadAllLines(files.Single(f => f.EndsWith("_probes.csv", StringComparison.Ordinal))).Length, Is.EqualTo(2 + 80));
                Assert.That(json.RootElement.GetProperty("schema").GetString(), Is.EqualTo("wirelessscanner.connection_report.v1"));
                Assert.That(json.RootElement.GetProperty("analyticsVersion").GetString(), Is.EqualTo("CONN-ANALYTICS-1"));
                Assert.That(json.RootElement.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("type").GetString() == "Outage"), Is.True);
                Assert.That(json.RootElement.GetProperty("verdicts").GetArrayLength(), Is.EqualTo(4));
                Assert.That(info, Does.Contain("Definitions").And.Contain("Limits").And.Contain("ISP hop"));
                Assert.That(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "c.pdf")), 0, 4), Is.EqualTo("%PDF"));
            });
        }
        finally
        {
            SqliteCleanup();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class TestDbFactory(DbContextOptions<WlanDbContext> o) : IDbContextFactory<WlanDbContext>
    {
        public WlanDbContext CreateDbContext() => new(o);
    }
}
