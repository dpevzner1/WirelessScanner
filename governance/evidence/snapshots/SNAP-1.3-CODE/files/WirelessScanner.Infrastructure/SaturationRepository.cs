using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

/// <summary>SQLite persistence for saturation tests (SPEC-SAT-001 §9). One transaction per cycle.</summary>
public class SaturationRepository : ISaturationRepository
{
    private readonly IDbContextFactory<WlanDbContext> _contextFactory;

    internal static readonly JsonSerializerOptions ConfigJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public SaturationRepository(IDbContextFactory<WlanDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
        using var context = _contextFactory.CreateDbContext();
        SchemaMigrator.Upgrade(context);
    }

    public async Task SaveTestAsync(SaturationTestConfig config, DateTime startUtc, SaturationTestStatus status)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.SaturationTests.Add(new DbSaturationTest
        {
            TestId = config.TestId,
            Name = config.Name,
            Scope = (int)config.Scope,
            ConfigJson = JsonSerializer.Serialize(config, ConfigJson),
            InterfaceName = config.InterfaceName,
            Location = config.Location,
            Facility = config.Facility,
            StartTime = startUtc,
            Status = (int)status
        });
        await db.SaveChangesAsync();
    }

    public async Task UpdateTestStatusAsync(Guid testId, SaturationTestStatus status, DateTime? endUtc, double? activeSeconds = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var t = await db.SaturationTests.FindAsync(testId);
        if (t == null) return;
        t.Status = (int)status;
        if (endUtc.HasValue) t.EndTime = endUtc;
        if (activeSeconds.HasValue) t.ActiveSeconds = activeSeconds;
        await db.SaveChangesAsync();
    }

    public async Task SaveCycleAsync(Guid testId, SaturationCycle cycle)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.SaturationCycles.Add(new DbSaturationCycle
        {
            TestId = testId,
            Sequence = cycle.Sequence,
            WallUtc = cycle.WallUtc,
            ElapsedMs = cycle.ElapsedMs,
            Status = (int)cycle.Status,
            DurationMs = cycle.DurationMs,
            BssCount = cycle.BssCount,
            ErrorCode = cycle.ErrorCode,
            ErrorMessage = cycle.ErrorMessage
        });
        db.SaturationObservations.AddRange(cycle.Observations.Select(o => new DbSaturationObservation
        {
            TestId = testId,
            CycleSequence = cycle.Sequence,
            Bssid = o.Bssid,
            Ssid = o.Ssid,
            Status = (int)o.Status,
            Rssi = o.Rssi,
            LinkQuality = o.LinkQuality,
            Channel = o.Channel,
            Band = o.Band,
            ChannelWidth = o.ChannelWidth,
            BeaconAgeMs = o.BeaconAgeMs,
            CoChannelCount = o.CoChannelCount,
            OverlapCount = o.OverlapCount,
            Security = o.Security,
            Vendor = o.Vendor,
            PhyType = o.PhyType,
            BeaconIntervalTu = o.BeaconIntervalTu,
            CenterFrequencyMhz = o.CenterFrequencyMhz,
            StationCount = o.StationCount,
            ChannelUtilizationPct = o.ChannelUtilizationPct,
            StrongestOverlapRssi = o.StrongestOverlapRssi
        }));
        await db.SaveChangesAsync(); // one transaction per cycle
    }

    public async Task SaveMarkerAsync(Guid testId, SaturationMarker marker)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.SaturationMarkers.Add(new DbSaturationMarker { TestId = testId, WallUtc = marker.WallUtc, ElapsedMs = marker.ElapsedMs, Text = marker.Text });
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<SaturationTestRecord>> GetTestsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var tests = await db.SaturationTests.OrderByDescending(t => t.StartTime).ToListAsync();
        var counts = await db.SaturationCycles.GroupBy(c => c.TestId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return tests.Select(t => ToRecord(t, counts.GetValueOrDefault(t.TestId))).ToList();
    }

    public async Task<SaturationTestRecord?> GetTestAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var t = await db.SaturationTests.FindAsync(testId);
        if (t == null) return null;
        int count = await db.SaturationCycles.CountAsync(c => c.TestId == testId);
        return ToRecord(t, count);
    }

    public async Task<IReadOnlyList<SaturationCycle>> GetCyclesAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var cycles = await db.SaturationCycles.Where(c => c.TestId == testId).OrderBy(c => c.Sequence).ToListAsync();
        var obs = await db.SaturationObservations.Where(o => o.TestId == testId).OrderBy(o => o.CycleSequence).ThenBy(o => o.Id).ToListAsync();
        var bySeq = obs.GroupBy(o => o.CycleSequence).ToDictionary(g => g.Key, g => g.Select(o => new SaturationObservation(
            o.CycleSequence, o.Bssid, o.Ssid, (ObservationStatus)o.Status, o.Rssi, o.LinkQuality, o.Channel, o.Band, o.ChannelWidth,
            o.BeaconAgeMs, o.CoChannelCount, o.OverlapCount, o.Security, o.Vendor, o.PhyType, o.BeaconIntervalTu, o.CenterFrequencyMhz,
            o.StationCount, o.ChannelUtilizationPct, o.StrongestOverlapRssi)).ToList());
        return cycles.Select(c => new SaturationCycle(c.Sequence, DateTime.SpecifyKind(c.WallUtc, DateTimeKind.Utc), c.ElapsedMs, (ScanCycleStatus)c.Status,
            c.DurationMs, c.BssCount, c.ErrorCode, c.ErrorMessage,
            (IReadOnlyList<SaturationObservation>?)bySeq.GetValueOrDefault(c.Sequence) ?? Array.Empty<SaturationObservation>())).ToList();
    }

    public async Task<IReadOnlyList<SaturationMarker>> GetMarkersAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return (await db.SaturationMarkers.Where(m => m.TestId == testId).OrderBy(m => m.ElapsedMs).ToListAsync())
            .Select(m => new SaturationMarker(DateTime.SpecifyKind(m.WallUtc, DateTimeKind.Utc), m.ElapsedMs, m.Text)).ToList();
    }

    public async Task DeleteTestAsync(Guid testId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var t = await db.SaturationTests.FindAsync(testId);
        if (t == null) return;
        db.SaturationTests.Remove(t);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("VACUUM;");
    }

    private static SaturationTestRecord ToRecord(DbSaturationTest t, int cycleCount)
    {
        SaturationTestConfig config;
        try
        {
            config = JsonSerializer.Deserialize<SaturationTestConfig>(t.ConfigJson, ConfigJson) ?? new SaturationTestConfig { TestId = t.TestId, Name = t.Name };
        }
        catch (JsonException)
        {
            config = new SaturationTestConfig { TestId = t.TestId, Name = t.Name, Location = t.Location, Facility = t.Facility, InterfaceName = t.InterfaceName };
        }
        // The table columns are the authority for identity: a config blob missing fields must never yield a new random TestId.
        config = config with
        {
            TestId = t.TestId,
            Name = string.IsNullOrEmpty(config.Name) ? t.Name : config.Name,
            Location = string.IsNullOrEmpty(config.Location) ? t.Location : config.Location,
            Facility = string.IsNullOrEmpty(config.Facility) ? t.Facility : config.Facility,
            InterfaceName = string.IsNullOrEmpty(config.InterfaceName) ? t.InterfaceName : config.InterfaceName
        };
        return new SaturationTestRecord(config, DateTime.SpecifyKind(t.StartTime, DateTimeKind.Utc),
            t.EndTime.HasValue ? DateTime.SpecifyKind(t.EndTime.Value, DateTimeKind.Utc) : null, (SaturationTestStatus)t.Status, cycleCount, t.ActiveSeconds);
    }
}
