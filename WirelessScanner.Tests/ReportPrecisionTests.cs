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

/// <summary>
/// Report completeness and precision (owner request 2026-10-16, DR-0012, SAT-ANALYTICS-2). Defects found in the first live
/// 1.2.0 report: active time short of the real duration, exposure longer than the test, a 35.6 dB/h "trend" from 5 minutes,
/// co-channel counting cached entries and ignoring bonded channels, no provenance, unreadable chart labels.
/// </summary>
[TestFixture]
public class ReportPrecisionTests
{
    // ---------------- capture: parsers ----------------

    [Test]
    public void BssLoad_ParsedFromElement11_AndNullWhenAbsentOrTruncated()
    {
        var withLoad = new byte[] { 0, 3, (byte)'a', (byte)'b', (byte)'c', 11, 5, 0x0C, 0x00, 0x80, 0x10, 0x27 };
        var load = WlanAdapter.ParseBssLoad(withLoad);
        Assert.Multiple(() =>
        {
            Assert.That(load, Is.Not.Null);
            Assert.That(load!.Value.Stations, Is.EqualTo(12));
            Assert.That(load.Value.UtilizationPct, Is.EqualTo(50.2), "128/255 = 50.2 %");
            Assert.That(load.Value.AdmissionCapacity, Is.EqualTo(10000));
            Assert.That(WlanAdapter.ParseBssLoad(new byte[] { 0, 1, (byte)'x' }), Is.Null, "not advertised");
            Assert.That(WlanAdapter.ParseBssLoad(new byte[] { 11, 3, 1, 0, 9 }), Is.Null, "malformed element ignored, never guessed");
        });
    }

    [TestCase(7u, "802.11n")]
    [TestCase(8u, "802.11ac")]
    [TestCase(10u, "802.11ax")]
    [TestCase(11u, "802.11be")]
    [TestCase(0u, null)]
    [TestCase(99u, null)]
    public void PhyType_Names(uint phy, string? expected) => Assert.That(WlanAdapter.PhyName(phy), Is.EqualTo(expected));

    // ---------------- geometry ----------------

    private static AccessPoint Ap(string bssid, string band, int ch, int? w, int rssi, double? ageMs = 100) =>
        new(bssid, "N", null, band, ch, w, rssi, null, 0, 70, "WPA2-Personal", "Infrastructure", "V", null, DateTime.UtcNow, ageMs);

    [Test]
    public void Overlap_UsesBondedBlocks_InEveryBand()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChannelMath.Overlaps(Ap("a", "5 GHz", 36, 80, -50), Ap("b", "5 GHz", 44, 20, -60)), Is.True, "80 MHz 36-48 covers ch 44");
            Assert.That(ChannelMath.Overlaps(Ap("a", "5 GHz", 36, 20, -50), Ap("b", "5 GHz", 40, 20, -60)), Is.False, "adjacent 20 MHz channels do not overlap");
            Assert.That(ChannelMath.Overlaps(Ap("a", "5 GHz", 40, 160, -50), Ap("b", "5 GHz", 60, 20, -60)), Is.True, "160 MHz 36-64 covers ch 60");
            Assert.That(ChannelMath.Overlaps(Ap("a", "2.4 GHz", 1, 20, -50), Ap("b", "2.4 GHz", 4, 20, -60)), Is.True);
            Assert.That(ChannelMath.Overlaps(Ap("a", "2.4 GHz", 1, 20, -50), Ap("b", "2.4 GHz", 6, 20, -60)), Is.False, "1 and 6 are clean");
            Assert.That(ChannelMath.Overlaps(Ap("a", "5 GHz", 36, 20, -50), Ap("b", "6 GHz", 1, 20, -60)), Is.False, "different bands");
            Assert.That(ChannelMath.IsDfs("5 GHz", 52, 20), Is.True);
            Assert.That(ChannelMath.IsDfs("5 GHz", 36, 80), Is.False, "36-48 ends at 5250 MHz: touching is not inside");
            Assert.That(ChannelMath.IsDfs("5 GHz", 36, 160), Is.True, "160 MHz block reaches into 52-64");
        });
    }

    // ---------------- capture: observation builder ----------------

    [Test]
    public void Neighbours_AreFreshOnly_IncludeBondedOverlap_AndGiveStrongestForMargin()
    {
        var cfg = new SaturationTestConfig { Scope = SaturationScope.SingleAp, TargetBssids = new[] { "T" }, Interval = TimeSpan.FromSeconds(5) };
        var target = Ap("T", "5 GHz", 40, 160, -55) with { PhyType = "802.11ax", StationCount = 7, ChannelUtilizationPct = 31.4, BeaconIntervalTu = 100 };
        var list = new List<AccessPoint>
        {
            target,
            Ap("co-fresh", "5 GHz", 40, 20, -70),
            Ap("co-cached", "5 GHz", 40, 20, -40, ageMs: 60_000),      // stale cached entry: ignored (was counted in 1.2)
            Ap("bonded", "5 GHz", 56, 20, -62),                        // inside the 160 MHz block
            Ap("outside", "5 GHz", 100, 20, -30),
        };
        var obs = SaturationObservationBuilder.Build(cfg, new SaturationTargetTracker(cfg),
            new ScanCycleResult(ScanCycleStatus.Fresh, DateTime.UtcNow, DateTime.UtcNow, 1000, list), 1).Single();
        Assert.Multiple(() =>
        {
            Assert.That(obs.CoChannelCount, Is.EqualTo(1));
            Assert.That(obs.OverlapCount, Is.EqualTo(1));
            Assert.That(obs.StrongestOverlapRssi, Is.EqualTo(-62), "-40 was cached, -30 is outside the block");
            Assert.That(obs.PhyType, Is.EqualTo("802.11ax"));
            Assert.That(obs.StationCount, Is.EqualTo(7));
            Assert.That(obs.ChannelUtilizationPct, Is.EqualTo(31.4));
            Assert.That(obs.BeaconIntervalTu, Is.EqualTo(100));
        });
    }

    // ---------------- analytics SAT-ANALYTICS-2 ----------------

    private static SaturationCycle C(long seq, long elapsedMs, int rssi, int? neighbour = null, int? stations = null, double? util = null, int lq = 80) =>
        new(seq, Sat.T0.AddMilliseconds(elapsedMs), elapsedMs, ScanCycleStatus.Fresh, 1500, 1, null, null, new[]
        {
            new SaturationObservation(seq, Sat.A, "Net", ObservationStatus.Observed, rssi, lq, 36, "5 GHz", 20, 300, 0, 0,
                Security: "WPA2-Personal", Vendor: "Cisco Systems", PhyType: "802.11ac", BeaconIntervalTu: 100, CenterFrequencyMhz: 5180,
                StationCount: stations, ChannelUtilizationPct: util, StrongestOverlapRssi: neighbour)
        });

    [Test]
    public void ActiveTime_IsMeasured_AndExposureNeverExceedsTheTest()
    {
        // The live 1.2.0 case: 60 cycles, first at 1.149 s, last at 296.664 s, 5 s interval, 5 min planned; all below -70 dBm.
        var cfg = Sat.Config() with { Duration = TimeSpan.FromMinutes(5) };
        var cycles = Enumerable.Range(0, 60).Select(i => C(i + 1, 1149 + (long)(i * 5009.6), -77)).ToList();
        var a = new SaturationAnalyzer();
        var measured = a.Analyze(cfg, cycles, Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(5), SaturationTestStatus.Completed, 300.0);
        var legacy = a.Analyze(cfg, cycles, Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(5), SaturationTestStatus.Completed);
        var t = measured.Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(measured.AnalyticsVersion, Is.EqualTo("SAT-ANALYTICS-3"));
            Assert.That(measured.ActiveSeconds, Is.EqualTo(300.0), "measured, not the last scan's time (296.7 s in 1.2.0)");
            Assert.That(legacy.ActiveSeconds, Is.EqualTo(300.0).Within(0.001), "pre-1.3 tests: last cycle + interval, capped at the planned duration");
            Assert.That(t.LongestBelowFairSeconds, Is.LessThanOrEqualTo(300.0 - 1.149 + 1e-9), "1.2.0 reported 301 s, longer than the test");
            Assert.That(t.LongestBelowFairSeconds, Is.EqualTo(300.0 - 1.149).Within(0.001), "from the first observation to the end of the test");
            Assert.That(t.PctBelowFair, Is.EqualTo(100));
        });
    }

    [Test]
    public void Trend_IsWithheldBelow15Minutes_AndReportedAbove()
    {
        var a = new SaturationAnalyzer();
        var shortCycles = Enumerable.Range(0, 60).Select(i => C(i + 1, i * 5000L, -50 - i / 10)).ToList();     // 5 min, falling
        var longCycles = Enumerable.Range(0, 200).Select(i => C(i + 1, i * 5000L, -50 - i / 20)).ToList();     // 16.6 min, falling
        var s = a.Analyze(Sat.Config(), shortCycles, Array.Empty<SaturationMarker>(), Sat.T0, null, SaturationTestStatus.Completed, 300).Targets.Single();
        var l = a.Analyze(Sat.Config() with { Duration = TimeSpan.FromMinutes(20) }, longCycles, Array.Empty<SaturationMarker>(), Sat.T0, null, SaturationTestStatus.Completed, 1000).Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(s.TrendDbPerHour, Is.Null, "no extrapolated per-hour number from 5 minutes (1.2.0 printed 35.6 dB/h)");
            Assert.That(s.ObservedSpanSeconds, Is.EqualTo(295));
            Assert.That(l.TrendDbPerHour, Is.Not.Null.And.LessThan(0));
            Assert.That(l.Findings, Has.Some.Contains("Trending down"));
        });
    }

    [Test]
    public void LinkQuality_BssLoad_InterferenceMargin_AndIdentity_AreSummarised()
    {
        var cycles = new List<SaturationCycle>
        {
            C(1, 0, -50, neighbour: -75, stations: 4, util: 20, lq: 90),
            C(2, 5000, -52, neighbour: -60, stations: 9, util: 60, lq: 70),
            C(3, 10000, -51, neighbour: null, stations: null, util: null, lq: 80),
        };
        var t = new SaturationAnalyzer().Analyze(Sat.Config(), cycles, Array.Empty<SaturationMarker>(), Sat.T0, null, SaturationTestStatus.Completed, 15).Targets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(t.Vendor, Is.EqualTo("Cisco Systems"));
            Assert.That(t.Security, Is.EqualTo("WPA2-Personal"));
            Assert.That(t.PhyType, Is.EqualTo("802.11ac"));
            Assert.That(t.BeaconIntervalTu, Is.EqualTo(100));
            Assert.That(t.CenterFrequencyMhz, Is.EqualTo(5180));
            Assert.That(t.IsDfs, Is.False);
            Assert.That(t.LinkQualityMean, Is.EqualTo(80));
            Assert.That(t.LinkQualityMin, Is.EqualTo(70));
            Assert.That(t.SnrEstimatedMeanDb, Is.EqualTo(-51 + 95));
            Assert.That(t.LoadAdvertisedPct, Is.EqualTo(200.0 / 3).Within(1e-9));
            Assert.That(t.StationCountMean, Is.EqualTo(6.5));
            Assert.That(t.StationCountMax, Is.EqualTo(9));
            Assert.That(t.ChannelUtilizationMeanPct, Is.EqualTo(40));
            Assert.That(t.ChannelUtilizationMaxPct, Is.EqualTo(60));
            Assert.That(t.InterferenceMarginMedianDb, Is.EqualTo(16.5), "margins 25 and 8 -> median 16.5");
            Assert.That(t.InterferenceMarginMinDb, Is.EqualTo(8));
            Assert.That(t.StrongestOverlapRssiMax, Is.EqualTo(-60));
            Assert.That(t.Findings, Has.Some.Contains("Interference margin as low as 8 dB"));
            Assert.That(t.Findings, Has.Some.Contains("channel busy up to 60%"));
        });
    }

    // ---------------- storage ----------------

    [Test]
    public async Task SchemaUpgrade_AddsMissingColumns_KeepsRows_AndRoundTripsNewFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws_schema3_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WlanDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            // Simulate a 1.2 (schema 2) database: same tables without the 1.3 columns, with one stored test.
            using (var ctx = new WlanDbContext(options)) ctx.Database.EnsureCreated();
            using (var con = new SqliteConnection($"Data Source={path}"))
            {
                con.Open();
                foreach (var sql in new[]
                {
                    "ALTER TABLE SaturationTests DROP COLUMN ActiveSeconds",
                    "ALTER TABLE SaturationObservations DROP COLUMN Security", "ALTER TABLE SaturationObservations DROP COLUMN Vendor",
                    "ALTER TABLE SaturationObservations DROP COLUMN PhyType", "ALTER TABLE SaturationObservations DROP COLUMN BeaconIntervalTu",
                    "ALTER TABLE SaturationObservations DROP COLUMN CenterFrequencyMhz", "ALTER TABLE SaturationObservations DROP COLUMN StationCount",
                    "ALTER TABLE SaturationObservations DROP COLUMN ChannelUtilizationPct", "ALTER TABLE SaturationObservations DROP COLUMN StrongestOverlapRssi",
                    "PRAGMA user_version = 2",
                    "INSERT INTO SaturationTests (TestId, Name, Scope, ConfigJson, InterfaceName, Location, Facility, StartTime, Status) VALUES ('11111111-2222-3333-4444-555555555555','Old',0,'{}','A','L','F','2026-10-15 10:00:00',2)"
                })
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
            }
            MigrationResult result;
            using (var ctx = new WlanDbContext(options)) result = SchemaMigrator.Upgrade(ctx);
            Assert.That(result.CreatedObjects, Does.Contain("SaturationTests.ActiveSeconds").And.Contain("SaturationObservations.StationCount"));
            Assert.That(result.ToVersion, Is.EqualTo(SchemaMigrator.CurrentSchemaVersion)); // 4 since 1.5 (connection test tables, DR-0014)

            var repo = new SaturationRepository(new TestDbFactory(options));
            var old = (await repo.GetTestsAsync()).Single(t => t.Config.Name == "Old" || t.Config.TestId == Guid.Parse("11111111-2222-3333-4444-555555555555"));
            Assert.That(old.ActiveSeconds, Is.Null, "pre-1.3 test keeps working; active time is reconstructed");

            var cfg = Sat.Config() with { Name = "New" };
            await repo.SaveTestAsync(cfg, Sat.T0, SaturationTestStatus.Running);
            await repo.SaveCycleAsync(cfg.TestId, C(1, 0, -50, neighbour: -70, stations: 3, util: 12.5));
            await repo.UpdateTestStatusAsync(cfg.TestId, SaturationTestStatus.Completed, Sat.T0.AddMinutes(10), 600.0);
            var back = (await repo.GetCyclesAsync(cfg.TestId)).Single().Observations.Single();
            var rec = await repo.GetTestAsync(cfg.TestId);
            Assert.Multiple(() =>
            {
                Assert.That(rec!.ActiveSeconds, Is.EqualTo(600.0));
                Assert.That(back.StationCount, Is.EqualTo(3));
                Assert.That(back.ChannelUtilizationPct, Is.EqualTo(12.5));
                Assert.That(back.StrongestOverlapRssi, Is.EqualTo(-70));
                Assert.That(back.PhyType, Is.EqualTo("802.11ac"));
                Assert.That(back.Vendor, Is.EqualTo("Cisco Systems"));
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<WlanDbContext>
    {
        private readonly DbContextOptions<WlanDbContext> _o;
        public TestDbFactory(DbContextOptions<WlanDbContext> o) => _o = o;
        public WlanDbContext CreateDbContext() => new(_o);
    }

    // ---------------- exports: the report contract ----------------

    private static SaturationReport ContractReport()
    {
        var env = new SaturationEnvironment
        {
            AppVersion = "1.3.0", OsDescription = "Windows 10 (X64)", MachineName = "PC1", TimeZoneId = "Central Standard Time", UtcOffsetMinutes = -300,
            AdapterDescription = "Intel(R) Dual Band Wireless-AC 7265", AdapterMac = "00:11:22:33:44:55", DriverVersion = "20.70.32.1", DriverDate = "2023-01-05",
            DriverProvider = "Intel", CapturedUtc = Sat.T0,
            ConnectionAtStart = new ClientConnectionInfo { Connected = true, State = "connected", Ssid = "Net", Bssid = Sat.A, PhyType = "802.11ac", RxRateMbps = 866.7, TxRateMbps = 780, SignalQualityPct = 90, Channel = 36, Band = "5 GHz" }
        };
        var cfg = Sat.Config() with { Environment = env, Duration = TimeSpan.FromMinutes(5) };
        var cycles = Enumerable.Range(0, 60).Select(i => C(i + 1, 1000 + i * 5000L, -60 - i % 3, neighbour: -72, stations: 5, util: 25)).ToList();
        return new SaturationAnalyzer().Analyze(cfg, cycles, Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(5), SaturationTestStatus.Completed, 300);
    }

    [Test]
    public async Task Json_CarriesProvenancePrecisionAndAllMetrics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ws_contract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "r.json");
            await new SaturationExportService().ExportJsonAsync(file, ContractReport());
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var root = doc.RootElement;
            var t = root.GetProperty("targets")[0];
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("schema").GetString(), Is.EqualTo("wirelessscanner.saturation_report.v3"));
                Assert.That(root.GetProperty("analyticsVersion").GetString(), Is.EqualTo("SAT-ANALYTICS-3"));
                Assert.That(root.GetProperty("generatedBy").GetString(), Does.StartWith("WirelessScanner "));
                Assert.That(root.GetProperty("precision").GetString(), Does.Contain("nearest-rank"));
                Assert.That(root.GetProperty("neighbours").GetString(), Does.Contain("FRESH"));
                Assert.That(root.GetProperty("environment").GetProperty("driverVersion").GetString(), Is.EqualTo("20.70.32.1"));
                Assert.That(root.GetProperty("environment").GetProperty("connectionAtStart").GetProperty("rxRateMbps").GetDouble(), Is.EqualTo(866.7));
                Assert.That(root.GetProperty("startLocal").GetString(), Does.Match(@"[+-]\d\d:\d\d$"), "local time carries its UTC offset");
                Assert.That(root.GetProperty("activeSeconds").GetDouble(), Is.EqualTo(300));
                foreach (var name in new[] { "vendor", "security", "phyType", "beaconIntervalTu", "centerFrequencyMhz", "isDfs", "linkQualityMean", "snrEstimatedMeanDb",
                                             "stationCountMean", "channelUtilizationMeanPct", "interferenceMarginMedianDb", "interferenceMarginMinDb", "observedSpanSeconds" })
                    Assert.That(t.TryGetProperty(name, out _), Is.True, name);
                Assert.That(t.GetProperty("trendDbPerHour").ValueKind, Is.EqualTo(JsonValueKind.Null), "5-minute test: no trend number");
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task CsvBundle_HasInfoFile_LocalTimes_AndNewColumns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ws_contract_{Guid.NewGuid():N}");
        try
        {
            var files = await new SaturationExportService().ExportCsvBundleAsync(dir, "t", ContractReport());
            string Read(string suffix) => File.ReadAllText(files.Single(f => f.EndsWith(suffix)));
            var info = Read("_info.csv");
            var summaryHead = Read("_summary.csv").Split('\n')[0];
            var samplesHead = Read("_samples.csv").Split('\n')[1];
            var cyclesHead = Read("_cycles.csv").Split('\n')[0];
            Assert.Multiple(() =>
            {
                Assert.That(files, Has.Count.EqualTo(6));
                foreach (var key in new[] { "ReportSchema", "AnalyticsVersion", "GeneratedBy", "StartUtc", "StartLocal", "ActiveSeconds", "PlannedSeconds", "DriverVersion",
                                            "PcConnectionAtStart", "TimeZone", "Precision", "Neighbours", "MeasurementBoundary", "Thresholds" })
                    Assert.That(info, Does.Contain(key + ","), key);
                Assert.That(info, Does.Contain("connected to Net"));
                foreach (var col in new[] { "Vendor", "Security", "PhyType", "BeaconInterval_TU", "DFS", "LinkQuality_Mean_pct", "SNR_Estimated_Mean_dB", "ObservedSpan_s",
                                            "BssLoad_Stations_Mean", "BssLoad_ChannelBusy_Max_pct", "InterferenceMargin_Min_dB", "ActiveSec", "AppVersion" })
                    Assert.That(summaryHead, Does.Contain(col), col);
                foreach (var col in new[] { "WallLocal", "Overlap_Count", "StrongestNeighbour_dBm", "InterferenceMargin_dB", "BssLoad_Stations", "PhyType" })
                    Assert.That(samplesHead, Does.Contain(col), col);
                Assert.That(cyclesHead, Does.Contain("WallLocal"));
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Re-generates the reports of every test in a COPY of a real database (WS_REGEN_DB) into WS_REGEN_OUT, with the current
    /// analytics and exporter. Explicit: used to compare a live report before/after a change. Never touches the live database.
    /// </summary>
    [Test, Explicit("Needs WS_REGEN_DB and WS_REGEN_OUT"), Category("ReportRegeneration")]
    public async Task RegenerateReportsFromDatabaseCopy()
    {
        string? db = Environment.GetEnvironmentVariable("WS_REGEN_DB"), outDir = Environment.GetEnvironmentVariable("WS_REGEN_OUT");
        if (db == null || outDir == null) Assert.Ignore("Set WS_REGEN_DB and WS_REGEN_OUT.");
        Assert.That(db, Does.Not.Contain(@"ProgramData\WirelessScanner").IgnoreCase, "work on a copy only");
        var options = new DbContextOptionsBuilder<WlanDbContext>().UseSqlite($"Data Source={db}").Options;
        var repo = new SaturationRepository(new TestDbFactory(options));
        var export = new SaturationExportService();
        foreach (var rec in await repo.GetTestsAsync())
        {
            var cycles = await repo.GetCyclesAsync(rec.Config.TestId);
            var report = new SaturationAnalyzer().Analyze(rec.Config, cycles, await repo.GetMarkersAsync(rec.Config.TestId), rec.StartUtc, rec.EndUtc, rec.Status, rec.ActiveSeconds);
            string name = $"regen_{rec.StartUtc:yyyyMMdd_HHmm}";
            await export.ExportPdfAsync(Path.Combine(outDir!, name + ".pdf"), report);
            await export.ExportJsonAsync(Path.Combine(outDir!, name + ".json"), report);
            await export.ExportCsvBundleAsync(outDir!, name, report);
            TestContext.Out.WriteLine($"REGEN {name}: active {report.ActiveSeconds:0.000} s, targets {report.Targets.Count}, " +
                string.Join("; ", report.Targets.Select(t => $"longestBelowFair {t.LongestBelowFairSeconds:0.0} s, trend {(t.TrendDbPerHour?.ToString("0.0") ?? "null")}, co {t.CoChannelMean:0.00}, ovl {t.OverlapMean:0.00}")));
        }
        SqliteConnection.ClearAllPools();
    }

    [Test]
    public async Task SessionCsv_IsCultureInvariant_AndLabelsUtc()
    {
        // 1.2: values used the current culture (de-DE wrote "1,500" and broke the CSV) and times were UTC without saying so.
        var repo = new MockSessionRepository();
        var id = Guid.NewGuid();
        await repo.SaveSessionAsync(new CaptureSession(id, "S", "All", "Lab", Guid.NewGuid(), "Adapter", Sat.T0, Sat.T0.AddMinutes(1), CaptureSessionMode.Stationary, CaptureSessionStatus.Completed));
        await repo.SaveSampleAsync(new TelemetrySample(Sat.T0, "Z", Sat.A, "Net", "5 GHz", 36, -55, 40, 90, 1.5, null), id);
        var file = Path.Combine(Path.GetTempPath(), $"ws_session_{Guid.NewGuid():N}.csv");
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            await new ExportService(repo, new StatisticsCalculator()).ExportToCsvAsync(file, id);
            var text = await File.ReadAllTextAsync(file);
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("Timestamp_UTC"));
                Assert.That(text, Does.Contain("# Start Time (UTC): 2026-10-15 12:00:00"));
                Assert.That(text, Does.Contain("by WirelessScanner "));
                Assert.That(text, Does.Contain(",1.500"), "invariant decimal point");
                Assert.That(text, Does.Not.Contain("1,500"));
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Test]
    public void LocalTimes_CarryTheirOffset()
    {
        var text = SaturationExportService.LocalWithOffset(Sat.T0);
        Assert.That(text, Does.Match(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \(UTC[+-]\d\d:\d\d\)$"));
    }
}
