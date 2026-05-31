using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

public class MockSessionRepository : ISessionRepository
{
    private readonly object _lock = new();

    public List<CaptureSession> SavedSessions { get; } = new();
    public List<TelemetrySample> SavedSamples { get; } = new();
    public List<(Guid SessionId, CaptureSessionStatus Status, DateTime? EndTime)> StatusUpdates { get; } = new();

    public Task SaveSessionAsync(CaptureSession session)
    {
        lock (_lock)
        {
            SavedSessions.Add(session);
        }
        return Task.CompletedTask;
    }

    public Task SaveSampleAsync(TelemetrySample sample, Guid sessionId)
    {
        lock (_lock)
        {
            SavedSamples.Add(sample);
        }
        return Task.CompletedTask;
    }

    public Task UpdateSessionStatusAsync(Guid sessionId, CaptureSessionStatus status, DateTime? endTime)
    {
        lock (_lock)
        {
            StatusUpdates.Add((sessionId, status, endTime));
        }
        return Task.CompletedTask;
    }

    public Task<CaptureSession?> GetSessionAsync(Guid sessionId)
    {
        lock (_lock)
        {
            var session = SavedSessions.Find(s => s.SessionId == sessionId);
            return Task.FromResult(session);
        }
    }

    public Task<IEnumerable<TelemetrySample>> GetSamplesAsync(Guid sessionId)
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<TelemetrySample>>(SavedSamples.ToList());
        }
    }

    public Task<IEnumerable<CaptureSession>> GetAllSessionsAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<CaptureSession>>(SavedSessions.ToList());
        }
    }

    public Task<IEnumerable<CaptureSession>> GetSessionsByDateAsync(DateTime start, DateTime end)
    {
        lock (_lock)
        {
            var filtered = SavedSessions.Where(s => s.StartTime >= start && s.StartTime <= end).ToList();
            return Task.FromResult<IEnumerable<CaptureSession>>(filtered);
        }
    }

    public Task DeleteSessionAsync(Guid sessionId)
    {
        lock (_lock)
        {
            SavedSessions.RemoveAll(s => s.SessionId == sessionId);
        }
        return Task.CompletedTask;
    }

    public Task PurgeSessionsByAgeAsync(DateTime threshold)
    {
        lock (_lock)
        {
            SavedSessions.RemoveAll(s => s.StartTime < threshold);
        }
        return Task.CompletedTask;
    }
}

[TestFixture]
public class TelemetryTests
{
    private StatisticsCalculator _calculator = null!;
    private MockSessionRepository _repository = null!;
    private SessionManager _sessionManager = null!;

    [SetUp]
    public void Setup()
    {
        _calculator = new StatisticsCalculator();
        _repository = new MockSessionRepository();
        _sessionManager = new SessionManager(_repository, new NullLogger());
    }

    [Test]
    public void CalculateStats_WithEmptyLists_ReturnsZeroedStats()
    {
        var stats = _calculator.CalculateStats(Array.Empty<int>(), Array.Empty<double>());
        
        Assert.Multiple(() =>
        {
            Assert.That(stats.MinRssi, Is.EqualTo(0));
            Assert.That(stats.MaxRssi, Is.EqualTo(0));
            Assert.That(stats.MeanRssi, Is.EqualTo(0.0));
            Assert.That(stats.StdDevRssi, Is.EqualTo(0.0));
            Assert.That(stats.AvgJitter, Is.EqualTo(0.0));
        });
    }

    [Test]
    public void CalculateStats_WithValidData_ComputesCorrectMetrics()
    {
        var rssiList = new List<int> { -50, -60, -70, -80, -90 };
        var jitterList = new List<double> { 2.0, 4.0, 6.0 };

        var stats = _calculator.CalculateStats(rssiList, jitterList);

        Assert.Multiple(() =>
        {
            Assert.That(stats.MinRssi, Is.EqualTo(-90));
            Assert.That(stats.MaxRssi, Is.EqualTo(-50));
            Assert.That(stats.MeanRssi, Is.EqualTo(-70.0));
            
            // Expected StdDev: sqrt(((50-70)^2 + (60-70)^2 + 0 + (80-70)^2 + (90-70)^2)/5)
            // = sqrt((400 + 100 + 0 + 100 + 400)/5) = sqrt(1000/5) = sqrt(200) ≈ 14.142
            Assert.That(stats.StdDevRssi, Is.EqualTo(Math.Sqrt(200.0)).Within(0.001));
            Assert.That(stats.AvgJitter, Is.EqualTo(4.0));
        });
    }

    [Test]
    public void SessionManager_LifecycleTransitions_UpdatesCorrectly()
    {
        var device = new NetworkInterfaceInfo(
            Guid.NewGuid(), 
            "TestAdapter", 
            "Desc", 
            "PCI\\VEN_123", 
            NetworkDeviceType.Wireless_WiFi, 
            true,
            "00:11:22:33:44:55",
            "192.168.1.5"
        );

        // Start
        var session = _sessionManager.StartSession("Test Run", "Scope IoT", "Warehouse A", device, CaptureSessionMode.Roaming);

        Assert.Multiple(() =>
        {
            Assert.That(_sessionManager.ActiveSession, Is.Not.Null);
            Assert.That(_sessionManager.ActiveSession!.Status, Is.EqualTo(CaptureSessionStatus.Active));
            Assert.That(_sessionManager.ActiveSession.Name, Is.EqualTo("Test Run"));
            Assert.That(_sessionManager.ActiveSession.Scope, Is.EqualTo("Scope IoT"));
            Assert.That(_sessionManager.ActiveSession.FacilityName, Is.EqualTo("Warehouse A"));
        });

        // Pause
        _sessionManager.PauseSession();
        Assert.That(_sessionManager.ActiveSession!.Status, Is.EqualTo(CaptureSessionStatus.Paused));

        // Resume
        _sessionManager.ResumeSession();
        Assert.That(_sessionManager.ActiveSession!.Status, Is.EqualTo(CaptureSessionStatus.Active));

        // Stop
        _sessionManager.StopSession();
        Assert.That(_sessionManager.ActiveSession!.Status, Is.EqualTo(CaptureSessionStatus.Completed));
        Assert.That(_sessionManager.ActiveSession.EndTime, Is.Not.Null);
    }

    [Test]
    public async Task CalculateJitter_ConsecutiveSamples_CalculatesCorrectAbsoluteDifference()
    {
        const string bssid = "AA:BB:CC:DD:EE:FF";

        // First sample (no previous, returns 0)
        double jitter1 = _sessionManager.CalculateJitterFor(bssid, -60);
        Assert.That(jitter1, Is.EqualTo(0.0));

        // Second sample (diff of -60 and -62 is 2)
        double jitter2 = _sessionManager.CalculateJitterFor(bssid, -62);
        Assert.That(jitter2, Is.EqualTo(2.0));

        // Third sample (diff of -62 and -55 is 7)
        double jitter3 = _sessionManager.CalculateJitterFor(bssid, -55);
        Assert.That(jitter3, Is.EqualTo(7.0));
    }

    [Test]
    public async Task SessionRepository_DbIntegration_SavesAndCascades()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WlanDbContext>()
            .UseSqlite("Data Source=test_wireless_scanner.db")
            .Options;

        var mockFactory = new MockDbContextFactory(options);
        
        // Ensure clean DB
        using (var context = new WlanDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }

        var repo = new SessionRepository(mockFactory);

        var device = new NetworkInterfaceInfo(
            Guid.NewGuid(), 
            "TestAdapter", 
            "Desc", 
            "PCI\\VEN_123", 
            NetworkDeviceType.Wireless_WiFi, 
            true,
            "00:11:22:33:44:55",
            "192.168.1.5"
        );

        var session = new CaptureSession(
            SessionId: Guid.NewGuid(),
            Name: "Test Run DB",
            Scope: "Scope DB",
            FacilityName: "Facility DB",
            InterfaceId: device.Id,
            InterfaceName: device.Name,
            StartTime: DateTime.UtcNow,
            EndTime: null,
            Mode: CaptureSessionMode.Roaming,
            Status: CaptureSessionStatus.Active
        );

        // Save session
        await repo.SaveSessionAsync(session);

        // Save sample
        var sample = new TelemetrySample(
            Timestamp: DateTime.UtcNow,
            SurveyPoint: "Point A",
            BSSID: "00:11:22:33:44:55",
            SSID: "Test SSID",
            Band: "5 GHz",
            Channel: 36,
            RSSI: -55,
            SNR: 40,
            Quality: 90,
            Jitter: 1.5,
            NoiseFloor: -95
        );
        await repo.SaveSampleAsync(sample, session.SessionId);

        // Verify they are saved
        using (var context = new WlanDbContext(options))
        {
            var savedSession = await context.Sessions.FindAsync(session.SessionId);
            Assert.That(savedSession, Is.Not.Null);
            Assert.That(savedSession!.Name, Is.EqualTo("Test Run DB"));

            var savedSample = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                context.Samples, s => s.SessionId == session.SessionId);
            Assert.That(savedSample, Is.Not.Null);
            Assert.That(savedSample!.SSID, Is.EqualTo("Test SSID"));
        }

        // Update session
        await repo.UpdateSessionStatusAsync(session.SessionId, CaptureSessionStatus.Completed, DateTime.UtcNow);

        // Verify update
        using (var context = new WlanDbContext(options))
        {
            var savedSession = await context.Sessions.FindAsync(session.SessionId);
            Assert.That(savedSession!.Status, Is.EqualTo((int)CaptureSessionStatus.Completed));
            Assert.That(savedSession.EndTime, Is.Not.Null);
        }

        // Test Cascade Delete
        using (var context = new WlanDbContext(options))
        {
            var savedSession = await context.Sessions.FindAsync(session.SessionId);
            context.Sessions.Remove(savedSession!);
            await context.SaveChangesAsync();
        }

        // Verify sample is gone
        using (var context = new WlanDbContext(options))
        {
            var savedSample = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                context.Samples, s => s.SessionId == session.SessionId);
            Assert.That(savedSample, Is.Null);
        }

        // Clean up
        using (var context = new WlanDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task ExportService_CsvAndJsonAndPdf_GenerateCorrectFiles()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WlanDbContext>()
            .UseSqlite("Data Source=test_export_scanner.db")
            .Options;

        var mockFactory = new MockDbContextFactory(options);
        
        using (var context = new WlanDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }

        var repo = new SessionRepository(mockFactory);
        var calculator = new StatisticsCalculator();
        var exporter = new ExportService(repo, calculator);

        var device = new NetworkInterfaceInfo(
            Guid.NewGuid(), 
            "TestAdapter", 
            "Desc", 
            "PCI\\VEN_123", 
            NetworkDeviceType.Wireless_WiFi, 
            true,
            "00:11:22:33:44:55",
            "192.168.1.5"
        );

        var session = new CaptureSession(
            SessionId: Guid.NewGuid(),
            Name: "Export Test Session",
            Scope: "Scope Test",
            FacilityName: "Facility Test",
            InterfaceId: device.Id,
            InterfaceName: device.Name,
            StartTime: DateTime.UtcNow,
            EndTime: DateTime.UtcNow.AddMinutes(5),
            Mode: CaptureSessionMode.Roaming,
            Status: CaptureSessionStatus.Completed
        );

        await repo.SaveSessionAsync(session);

        var sample = new TelemetrySample(
            Timestamp: DateTime.UtcNow,
            SurveyPoint: "Point A",
            BSSID: "00:11:22:33:44:55",
            SSID: "Export SSID",
            Band: "5 GHz",
            Channel: 36,
            RSSI: -55,
            SNR: 40,
            Quality: 90,
            Jitter: 1.5,
            NoiseFloor: -95
        );
        await repo.SaveSampleAsync(sample, session.SessionId);

        string csvPath = "test_export.csv";
        string jsonPath = "test_export.json";
        string pdfPath = "test_export.pdf";

        try
        {
            // Test CSV Export
            await exporter.ExportToCsvAsync(csvPath, session.SessionId);
            Assert.That(File.Exists(csvPath), Is.True);
            string csvContent = await File.ReadAllTextAsync(csvPath);
            Assert.That(csvContent, Does.Contain("Export Test Session"));
            Assert.That(csvContent, Does.Contain("00:11:22:33:44:55"));

            // Test JSON Export
            await exporter.ExportToJsonAsync(jsonPath, session.SessionId);
            Assert.That(File.Exists(jsonPath), Is.True);
            string jsonContent = await File.ReadAllTextAsync(jsonPath);
            Assert.That(jsonContent, Does.Contain("Export Test Session"));
            Assert.That(jsonContent, Does.Contain("00:11:22:33:44:55"));

            // Test PDF Export
            var metadata = new ExportMetadata("Client Antigrav", "Surveyor Demit", "Some descriptive survey notes.");
            await exporter.ExportToPdfAsync(pdfPath, session.SessionId, metadata);
            Assert.That(File.Exists(pdfPath), Is.True);
        }
        finally
        {
            if (File.Exists(csvPath)) File.Delete(csvPath);
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
            if (File.Exists(pdfPath)) File.Delete(pdfPath);

            using (var context = new WlanDbContext(options))
            {
                await context.Database.EnsureDeletedAsync();
            }
        }
    }
}

public class MockDbContextFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<WlanDbContext>
{
    private readonly Microsoft.EntityFrameworkCore.DbContextOptions<WlanDbContext> _options;

    public MockDbContextFactory(Microsoft.EntityFrameworkCore.DbContextOptions<WlanDbContext> options)
    {
        _options = options;
    }

    public WlanDbContext CreateDbContext()
    {
        return new WlanDbContext(_options);
    }
}

public class NullLogger : IAppLogger
{
    public void Info(string message) {}
    public void Warn(string message) {}
    public void Error(string message, Exception? ex = null) {}
}

public class MockNetworkAdapterProvider : INetworkAdapterProvider
{
    public List<NetworkInterfaceInfo> Interfaces { get; } = new();
    public List<AccessPoint> AccessPoints { get; } = new();
    public Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync() =>
        Task.FromResult<IEnumerable<NetworkInterfaceInfo>>(Interfaces);
    public Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId) =>
        Task.FromResult<IEnumerable<AccessPoint>>(AccessPoints);
    public Task TriggerHardwareScanAsync(Guid interfaceId) => Task.CompletedTask;
    public Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId) =>
        Task.FromResult(new WiredInterfaceStats(0, "0.0.0.0", "Down", 0));
    public event Action? HardwareChanged { add { } remove { } }
    public void NotifyHardwareChange() {}
}

public class MockExportService : IExportService
{
    public List<Guid> SingleCsvExports { get; } = new();
    public List<Guid> SingleJsonExports { get; } = new();
    public List<(Guid SessionId, ExportMetadata Metadata)> SinglePdfExports { get; } = new();
    public List<List<Guid>> BatchCsvExports { get; } = new();
    public List<List<Guid>> BatchJsonExports { get; } = new();

    public Task ExportToCsvAsync(string filePath, Guid sessionId, ExportFields? fields = null)
    {
        SingleCsvExports.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task ExportToJsonAsync(string filePath, Guid sessionId, ExportFields? fields = null)
    {
        SingleJsonExports.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task ExportToPdfAsync(string filePath, Guid sessionId, ExportMetadata metadata, ExportFields? fields = null)
    {
        SinglePdfExports.Add((sessionId, metadata));
        return Task.CompletedTask;
    }

    public Task ExportToCsvAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null)
    {
        BatchCsvExports.Add(sessionIds.ToList());
        return Task.CompletedTask;
    }

    public Task ExportToJsonAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null)
    {
        BatchJsonExports.Add(sessionIds.ToList());
        return Task.CompletedTask;
    }
}

public class MockRestApiServer : IRestApiServer
{
    public bool IsRunning { get; set; }
    public int Port { get; set; } = 5005;
    public string ApiKey { get; set; } = "";
    public Task StartAsync(int port, string apiKey)
    {
        IsRunning = true;
        Port = port;
        ApiKey = apiKey;
        return Task.CompletedTask;
    }
    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }
    public void UpdateLiveAccessPoints(IEnumerable<AccessPoint> accessPoints) {}
}

public class MockSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string> _settings = new();
    public Task<string?> GetSettingAsync(string key)
    {
        _settings.TryGetValue(key, out var val);
        return Task.FromResult<string?>(val);
    }
    public Task SaveSettingAsync(string key, string value)
    {
        _settings[key] = value;
        return Task.CompletedTask;
    }
}

[TestFixture]
public class ViewModelTests
{
    [Test]
    public async Task ExportApiKey_SavesMarkdownFileInApiSubfolder()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var viewModel = new MainWindowViewModel(
            adapterProvider,
            sessionManager,
            sessionRepo,
            exportService,
            apiServer,
            settingsRepo
        );

        // Generate an API key
        viewModel.GenerateApiKeyCommand.Execute(null);
        Assert.That(viewModel.ApiKey, Does.StartWith("ws_live_"));

        // Set API Port
        viewModel.ApiPort = "9999";
        viewModel.ApiEnabled = true;

        // Path where it should be saved
        string expectedDir = Path.Combine(AppContext.BaseDirectory, "API");
        string expectedPath = Path.Combine(expectedDir, "api_key.md");

        if (File.Exists(expectedPath))
        {
            File.Delete(expectedPath);
        }

        // Act
        viewModel.ExportApiKeyCommand.Execute(null);

        // Assert
        Assert.That(File.Exists(expectedPath), Is.True, $"Expected API key file to be created at: {expectedPath}");
        string fileContent = await File.ReadAllTextAsync(expectedPath);
        Assert.Multiple(() =>
        {
            Assert.That(fileContent, Does.Contain(viewModel.ApiKey));
            Assert.That(fileContent, Does.Contain("9999"));
            Assert.That(fileContent, Does.Contain("ws_live_"));
        });

        // Clean up
        if (File.Exists(expectedPath))
        {
            File.Delete(expectedPath);
        }
    }

    [Test]
    public async Task DeleteApiKey_ClearsKeyAndRemovesFile()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var viewModel = new MainWindowViewModel(
            adapterProvider,
            sessionManager,
            sessionRepo,
            exportService,
            apiServer,
            settingsRepo
        );

        // Generate and export key
        viewModel.GenerateApiKeyCommand.Execute(null);
        string key = viewModel.ApiKey;

        string expectedPath = Path.Combine(AppContext.BaseDirectory, "API", "api_key.md");
        viewModel.ExportApiKeyCommand.Execute(null);
        Assert.That(File.Exists(expectedPath), Is.True);

        // Act
        viewModel.DeleteApiKeyCommand.Execute(null);

        // Assert
        Assert.That(viewModel.ApiKey, Is.Empty);
        Assert.That(File.Exists(expectedPath), Is.False, "Expected API key file to be deleted upon calling clear");
    }

    [Test]
    public void FilteredActiveSessionLogs_FiltersLogsBasedOnCheckedKeys()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var viewModel = new MainWindowViewModel(
            adapterProvider,
            sessionManager,
            sessionRepo,
            exportService,
            apiServer,
            settingsRepo
        );

        // Add some log items to ActiveSessionLogs
        var sample1 = new TelemetrySample(DateTime.UtcNow, "Zone 1", "00:11:22:33:44:55", "SSID_A", "5 GHz", 36, -50, 40, 90, 0, null);
        var sample2 = new TelemetrySample(DateTime.UtcNow, "Zone 1", "66:77:88:99:AA:BB", "SSID_B", "2.4 GHz", 6, -60, 30, 75, 0, null);

        viewModel.ActiveSessionLogs.Add(new LiveCaptureLogItem(sample1, "• 0 dB", "#888888"));
        viewModel.ActiveSessionLogs.Add(new LiveCaptureLogItem(sample2, "• 0 dB", "#888888"));

        // Act: Nothing checked
        Assert.That(viewModel.FilteredActiveSessionLogs, Has.Count.EqualTo(2));

        // Act: SSID View, check SSID_A
        viewModel.IsSsidView = true;
        var displayItemSsid = new AccessPointDisplayItem(
            new AccessPoint("00:11:22:33:44:55", "SSID_A", null, "5 GHz", 36, 20, -50, null, 40, 90, "WPA2", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow),
            false,
            true,
            item => {
                var method = typeof(MainWindowViewModel).GetMethod("OnItemCheckedChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method?.Invoke(viewModel, new object[] { item });
            }
        );
        displayItemSsid.IsChecked = true;

        // Assert: Filtered logs should contain only SSID_A
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredActiveSessionLogs, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredActiveSessionLogs[0].SSID, Is.EqualTo("SSID_A"));
        });

        // Act: AP (BSSID) View, check BSSID of sample2
        viewModel.IsSsidView = false; // This clears _checkedKeys
        var displayItemAp = new AccessPointDisplayItem(
            new AccessPoint("66:77:88:99:AA:BB", "SSID_B", null, "2.4 GHz", 6, 20, -60, null, 30, 75, "WPA2", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow),
            false,
            false,
            item => {
                var method = typeof(MainWindowViewModel).GetMethod("OnItemCheckedChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method?.Invoke(viewModel, new object[] { item });
            }
        );
        displayItemAp.IsChecked = true;

        // Assert: Filtered logs should contain only SSID_B (BSSID 66:77:88:99:AA:BB)
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredActiveSessionLogs, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredActiveSessionLogs[0].BSSID, Is.EqualTo("66:77:88:99:AA:BB"));
        });
    }

    [Test]
    public async Task FilteredHistoricalSessions_FiltersByNameCaseInsensitive()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var s1 = new CaptureSession(Guid.NewGuid(), "Office Wi-Fi Survey", "Scope A", "Facility X", Guid.Empty, "wifi", DateTime.UtcNow, null, CaptureSessionMode.Roaming, CaptureSessionStatus.Completed);
        var s2 = new CaptureSession(Guid.NewGuid(), "Warehouse Coverage Map", "Scope B", "Facility Y", Guid.Empty, "wifi", DateTime.UtcNow, null, CaptureSessionMode.Stationary, CaptureSessionStatus.Completed);
        await sessionRepo.SaveSessionAsync(s1);
        await sessionRepo.SaveSessionAsync(s2);

        var viewModel = new MainWindowViewModel(adapterProvider, sessionManager, sessionRepo, exportService, apiServer, settingsRepo);
        await viewModel.LoadHistoricalSessionsAsync();

        // Act & Assert - Name Filter Office
        viewModel.HistoryFilterName = "office";
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredHistoricalSessions, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredHistoricalSessions[0].Name, Is.EqualTo("Office Wi-Fi Survey"));
        });

        // Act & Assert - Name Filter Coverage (case-insensitive check)
        viewModel.HistoryFilterName = "COVERAGE";
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredHistoricalSessions, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredHistoricalSessions[0].Name, Is.EqualTo("Warehouse Coverage Map"));
        });

        // Act & Assert - Name Filter not found
        viewModel.HistoryFilterName = "Notfound";
        Assert.That(viewModel.FilteredHistoricalSessions, Is.Empty);
    }

    [Test]
    public async Task FilteredHistoricalSessions_FiltersByDateRange()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var baseTime = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);
        var s1 = new CaptureSession(Guid.NewGuid(), "Session 1", "Scope A", "Facility X", Guid.Empty, "wifi", baseTime, null, CaptureSessionMode.Roaming, CaptureSessionStatus.Completed);
        var s2 = new CaptureSession(Guid.NewGuid(), "Session 2", "Scope B", "Facility Y", Guid.Empty, "wifi", baseTime.AddDays(5), null, CaptureSessionMode.Stationary, CaptureSessionStatus.Completed);
        var s3 = new CaptureSession(Guid.NewGuid(), "Session 3", "Scope C", "Facility Z", Guid.Empty, "wifi", baseTime.AddDays(10), null, CaptureSessionMode.Stationary, CaptureSessionStatus.Completed);
        await sessionRepo.SaveSessionAsync(s1);
        await sessionRepo.SaveSessionAsync(s2);
        await sessionRepo.SaveSessionAsync(s3);

        var viewModel = new MainWindowViewModel(adapterProvider, sessionManager, sessionRepo, exportService, apiServer, settingsRepo);
        await viewModel.LoadHistoricalSessionsAsync();

        // Filter: May 22 to May 27 (should only include Session 2)
        viewModel.HistoryFilterFromDate = baseTime.AddDays(2);
        viewModel.HistoryFilterToDate = baseTime.AddDays(7);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredHistoricalSessions, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredHistoricalSessions[0].Name, Is.EqualTo("Session 2"));
        });
        
        // Filter: May 15 to May 22 (should only include Session 1)
        viewModel.HistoryFilterFromDate = baseTime.AddDays(-5);
        viewModel.HistoryFilterToDate = baseTime.AddDays(2);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.FilteredHistoricalSessions, Has.Count.EqualTo(1));
            Assert.That(viewModel.FilteredHistoricalSessions[0].Name, Is.EqualTo("Session 1"));
        });
    }

    [Test]
    public async Task ExportHistoryCommands_ExportsSingleOrMultipleSessions()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var s1 = new CaptureSession(id1, "Session 1", "Scope A", "Facility X", Guid.Empty, "wifi", DateTime.UtcNow, null, CaptureSessionMode.Roaming, CaptureSessionStatus.Completed);
        var s2 = new CaptureSession(id2, "Session 2", "Scope B", "Facility Y", Guid.Empty, "wifi", DateTime.UtcNow, null, CaptureSessionMode.Stationary, CaptureSessionStatus.Completed);
        await sessionRepo.SaveSessionAsync(s1);
        await sessionRepo.SaveSessionAsync(s2);

        var viewModel = new MainWindowViewModel(adapterProvider, sessionManager, sessionRepo, exportService, apiServer, settingsRepo);
        await viewModel.LoadHistoricalSessionsAsync();

        // 1. Single session export
        viewModel.SelectedHistorySession = s1;
        // Mock ShowSaveFileDialogHook to return a path
        viewModel.ShowSaveFileDialogHook = (filter, defaultExt, filename) => "test_single.csv";
        viewModel.ExportHistoryCsvCommand.Execute(null);

        Assert.That(exportService.BatchCsvExports, Has.Count.EqualTo(1));
        Assert.That(exportService.BatchCsvExports[0], Contains.Item(id1));

        // 2. Batch export (multi-select)
        viewModel.SelectedHistorySessions = new List<CaptureSession> { s1, s2 };
        viewModel.ShowSaveFileDialogHook = (filter, defaultExt, filename) => "test_batch.json";
        viewModel.ExportHistoryJsonCommand.Execute(null);

        Assert.That(exportService.BatchJsonExports, Has.Count.EqualTo(1));
        Assert.That(exportService.BatchJsonExports[0], Contains.Item(id1));
        Assert.That(exportService.BatchJsonExports[0], Contains.Item(id2));
    }

    [Test]
    public async Task PerformScanAsync_FiltersCapturedTelemetry_BasedOnCheckedKeys()
    {
        // Arrange
        var adapterProvider = new MockNetworkAdapterProvider();
        var sessionRepo = new MockSessionRepository();
        var sessionManager = new SessionManager(sessionRepo, new NullLogger());
        var exportService = new MockExportService();
        var apiServer = new MockRestApiServer();
        var settingsRepo = new MockSettingsRepository();

        var device = new NetworkInterfaceInfo(
            Guid.NewGuid(), 
            "TestWiFi", 
            "WiFi Interface", 
            "PCI\\VEN_ABC", 
            NetworkDeviceType.Wireless_WiFi, 
            true,
            "00:11:22:33:44:55",
            "192.168.1.10"
        );

        adapterProvider.Interfaces.Add(device);

        var ap1 = new AccessPoint("00:AA:BB:CC:DD:01", "SSID_A", null, "5 GHz", 36, 20, -50, null, 40, 90, "WPA2", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow);
        var ap2 = new AccessPoint("00:AA:BB:CC:DD:02", "SSID_B", null, "2.4 GHz", 6, 20, -60, null, 30, 75, "WPA2", "Infrastructure", null, Array.Empty<string>(), DateTime.UtcNow);
        adapterProvider.AccessPoints.Add(ap1);
        adapterProvider.AccessPoints.Add(ap2);

        var viewModel = new MainWindowViewModel(adapterProvider, sessionManager, sessionRepo, exportService, apiServer, settingsRepo);
        viewModel.SelectedAdapter = device;

        // Start session
        sessionManager.StartSession("Test Dynamic Filter", "All", "Lab", device, CaptureSessionMode.Roaming);
        viewModel.IsSessionActive = true;

        async Task WaitForSavedSamplesAsync(int expectedCount)
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < 2000)
            {
                if (sessionRepo.SavedSamples.Count >= expectedCount) return;
                await Task.Delay(10);
            }
        }

        // Act 1: No filters checked -> captures both
        await viewModel.PerformScanAsync();
        await WaitForSavedSamplesAsync(2);
        
        Assert.That(sessionRepo.SavedSamples, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sessionRepo.SavedSamples.Any(s => s.SSID == "SSID_A"), Is.True);
            Assert.That(sessionRepo.SavedSamples.Any(s => s.SSID == "SSID_B"), Is.True);
        });

        // Act 2: SSID View, check SSID_A -> captures only SSID_A
        sessionRepo.SavedSamples.Clear();
        viewModel.IsSsidView = true;
        
        var displayItemA = new AccessPointDisplayItem(ap1, false, true, item => {
            var method = typeof(MainWindowViewModel).GetMethod("OnItemCheckedChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(viewModel, new object[] { item });
        });
        displayItemA.IsChecked = true;

        await viewModel.PerformScanAsync();
        await WaitForSavedSamplesAsync(1);

        Assert.Multiple(() =>
        {
            Assert.That(sessionRepo.SavedSamples, Has.Count.EqualTo(1));
            Assert.That(sessionRepo.SavedSamples[0].SSID, Is.EqualTo("SSID_A"));
        });

        // Act 3: AP View (BSSID), check BSSID of ap2 -> captures only ap2
        sessionRepo.SavedSamples.Clear();
        viewModel.IsSsidView = false; // clears checked keys

        var displayItemB = new AccessPointDisplayItem(ap2, false, false, item => {
            var method = typeof(MainWindowViewModel).GetMethod("OnItemCheckedChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(viewModel, new object[] { item });
        });
        displayItemB.IsChecked = true;

        await viewModel.PerformScanAsync();
        await WaitForSavedSamplesAsync(1);

        Assert.Multiple(() =>
        {
            Assert.That(sessionRepo.SavedSamples, Has.Count.EqualTo(1));
            Assert.That(sessionRepo.SavedSamples[0].BSSID, Is.EqualTo("00:AA:BB:CC:DD:02"));
        });
    }
}
