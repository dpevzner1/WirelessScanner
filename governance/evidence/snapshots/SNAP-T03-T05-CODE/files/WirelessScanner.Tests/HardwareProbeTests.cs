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
}
