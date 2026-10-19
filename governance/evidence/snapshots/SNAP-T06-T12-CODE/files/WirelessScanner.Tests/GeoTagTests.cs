using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>Owner requirement 2026-10-15: geolocation captured with each saturation test and present in every report.</summary>
[TestFixture]
public class GeoTagTests
{
    private sealed class FakeGeo : IGeoLocationProvider
    {
        public GeoTag? Tag;
        public string? Error;
        public Task<(GeoTag? Tag, string? Error)> GetCurrentAsync(TimeSpan timeout) => Task.FromResult((Tag, Error));
    }

    private static SaturationTestViewModel Vm(IGeoLocationProvider geo)
    {
        var provider = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider();
        var repo = new MemorySaturationRepository();
        return new SaturationTestViewModel(provider, new SaturationTestRunner(provider, repo, new SaturationAnalyzer(), new NullLogger()),
            repo, new SaturationAnalyzer(), new SaturationExportService(), geo)
        { Ui = a => a(), Confirm = (_, _) => true, KeepAwake = _ => { }, IsOnBattery = () => false };
    }

    [TestCase(91.0, 0.0, "Latitude")]
    [TestCase(0.0, 181.0, "Longitude")]
    [TestCase(10.0, null, "both")]
    public void Validate_RejectsOutOfRangeAndPartialCoordinates(double? lat, double? lon, string expected)
    {
        Assert.That(GeoTag.Validate(lat, lon), Does.Contain(expected));
    }

    [Test]
    public void GeoTag_FormatsCoordinatesAndMapLinkInvariantCulture()
    {
        var g = new GeoTag { Latitude = 41.878113, Longitude = -87.629799, AccuracyMeters = 12.4 };
        Assert.Multiple(() =>
        {
            Assert.That(g.CoordinatesText, Is.EqualTo("41.878113, -87.629799 (±12 m)"));
            Assert.That(g.MapUrl, Does.Contain("mlat=41.878113").And.Contain("mlon=-87.629799"));
            Assert.That(new GeoTag().CoordinatesText, Is.EqualTo("not recorded"));
        });
    }

    [Test]
    public async Task ViewModel_DetectFillsCoordinates_ManualEditValidated_AndConfigCarriesGeo()
    {
        var geo = new FakeGeo { Tag = new GeoTag { Latitude = 41.5, Longitude = -88.25, AccuracyMeters = 30, Source = "windows-location (WiFi)", CapturedUtc = DateTime.UtcNow } };
        var vm = Vm(geo);
        await vm.LoadAdaptersAsync();
        await vm.DiscoverAsync();
        vm.Candidates[0].IsSelected = true;
        vm.Location = "Warehouse bay 4";
        await vm.DetectLocationAsync();
        vm.Building = "B2";
        vm.Floor = "1";
        vm.Zone = "Bay 4";

        var cfg = vm.BuildConfig();
        Assert.Multiple(() =>
        {
            Assert.That(vm.CanStart, Is.True, vm.ValidationMessage);
            Assert.That(cfg.Geo.Latitude, Is.EqualTo(41.5));
            Assert.That(cfg.Geo.Longitude, Is.EqualTo(-88.25));
            Assert.That(cfg.Geo.Source, Does.StartWith("windows-location"));
            Assert.That(cfg.Geo.Floor, Is.EqualTo("1"));
        });

        vm.Latitude = "abc";
        Assert.That(vm.ValidationMessage, Does.Contain("decimal degrees"));
        vm.Latitude = "95";
        Assert.That(vm.ValidationMessage, Does.Contain("between -90 and 90"));
        vm.Latitude = "41,6"; // comma decimal accepted
        Assert.Multiple(() =>
        {
            Assert.That(vm.CanStart, Is.True);
            Assert.That(vm.BuildConfig().Geo.Source, Is.EqualTo("manual"), "manual edit marks source manual");
        });
    }

    [Test]
    public async Task ViewModel_DetectFailure_ShowsReason_AndKeepsManualEntryPossible()
    {
        var vm = Vm(new FakeGeo { Error = "Windows Location access is Denied." });
        await vm.DetectLocationAsync();
        Assert.Multiple(() =>
        {
            Assert.That(vm.GeoStatus, Does.Contain("Denied"));
            Assert.That(vm.Latitude, Is.Empty);
        });
    }

    [Test]
    public async Task Exports_CarryGeolocation_OnEveryRow_AndInPdfAndJson()
    {
        var cfg = Sat.Config() with { Geo = new GeoTag { Latitude = 41.878113, Longitude = -87.629799, AccuracyMeters = 8, Source = "manual", Building = "HQ", Floor = "3", Zone = "East" } };
        var report = new SaturationAnalyzer().Analyze(cfg, Sat.Series(-50, -51, null, -52), Array.Empty<SaturationMarker>(), Sat.T0, Sat.T0.AddSeconds(20), SaturationTestStatus.Completed);
        var dir = Path.Combine(Path.GetTempPath(), $"ws_geo_{Guid.NewGuid():N}");
        try
        {
            var export = new SaturationExportService();
            var files = await export.ExportCsvBundleAsync(dir, "geo", report);
            await export.ExportJsonAsync(Path.Combine(dir, "geo.json"), report);
            await export.ExportPdfAsync(Path.Combine(dir, "geo.pdf"), report);
            var sampleLines = File.ReadAllLines(files.Single(f => f.EndsWith("_samples.csv"))).Skip(2).ToList();
            var header = File.ReadAllLines(files.Single(f => f.EndsWith("_samples.csv")))[1];
            var summary = File.ReadAllText(files.Single(f => f.EndsWith("_summary.csv")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "geo.json")));
            Assert.Multiple(() =>
            {
                Assert.That(header, Does.EndWith("Latitude,Longitude,GeoAccuracy_m,GeoSource,Building,Floor,Zone,Location"));
                Assert.That(sampleLines, Has.Count.EqualTo(4));
                Assert.That(sampleLines.All(l => l.Contains("41.878113,-87.629799,8,manual,HQ,3,East")), Is.True, "every row, including missing rows");
                Assert.That(summary, Does.Contain("41.878113"));
                Assert.That(json.RootElement.GetProperty("geo").GetProperty("latitude").GetDouble(), Is.EqualTo(41.878113));
                Assert.That(json.RootElement.GetProperty("geo").GetProperty("mapUrl").GetString(), Does.Contain("openstreetmap"));
                Assert.That(new FileInfo(Path.Combine(dir, "geo.pdf")).Length, Is.GreaterThan(10_000));
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
