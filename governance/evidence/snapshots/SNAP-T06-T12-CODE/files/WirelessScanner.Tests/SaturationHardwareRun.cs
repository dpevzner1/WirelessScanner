using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// SAT-AC-10 hardware validation (manual, explicit): a real saturation test on the machine's Wi-Fi adapter.
///   set WS_SAT_MINUTES=15            (default 15)
///   set WS_SAT_SSID=&lt;network name&gt;   (default: strongest SSID in the first scan)
///   set WS_SAT_OUT=&lt;folder&gt;           (raw exports: PDF/CSV/JSON — contain SSIDs/BSSIDs, keep local)
///   dotnet test WirelessScanner.Tests --filter "Category=HardwareSaturation"
/// Prints a REDACTED summary (no SSIDs/BSSIDs) with SHA-256 of the raw exports for the evidence trail.
/// </summary>
[TestFixture, Explicit("Long-running real-adapter test"), Category("HardwareSaturation")]
public class SaturationHardwareRun
{
    [Test]
    public async Task RealAdapter_SaturationTest_ProducesCompleteReport()
    {
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("WS_SAT_MINUTES"), out var m) ? m : 15;
        string outDir = Environment.GetEnvironmentVariable("WS_SAT_OUT") ?? Path.Combine(Path.GetTempPath(), "ws_sat_hw");
        Directory.CreateDirectory(outDir);

        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).First(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        string? ssid = Environment.GetEnvironmentVariable("WS_SAT_SSID");
        if (string.IsNullOrWhiteSpace(ssid))
        {
            var first = await adapter.ScanAsync(wifi.Id, TimeSpan.FromSeconds(6));
            ssid = first.AccessPoints.Where(a => a.SSID != "<Hidden SSID>").OrderByDescending(a => a.RSSI).First().SSID;
        }

        var config = new SaturationTestConfig
        {
            Name = "SAT-AC-10 hardware validation",
            Scope = SaturationScope.SingleSsid,
            TargetSsids = new[] { ssid },
            Duration = TimeSpan.FromMinutes(minutes),
            Interval = TimeSpan.FromSeconds(5),
            Location = "Owner workstation (redacted)",
            Operator = "Claude Code (automated hardware run)",
            InterfaceId = wifi.Id,
            InterfaceName = wifi.Description
        };

        var repo = new MemorySaturationRepository();
        await using var runner = new SaturationTestRunner(adapter, repo, new SaturationAnalyzer(), new NullLogger());
        await runner.StartAsync(config);
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, minutes * 30)));
        runner.AddMarker("automated mid-test marker");
        var report = await runner.WaitForCompletionAsync();
        Assert.That(report, Is.Not.Null);

        var export = new SaturationExportService();
        var pdf = Path.Combine(outDir, "sat_hw.pdf");
        var json = Path.Combine(outDir, "sat_hw.json");
        await export.ExportPdfAsync(pdf, report!);
        await export.ExportJsonAsync(json, report!);
        var csvs = await export.ExportCsvBundleAsync(outDir, "sat_hw", report!);

        string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        var h = report!.Health;
        var redacted = new
        {
            analyticsVersion = report.AnalyticsVersion,
            adapter = wifi.Description,
            plannedMinutes = minutes,
            activeSeconds = Math.Round(report.ActiveSeconds),
            status = report.Status.ToString(),
            cycles = new { h.Total, h.Fresh, h.Stale, h.Failed, meanScanMs = Math.Round(h.MeanScanMs ?? 0), maxScanMs = Math.Round(h.MaxScanMs ?? 0), maxGapSeconds = Math.Round(h.MaxGapSeconds, 1), errors = h.ErrorCounts },
            targetsTracked = report.Targets.Count,
            targets = report.Targets.Select((t, i) => new
            {
                target = $"BSSID-{i + 1}",
                band = t.Bands,
                t.Observed, t.Missing, t.Stale, t.Unknown,
                availabilityPct = t.AvailabilityPct is double a ? Math.Round(a, 1) : (double?)null,
                mean = t.Mean is double mn ? Math.Round(mn, 1) : (double?)null,
                sd = t.StdDev is double sd ? Math.Round(sd, 2) : (double?)null,
                t.Min, t.Max,
                variationMeanDb = t.VariationMean is double v ? Math.Round(v, 2) : (double?)null,
                t.DropCount, t.PeakCount, t.DropoutCount, t.ChannelChangeCount,
                score = t.StabilityScore, t.Grade
            }),
            eventsByType = report.Events.GroupBy(e => e.Type.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            markers = report.Markers.Count,
            rawExports = new[] { pdf, json }.Concat(csvs).Select(p => new { file = Path.GetFileName(p), bytes = new FileInfo(p).Length, sha256 = Sha(p) })
        };
        TestContext.Out.WriteLine("SATURATION_HW_REDACTED " + JsonSerializer.Serialize(redacted));

        Assert.Multiple(() =>
        {
            Assert.That(report.Status, Is.EqualTo(SaturationTestStatus.Completed));
            Assert.That(h.Total, Is.GreaterThanOrEqualTo(minutes * 60 / 5 * 0.8), "cycle cadence roughly honoured");
            Assert.That(report.Targets, Is.Not.Empty);
            Assert.That(File.Exists(pdf) && new FileInfo(pdf).Length > 10_000, Is.True);
        });
    }
}
