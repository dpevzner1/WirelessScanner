using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>Synthetic-series fixtures with hand-computed answers (SAT-AC-02/03/04).</summary>
internal static class Sat
{
    public static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    public const string A = "AA:AA:AA:AA:AA:01";
    public const string B = "AA:AA:AA:AA:AA:02";

    public static SaturationTestConfig Config(SaturationScope scope = SaturationScope.SingleAp, params string[] targets) => new()
    {
        Name = "Synthetic",
        Scope = scope,
        TargetBssids = scope is SaturationScope.SingleAp or SaturationScope.MultipleAps ? (targets.Length > 0 ? targets : new[] { A }) : Array.Empty<string>(),
        TargetSsids = scope is SaturationScope.SingleSsid or SaturationScope.MultipleSsids ? targets : Array.Empty<string>(),
        Interval = TimeSpan.FromSeconds(5),
        Duration = TimeSpan.FromMinutes(10),
        Location = "Lab"
    };

    public static SaturationObservation Obs(long seq, string bssid, int? rssi, ObservationStatus status = ObservationStatus.Observed, int ch = 36, string band = "5 GHz", string ssid = "Net") =>
        new(seq, bssid, ssid, status, status is ObservationStatus.Observed or ObservationStatus.Stale ? rssi : null, rssi is null ? null : 80,
            status is ObservationStatus.Observed or ObservationStatus.Stale ? ch : null, status is ObservationStatus.Observed or ObservationStatus.Stale ? band : null,
            20, 500, 0, null);

    /// <summary>One target, one value per cycle; null = Missing (fresh cycle).</summary>
    public static List<SaturationCycle> Series(params int?[] rssi) =>
        rssi.Select((r, i) => Cycle(i + 1, r is null ? Obs(i + 1, A, null, ObservationStatus.Missing) : Obs(i + 1, A, r))).ToList();

    public static SaturationCycle Cycle(long seq, params SaturationObservation[] obs) =>
        new(seq, T0.AddSeconds((seq - 1) * 5), (seq - 1) * 5000, ScanCycleStatus.Fresh, 1500, obs.Length, null, null, obs);
}

[TestFixture]
public class SaturationAnalyzerTests
{
    private readonly SaturationAnalyzer _analyzer = new();

    private SaturationReport Run(List<SaturationCycle> cycles, SaturationTestConfig? cfg = null, IReadOnlyList<SaturationMarker>? markers = null) =>
        _analyzer.Analyze(cfg ?? Sat.Config(), cycles, markers ?? Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(1), SaturationTestStatus.Completed);

    [Test]
    public void Statistics_MatchHandComputedValues()
    {
        var r = Run(Sat.Series(-50, -60, -70, -80, -90));
        var t = r.Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(t.Mean, Is.EqualTo(-70));
            Assert.That(t.StdDev, Is.EqualTo(Math.Sqrt(200)).Within(1e-9));
            Assert.That(t.Median, Is.EqualTo(-70));
            Assert.That(t.P5, Is.EqualTo(-90));
            Assert.That(t.P25, Is.EqualTo(-80));
            Assert.That(t.P75, Is.EqualTo(-60));
            Assert.That(t.P95, Is.EqualTo(-50));
            Assert.That(t.Iqr, Is.EqualTo(20));
            Assert.That(t.Range, Is.EqualTo(40));
            Assert.That(t.Min, Is.EqualTo(-90));
            Assert.That(t.Max, Is.EqualTo(-50));
            Assert.That(t.VariationMean, Is.EqualTo(10));
            Assert.That(t.VariationMax, Is.EqualTo(10));
            Assert.That(t.Tier, Is.EqualTo("Fair"));
        });
    }

    [Test]
    public void Drop_DetectedWithBaselineHysteresisAndRecovery()
    {
        // baseline -50 (median of 6); -60 starts the drop (<= -58); -55 stays open (< -54); -53 closes it.
        var r = Run(Sat.Series(-50, -50, -50, -50, -50, -50, -60, -62, -55, -53, -50, -50, -50));
        var drop = r.Events.Single(e => e.Type == SaturationEventType.Drop);
        Assert.Multiple(() =>
        {
            Assert.That(drop.BaselineDbm, Is.EqualTo(-50));
            Assert.That(drop.ExtremeRssi, Is.EqualTo(-62));
            Assert.That(drop.MagnitudeDb, Is.EqualTo(12));
            Assert.That(drop.StartElapsedMs, Is.EqualTo(30000));
            Assert.That(drop.EndElapsedMs, Is.EqualTo(45000));
            Assert.That(drop.DurationSeconds, Is.EqualTo(15));
            Assert.That(drop.RecoverySeconds, Is.EqualTo(10));
            Assert.That(drop.Cycles, Is.EqualTo(4));
            Assert.That(r.Targets[0].DropCount, Is.EqualTo(1));
            Assert.That(r.Targets[0].WorstDropDb, Is.EqualTo(12));
        });
    }

    [Test]
    public void Noise_BelowThreshold_ProducesNoEvents()
    {
        var noisy = new int?[] { -60, -63, -57, -61, -59, -64, -56, -60, -62, -58, -61, -57, -63, -60 };
        var r = Run(Sat.Series(noisy));
        Assert.Multiple(() =>
        {
            Assert.That(r.Events, Is.Empty);
            Assert.That(r.Targets[0].Findings.Single(), Does.StartWith("Stable"));
        });
    }

    [Test]
    public void NoEvent_BeforeMinimumBaselineSamples_ThenStepChangeIsAnUnrecoveredDrop()
    {
        // Cycle 3 (-70) arrives with only 2 baseline samples: it cannot trigger and joins the window.
        // Cycle 4 sees baseline median(-50,-50,-70) = -50, so the persistent step is one unrecovered drop.
        var r = Run(Sat.Series(-50, -50, -70, -70, -70));
        var drops = r.Events.Where(e => e.Type == SaturationEventType.Drop).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(drops, Has.Count.EqualTo(1));
            Assert.That(drops[0].StartElapsedMs, Is.EqualTo(15000), "not at cycle 3 (10 s)");
            Assert.That(drops[0].EndUtc, Is.Null);
        });
    }

    [Test]
    public void TrendFinding_SuppressedForShortTests_ReportedForLongOnes()
    {
        var shortRun = Sat.Series(Enumerable.Range(0, 14).Select(i => (int?)(-60 + (i % 2))).ToArray());
        var longRun = Enumerable.Range(0, 20).Select(i => Sat.Cycle(i + 1, Sat.Obs(i + 1, Sat.A, -50 - i / 4)) with { ElapsedMs = i * 60_000L }).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Run(shortRun).Targets[0].Findings, Has.None.Contains("Trending"));
            Assert.That(Run(longRun).Targets[0].Findings, Has.Some.Contains("Trending down"));
        });
    }

    [Test]
    public void Peak_Detected()
    {
        var r = Run(Sat.Series(-60, -60, -60, -60, -60, -50, -51, -57, -60));
        var peak = r.Events.Single(e => e.Type == SaturationEventType.Peak);
        Assert.Multiple(() =>
        {
            Assert.That(peak.MagnitudeDb, Is.EqualTo(10));
            Assert.That(peak.ExtremeRssi, Is.EqualTo(-50));
        });
    }

    [Test]
    public void UnrecoveredDrop_ReportedOpen()
    {
        var r = Run(Sat.Series(-50, -50, -50, -50, -65, -66));
        var drop = r.Events.Single(e => e.Type == SaturationEventType.Drop);
        Assert.Multiple(() =>
        {
            Assert.That(drop.EndUtc, Is.Null);
            Assert.That(drop.Detail, Does.Contain("not recovered"));
        });
    }

    [Test]
    public void Dropout_RequiresConsecutiveMisses_StaleDoesNotBreakRun()
    {
        var cycles = Sat.Series(-60, -60, null, -60, null, null, null, -60);
        // insert a stale observation inside the 3-miss run: should neither extend nor break it
        cycles.Insert(5, Sat.Cycle(99, Sat.Obs(99, Sat.A, -60, ObservationStatus.Stale)));
        cycles = cycles.Select((c, i) => c with { Sequence = i + 1, ElapsedMs = i * 5000, WallUtc = Sat.T0.AddSeconds(i * 5) }).ToList();
        var r = Run(cycles);
        var dropouts = r.Events.Where(e => e.Type == SaturationEventType.Dropout).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(dropouts, Has.Count.EqualTo(1), "single miss is not a dropout");
            Assert.That(dropouts[0].Cycles, Is.EqualTo(3));
            Assert.That(r.Targets[0].Missing, Is.EqualTo(4));
            Assert.That(r.Targets[0].Stale, Is.EqualTo(1));
            Assert.That(r.Targets[0].AvailabilityPct, Is.EqualTo(100.0 * 4 / 8));
        });
    }

    [Test]
    public void MissingAndUnknown_NeverEnterStatistics()
    {
        var cycles = Sat.Series(-60, null, -60);
        cycles.Add(Sat.Cycle(4, Sat.Obs(4, Sat.A, null, ObservationStatus.Unknown)) with { Status = ScanCycleStatus.Failed, ElapsedMs = 15000 });
        var t = Run(cycles).Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(t.Mean, Is.EqualTo(-60));
            Assert.That(t.Min, Is.EqualTo(-60));
            Assert.That(t.Unknown, Is.EqualTo(1));
            Assert.That(t.AvailabilityPct, Is.EqualTo(100.0 * 2 / 3), "unknown excluded from availability");
        });
    }

    [Test]
    public void ChannelChange_Detected()
    {
        var cycles = new List<SaturationCycle>
        {
            Sat.Cycle(1, Sat.Obs(1, Sat.A, -60, ch: 36)),
            Sat.Cycle(2, Sat.Obs(2, Sat.A, -60, ch: 36)),
            Sat.Cycle(3, Sat.Obs(3, Sat.A, -61, ch: 149)),
        };
        var r = Run(cycles);
        var e = r.Events.Single(x => x.Type == SaturationEventType.ChannelChange);
        Assert.Multiple(() =>
        {
            Assert.That(e.Detail, Does.Contain("36").And.Contain("149"));
            Assert.That(r.Targets[0].ChannelChangeCount, Is.EqualTo(1));
            Assert.That(r.Targets[0].Channels, Is.EqualTo("36, 149"));
        });
    }

    [Test]
    public void ThresholdExposure_IsTimeWeighted()
    {
        var t = Run(Sat.Series(-60, -60, -75, -85)).Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(t.PctBelowFair, Is.EqualTo(50));
            Assert.That(t.PctBelowUnusable, Is.EqualTo(25));
            Assert.That(t.LongestBelowFairSeconds, Is.EqualTo(10));
            Assert.That(t.LongestBelowUnusableSeconds, Is.EqualTo(5));
        });
    }

    [Test]
    public void Trend_IsLeastSquaresSlopePerHour()
    {
        var cycles = Enumerable.Range(0, 4).Select(i => Sat.Cycle(i + 1, Sat.Obs(i + 1, Sat.A, -50 - 2 * i)) with { ElapsedMs = i * 3_600_000L }).ToList();
        Assert.That(Run(cycles).Targets[0].TrendDbPerHour, Is.EqualTo(-2).Within(1e-9));
    }

    [Test]
    public void StrongestBssidChange_UsesHysteresis_AndPreviousGone()
    {
        var cfg = Sat.Config(SaturationScope.SingleSsid, "Net");
        var cycles = new List<SaturationCycle>
        {
            Sat.Cycle(1, Sat.Obs(1, Sat.A, -60), Sat.Obs(1, Sat.B, -70)),
            Sat.Cycle(2, Sat.Obs(2, Sat.A, -60), Sat.Obs(2, Sat.B, -58)),  // +2 dB: below 3 dB hysteresis
            Sat.Cycle(3, Sat.Obs(3, Sat.A, -60), Sat.Obs(3, Sat.B, -56)),  // +4 dB: change A->B
            Sat.Cycle(4, Sat.Obs(4, Sat.A, -60), Sat.Obs(4, Sat.B, null, ObservationStatus.Missing)), // B gone: change B->A
        };
        var r = Run(cycles, cfg);
        var changes = r.Events.Where(e => e.Type == SaturationEventType.StrongestBssidChange).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(changes, Has.Count.EqualTo(2));
            Assert.That(changes[0].Detail, Does.Contain(Sat.A).And.Contain(Sat.B));
            Assert.That(changes[1].Detail, Does.Contain("not observed"));
            Assert.That(r.Ssids.Single().StrongestBssidChanges, Is.EqualTo(2));
            Assert.That(r.Ssids.Single().BestMax, Is.EqualTo(-56));
        });
    }

    [Test]
    public void StabilityScore_PerfectSignalIsGradeA_AndPenaltiesApply()
    {
        var perfect = Run(Sat.Series(-55, -55, -55, -55)).Targets.Single();
        // 4 observed + 4 missing (two dropouts? no: one run of 4 >= 2 -> one dropout); sigma 0; availability 50
        var poor = Run(Sat.Series(-55, -55, null, null, null, null, -55, -55)).Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(perfect.StabilityScore, Is.EqualTo(100));
            Assert.That(perfect.Grade, Is.EqualTo("A"));
            // 100 - 0 - 0.5*50 - 5*1/0.25 = 55
            Assert.That(poor.StabilityScore, Is.EqualTo(55));
            Assert.That(poor.Grade, Is.EqualTo("D"));
        });
    }

    [Test]
    public void Analysis_IsDeterministic()
    {
        var cycles = Sat.Series(-50, -50, -50, -50, -65, -50, null, null, -52);
        var cfg = Sat.Config(); // same config (and TestId) for both runs
        var a = JsonSerializer.Serialize(Run(cycles, cfg), SaturationExportService.JsonOptions);
        var b = JsonSerializer.Serialize(Run(cycles, cfg), SaturationExportService.JsonOptions);
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Health_CountsStatusesGapsAndErrors()
    {
        var cycles = Sat.Series(-60, -60, -60);
        cycles[1] = cycles[1] with { Status = ScanCycleStatus.Stale, ErrorCode = "scan_timeout" };
        cycles[2] = cycles[2] with { Status = ScanCycleStatus.Failed, ErrorCode = "1168", ElapsedMs = 30000 };
        var h = Run(cycles).Health;
        Assert.Multiple(() =>
        {
            Assert.That(h.Fresh, Is.EqualTo(1));
            Assert.That(h.Stale, Is.EqualTo(1));
            Assert.That(h.Failed, Is.EqualTo(1));
            Assert.That(h.MaxGapSeconds, Is.EqualTo(25));
            Assert.That(h.ErrorCounts["1168"], Is.EqualTo(1));
        });
    }
}

[TestFixture]
public class SaturationObservationBuilderTests
{
    private static AccessPoint Ap(string bssid, string ssid, int rssi, int ch = 36, string band = "5 GHz", double? age = 300) =>
        new(bssid, ssid, null, band, ch, 20, rssi, null, rssi + 95, 80, "WPA2-Personal", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow, age);

    private static ScanCycleResult Result(ScanCycleStatus status, params AccessPoint[] aps) =>
        new(status, DateTime.UtcNow, DateTime.UtcNow, 1000, aps);

    [Test]
    public void ExplicitTarget_AbsentInFreshCycle_IsMissing_InStaleCycle_IsUnknown()
    {
        var cfg = Sat.Config(SaturationScope.SingleAp, Sat.A);
        var tracker = new SaturationTargetTracker(cfg);
        var fresh = SaturationObservationBuilder.Build(cfg, tracker, Result(ScanCycleStatus.Fresh, Ap(Sat.B, "Other", -50)), 1);
        var stale = SaturationObservationBuilder.Build(cfg, tracker, Result(ScanCycleStatus.Stale), 2);
        var failed = SaturationObservationBuilder.Build(cfg, tracker, ScanCycleResult.Failure(DateTime.UtcNow, "1168", "gone"), 3);
        Assert.Multiple(() =>
        {
            Assert.That(fresh.Single().Status, Is.EqualTo(ObservationStatus.Missing));
            Assert.That(fresh.Single().Rssi, Is.Null, "missing is never numeric");
            Assert.That(stale.Single().Status, Is.EqualTo(ObservationStatus.Unknown));
            Assert.That(failed.Single().Status, Is.EqualTo(ObservationStatus.Unknown));
        });
    }

    [Test]
    public void OldBeacon_IsStale_EvenInFreshCycle()
    {
        var cfg = Sat.Config(SaturationScope.SingleAp, Sat.A);
        var obs = SaturationObservationBuilder.Build(cfg, new SaturationTargetTracker(cfg), Result(ScanCycleStatus.Fresh, Ap(Sat.A, "Net", -60, age: 42_000)), 1);
        Assert.That(obs.Single().Status, Is.EqualTo(ObservationStatus.Stale));
    }

    [Test]
    public void SsidScope_RegistersBssidsOnFirstSight_AndCountsCoChannel()
    {
        var cfg = Sat.Config(SaturationScope.SingleSsid, "Net");
        var tracker = new SaturationTargetTracker(cfg);
        var c1 = SaturationObservationBuilder.Build(cfg, tracker, Result(ScanCycleStatus.Fresh,
            Ap(Sat.A, "Net", -55, ch: 6, band: "2.4 GHz"), Ap("BB:00:00:00:00:01", "Other", -70, ch: 6, band: "2.4 GHz"), Ap("BB:00:00:00:00:02", "Other", -72, ch: 8, band: "2.4 GHz")), 1);
        var c2 = SaturationObservationBuilder.Build(cfg, tracker, Result(ScanCycleStatus.Fresh, Ap(Sat.B, "Net", -65)), 2);
        Assert.Multiple(() =>
        {
            Assert.That(c1.Select(o => o.Bssid), Is.EqualTo(new[] { Sat.A }));
            Assert.That(c1[0].CoChannelCount, Is.EqualTo(1));
            Assert.That(c1[0].OverlapCount, Is.EqualTo(1));
            Assert.That(c2.Select(o => (o.Bssid, o.Status)), Is.EqualTo(new[] { (Sat.A, ObservationStatus.Missing), (Sat.B, ObservationStatus.Observed) }));
        });
    }
}

/// <summary>In-memory repository for runner tests.</summary>
internal sealed class MemorySaturationRepository : ISaturationRepository
{
    public readonly List<SaturationTestRecord> Tests = new();
    public readonly List<SaturationCycle> Cycles = new();
    public readonly List<SaturationMarker> Markers = new();
    public readonly List<SaturationTestStatus> StatusUpdates = new();

    public Task SaveTestAsync(SaturationTestConfig config, DateTime startUtc, SaturationTestStatus status) { Tests.Add(new(config, startUtc, null, status, 0)); return Task.CompletedTask; }
    public Task UpdateTestStatusAsync(Guid testId, SaturationTestStatus status, DateTime? endUtc, double? activeSeconds = null) { StatusUpdates.Add(status); ActiveSeconds = activeSeconds ?? ActiveSeconds; return Task.CompletedTask; }
    public double? ActiveSeconds;
    public Task SaveCycleAsync(Guid testId, SaturationCycle cycle)
    {
        if (!Tests.Any(t => t.Config.TestId == testId)) throw new InvalidOperationException("FK: cycle before test");
        Cycles.Add(cycle); return Task.CompletedTask;
    }
    public Task SaveMarkerAsync(Guid testId, SaturationMarker marker) { Markers.Add(marker); return Task.CompletedTask; }
    public Task<IReadOnlyList<SaturationTestRecord>> GetTestsAsync() => Task.FromResult<IReadOnlyList<SaturationTestRecord>>(Tests);
    public Task<SaturationTestRecord?> GetTestAsync(Guid testId) => Task.FromResult(Tests.FirstOrDefault(t => t.Config.TestId == testId));
    public Task<IReadOnlyList<SaturationCycle>> GetCyclesAsync(Guid testId) => Task.FromResult<IReadOnlyList<SaturationCycle>>(Cycles);
    public Task<IReadOnlyList<SaturationMarker>> GetMarkersAsync(Guid testId) => Task.FromResult<IReadOnlyList<SaturationMarker>>(Markers);
    public Task DeleteTestAsync(Guid testId) { Tests.RemoveAll(t => t.Config.TestId == testId); return Task.CompletedTask; }
}

[TestFixture]
public class SaturationRunnerTests
{
    private static SaturationTestConfig Fast(TimeSpan? duration) => new()
    {
        Name = "Runner",
        Scope = SaturationScope.SingleSsid,
        TargetSsids = new[] { "PlantOps" },
        Interval = TimeSpan.FromMilliseconds(20),
        ScanTimeout = TimeSpan.FromMilliseconds(50),
        Duration = duration,
        Location = "Bench"
    };

    [Test]
    public async Task Runs_ForDuration_PersistsInOrder_AndReports()
    {
        var repo = new MemorySaturationRepository();
        var provider = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider();
        await using var runner = new SaturationTestRunner(provider, repo, new SaturationAnalyzer(), new NullLogger());
        int progress = 0;
        runner.Progress += _ => progress++;
        await runner.StartAsync(Fast(TimeSpan.FromMilliseconds(300)));
        var report = await runner.WaitForCompletionAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report, Is.Not.Null);
            Assert.That(report!.Status, Is.EqualTo(SaturationTestStatus.Completed));
            Assert.That(report.Cycles.Count, Is.GreaterThanOrEqualTo(5));
            Assert.That(report.Targets.Select(t => t.Ssid).Distinct(), Is.EqualTo(new[] { "PlantOps" }));
            Assert.That(report.Targets, Has.Count.EqualTo(2), "both PlantOps BSSIDs tracked");
            Assert.That(repo.Cycles, Has.Count.EqualTo(report.Cycles.Count), "every cycle persisted after the test row");
            Assert.That(repo.StatusUpdates.Last(), Is.EqualTo(SaturationTestStatus.Completed));
            Assert.That(progress, Is.EqualTo(report.Cycles.Count));
        });
    }

    [Test]
    public async Task PauseResumeStop_AndMarkers_AreRecorded()
    {
        var repo = new MemorySaturationRepository();
        await using var runner = new SaturationTestRunner(new WirelessScanner.Infrastructure.MockNetworkAdapterProvider(), repo, new SaturationAnalyzer(), new NullLogger());
        await runner.StartAsync(Fast(null));
        await Task.Delay(80);
        runner.AddMarker("microwave on");
        runner.Pause();
        int pausedCount = repo.Cycles.Count;
        await Task.Delay(120);
        int afterPause = repo.Cycles.Count;
        runner.Resume();
        await Task.Delay(80);
        var report = await runner.StopAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report!.Status, Is.EqualTo(SaturationTestStatus.Stopped));
            Assert.That(afterPause - pausedCount, Is.LessThanOrEqualTo(1), "no scanning while paused");
            Assert.That(report.Markers.Select(m => m.Text), Is.EqualTo(new[] { "microwave on", "Paused", "Resumed" }));
            Assert.That(repo.StatusUpdates, Does.Contain(SaturationTestStatus.Paused));
        });
    }

    [Test]
    public async Task FailedCycles_AreRecorded_NotDropped()
    {
        var provider = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider { CycleStatusOverride = n => n % 3 == 0 ? ScanCycleStatus.Failed : ScanCycleStatus.Fresh };
        await using var runner = new SaturationTestRunner(provider, new MemorySaturationRepository(), new SaturationAnalyzer(), new NullLogger());
        await runner.StartAsync(Fast(TimeSpan.FromMilliseconds(250)));
        var report = await runner.WaitForCompletionAsync();
        Assert.Multiple(() =>
        {
            Assert.That(report!.Health.Failed, Is.GreaterThanOrEqualTo(1));
            Assert.That(report.Health.ErrorCounts.ContainsKey("1168"), Is.True);
            Assert.That(report.Targets.All(t => t.Unknown >= 1), Is.True, "failed cycles become Unknown, not Missing");
        });
    }

    [Test]
    public void Start_WithoutTargets_IsRejected()
    {
        var runner = new SaturationTestRunner(new WirelessScanner.Infrastructure.MockNetworkAdapterProvider(), new MemorySaturationRepository(), new SaturationAnalyzer(), new NullLogger());
        Assert.ThrowsAsync<ArgumentException>(() => runner.StartAsync(new SaturationTestConfig { Scope = SaturationScope.SingleAp }));
    }
}

[TestFixture]
public class SaturationPersistenceAndExportTests
{
    private string _db = null!;
    private DbContextOptions<WlanDbContext> _options = null!;

    [SetUp]
    public void Setup()
    {
        _db = Path.Combine(Path.GetTempPath(), $"ws_sat_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<WlanDbContext>().UseSqlite($"Data Source={_db}").Options;
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static SaturationReport SampleReport()
    {
        var cfg = Sat.Config() with { Client = "Client, Inc.", Operator = "Tester", Notes = "Line1 \"quoted\"" };
        var cycles = Sat.Series(-50, -50, -50, -50, -62, -50, null, null, -51);
        var markers = new[] { new SaturationMarker(Sat.T0.AddSeconds(12), 12000, "door closed") };
        return new SaturationAnalyzer().Analyze(cfg, cycles, markers, Sat.T0, Sat.T0.AddSeconds(45), SaturationTestStatus.Completed);
    }

    [Test]
    public async Task Repository_RoundTripsTestCyclesMarkers_AndCascadesDelete()
    {
        var repo = new SaturationRepository(new MockDbContextFactory(_options));
        var report = SampleReport();
        await repo.SaveTestAsync(report.Config, report.StartUtc, SaturationTestStatus.Running);
        foreach (var c in report.Cycles) await repo.SaveCycleAsync(report.Config.TestId, c);
        foreach (var m in report.Markers) await repo.SaveMarkerAsync(report.Config.TestId, m);
        await repo.UpdateTestStatusAsync(report.Config.TestId, SaturationTestStatus.Completed, report.EndUtc);

        var record = await repo.GetTestAsync(report.Config.TestId);
        var cycles = await repo.GetCyclesAsync(report.Config.TestId);
        var markers = await repo.GetMarkersAsync(report.Config.TestId);
        var reanalysed = new SaturationAnalyzer().Analyze(record!.Config, cycles, markers, record.StartUtc, record.EndUtc, record.Status);

        Assert.Multiple(() =>
        {
            Assert.That(record.Status, Is.EqualTo(SaturationTestStatus.Completed));
            Assert.That(record.CycleCount, Is.EqualTo(report.Cycles.Count));
            Assert.That(record.Config.Interval, Is.EqualTo(report.Config.Interval));
            Assert.That(cycles.SelectMany(c => c.Observations).Count(o => o.Status == ObservationStatus.Missing), Is.EqualTo(2));
            Assert.That(markers.Single().Text, Is.EqualTo("door closed"));
            Assert.That(reanalysed.Events.Count, Is.EqualTo(report.Events.Count), "report re-derivable from storage (SAT-AC-08)");
        });

        await repo.DeleteTestAsync(report.Config.TestId);
        using var ctx = new WlanDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(ctx.SaturationCycles.Count(), Is.EqualTo(0));
            Assert.That(ctx.SaturationObservations.Count(), Is.EqualTo(0));
            Assert.That(ctx.SaturationMarkers.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Exports_CsvBundleJsonPdf_ContainAllSections()
    {
        var report = SampleReport();
        var dir = Path.Combine(Path.GetTempPath(), $"ws_sat_export_{Guid.NewGuid():N}");
        var export = new SaturationExportService();
        try
        {
            var files = await export.ExportCsvBundleAsync(dir, "test", report);
            var json = Path.Combine(dir, "test.json");
            var pdf = Path.Combine(dir, "test.pdf");
            await export.ExportJsonAsync(json, report);
            await export.ExportPdfAsync(pdf, report);

            string samples = File.ReadAllText(files.Single(f => f.EndsWith("_samples.csv")));
            string summary = File.ReadAllText(files.Single(f => f.EndsWith("_summary.csv")));
            string events = File.ReadAllText(files.Single(f => f.EndsWith("_events.csv")));
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var pdfBytes = File.ReadAllBytes(pdf);

            Assert.Multiple(() =>
            {
                Assert.That(files.Select(Path.GetFileName), Is.EquivalentTo(new[] { "test_info.csv", "test_samples.csv", "test_events.csv", "test_summary.csv", "test_cycles.csv", "test_markers.csv" }));
                Assert.That(samples, Does.Contain(",Missing,"), "missing rows included (SAT-AC-06)");
                Assert.That(samples.Split('\n').Count(l => l.Contains(",Missing,")), Is.EqualTo(2));
                Assert.That(samples, Does.Contain("RSSI_dBm").And.Contain("BeaconAge_ms"));
                Assert.That(summary, Does.Contain("StabilityScore").And.Contain("Findings"));
                Assert.That(events, Does.Contain("Drop").And.Contain("Dropout"));
                Assert.That(doc.RootElement.GetProperty("schema").GetString(), Is.EqualTo(SaturationExportService.JsonSchema));
                Assert.That(doc.RootElement.GetProperty("analyticsVersion").GetString(), Is.EqualTo(SaturationAnalyzer.AnalyticsVersion));
                Assert.That(doc.RootElement.GetProperty("measurementBoundary").GetString(), Does.Contain("UNAVAILABLE"));
                Assert.That(doc.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(report.Events.Count));
                Assert.That(pdfBytes.Length, Is.GreaterThan(10_000));
                Assert.That(System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 4), Is.EqualTo("%PDF"));
                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty, "atomic writes leave no temp files");
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Csv_EscapesCommasQuotesAndNewlines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SaturationExportService.Csv("a,b"), Is.EqualTo("\"a,b\""));
            Assert.That(SaturationExportService.Csv("say \"hi\""), Is.EqualTo("\"say \"\"hi\"\"\""));
            Assert.That(SaturationExportService.Csv("plain"), Is.EqualTo("plain"));
        });
    }
}
