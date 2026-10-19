using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// Real-adapter probes (manual, explicit). Run with:
///   dotnet test WirelessScanner.Tests --filter "Category=Hardware"
/// They perform ordinary WLAN scans (the same operation Windows performs routinely) and print a JSON
/// summary so the evidence can be attested. They never write to the application database.
/// </summary>
[TestFixture, Explicit("Requires a real Wi-Fi adapter"), Category("Hardware")]
public class HardwareProbeTests
{
    [Test]
    public async Task ScanAsync_OnRealAdapter_ReportsCycleStatusesAndBeaconAges()
    {
        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).FirstOrDefault(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        Assert.That(wifi, Is.Not.Null, "No Wi-Fi interface found");

        var cycles = new System.Collections.Generic.List<object>();
        var rssiByBssid = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>();
        for (int i = 0; i < 4; i++)
        {
            var c = await adapter.ScanAsync(wifi!.Id, TimeSpan.FromSeconds(6));
            foreach (var ap in c.AccessPoints)
            {
                if (!rssiByBssid.TryGetValue(ap.BSSID, out var l)) rssiByBssid[ap.BSSID] = l = new();
                l.Add(ap.RSSI);
            }
            var ages = c.AccessPoints.Where(a => a.BeaconAgeMs.HasValue).Select(a => a.BeaconAgeMs!.Value).OrderBy(a => a).ToList();
            cycles.Add(new
            {
                cycle = i + 1,
                status = c.Status.ToString(),
                durationMs = Math.Round(c.DurationMs),
                aps = c.AccessPoints.Count,
                beaconAgeKnown = ages.Count,
                beaconAgeMedianMs = ages.Count > 0 ? Math.Round(ages[ages.Count / 2]) : (double?)null,
                beaconAgeMaxMs = ages.Count > 0 ? Math.Round(ages[^1]) : (double?)null,
                c.ErrorCode,
                c.ErrorMessage,
                bands = c.AccessPoints.GroupBy(a => a.Band).ToDictionary(g => g.Key, g => g.Count()),
                widthsKnown = c.AccessPoints.Count(a => a.ChannelWidth.HasValue),
                security = c.AccessPoints.GroupBy(a => a.SecurityType).ToDictionary(g => g.Key, g => g.Count())
            });
            await Task.Delay(3000);
        }

        int bssidsWithChange = rssiByBssid.Values.Count(v => v.Distinct().Count() > 1);
        var summary = new
        {
            adapter = wifi!.Description,
            cycles,
            bssidsSeen = rssiByBssid.Count,
            bssidsWithRssiChangeAcrossCycles = bssidsWithChange
        };
        TestContext.Out.WriteLine("HARDWARE_PROBE_JSON " + JsonSerializer.Serialize(summary));

        Assert.That(cycles, Has.Count.EqualTo(4));
    }

    /// <summary>
    /// F18 on real hardware: concurrent scan requests on one adapter are coalesced into one WLAN scan, and a live view
    /// plus a saturation test sharing the scheduler run without failed cycles.
    /// </summary>
    [Test]
    public async Task ConcurrentConsumers_OnRealAdapter_ShareScans_WithoutFailures()
    {
        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).FirstOrDefault(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        Assert.That(wifi, Is.Not.Null, "No Wi-Fi interface found");

        // 1. adapter-level coalescing
        var burst = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => adapter.ScanAsync(wifi!.Id, TimeSpan.FromSeconds(6))));
        bool coalesced = ReferenceEquals(burst[0], burst[1]) && ReferenceEquals(burst[1], burst[2]);

        // 2. live cadence (5 s) + saturation runner (5 s) on one scheduler for ~35 s
        await using var scheduler = new WirelessScanner.Application.ScanScheduler(adapter);
        scheduler.SetAdapter(wifi!.Id);
        using var live = scheduler.RequestCadence(TimeSpan.FromSeconds(5));
        var statuses = new System.Collections.Concurrent.ConcurrentBag<string>();
        scheduler.CycleCompleted += c => statuses.Add(c.Result.Status.ToString());
        var repo = new MemorySaturationRepository();
        await using var runner = new WirelessScanner.Application.SaturationTestRunner(scheduler, repo, new WirelessScanner.Application.SaturationAnalyzer(), new NullLogger());
        await runner.StartAsync(new SaturationTestConfig
        {
            Name = "F18 probe", Scope = SaturationScope.AllVisible, Interval = TimeSpan.FromSeconds(5), ScanTimeout = TimeSpan.FromSeconds(6),
            Duration = TimeSpan.FromSeconds(35), InterfaceId = wifi.Id, Location = "Bench"
        });
        var report = await runner.WaitForCompletionAsync();

        var summary = new
        {
            adapter = wifi.Description,
            burstStatuses = burst.Select(b => b.Status.ToString()).ToArray(),
            burstCoalesced = coalesced,
            schedulerCycles = statuses.Count,
            schedulerFailed = statuses.Count(s => s == "Failed"),
            schedulerStatusCounts = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()),
            testCycles = report!.Cycles.Count,
            testFresh = report.Health.Fresh,
            testStale = report.Health.Stale,
            testFailed = report.Health.Failed
        };
        TestContext.Out.WriteLine("HARDWARE_PROBE_JSON " + JsonSerializer.Serialize(summary));

        Assert.Multiple(() =>
        {
            Assert.That(coalesced, Is.True, "concurrent requests must share one WLAN scan");
            Assert.That(statuses.Count(s => s == "Failed"), Is.EqualTo(0));
            Assert.That(report.Health.Failed, Is.EqualTo(0));
            Assert.That(report.Cycles.Count, Is.InRange(5, 9));
        });
    }
}
