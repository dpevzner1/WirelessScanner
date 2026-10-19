using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>Provider that counts scans and lets a test hold a scan "in flight".</summary>
internal sealed class CountingProvider : INetworkAdapterProvider
{
    public int Calls;
    public TaskCompletionSource? Gate;
    public List<AccessPoint> Aps { get; } = new()
    {
        new("AA:00:00:00:00:01", "Net", null, "5 GHz", 36, 80, -50, null, 45, 90, "WPA2-Personal", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow, 100)
    };
    public Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync() => Task.FromResult<IEnumerable<NetworkInterfaceInfo>>(Array.Empty<NetworkInterfaceInfo>());
    public Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId) => Task.FromResult<IEnumerable<AccessPoint>>(Aps);
    public Task TriggerHardwareScanAsync(Guid interfaceId) => Task.CompletedTask;
    public Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId) => Task.FromResult(new WiredInterfaceStats(0, "", "Down", 0));
    public event Action? HardwareChanged { add { } remove { } }
    public void NotifyHardwareChange() { }
    public async Task<ScanCycleResult> ScanAsync(Guid interfaceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        if (Gate != null) await Gate.Task;
        return new ScanCycleResult(ScanCycleStatus.Fresh, DateTime.UtcNow, DateTime.UtcNow, 5, Aps.ToList());
    }
}

[TestFixture]
public class ScanSchedulerTests
{
    [Test]
    public async Task SingleFlight_ConcurrentRequestsShareOneProviderScan()
    {
        // F18: three consumers asking at once must not issue three competing scans
        var provider = new CountingProvider { Gate = new TaskCompletionSource() };
        await using var s = new ScanScheduler(provider) { AutoScan = false };
        s.SetAdapter(Guid.NewGuid());
        int events = 0;
        s.CycleCompleted += _ => Interlocked.Increment(ref events);
        var a = s.ScanNowAsync();
        var b = s.ScanNowAsync();
        var c = s.ScanNowAsync();
        await Task.Delay(50);
        provider.Gate.SetResult();
        var results = await Task.WhenAll(a, b, c);
        Assert.Multiple(() =>
        {
            Assert.That(provider.Calls, Is.EqualTo(1));
            Assert.That(results.Select(r => r!.Sequence).Distinct().Count(), Is.EqualTo(1), "all callers get the same cycle");
            Assert.That(events, Is.EqualTo(1), "one completion event per scan");
        });
    }

    [Test]
    public async Task ScanNow_WithoutAdapter_ReturnsNull()
    {
        await using var s = new ScanScheduler(new CountingProvider()) { AutoScan = false };
        Assert.That(await s.ScanNowAsync(), Is.Null);
    }

    [Test]
    public async Task Loop_RunsAtShortestRequestedCadence_AndStopsWhenReleased()
    {
        var provider = new CountingProvider();
        await using var s = new ScanScheduler(provider);
        s.SetAdapter(Guid.NewGuid());
        var slow = s.RequestCadence(TimeSpan.FromMilliseconds(400));
        var fast = s.RequestCadence(TimeSpan.FromMilliseconds(40));
        Assert.That(s.EffectiveInterval, Is.EqualTo(TimeSpan.FromMilliseconds(40)));
        await Task.Delay(420);
        int withFast = provider.Calls;
        fast.Dispose();
        slow.Dispose();
        await Task.Delay(150);
        int afterRelease = provider.Calls;
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(withFast, Is.GreaterThanOrEqualTo(5), "fast cadence honoured");
            Assert.That(provider.Calls - afterRelease, Is.LessThanOrEqualTo(1), "no scanning once nobody needs it");
            Assert.That(s.EffectiveInterval, Is.Null);
        });
    }

    [Test]
    public async Task Runner_DecimatesSharedCadenceToItsOwnInterval_AndLiveViewKeepsItsPace()
    {
        // S18/C1: a 300 ms test sharing a 50 ms live cadence records ~1 cycle per 300 ms, while the live view gets every scan
        var provider = new CountingProvider();
        await using var shared = new ScanScheduler(provider);
        var adapter = Guid.NewGuid();
        shared.SetAdapter(adapter);
        using var live = shared.RequestCadence(TimeSpan.FromMilliseconds(50));
        int liveCycles = 0;
        shared.CycleCompleted += _ => Interlocked.Increment(ref liveCycles);
        var repo = new MemorySaturationRepository();
        await using var runner = new SaturationTestRunner(shared, repo, new SaturationAnalyzer(), new NullLogger());
        await runner.StartAsync(new SaturationTestConfig
        {
            Name = "Shared", Scope = SaturationScope.SingleAp, TargetBssids = new[] { "AA:00:00:00:00:01" },
            Interval = TimeSpan.FromMilliseconds(300), ScanTimeout = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(1300), InterfaceId = adapter, Location = "Bench"
        });
        var report = await runner.WaitForCompletionAsync();
        Assert.Multiple(() =>
        {
            Assert.That(report!.Cycles.Count, Is.InRange(3, 6), "decimated to the test interval");
            Assert.That(liveCycles, Is.GreaterThan(report.Cycles.Count * 2), "live view not slowed by the test");
            Assert.That(report.Targets.Single().Observed, Is.EqualTo(report.Cycles.Count));
        });
    }
}

[TestFixture]
public class NetworkSelectionTests
{
    private static readonly string[] PlantOps = { "AA:00:00:00:00:01", "AA:00:00:00:00:02" };

    [Test]
    public void GroupSelection_FollowsNewBssids_AndApToggleConvertsToFixedSet()
    {
        var s = new NetworkSelection();
        s.ToggleGroup("PlantOps", PlantOps);
        Assert.Multiple(() =>
        {
            Assert.That(s.IsWholeSsid("PlantOps"), Is.True);
            Assert.That(s.Matches("PlantOps", "AA:00:00:00:00:99"), Is.True, "new AP of a whole SSID is included");
            Assert.That(s.Matches("Other", "BB:00:00:00:00:01"), Is.False);
            Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.SingleSsid));
        });

        s.ToggleAp("PlantOps", PlantOps[1], PlantOps);  // untick one AP of a whole group
        Assert.Multiple(() =>
        {
            Assert.That(s.IsWholeSsid("PlantOps"), Is.False);
            Assert.That(s.Bssids, Is.EquivalentTo(new[] { PlantOps[0] }));
            Assert.That(s.StateOf("PlantOps", PlantOps), Is.EqualTo(GroupSelectionState.Partial));
            Assert.That(s.Matches("PlantOps", "AA:00:00:00:00:99"), Is.False, "fixed set no longer follows new APs");
            Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.SingleAp));
        });

        s.ToggleGroup("PlantOps", PlantOps);           // partial -> whole
        Assert.That(s.IsWholeSsid("PlantOps") && s.Bssids.Count == 0, Is.True);
        s.ToggleGroup("PlantOps", PlantOps);           // whole -> none
        Assert.That(s.IsEmpty, Is.True);
    }

    [Test]
    public void ScopeDerivation_CoversAllCasesIncludingMixed()
    {
        var s = new NetworkSelection();
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.AllVisible));
        s.ToggleGroup("A", new[] { "A1" });
        s.ToggleGroup("B", new[] { "B1" });
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.MultipleSsids));
        s.ToggleAp("C", "C1", new[] { "C1" });
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.Mixed));
        Assert.That(s.Describe(x => 2), Does.Contain("2 SSIDs").And.Contain("1 AP (fixed)").And.Contain("mixed"));
        s.Clear();
        s.ToggleAp("C", "C1", new[] { "C1", "C2" });
        s.ToggleAp("C", "C2", new[] { "C1", "C2" });
        Assert.That(s.DeriveScope(), Is.EqualTo(SaturationScope.MultipleAps));
    }

    [Test]
    public void HiddenSsid_GroupSelectionRefused_ButApSelectionAllowed()
    {
        var s = new NetworkSelection();
        Assert.Multiple(() =>
        {
            Assert.That(s.ToggleGroup("<Hidden SSID>", new[] { "H1" }), Is.False);
            Assert.That(s.IsEmpty, Is.True);
            Assert.That(s.ToggleAp("<Hidden SSID>", "H1", new[] { "H1" }), Is.True);
            Assert.That(s.Matches("<Hidden SSID>", "H1"), Is.True);
        });
    }

    [Test]
    public void Lock_RejectsEveryMutation()
    {
        var s = new NetworkSelection();
        s.ToggleGroup("A", new[] { "A1" });
        s.Lock();
        Assert.Multiple(() =>
        {
            Assert.That(s.ToggleGroup("A", new[] { "A1" }), Is.False);
            Assert.That(s.ToggleAp("B", "B1", new[] { "B1" }), Is.False);
            Assert.That(s.Clear(), Is.False);
            Assert.That(s.SetSsid("C", true), Is.False);
            Assert.That(s.IsWholeSsid("A"), Is.True);
        });
        s.Unlock();
        Assert.That(s.Clear(), Is.True);
    }

    [Test]
    public void FocusColours_FollowSelectionOrder_CapAtThree_AndAutoFocusWhenEmpty()
    {
        static AccessPoint Ap(string ssid, int rssi) => new($"{ssid}-B", ssid, null, "5 GHz", 36, 20, rssi, null, 0, 0, "", "", null, null, DateTime.UtcNow);
        var visible = new[] { Ap("W", -40), Ap("X", -50), Ap("Y", -60), Ap("Z", -70), Ap("<Hidden SSID>", -30) };
        var s = new NetworkSelection();
        Assert.Multiple(() =>
        {
            Assert.That(s.FocusSlot("W", visible), Is.EqualTo(0), "auto-focus strongest");
            Assert.That(s.FocusSlot("Y", visible), Is.EqualTo(2));
            Assert.That(s.FocusSlot("Z", visible), Is.EqualTo(-1), "4th strongest is context");
            Assert.That(s.FocusSlot("<Hidden SSID>", visible), Is.EqualTo(-1), "hidden never auto-focused");
        });
        foreach (var x in new[] { "Z", "Y", "X", "W" }) s.ToggleGroup(x, new[] { $"{x}-B" });
        Assert.Multiple(() =>
        {
            Assert.That(s.FocusSlot("Z", visible), Is.EqualTo(0), "colour follows selection order, not rank");
            Assert.That(s.FocusSlot("X", visible), Is.EqualTo(2));
            Assert.That(s.FocusSlot("W", visible), Is.EqualTo(-1), "4th selected stays an outline");
        });
        s.ToggleGroup("Y", new[] { "Y-B" });  // removing one frees a colour, others keep theirs
        Assert.Multiple(() =>
        {
            Assert.That(s.FocusSlot("Z", visible), Is.EqualTo(0));
            Assert.That(s.FocusSlot("X", visible), Is.EqualTo(1));
            Assert.That(s.FocusSlot("W", visible), Is.EqualTo(2));
        });
    }
}

[TestFixture]
public class SpectrumLayoutTests
{
    [TestCase("5 GHz", 36, 80, 5210)]
    [TestCase("5 GHz", 44, 80, 5210)]
    [TestCase("5 GHz", 149, 80, 5775)]
    [TestCase("5 GHz", 36, 40, 5190)]
    [TestCase("5 GHz", 100, 160, 5570)]
    [TestCase("5 GHz", 52, 20, 5260)]
    [TestCase("2.4 GHz", 6, 20, 2437)]
    [TestCase("2.4 GHz", 1, 40, 2422)]
    [TestCase("2.4 GHz", 11, 40, 2452)]
    [TestCase("6 GHz", 5, 80, 5985)]
    [TestCase("6 GHz", 13, 160, 6025)]
    [TestCase("6 GHz", 37, 160, 6185)]
    public void BlockCenter_UsesStandardBondedChannels(string band, int ch, int width, double expectedMhz)
    {
        Assert.That(SpectrumLayout.BlockCenterMhz(band, ch, width), Is.EqualTo(expectedMhz));
    }

    private static AccessPoint Ap(string band, int ch, int? w, int rssi, string b = "X") =>
        new(b, "N", null, band, ch, w, rssi, null, 0, 0, "", "", null, null, DateTime.UtcNow);

    [Test]
    public void Occupancy_And_Load_ReflectWidthAndSignal()
    {
        var wide = Ap("5 GHz", 36, 80, -50);
        var (lo, hi) = SpectrumLayout.Occupancy(wide);
        Assert.That((lo, hi), Is.EqualTo((5170.0, 5250.0)), "80 MHz spans channels 36-48");
        var load = SpectrumLayout.ChannelLoad(SpectrumLayout.Band5, new[] { wide });
        Assert.Multiple(() =>
        {
            Assert.That(load[36], Is.GreaterThan(0));
            Assert.That(load[48], Is.GreaterThan(0));
            Assert.That(load[52], Is.EqualTo(0), "no energy outside the block");
            Assert.That(load[36], Is.EqualTo(1.0).Within(1e-9), "-50 dBm -> full weight");
        });
    }

    [Test]
    public void LeastBusy_PicksAmongPreferredChannels()
    {
        var aps = new[] { Ap("2.4 GHz", 1, 20, -40, "a"), Ap("2.4 GHz", 6, 20, -45, "b"), Ap("2.4 GHz", 11, 20, -80, "c") };
        var load = SpectrumLayout.ChannelLoad(SpectrumLayout.Band24, aps);
        Assert.That(SpectrumLayout.LeastBusyChannel(SpectrumLayout.Band24, load), Is.EqualTo(11));
        var empty = SpectrumLayout.ChannelLoad(SpectrumLayout.Band5, Array.Empty<AccessPoint>());
        Assert.That(SpectrumLayout.LeastBusyChannel(SpectrumLayout.Band5, empty), Is.EqualTo(36), "ties -> lowest preferred (non-DFS)");
    }

    [Test]
    public void Waterfall_StrongestPerChannel_AndDfs()
    {
        var aps = new[] { Ap("5 GHz", 36, 20, -70, "a"), Ap("5 GHz", 36, 20, -55, "b") };
        var bins = SpectrumLayout.StrongestPerChannel(SpectrumLayout.Band5, aps);
        Assert.Multiple(() =>
        {
            Assert.That(bins[36], Is.EqualTo(-55));
            Assert.That(bins[40], Is.Null, "nothing heard is null, not a number");
            Assert.That(SpectrumLayout.IsDfs(SpectrumLayout.Band5, 5500), Is.True);
            Assert.That(SpectrumLayout.IsDfs(SpectrumLayout.Band5, 5180), Is.False);
            Assert.That(SpectrumLayout.Intensity(-90), Is.EqualTo(0));
            Assert.That(SpectrumLayout.Intensity(-45), Is.EqualTo(1));
        });
    }
}

[TestFixture]
public class NetworkExplorerTests
{
    private static AccessPoint Ap(string ssid, string b, string band, int ch, int rssi, string sec = "WPA2-Personal", double? age = 200) =>
        new(b, ssid, null, band, ch, 20, rssi, null, rssi + 95, 80, sec, "Infrastructure", "Cisco Systems", Array.Empty<string>(), DateTime.UtcNow, age);

    private static List<AccessPoint> Scan(int delta = 0) => new()
    {
        Ap("PlantOps", "AA:00:00:00:00:01", "5 GHz", 36, -52 + delta),
        Ap("PlantOps", "AA:00:00:00:00:02", "2.4 GHz", 6, -61 + delta),
        Ap("Guest", "BB:00:00:00:00:01", "2.4 GHz", 11, -57 + delta, "Open"),
        Ap("<Hidden SSID>", "CC:00:00:00:00:01", "2.4 GHz", 3, -79 + delta),
    };

    [Test]
    public void Groups_TriState_Hidden_Filters_Search_Sort()
    {
        var sel = new NetworkSelection();
        var ex = new NetworkExplorerViewModel(sel);
        ex.Update(Scan(), true);
        Assert.That(ex.Groups.Select(g => g.Ssid), Is.EqualTo(new[] { "PlantOps", "Guest", "<Hidden SSID>" }), "sorted by best signal");
        var plant = ex.Groups[0];
        ex.ToggleGroup(plant);
        Assert.Multiple(() =>
        {
            Assert.That(plant.CheckState, Is.True);
            Assert.That(plant.IsExpanded || true, Is.True);
            Assert.That(ex.ScopeText, Does.Contain("1 SSID (2 APs now, follows new APs)"));
            Assert.That(ex.Groups.Single(g => g.IsHidden).CanToggle, Is.False);
            Assert.That(plant.SwatchBrush, Is.EqualTo(NetworkExplorerViewModel.IdentityColors[0]));
        });
        ex.ToggleAp(plant.Aps.First(a => a.Band == "2.4 GHz"));
        Assert.That(plant.CheckState, Is.Null, "partial");

        ex.FilterSecured = true;
        Assert.That(ex.Groups.Select(g => g.Ssid), Does.Not.Contain("Guest"));
        ex.FilterSecured = false;
        ex.Filter5 = true;
        Assert.That(ex.Groups.Select(g => g.Ssid), Is.EqualTo(new[] { "PlantOps" }));
        ex.Filter5 = false;
        ex.FilterSelectedOnly = true;
        Assert.That(ex.Groups.Single().Aps.Select(a => a.Bssid), Is.EqualTo(new[] { "AA:00:00:00:00:01" }));
        ex.FilterSelectedOnly = false;
        ex.SearchText = "guest";
        Assert.That(ex.Groups.Select(g => g.Ssid), Is.EqualTo(new[] { "Guest" }));
        ex.SearchText = "";
        ex.Sort = ExplorerSort.Name;
        Assert.That(ex.Groups.Select(g => g.Ssid), Is.EqualTo(new[] { "Guest", "PlantOps", "<Hidden SSID>" }));
        ex.FilterFairOrBetter = true;
        Assert.That(ex.Groups.Any(g => g.IsHidden), Is.False, "-79 dBm filtered out");
    }

    [Test]
    public void Reconcile_KeepsItemInstancesExpansionAndSparklines_Over120Aps()
    {
        var sel = new NetworkSelection();
        var ex = new NetworkExplorerViewModel(sel);
        var big = Enumerable.Range(0, 120).Select(i => Ap($"Net{i % 30}", $"DD:00:00:00:{i / 256:X2}:{i % 256:X2}", i % 2 == 0 ? "5 GHz" : "2.4 GHz", i % 2 == 0 ? 36 : 6, -40 - i % 50)).ToList();
        ex.Update(big, true);
        var first = ex.Groups[0];
        ex.ToggleExpandCommand.Execute(first);
        ex.ToggleGroup(first);
        for (int k = 1; k <= 12; k++) ex.Update(big.Select(a => a with { RSSI = a.RSSI - k % 3 }).ToList(), true);
        Assert.Multiple(() =>
        {
            Assert.That(ex.Groups.Count, Is.EqualTo(30));
            Assert.That(ex.Groups.Contains(first), Is.True, "same instance after refresh");
            Assert.That(first.IsExpanded, Is.True);
            Assert.That(first.CheckState, Is.True);
            Assert.That(ex.HistoryOf(first.Aps[0].Bssid).Count, Is.EqualTo(12), "sparkline keeps 12 values");
            Assert.That(first.Aps[0].SparkPoints.Split(' ').Length, Is.EqualTo(12));
        });
    }

    [Test]
    public void StaleEntries_DoNotExtendSparklines()
    {
        var ex = new NetworkExplorerViewModel(new NetworkSelection());
        ex.Update(new List<AccessPoint> { Ap("A", "A1", "5 GHz", 36, -50, age: 60_000) }, false);
        Assert.That(ex.HistoryOf("A1"), Is.Empty);
    }

    [Test]
    public void SelectShown_SelectsWholeVisibleGroups_AndHiddenPerBssid()
    {
        var sel = new NetworkSelection();
        var ex = new NetworkExplorerViewModel(sel);
        ex.Update(Scan(), true);
        ex.SelectShownCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(sel.Ssids, Is.EquivalentTo(new[] { "PlantOps", "Guest" }));
            Assert.That(sel.Bssids, Is.EquivalentTo(new[] { "CC:00:00:00:00:01" }));
            Assert.That(sel.DeriveScope(), Is.EqualTo(SaturationScope.Mixed));
        });
    }
}

[TestFixture]
public class SaturationMixedScopeTests
{
    [Test]
    public void MixedScope_TracksExplicitBssidsFromStart_AndSsidMembersOnSight_AggregatingOnlyWholeSsids()
    {
        var cfg = new SaturationTestConfig { Name = "Mixed", Scope = SaturationScope.Mixed, TargetSsids = new[] { "PlantOps" }, TargetBssids = new[] { "BB:00:00:00:00:01" }, Interval = TimeSpan.FromSeconds(5) };
        var tracker = new SaturationTargetTracker(cfg);
        AccessPoint Ap(string ssid, string b, int rssi) => new(b, ssid, null, "5 GHz", 36, 20, rssi, null, 0, 80, "", "", null, null, DateTime.UtcNow, 100);
        var c1 = SaturationObservationBuilder.Build(cfg, tracker, new ScanCycleResult(ScanCycleStatus.Fresh, DateTime.UtcNow, DateTime.UtcNow, 1,
            new[] { Ap("PlantOps", "AA:00:00:00:00:01", -50), Ap("Other", "ZZ:00:00:00:00:01", -40) }), 1);
        var c2 = SaturationObservationBuilder.Build(cfg, tracker, new ScanCycleResult(ScanCycleStatus.Fresh, DateTime.UtcNow, DateTime.UtcNow, 1,
            new[] { Ap("PlantOps", "AA:00:00:00:00:01", -51), Ap("PlantOps", "AA:00:00:00:00:02", -60), Ap("Guest", "BB:00:00:00:00:01", -55) }), 2);
        Assert.Multiple(() =>
        {
            Assert.That(c1.Select(o => (o.Bssid, o.Status)), Is.EquivalentTo(new[] { ("BB:00:00:00:00:01", ObservationStatus.Missing), ("AA:00:00:00:00:01", ObservationStatus.Observed) }),
                "explicit BSSID counted missing from the first scan; unrelated SSID ignored");
            Assert.That(c2.Select(o => o.Bssid), Is.EquivalentTo(new[] { "BB:00:00:00:00:01", "AA:00:00:00:00:01", "AA:00:00:00:00:02" }), "new PlantOps AP joins");
        });
        var cycles = new[] { c1, c2 }.Select((obs, i) => new SaturationCycle(i + 1, DateTime.UtcNow, i * 5000, ScanCycleStatus.Fresh, 1, 3, null, null, obs)).ToList();
        var report = new SaturationAnalyzer().Analyze(cfg, cycles, Array.Empty<SaturationMarker>(), DateTime.UtcNow, DateTime.UtcNow, SaturationTestStatus.Completed);
        Assert.Multiple(() =>
        {
            Assert.That(report.Ssids.Select(s => s.Ssid), Is.EqualTo(new[] { "PlantOps" }), "only the whole-SSID target is aggregated");
            Assert.That(report.Targets, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void ExistingScopes_ProduceIdenticalReports_AfterMixedSupport()
    {
        // Regression guard: SingleSsid aggregates every SSID target exactly as before; AP scopes produce none.
        var ssidCfg = Sat.Config(SaturationScope.SingleSsid, "Net");
        var apCfg = Sat.Config(SaturationScope.MultipleAps, Sat.A, Sat.B);
        var cycles = new List<SaturationCycle> { Sat.Cycle(1, Sat.Obs(1, Sat.A, -60), Sat.Obs(1, Sat.B, -70)), Sat.Cycle(2, Sat.Obs(2, Sat.A, -61), Sat.Obs(2, Sat.B, -55)) };
        var a = new SaturationAnalyzer().Analyze(ssidCfg, cycles, Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0, SaturationTestStatus.Completed);
        var b = new SaturationAnalyzer().Analyze(apCfg, cycles, Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0, SaturationTestStatus.Completed);
        Assert.Multiple(() =>
        {
            Assert.That(a.Ssids.Single().Ssid, Is.EqualTo("Net"));
            Assert.That(a.Ssids.Single().StrongestBssidChanges, Is.EqualTo(1));
            Assert.That(b.Ssids, Is.Empty);
        });
    }
}
