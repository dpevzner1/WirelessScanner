using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

/// <summary>SQLite persistence for connection tests (DR-0014, schema 4). Probes and link samples are canonical; reports are derived.</summary>
public sealed class ConnectionRepository : IConnectionRepository
{
    private readonly IDbContextFactory<WlanDbContext> _contextFactory;

    public ConnectionRepository(IDbContextFactory<WlanDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
        using var context = _contextFactory.CreateDbContext();
        SchemaMigrator.Upgrade(context);
    }

    public async Task SaveTestAsync(ConnectionTestConfig config, DateTime startUtc, ConnectionTestStatus status)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.ConnectionTests.Add(new DbConnectionTest
        {
            TestId = config.TestId,
            Name = config.Name,
            ConfigJson = JsonSerializer.Serialize(config, SaturationRepository.ConfigJson),
            InterfaceName = config.InterfaceName,
            Location = config.Location,
            Ssid = config.ContextAtStart?.Ssid ?? "",
            StartTime = startUtc,
            Status = (int)status,
            LinkedSaturationTestId = config.LinkedSaturationTestId
        });
        await db.SaveChangesAsync();
    }

    public async Task UpdateTestStatusAsync(Guid testId, ConnectionTestStatus status, DateTime? endUtc, double? activeSeconds = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var t = await db.ConnectionTests.FindAsync(testId);
        if (t == null) return;
        t.Status = (int)status;
        if (endUtc.HasValue) t.EndTime = endUtc;
        if (activeSeconds.HasValue) t.ActiveSeconds = activeSeconds;
        await db.SaveChangesAsync();
    }

    public async Task SaveProbesAsync(Guid testId, IReadOnlyList<ConnectionProbeResult> probes)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.ConnectionProbes.AddRange(probes.Select(p => new DbConnectionProbe
        {
            TestId = testId, Sequence = p.Sequence, WallUtc = p.WallUtc, ElapsedMs = p.ElapsedMs, Segment = (int)p.Segment, Target = p.Target,
            Method = (int)p.Method, Outcome = (int)p.Outcome, RttMs = p.RttMs, Detail = p.Detail
        }));
        await db.SaveChangesAsync();
    }

    public async Task SaveLinkSamplesAsync(Guid testId, IReadOnlyList<ConnectionLinkSample> samples)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.ConnectionLinkSamples.AddRange(samples.Select(l => new DbConnectionLinkSample
        {
            TestId = testId, Sequence = l.Sequence, WallUtc = l.WallUtc, ElapsedMs = l.ElapsedMs, Connected = l.Connected, Ssid = l.Ssid, Bssid = l.Bssid,
            Channel = l.Channel, Rssi = l.Rssi, SignalQualityPct = l.SignalQualityPct, RxRateMbps = l.RxRateMbps, TxRateMbps = l.TxRateMbps,
            LocalIp = l.LocalIp, Gateway = l.Gateway, IfBytesIn = l.IfBytesIn, IfBytesOut = l.IfBytesOut, GapMs = l.GapMs,
            TxFrames = (long?)l.Counters?.TxFrames, TxRetries = (long?)l.Counters?.TxRetries, TxFailed = (long?)l.Counters?.TxFailed,
            AckFailures = (long?)l.Counters?.AckFailures, RxFrames = (long?)l.Counters?.RxFrames, FcsErrors = (long?)l.Counters?.FcsErrors
        }));
        await db.SaveChangesAsync();
    }

    public async Task SaveMarkerAsync(Guid testId, ConnectionMarker marker)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.ConnectionResults.Add(new DbConnectionResult { TestId = testId, Kind = "marker", ElapsedMs = marker.ElapsedMs, Json = JsonSerializer.Serialize(marker, SaturationRepository.ConfigJson) });
        await db.SaveChangesAsync();
    }

    public Task SaveSpeedTestAsync(Guid testId, SpeedTestResult result) => SaveResultAsync(testId, "speed", result.ElapsedMs, result);
    public Task SaveTwoWayAsync(Guid testId, TwoWayResult result) => SaveResultAsync(testId, "twoway", result.ElapsedMs, result);

    private async Task SaveResultAsync<T>(Guid testId, string kind, long elapsedMs, T value)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.ConnectionResults.Add(new DbConnectionResult { TestId = testId, Kind = kind, ElapsedMs = elapsedMs, Json = JsonSerializer.Serialize(value, SaturationRepository.ConfigJson) });
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ConnectionTestRecord>> GetTestsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var tests = await db.ConnectionTests.OrderByDescending(t => t.StartTime).ToListAsync();
        var counts = await db.ConnectionProbes.GroupBy(p => p.TestId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return tests.Select(t =>
        {
            ConnectionTestConfig cfg;
            try { cfg = JsonSerializer.Deserialize<ConnectionTestConfig>(t.ConfigJson, SaturationRepository.ConfigJson) ?? new ConnectionTestConfig(); }
            catch (JsonException) { cfg = new ConnectionTestConfig(); }
            cfg = cfg with
            {
                TestId = t.TestId,
                Name = string.IsNullOrEmpty(cfg.Name) ? t.Name : cfg.Name,
                Location = string.IsNullOrEmpty(cfg.Location) ? t.Location : cfg.Location,
                InterfaceName = string.IsNullOrEmpty(cfg.InterfaceName) ? t.InterfaceName : cfg.InterfaceName
            };
            return new ConnectionTestRecord(cfg, DateTime.SpecifyKind(t.StartTime, DateTimeKind.Utc), t.EndTime.HasValue ? DateTime.SpecifyKind(t.EndTime.Value, DateTimeKind.Utc) : null,
                (ConnectionTestStatus)t.Status, counts.GetValueOrDefault(t.TestId), t.ActiveSeconds);
        }).ToList();
    }

    public async Task<(IReadOnlyList<ConnectionProbeResult> Probes, IReadOnlyList<ConnectionLinkSample> Link, IReadOnlyList<SpeedTestResult> Speed,
        IReadOnlyList<TwoWayResult> TwoWay, IReadOnlyList<ConnectionMarker> Markers)> GetDataAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var probes = (await db.ConnectionProbes.Where(p => p.TestId == testId).OrderBy(p => p.ElapsedMs).ThenBy(p => p.Id).ToListAsync())
            .Select(p => new ConnectionProbeResult(p.Sequence, DateTime.SpecifyKind(p.WallUtc, DateTimeKind.Utc), p.ElapsedMs, (ConnectionSegment)p.Segment, p.Target,
                (ProbeMethod)p.Method, (ProbeOutcome)p.Outcome, p.RttMs, p.Detail)).ToList();
        var link = (await db.ConnectionLinkSamples.Where(l => l.TestId == testId).OrderBy(l => l.ElapsedMs).ThenBy(l => l.Id).ToListAsync())
            .Select(l => new ConnectionLinkSample(l.Sequence, DateTime.SpecifyKind(l.WallUtc, DateTimeKind.Utc), l.ElapsedMs, l.Connected, l.Ssid, l.Bssid, l.Channel,
                l.Rssi, l.SignalQualityPct, l.RxRateMbps, l.TxRateMbps, l.LocalIp, l.Gateway, l.IfBytesIn, l.IfBytesOut,
                l.TxFrames.HasValue ? new WlanFrameCounters((ulong)l.TxFrames.Value, (ulong)(l.TxRetries ?? 0), (ulong)(l.TxFailed ?? 0), (ulong)(l.AckFailures ?? 0),
                    (ulong)(l.RxFrames ?? 0), (ulong)(l.FcsErrors ?? 0)) : null, l.GapMs)).ToList();
        var results = await db.ConnectionResults.Where(r => r.TestId == testId).OrderBy(r => r.ElapsedMs).ThenBy(r => r.Id).ToListAsync();
        T? Read<T>(DbConnectionResult r) where T : class { try { return JsonSerializer.Deserialize<T>(r.Json, SaturationRepository.ConfigJson); } catch (JsonException) { return null; } }
        return (probes, link,
            results.Where(r => r.Kind == "speed").Select(Read<SpeedTestResult>).OfType<SpeedTestResult>().ToList(),
            results.Where(r => r.Kind == "twoway").Select(Read<TwoWayResult>).OfType<TwoWayResult>().ToList(),
            results.Where(r => r.Kind == "marker").Select(Read<ConnectionMarker>).OfType<ConnectionMarker>().ToList());
    }

    public async Task DeleteTestAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var t = await db.ConnectionTests.FindAsync(testId);
        if (t == null) return;
        db.ConnectionTests.Remove(t);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("VACUUM;");
    }
}

/// <summary>Demo mode (WIRELESSSCANNER_DEMO=1): simulated connection with realistic latency, occasional loss and one brief ISP outage.</summary>
public sealed class MockConnectionProber : IConnectionProber
{
    private readonly INetworkAdapterProvider _adapters;
    private readonly Random _rng = new(42);
    private readonly DateTime _start = DateTime.UtcNow;
    public MockConnectionProber(INetworkAdapterProvider adapters) => _adapters = adapters;

    public async Task<ConnectionContext> GetContextAsync(Guid interfaceId, CancellationToken ct = default)
    {
        var link = await _adapters.ReadLinkAsync(interfaceId);
        return new ConnectionContext
        {
            WifiConnected = true, InterfaceId = interfaceId, InterfaceName = "Simulated Wi-Fi", Ssid = link?.Ssid ?? "Home", Bssid = link?.Bssid ?? "9A:3C:5E:26:42:70",
            Channel = link?.Channel ?? 149, Band = "5 GHz", PhyType = "802.11ax", Rssi = link?.Rssi ?? -50, RxRateMbps = 866.7, TxRateMbps = 780,
            LocalIPv4 = "192.168.1.100", Gateways = new[] { "192.168.1.1" }, DnsServers = new[] { "192.168.1.1" }
        };
    }

    public Task<WlanLinkReading?> ReadLinkAsync(Guid interfaceId) => _adapters.ReadLinkAsync(interfaceId);
    public (long BytesIn, long BytesOut)? InterfaceBytes(Guid interfaceId)
    {
        long t = (long)(DateTime.UtcNow - _start).TotalMilliseconds;
        return (t * 40, t * 12);
    }

    private bool IspOutage => (DateTime.UtcNow - _start).TotalSeconds % 300 is > 120 and < 128;

    private ProbeReply Reply(double baseMs, double spread, double lossPct)
    {
        if (_rng.NextDouble() * 100 < lossPct) return ProbeReply.Fail(ProbeOutcome.Timeout);
        return new ProbeReply(ProbeOutcome.Ok, Math.Round(baseMs + _rng.NextDouble() * spread, 3));
    }

    public async Task<ProbeReply> IcmpAsync(string target, string source, int ttl, int timeoutMs, CancellationToken ct = default)
    {
        await Task.Delay(5, ct);
        if (ttl <= 1) return new ProbeReply(ProbeOutcome.TtlExpired, 2.5, "192.168.1.1");
        if (ttl == 2) return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : new ProbeReply(ProbeOutcome.TtlExpired, 9.5, "203.0.113.1");
        if (target == "192.168.1.1") return Reply(2, 3, 0.2);
        if (target == "203.0.113.1") return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : Reply(9, 4, 0.5);
        return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : Reply(18, 8, 0.5) with { From = target };
    }

    public async Task<ProbeReply> TcpConnectAsync(string target, int port, string source, int timeoutMs, CancellationToken ct = default)
    {
        await Task.Delay(5, ct);
        return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : Reply(20, 10, 0.3);
    }

    public async Task<ProbeReply> DnsQueryAsync(string server, string name, string source, int timeoutMs, CancellationToken ct = default)
    {
        await Task.Delay(5, ct);
        return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : Reply(12, 20, 0.5);
    }

    public async Task<ProbeReply> HttpsAsync(string url, string source, int timeoutMs, CancellationToken ct = default)
    {
        await Task.Delay(5, ct);
        return IspOutage ? ProbeReply.Fail(ProbeOutcome.Timeout) : Reply(140, 80, 0.5);
    }
}
