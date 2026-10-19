using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>Physical units through selection, explorer, test configuration, analytics and reports (DR-0013).</summary>
[TestFixture]
public class PhysicalUnitIntegrationTests
{
    private const string R24a = "9A:3C:5E:1F:42:70", R24b = "9A:3C:5E:1F:42:73", R5a = "9A:3C:5E:26:42:70", R5b = "9A:3C:5E:26:42:74", R5h = "9A:3C:5E:26:42:76";

    private static PhysicalUnitGrouper Router(out PhysicalUnit unit)
    {
        var g = new PhysicalUnitGrouper();
        static AccessPoint A(string b, string s, string band, int ch, double tsf) =>
            new(b, s, null, band, ch, 20, -50, null, 0, 0, "WPA2", "Infrastructure", "Unknown Vendor", null, DateTime.UtcNow) { TsfOffsetMs = tsf };
        g.Observe(new[]
        {
            A(R24a, "Home", "2.4 GHz", 6, 10.0), A(R24b, "Home-IoT", "2.4 GHz", 6, 10.4),
            A(R5a, "Home", "5 GHz", 149, 900.0), A(R5b, "Home-Guest", "5 GHz", 149, 900.5), A(R5h, PhysicalUnitGrouper.HiddenSsid, "5 GHz", 149, 900.8),
            A("00:17:88:A1:B2:C3", "Other", "2.4 GHz", 11, 5000.0)
        }, DateTime.UtcNow);
        unit = g.Get(g.UnitOf(R24a)!)!;
        return g;
    }

    [Test]
    public void Selection_WholeUnit_IncludesEverySsidAndHiddenBss_AndDerivesUnitScopes()
    {
        var g = Router(out var unit);
        var s = new NetworkSelection { UnitResolver = g };
        Assert.That(s.ToggleUnit(unit), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(s.Matches("Home-IoT", R24b) && s.Matches(PhysicalUnitGrouper.HiddenSsid, R5h), Is.True);
            Assert.That(s.Matches("Other", "00:17:88:A1:B2:C3"), Is.False);
            Assert.That(s.UnitBssids(), Has.Count.EqualTo(5));
            Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.SingleUnit));
            Assert.That(s.StateOfUnit(unit), Is.EqualTo(GroupSelectionState.All));
            Assert.That(s.Describe(_ => 0), Does.Contain("1 access point (hardware: 5 BSSIDs"));
            Assert.That(s.StateOf("Home", new[] { R24a, R5a }), Is.EqualTo(GroupSelectionState.All), "SSID rows show the unit's members as selected");
        });

        s.ToggleUnit(g.Get(g.UnitOf("00:17:88:A1:B2:C3")!)!);
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.MultipleUnits));
        s.SetSsid("Office", true);
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.Mixed));
    }

    [Test]
    public void Selection_UntickingOneMember_ConvertsUnitToFixedBssids()
    {
        var g = Router(out var unit);
        var s = new NetworkSelection { UnitResolver = g };
        s.ToggleUnit(unit);
        s.ToggleAp(PhysicalUnitGrouper.HiddenSsid, R5h, new[] { R5h });
        Assert.Multiple(() =>
        {
            Assert.That(s.Units, Is.Empty);
            Assert.That(s.Bssids, Is.EquivalentTo(new[] { R24a, R24b, R5a, R5b }));
            Assert.That(s.Matches(PhysicalUnitGrouper.HiddenSsid, R5h), Is.False);
            Assert.That(s.StateOfUnit(unit), Is.EqualTo(GroupSelectionState.Partial));
        });
    }

    [Test]
    public void Selection_LockFreezesUnitMembership()
    {
        var g = Router(out var unit);
        var s = new NetworkSelection { UnitResolver = g };
        s.ToggleUnit(unit);
        s.Lock();
        g.MoveToNewUnit(R5h);   // regrouping while a test runs (the explorer refuses it; the model must not drift anyway)
        Assert.That(s.Matches(PhysicalUnitGrouper.HiddenSsid, R5h), Is.True, "frozen at lock time");
        Assert.That(s.ToggleUnit(unit), Is.False, "locked");
        s.Unlock();
        Assert.That(s.Matches(PhysicalUnitGrouper.HiddenSsid, R5h), Is.False, "after the test the current grouping applies");
    }

    [Test]
    public void Tracker_UnitScope_TracksMembersFromFirstCycle_WithSsids()
    {
        var cfg = new SaturationTestConfig
        {
            Scope = SaturationScope.SingleUnit,
            TargetBssids = new[] { R24a, R5h },
            TargetUnits = new[] { new SaturationUnitTarget { Id = "U-1", Members = new[] { new SaturationUnitMember(R24a, "Home", "2.4 GHz", "r1"), new SaturationUnitMember(R5h, "", "5 GHz", "r2") } } }
        };
        var tracker = new SaturationTargetTracker(cfg);
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Bssids, Is.EqualTo(new[] { R24a, R5h }));
            Assert.That(tracker.SsidOf(R24a), Is.EqualTo("Home"));
            Assert.That(tracker.InScope(new AccessPoint("11:22:33:44:55:66", "Home", null, "5 GHz", 36, 20, -50, null, 0, 0, "", "", null, null, DateTime.UtcNow)), Is.False,
                "a unit test never picks up other BSSIDs of the same SSID");
        });
    }

    [Test]
    public async Task Explorer_AccessPointView_SelectsUnit_AndTestConfigResolvesItAtStart()
    {
        var (vm, sat, _, scheduler) = LiveSurveyFixture.Build();
        await vm.PerformScanAsync();
        vm.Explorer.Grouping = ExplorerGrouping.AccessPoint;
        var plant = vm.Explorer.UnitGroups.Single(u => u.Aps.Any(a => a.Ssid == "PlantOps"));
        Assert.Multiple(() =>
        {
            Assert.That(vm.Explorer.IsUnitView, Is.True);
            Assert.That(plant.Aps, Has.Count.EqualTo(4), "PlantOps radios share one MAC family");
            Assert.That(plant.ConfidenceText, Is.EqualTo("MEDIUM"));
            Assert.That(plant.EvidenceText, Does.Contain("Related MAC addresses"));
            Assert.That(vm.Explorer.CountText, Does.Contain("access points"));
        });

        vm.Explorer.ToggleUnit(plant);
        var cfg = sat.BuildConfig();
        Assert.Multiple(() =>
        {
            Assert.That(cfg.Scope, Is.EqualTo(SaturationScope.SingleUnit));
            Assert.That(cfg.TargetUnits, Has.Count.EqualTo(1));
            Assert.That(cfg.TargetUnits[0].Members, Has.Count.EqualTo(4));
            Assert.That(cfg.TargetBssids, Has.Count.EqualTo(4));
            Assert.That(cfg.TargetUnits[0].Evidence, Is.Not.Empty);
            Assert.That(vm.FilteredAccessPoints.Select(a => a.SSID).Distinct(), Is.EqualTo(new[] { "PlantOps" }));
        });

        // corrections survive a refresh; the network view is unchanged
        vm.Explorer.RenameUnit(plant.Id, "Plant floor AP");
        await Task.Delay(50);
        Assert.That(vm.Explorer.UnitGroups.Single(u => u.Id == plant.Id).Name, Is.EqualTo("Plant floor AP"));
        vm.Explorer.Grouping = ExplorerGrouping.Network;
        Assert.That(vm.Explorer.Groups.Any(g => g.Ssid == "PlantOps"), Is.True);
        await scheduler.DisposeAsync();
    }

    [Test]
    public async Task Overrides_PersistInSettings_AndReloadOnStart()
    {
        var settings = new MockSettingsRepository();
        var provider = new CountingProvider();
        provider.Aps.Clear();
        provider.Aps.AddRange(LiveSurveyFixture.Environment());
        var repo = new MockSessionRepository();
        var vm = new MainWindowViewModel(provider, new SessionManager(repo, new NullLogger()), repo, new MockExportService(), new MockRestApiServer(), settings);
        vm.Adapters.Add(LiveSurveyFixture.Adapter);
        vm.SelectedAdapter = LiveSurveyFixture.Adapter;
        await vm.PerformScanAsync();
        vm.UnitGrouper.Observe(LiveSurveyFixture.Environment(), DateTime.UtcNow);
        vm.UnitGrouper.MoveToNewUnit("8C:5A:25:04:91:B3");
        await Task.Delay(50);
        var json = await settings.GetSettingAsync(MainWindowViewModel.UnitOverridesSettingKey);
        Assert.That(json, Does.Contain("8C:5A:25:04:91:B3"));

        var vm2 = new MainWindowViewModel(provider, new SessionManager(repo, new NullLogger()), repo, new MockExportService(), new MockRestApiServer(), settings);
        await Task.Delay(50);
        vm2.UnitGrouper.Observe(LiveSurveyFixture.Environment(), DateTime.UtcNow);
        Assert.That(vm2.UnitGrouper.UnitOf("8C:5A:25:04:91:B3"), Is.EqualTo("M-8C:5A:25:04:91:B3"));
    }

    // ---------------- analytics + reports ----------------

    private static SaturationUnitTarget RouterTarget() => new()
    {
        Id = "U-96", Name = "Home router", Confidence = UnitConfidence.High, Evidence = new[] { "Same radio clock." },
        Members = new[]
        {
            new SaturationUnitMember(R5a, "Home", "5 GHz", "5 GHz#R5"), new SaturationUnitMember(R5b, "Home-Guest", "5 GHz", "5 GHz#R5"),
            new SaturationUnitMember(R24a, "Home", "2.4 GHz", "2.4 GHz#R24")
        }
    };

    /// <summary>10 fresh cycles: 5 GHz SSIDs 1 dB apart, but Home-Guest missing in cycles 3 and 4; 2.4 GHz always there.</summary>
    private static List<SaturationCycle> RouterCycles() => Enumerable.Range(1, 10).Select(i =>
    {
        var obs = new List<SaturationObservation> { Sat.Obs(i, R5a, -50, ssid: "Home"), Sat.Obs(i, R24a, -40, band: "2.4 GHz", ch: 6, ssid: "Home") };
        obs.Add(i is 3 or 4 ? Sat.Obs(i, R5b, null, ObservationStatus.Missing, ssid: "Home-Guest") : Sat.Obs(i, R5b, -51, ssid: "Home-Guest"));
        return Sat.Cycle(i, obs.ToArray());
    }).ToList();

    [Test]
    public void AnalyzeUnit_Availability_Consistency_SsidOnlyOutage()
    {
        var u = SaturationAnalyzer.AnalyzeUnit(RouterTarget(), RouterCycles());
        var r5 = u.Radios.Single(r => r.Band == "5 GHz");
        Assert.Multiple(() =>
        {
            Assert.That(u.UnitAvailabilityPct, Is.EqualTo(100));
            Assert.That(u.BestMeanDbm, Is.EqualTo(-40));
            Assert.That(u.BssidCount, Is.EqualTo(3));
            Assert.That(u.SsidCount, Is.EqualTo(2));
            Assert.That(r5.ConsistencyMeanDeltaDb, Is.EqualTo(1.0));
            Assert.That(r5.ConsistencyMaxDeltaDb, Is.EqualTo(1.0));
            Assert.That(r5.SsidOnlyOutagePct[R5b], Is.EqualTo(20.0), "2 of 10 cycles with the radio on air");
            Assert.That(r5.SsidOnlyOutagePct[R5a], Is.EqualTo(0.0));
            Assert.That(u.Radios.Single(r => r.Band == "2.4 GHz").ConsistencyMeanDeltaDb, Is.Null, "one SSID on that radio");
            Assert.That(u.Findings, Has.Some.Contains("Home-Guest").And.Contains("SSID/service issue"));
            Assert.That(u.Findings, Has.None.Contains("differ by"), "1 dB is within one radio's expected spread");
        });
    }

    [Test]
    public void AnalyzeUnit_DivergingSsids_FlagMisgrouping_AndMediumConfidenceIsStated()
    {
        var target = RouterTarget() with { Confidence = UnitConfidence.Medium };
        var cycles = Enumerable.Range(1, 6).Select(i => Sat.Cycle(i, Sat.Obs(i, R5a, -50, ssid: "Home"), Sat.Obs(i, R5b, -62, ssid: "Home-Guest"))).ToList();
        var u = SaturationAnalyzer.AnalyzeUnit(target, cycles);
        Assert.Multiple(() =>
        {
            Assert.That(u.Radios.Single(r => r.Band == "5 GHz").ConsistencyMeanDeltaDb, Is.EqualTo(12.0));
            Assert.That(u.Findings, Has.Some.Contains("differ by 12.0 dB"));
            Assert.That(u.Findings, Has.Some.Contains("Grouping confidence Medium"));
            Assert.That(u.UnitAvailabilityPct, Is.EqualTo(100), "a member absent from every cycle does not matter while another member is on air");
        });
    }

    [Test]
    public void AnalyzeUnit_StaleCyclesDoNotCount_AndOffAirIsReported()
    {
        var cycles = RouterCycles();
        cycles[0] = cycles[0] with { Status = ScanCycleStatus.Stale };
        cycles[9] = Sat.Cycle(10, Sat.Obs(10, R5a, null, ObservationStatus.Missing), Sat.Obs(10, R5b, null, ObservationStatus.Missing), Sat.Obs(10, R24a, null, ObservationStatus.Missing));
        var u = SaturationAnalyzer.AnalyzeUnit(RouterTarget(), cycles);
        Assert.That(u.UnitAvailabilityPct, Is.EqualTo(Math.Round(800.0 / 9, 2)));
        Assert.That(u.Findings, Has.Some.Contains("completely off air"));
    }

    [Test]
    public async Task Reports_CarryUnitSection_InJsonCsvAndPdf_AndNothingExtraWithoutUnits()
    {
        var cfg = Sat.Config(SaturationScope.SingleUnit) with { TargetBssids = new[] { R5a, R5b, R24a }, TargetUnits = new[] { RouterTarget() } };
        var report = new SaturationAnalyzer().Analyze(cfg, RouterCycles(), Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(1), SaturationTestStatus.Completed);
        var dir = Path.Combine(Path.GetTempPath(), $"ws_units_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var export = new SaturationExportService();
            var files = await export.ExportCsvBundleAsync(dir, "t", report);
            await export.ExportJsonAsync(Path.Combine(dir, "t.json"), report);
            await export.ExportPdfAsync(Path.Combine(dir, "t.pdf"), report);
            var unitsCsv = File.ReadAllLines(files.Single(f => f.EndsWith("_units.csv", StringComparison.Ordinal)));
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "t.json")));
            var info = File.ReadAllText(files.Single(f => f.EndsWith("_info.csv", StringComparison.Ordinal)));
            Assert.Multiple(() =>
            {
                Assert.That(report.UnitSummaries, Has.Count.EqualTo(1));
                Assert.That(unitsCsv[0], Does.StartWith("# Access points (physical units) are INFERRED"));
                Assert.That(unitsCsv[1], Does.StartWith("UnitId,UnitName,Confidence"));
                Assert.That(unitsCsv.Length, Is.EqualTo(2 + 3), "one row per member");
                Assert.That(unitsCsv.Single(l => l.Contains($",{R5b},Home-Guest,")), Does.Contain(",20,"), "SSID-only outage column");
                Assert.That(json.RootElement.GetProperty("units")[0].GetProperty("name").GetString(), Is.EqualTo("Home router"));
                Assert.That(json.RootElement.GetProperty("test").GetProperty("targetUnits")[0].GetProperty("members").GetArrayLength(), Is.EqualTo(3));
                Assert.That(info, Does.Contain("TargetUnits").And.Contain("Home router [U-96, High, 3 BSSIDs]"));
                Assert.That(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "t.pdf")), 0, 4), Is.EqualTo("%PDF"));
            });

            var plain = new SaturationAnalyzer().Analyze(Sat.Config(), Sat.Series(-50, -51), Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddMinutes(1), SaturationTestStatus.Completed);
            var plainFiles = await export.ExportCsvBundleAsync(Path.Combine(dir, "plain"), "p", plain);
            Assert.That(plainFiles.Any(f => f.EndsWith("_units.csv", StringComparison.Ordinal)), Is.False, "no unit file for tests without units");
        }
        finally { Directory.Delete(dir, true); }
    }
}
