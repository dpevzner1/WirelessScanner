using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// Connection test on the real Wi-Fi connection (manual, explicit; DR-0014). Never changes the connection.
///   set WS_CONN_SECONDS=45     (default 45)
///   set WS_CONN_OUT=&lt;folder&gt;   (raw exports contain SSID/BSSID/IP addresses: keep local)
///   dotnet test WirelessScanner.Tests --filter "Category=HardwareConnection"
/// Prints a REDACTED summary (no SSID/BSSID/addresses) with SHA-256 of the raw exports for the evidence trail.
/// </summary>
[TestFixture, Explicit("Sends probes from the real Wi-Fi connection"), Category("HardwareConnection")]
public class ConnectionHardwareRun
{
    [Test]
    public async Task RealConnection_ProbesEverySegment_AndReportsWithoutChangingTheConnection()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("WS_CONN_SECONDS"), out var s) ? s : 45;
        string outDir = Environment.GetEnvironmentVariable("WS_CONN_OUT") ?? Path.Combine(Path.GetTempPath(), "ws_conn_hw");
        Directory.CreateDirectory(outDir);

        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).First(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        var prober = new WindowsConnectionProber(adapter, new NullLogger());
        var before = await adapter.ReadLinkAsync(wifi.Id);
        var ctx = await prober.GetContextAsync(wifi.Id);
        Assert.That(ctx.WifiConnected, Is.True, ctx.Problem);

        var config = new ConnectionTestConfig
        {
            Name = "DR-0014 hardware check", Duration = TimeSpan.FromSeconds(seconds), InterfaceId = wifi.Id, InterfaceName = wifi.Description,
            Location = "Owner workstation (redacted)", Operator = "Claude Code (automated hardware run)", ContextAtStart = ctx
        };
        var repo = new MemoryConnectionRepository();
        var log = new CapturingLogger();
        await using var runner = new ConnectionTestRunner(prober, repo, new ConnectionAnalyzer(), log);
        await runner.StartAsync(config);
        var report = (await runner.WaitForCompletionAsync())!;
        var after = await adapter.ReadLinkAsync(wifi.Id);

        var export = new ConnectionExportService();
        string prefix = $"conn_hw_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var files = (await export.ExportCsvBundleAsync(outDir, prefix, report)).ToList();
        files.Add(Path.Combine(outDir, prefix + ".json"));
        await export.ExportJsonAsync(files[^1], report);
        files.Add(Path.Combine(outDir, prefix + ".pdf"));
        await export.ExportPdfAsync(files[^1], report);

        foreach (var line in log.Lines.Where(l => l.Contains("discovery", StringComparison.OrdinalIgnoreCase))) TestContext.Out.WriteLine($"HW-CONN: log: {line}");
        TestContext.Out.WriteLine($"HW-CONN: {report.Totals.ProbesSent} probes = {report.Totals.ProbesOk} ok + {report.Totals.ProbesFailed} failed; {report.Totals.LinkSamples} link samples; active {report.ActiveSeconds:0.0} s");
        TestContext.Out.WriteLine($"HW-CONN: ISP hop found: {report.Config.ContextAtStart?.IspHop != null} at TTL {report.Config.ContextAtStart?.IspHopTtl} (echo: {report.Config.ContextAtStart?.IspHopAnswersEcho}); path hops {report.Config.ContextAtStart?.PathHops.Count}; other active interfaces: {report.Config.ContextAtStart?.OtherActiveInterfaces.Count}");
        foreach (var seg in report.Segments)
            TestContext.Out.WriteLine($"HW-CONN: {seg.Segment,-9} {seg.Method,-7} sent {seg.Sent,4} loss {seg.LossPct,6:0.##} % p50 {seg.RttP50,7:0.0} p95 {seg.RttP95,7:0.0} jitter {seg.JitterMs,6:0.0} ms outages {seg.Outages}");
        TestContext.Out.WriteLine($"HW-CONN: link counters reported by driver: {report.Link.CountersAvailable}; retry {report.Link.RetryPct?.ToString("0.##") ?? "n/a"} %; roams {report.Link.Roams}; RSSI mean {report.Link.RssiMean:0.0} dBm");
        TestContext.Out.WriteLine($"HW-CONN: verdicts: {string.Join("; ", report.Verdicts.Select(v => v.Use + " " + v.Rating))}; voice MOS {report.VoiceMos:0.00} (estimate)");
        TestContext.Out.WriteLine($"HW-CONN: events: {string.Join(", ", report.Events.GroupBy(e => e.Type).Select(g => $"{g.Key} {g.Count()}"))}");
        TestContext.Out.WriteLine($"HW-CONN: association unchanged by the test: {string.Equals(before?.Bssid, after?.Bssid, StringComparison.OrdinalIgnoreCase) && before?.Connected == after?.Connected}");
        foreach (var f in files)
            TestContext.Out.WriteLine($"HW-CONN: export {Path.GetFileName(f)} sha256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}");

        Assert.Multiple(() =>
        {
            Assert.That(report.Totals.ProbesSent, Is.GreaterThan(seconds));
            Assert.That(report.Segments.Any(x => x.Segment == ConnectionSegment.Gateway && x.Ok > 0), Is.True, "router answered");
            Assert.That(report.Segments.Any(x => x.Segment == ConnectionSegment.Internet && x.Ok > 0), Is.True, "internet answered");
            Assert.That(report.Totals.ProbesOk + report.Totals.ProbesFailed, Is.EqualTo(report.Totals.ProbesSent));
            Assert.That(after?.Connected, Is.True, "still connected");
        });
    }
}

internal sealed class CapturingLogger : IAppLogger
{
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
    public void Info(string message) => Lines.Enqueue("INFO " + message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
    public void Error(string message, Exception? ex = null) => Lines.Enqueue("ERROR " + message + (ex != null ? " " + ex.Message : ""));
}
