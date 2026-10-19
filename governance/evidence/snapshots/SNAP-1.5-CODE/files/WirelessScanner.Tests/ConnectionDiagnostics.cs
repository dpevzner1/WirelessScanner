using System;
using System.Linq;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>Explicit diagnostics for the real adapter (prints outcomes, never addresses or names).</summary>
[TestFixture, Explicit("Real adapter diagnostics"), Category("HardwareConnectionDiag")]
public class ConnectionDiagnostics
{
    [Test]
    public async Task PathDiscoveryAndRssi()
    {
        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).First(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        var prober = new WindowsConnectionProber(adapter, new NullLogger());
        var ctx = await prober.GetContextAsync(wifi.Id);
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            var first = await PathDiscovery.DiscoverAsync(prober, ctx, "1.1.1.1");
            string k0 = string.Join(",", first.PathHops.Select(h => h == "*" ? "*" : h == "1.1.1.1" ? "T" : PathDiscovery.IsLocalAddress(h) ? "L" : "P"));
            TestContext.Out.WriteLine($"DIAG cold discover: hops={k0} isp={(first.IspHop != null)} in {sw0.ElapsedMilliseconds} ms");
        }
        for (int ttl = 1; ttl <= 10; ttl++)
        {
            var r = await prober.IcmpAsync("1.1.1.1", ctx.LocalIPv4!, ttl, 1000);
            string kind = r.From == null ? "none" : r.From == "1.1.1.1" ? "target" : PathDiscovery.IsLocalAddress(r.From) ? "local" : ctx.Gateways.Contains(r.From) ? "gateway" : "public";
            TestContext.Out.WriteLine($"DIAG ttl {ttl}: {r.Outcome} from={kind} rtt={r.RttMs:0.0} detail={r.Detail}");
            if (r.Outcome == ProbeOutcome.Ok) break;
        }
        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = await PathDiscovery.DiscoverAsync(prober, ctx, "1.1.1.1");
            string kinds = string.Join(",", d.PathHops.Select(h => h == "*" ? "*" : h == "1.1.1.1" ? "T" : PathDiscovery.IsLocalAddress(h) ? "L" : ctx.Gateways.Contains(h) ? "G" : "P"));
            TestContext.Out.WriteLine($"DIAG discover {i}: hops={kinds} isp={(d.IspHop != null)} ttl={d.IspHopTtl} echo={d.IspHopAnswersEcho} in {sw.ElapsedMilliseconds} ms");
        }
        var link = await adapter.ReadLinkAsync(wifi.Id);
        TestContext.Out.WriteLine($"DIAG link: connected={link?.Connected} rssi={link?.Rssi} channel={link?.Channel} quality={link?.SignalQualityPct} counters={(link?.Counters != null)}");
    }
}
