using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// Manual visual review aid: renders a realistic synthetic saturation report to PNG pages.
///   set WS_PREVIEW_OUT=&lt;folder&gt; &amp;&amp; dotnet test WirelessScanner.Tests --filter "Category=ReportPreview"
/// </summary>
[TestFixture, Explicit("Visual review only"), Category("ReportPreview")]
public class ReportPreviewTests
{
    internal static SaturationReport Realistic()
    {
        var rnd = new Random(42);
        const string a = "8C:5A:25:04:91:B0", b = "8C:5A:25:04:91:B1";
        var cycles = new List<SaturationCycle>();
        var t0 = new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 360; i++)
        {
            long ms = i * 5000L;
            int ra = -58 + rnd.Next(-2, 3) + (i is >= 100 and < 112 ? -14 : 0);
            bool aMissing = i is >= 200 and < 206;
            int chA = i < 250 ? 36 : 149;
            int rb = -71 + rnd.Next(-3, 4) - i / 60;
            var obs = new List<SaturationObservation>
            {
                aMissing ? new(i + 1, a, "PlantOps", ObservationStatus.Missing, null, null, null, null, null, null, null, null)
                         : new(i + 1, a, "PlantOps", ObservationStatus.Observed, ra, 70, chA, "5 GHz", 80, 400, rnd.Next(0, 3), null),
                new(i + 1, b, "PlantOps", ObservationStatus.Observed, rb, 40, 6, "2.4 GHz", 20, 600, rnd.Next(1, 5), rnd.Next(2, 7))
            };
            var status = i % 37 == 0 ? ScanCycleStatus.Stale : ScanCycleStatus.Fresh;
            cycles.Add(new SaturationCycle(i + 1, t0.AddMilliseconds(ms), ms, status, 1800 + rnd.Next(0, 900), 24, status == ScanCycleStatus.Stale ? "scan_timeout" : null, null, obs));
        }
        var cfg = new SaturationTestConfig
        {
            Name = "Stock room soak", Scope = SaturationScope.SingleSsid, TargetSsids = new[] { "PlantOps" }, Duration = TimeSpan.FromMinutes(30),
            Interval = TimeSpan.FromSeconds(5), Location = "Stock room B", Facility = "Plant 2", Operator = "Field Engineer", Client = "Example Client",
            Notes = "Laptop on shelf 3, lid open.", InterfaceName = "Intel(R) Dual Band Wireless-AC 7265",
            Geo = new GeoTag { Latitude = 41.878113, Longitude = -87.629799, AccuracyMeters = 15, Source = "manual", CapturedUtc = t0, Building = "Plant 2", Floor = "1", Zone = "Stock room B" }
        };
        var markers = new[] { new SaturationMarker(t0.AddSeconds(495), 495_000, "forklift charging"), new SaturationMarker(t0.AddSeconds(1240), 1_240_000, "door closed") };
        return new SaturationAnalyzer().Analyze(cfg, cycles, markers, t0, t0.AddMinutes(30), SaturationTestStatus.Completed);
    }

    [Test]
    public void RenderPreviewPages()
    {
        string outDir = Environment.GetEnvironmentVariable("WS_PREVIEW_OUT") ?? Path.Combine(Path.GetTempPath(), "ws_preview");
        Directory.CreateDirectory(outDir);
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community; // same as SaturationExportService
        var doc = new SaturationReportDocument(Realistic());
        var images = doc.GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { RasterDpi = 80 }).ToList();
        for (int i = 0; i < Math.Min(images.Count, 6); i++) File.WriteAllBytes(Path.Combine(outDir, $"page{i + 1}.png"), images[i]);
        TestContext.Out.WriteLine($"PREVIEW_PAGES {images.Count} -> {outDir}");
        Assert.That(images, Is.Not.Empty);
    }
}
