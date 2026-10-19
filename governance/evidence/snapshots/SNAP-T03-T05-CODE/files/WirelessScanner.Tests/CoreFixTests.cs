using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>Tests for control tasks T03 (fresh scans), T04 (ordered persistence, schema upgrades), T05 (measurement semantics).</summary>
[TestFixture]
public class ParserTests
{
    [TestCase(2412000u, 1, "2.4 GHz")]
    [TestCase(2437000u, 6, "2.4 GHz")]
    [TestCase(2484000u, 14, "2.4 GHz")]
    [TestCase(5180000u, 36, "5 GHz")]
    [TestCase(5745000u, 149, "5 GHz")]
    [TestCase(5955000u, 1, "6 GHz")]   // F15: previously reported channel 3
    [TestCase(5975000u, 5, "6 GHz")]
    [TestCase(7115000u, 233, "6 GHz")]
    [TestCase(900000u, 0, "Unknown")]
    public void GetChannelAndBand_MapsFrequencies(uint khz, int channel, string band)
    {
        var (c, b) = WlanAdapter.GetChannelAndBand(khz);
        Assert.Multiple(() =>
        {
            Assert.That(c, Is.EqualTo(channel));
            Assert.That(b, Is.EqualTo(band));
        });
    }

    private static byte[] Ie(byte id, params byte[] body) => new[] { id, (byte)body.Length }.Concat(body).ToArray();

    [Test]
    public void ChannelWidth_UsesOperationElementsNotCapability()
    {
        // HT Capabilities (45) advertising 40 MHz support must NOT imply operation at 40 MHz (F16).
        var htCapOnly = Ie(45, 0x02, 0x00);
        Assert.That(WlanAdapter.ParseChannelWidth(htCapOnly, "2.4 GHz"), Is.Null, "capability alone gives unknown width");

        var ht20 = Ie(61, 6, 0x00, 0, 0, 0);
        Assert.That(WlanAdapter.ParseChannelWidth(ht20, "2.4 GHz"), Is.EqualTo(20));

        var ht40 = Ie(61, 36, 0x05, 0, 0, 0); // secondary above + STA width any
        Assert.That(WlanAdapter.ParseChannelWidth(ht40, "5 GHz"), Is.EqualTo(40));

        var vht80 = ht40.Concat(Ie(192, 1, 42, 0, 0, 0)).ToArray();
        Assert.That(WlanAdapter.ParseChannelWidth(vht80, "5 GHz"), Is.EqualTo(80));

        var vht160 = ht40.Concat(Ie(192, 1, 42, 50, 0, 0)).ToArray(); // CCFS1 - CCFS0 = 8
        Assert.That(WlanAdapter.ParseChannelWidth(vht160, "5 GHz"), Is.EqualTo(160));
    }

    [Test]
    public void ChannelWidth_SixGhzFromHeOperationOnly()
    {
        // HE Operation: ext id 36, params with bit17 (6 GHz info present), color, MCS(2), 6 GHz info (5 bytes) control=2 (80 MHz)
        var he = Ie(255, 36, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 37, 0x02, 39, 0, 0);
        Assert.That(WlanAdapter.ParseChannelWidth(he, "6 GHz"), Is.EqualTo(80));
        Assert.That(WlanAdapter.ParseChannelWidth(Array.Empty<byte>(), "6 GHz"), Is.Null, "no invented 6 GHz default");
    }

    private static byte[] Rsn(params byte[] akms)
    {
        var body = new List<byte> { 1, 0, 0x00, 0x0F, 0xAC, 0x04, 1, 0, 0x00, 0x0F, 0xAC, 0x04, (byte)akms.Length, 0 };
        foreach (var a in akms) body.AddRange(new byte[] { 0x00, 0x0F, 0xAC, a });
        return Ie(48, body.ToArray());
    }

    [TestCase(new byte[] { 2 }, "WPA2-Personal")]
    [TestCase(new byte[] { 1 }, "WPA2-Enterprise")]   // F17: was "WPA2-PSK"
    [TestCase(new byte[] { 8 }, "WPA3-Personal")]
    [TestCase(new byte[] { 2, 8 }, "WPA2/WPA3-Personal")]
    [TestCase(new byte[] { 12 }, "WPA3-Enterprise-192")]
    [TestCase(new byte[] { 18 }, "OWE (Enhanced Open)")]
    public void Security_FromAkmSuites(byte[] akms, string expected)
    {
        Assert.That(WlanAdapter.ParseSecurity(Rsn(akms), 0x0011), Is.EqualTo(expected));
    }

    [Test]
    public void Security_OpenAndWep()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WlanAdapter.ParseSecurity(Array.Empty<byte>(), 0x0001), Is.EqualTo("Open"));
            Assert.That(WlanAdapter.ParseSecurity(Array.Empty<byte>(), 0x0011), Is.EqualTo("WEP"));
        });
    }

    [Test]
    public void Rates_ParseSupportedAndExtended()
    {
        var ies = Ie(1, 0x82, 0x84, 0x8B, 0x96).Concat(Ie(50, 0x6C)).ToArray();
        Assert.That(WlanAdapter.ParseRates(ies), Is.EqualTo(new[] { "1.0", "2.0", "5.5", "11.0", "54.0" }));
    }

    [Test]
    public void BeaconAge_FromFileTime_AndRejectsImplausible()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        ulong twoSecondsAgo = (ulong)now.AddSeconds(-2).ToFileTimeUtc();
        Assert.Multiple(() =>
        {
            Assert.That(WlanAdapter.BeaconAgeMs(twoSecondsAgo, now), Is.EqualTo(2000).Within(1));
            Assert.That(WlanAdapter.BeaconAgeMs(0, now), Is.Null);
            Assert.That(WlanAdapter.BeaconAgeMs((ulong)now.AddDays(-3).ToFileTimeUtc(), now), Is.Null, "older than a day is implausible");
            Assert.That(WlanAdapter.BeaconAgeMs(12345, now), Is.Null, "monotonic-looking values are rejected");
        });
    }

    [Test]
    public void WlanErrorText_ExplainsAdapterLoss()
    {
        Assert.That(WlanErrorText.Describe(1168), Does.Contain("re-select the adapter"));
    }
}

[TestFixture]
public class OrderedPersistenceTests
{
    /// <summary>Repository that fails a sample insert whose session row was not written first (models the SQLite FK).</summary>
    private sealed class FkCheckingRepository : ISessionRepository
    {
        public readonly HashSet<Guid> Sessions = new();
        public readonly List<TelemetrySample> Samples = new();
        public int FkViolations;
        public async Task SaveSessionAsync(CaptureSession session)
        {
            await Task.Delay(30); // session insert is slower than sample inserts
            lock (Sessions) Sessions.Add(session.SessionId);
        }
        public Task SaveSampleAsync(TelemetrySample sample, Guid sessionId)
        {
            lock (Sessions)
            {
                if (!Sessions.Contains(sessionId)) { FkViolations++; throw new InvalidOperationException("FOREIGN KEY constraint failed"); }
                Samples.Add(sample);
            }
            return Task.CompletedTask;
        }
        public Task UpdateSessionStatusAsync(Guid sessionId, CaptureSessionStatus status, DateTime? endTime) => Task.CompletedTask;
        public Task<CaptureSession?> GetSessionAsync(Guid sessionId) => Task.FromResult<CaptureSession?>(null);
        public Task<IEnumerable<TelemetrySample>> GetSamplesAsync(Guid sessionId) => Task.FromResult<IEnumerable<TelemetrySample>>(Samples);
        public Task<IEnumerable<CaptureSession>> GetAllSessionsAsync() => Task.FromResult<IEnumerable<CaptureSession>>(Array.Empty<CaptureSession>());
        public Task<IEnumerable<CaptureSession>> GetSessionsByDateAsync(DateTime start, DateTime end) => GetAllSessionsAsync();
        public Task DeleteSessionAsync(Guid sessionId) => Task.CompletedTask;
        public Task PurgeSessionsByAgeAsync(DateTime threshold) => Task.CompletedTask;
    }

    private static NetworkInterfaceInfo Device() =>
        new(Guid.NewGuid(), "TestAdapter", "Desc", "PCI", NetworkDeviceType.Wireless_WiFi, true, "00:11:22:33:44:55", "10.0.0.2");

    private static TelemetrySample Sample(int rssi) =>
        new(DateTime.UtcNow, "Zone", "AA:BB:CC:DD:EE:FF", "Net", "5 GHz", 36, rssi, rssi + 95, 80, 0, null);

    [Test]
    public async Task SamplesSavedImmediatelyAfterStart_NeverViolateForeignKey()
    {
        // Baseline F02: fire-and-forget writes let samples land before the slower session insert.
        var repo = new FkCheckingRepository();
        var manager = new SessionManager(repo, new NullLogger());
        manager.StartSession("Race", "All", "Lab", Device(), CaptureSessionMode.Stationary);
        for (int i = 0; i < 20; i++) manager.SaveSample(Sample(-50 - i));
        await manager.FlushAsync();

        Assert.Multiple(() =>
        {
            Assert.That(repo.FkViolations, Is.EqualTo(0));
            Assert.That(repo.Samples, Has.Count.EqualTo(20));
        });
    }

    [Test]
    public async Task Writer_PreservesFifoOrder_AndContinuesAfterFailure()
    {
        await using var writer = new OrderedPersistenceWriter(new NullLogger());
        var order = new List<int>();
        writer.Enqueue("a", async () => { await Task.Delay(20); order.Add(1); });
        writer.Enqueue("b", () => throw new InvalidOperationException("boom"));
        writer.Enqueue("c", () => { order.Add(3); return Task.CompletedTask; });
        await writer.FlushAsync();

        Assert.Multiple(() =>
        {
            Assert.That(order, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(writer.Failures, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SaveSamples_WhenPaused_StoresNothing()
    {
        var repo = new MockSessionRepository();
        var manager = new SessionManager(repo, new NullLogger());
        manager.StartSession("P", "All", "Lab", Device(), CaptureSessionMode.Stationary);
        manager.PauseSession();
        manager.SaveSamples(new[] { Sample(-60) });
        await manager.FlushAsync();
        Assert.That(repo.SavedSamples, Is.Empty);
    }

    [Test]
    public void StartSession_WhilePaused_IsRejected()
    {
        var manager = new SessionManager(new MockSessionRepository(), new NullLogger());
        manager.StartSession("One", "All", "Lab", Device(), CaptureSessionMode.Roaming);
        manager.PauseSession();
        Assert.Throws<InvalidOperationException>(() => manager.StartSession("Two", "All", "Lab", Device(), CaptureSessionMode.Roaming));
    }
}

[TestFixture]
public class SchemaMigrationTests
{
    [Test]
    public void Upgrade_AddsMissingTables_ToLegacyDatabase_AndIsIdempotent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ws_legacy_{Guid.NewGuid():N}.db");
        try
        {
            // Legacy schema: only the original three tables (as created by the 2026-05 release)
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"CREATE TABLE ""Sessions"" (""SessionId"" TEXT NOT NULL CONSTRAINT ""PK_Sessions"" PRIMARY KEY, ""Name"" TEXT NOT NULL, ""Scope"" TEXT NOT NULL, ""FacilityName"" TEXT NOT NULL, ""InterfaceName"" TEXT NOT NULL, ""StartTime"" TEXT NOT NULL, ""EndTime"" TEXT NULL, ""Mode"" INTEGER NOT NULL, ""Status"" INTEGER NOT NULL);
                                    INSERT INTO Sessions VALUES ('11111111-1111-1111-1111-111111111111','Legacy','s','f','i','2026-05-28 10:00:00',NULL,0,2);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var options = new DbContextOptionsBuilder<WlanDbContext>().UseSqlite($"Data Source={path}").Options;
            MigrationResult first, second;
            using (var ctx = new WlanDbContext(options)) first = SchemaMigrator.Upgrade(ctx);
            using (var ctx = new WlanDbContext(options)) second = SchemaMigrator.Upgrade(ctx);

            using (var ctx = new WlanDbContext(options))
            {
                Assert.Multiple(() =>
                {
                    Assert.That(first.CreatedObjects, Does.Contain("Samples"));
                    Assert.That(first.CreatedObjects, Does.Contain("Settings"));
                    Assert.That(second.CreatedObjects, Is.Empty, "second run creates nothing");
                    Assert.That(second.FromVersion, Is.EqualTo(SchemaMigrator.CurrentSchemaVersion));
                    Assert.That(ctx.Sessions.Count(), Is.EqualTo(1), "existing rows preserved");
                    Assert.That(ctx.Samples.Count(), Is.EqualTo(0), "new table usable");
                });
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}

[TestFixture]
public class FreshScanCaptureTests
{
    private static (MainWindowViewModel Vm, MockSessionRepository Repo, MockNetworkAdapterProvider Provider, SessionManager Manager) Build()
    {
        var provider = new MockNetworkAdapterProvider();
        var repo = new MockSessionRepository();
        var manager = new SessionManager(repo, new NullLogger());
        var vm = new MainWindowViewModel(provider, manager, repo, new MockExportService(), new MockRestApiServer(), new MockSettingsRepository());
        var device = new NetworkInterfaceInfo(Guid.NewGuid(), "W", "W", "P", NetworkDeviceType.Wireless_WiFi, true, "00:00:00:00:00:01", "10.0.0.1");
        provider.Interfaces.Add(device);
        provider.AccessPoints.Add(new AccessPoint("00:AA:BB:CC:DD:01", "A", null, "5 GHz", 36, 20, -50, null, 45, 90, "WPA2-Personal", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow));
        vm.SelectedAdapter = device;
        manager.StartSession("S", "All", "Lab", device, CaptureSessionMode.Stationary);
        vm.IsSessionActive = true;
        return (vm, repo, provider, manager);
    }

    [Test]
    public async Task StaleCycle_WithUnknownBeaconAge_IsNotStoredAsSample()
    {
        var (vm, repo, provider, manager) = Build();
        provider.NextStatus = ScanCycleStatus.Stale;
        await vm.PerformScanAsync();
        await manager.FlushAsync();
        Assert.That(repo.SavedSamples, Is.Empty, "cached repeats must not become samples (F01)");
    }

    [Test]
    public async Task StaleCycle_WithRecentBeacon_IsStored()
    {
        var (vm, repo, provider, manager) = Build();
        provider.NextStatus = ScanCycleStatus.Stale;
        provider.AccessPoints[0] = provider.AccessPoints[0] with { BeaconAgeMs = 800 };
        await vm.PerformScanAsync();
        await manager.FlushAsync();
        Assert.That(repo.SavedSamples, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FailedCycle_StoresNothing_AndReportsReason()
    {
        var (vm, repo, provider, manager) = Build();
        provider.NextStatus = ScanCycleStatus.Failed;
        await vm.PerformScanAsync();
        await manager.FlushAsync();
        Assert.Multiple(() =>
        {
            Assert.That(repo.SavedSamples, Is.Empty);
            Assert.That(vm.LastCycleText, Does.Contain("FAILED"));
            Assert.That(vm.StatusText, Does.Contain("re-select the adapter"));
        });
    }

    [Test]
    public async Task FreshCycle_StoresOneSamplePerAp_WithUnavailableNoiseFloor()
    {
        var (vm, repo, provider, manager) = Build();
        await vm.PerformScanAsync();
        await manager.FlushAsync();
        Assert.Multiple(() =>
        {
            Assert.That(repo.SavedSamples, Has.Count.EqualTo(1));
            Assert.That(repo.SavedSamples[0].NoiseFloor, Is.Null, "noise floor is not measured (F09)");
        });
    }

    [Test]
    public async Task InfrastructureMockProvider_ScanCycles_SupportFaultInjection()
    {
        var mock = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider
        {
            CycleStatusOverride = n => n == 2 ? ScanCycleStatus.Failed : ScanCycleStatus.Fresh
        };
        var c1 = await mock.ScanAsync(Guid.Empty, TimeSpan.FromSeconds(1));
        var c2 = await mock.ScanAsync(Guid.Empty, TimeSpan.FromSeconds(1));
        Assert.Multiple(() =>
        {
            Assert.That(c1.Status, Is.EqualTo(ScanCycleStatus.Fresh));
            Assert.That(c1.AccessPoints, Is.Not.Empty);
            Assert.That(c1.AccessPoints.All(a => a.NoiseFloor == null), Is.True);
            Assert.That(c2.Status, Is.EqualTo(ScanCycleStatus.Failed));
            Assert.That(c2.ErrorCode, Is.EqualTo("1168"));
        });
    }
}

[TestFixture]
public class UnitLabelTests
{
    [Test]
    public void ExportsAndUi_NeverLabelRssiVariationInMilliseconds()
    {
        // F14: |dRSSI| is in dB. Scan source files for the old "ms" labelling of the jitter column.
        string root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "WirelessScanner.slnx"))) root = Path.GetDirectoryName(root)!;
        Assert.That(root, Is.Not.Null, "solution root");
        string export = File.ReadAllText(Path.Combine(root!, "WirelessScanner.Infrastructure", "ExportService.cs"));
        string xaml = File.ReadAllText(Path.Combine(root!, "WirelessScanner.Presentation", "MainWindow.xaml"));
        Assert.Multiple(() =>
        {
            Assert.That(export, Does.Not.Contain("Jitter:F1} ms"));
            Assert.That(xaml, Does.Not.Contain("Binding Jitter, StringFormat=\\{0:F1\\} ms"));
            Assert.That(export, Does.Contain("RSSI_Variation_dB"));
        });
    }
}
