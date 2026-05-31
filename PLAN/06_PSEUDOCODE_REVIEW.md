# WirelessScanner — Pseudocode & Logic Review

> **Version:** 1.0  
> **Date:** 2026-05-27  
> **DevOps Engine Gate:** `gate.logic.review_passed`  
> **Pre-requisite for:** Iteration 1 (Implementation)

---

## 1. Interface Definitions and Data Contracts

### 1.1 Network Adapter Interface (`INetworkAdapterProvider`)
```csharp
public enum NetworkDeviceType
{
    Wireless_WiFi,
    Wired_Ethernet,
    Other
}

public record NetworkInterfaceInfo(
    Guid Id, 
    string Name, 
    string Description, 
    string PnpDeviceId,
    NetworkDeviceType DeviceType, 
    bool IsConnected
);

public interface INetworkAdapterProvider
{
    // Enumerates all system network interfaces (wired + wireless)
    Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync();

    // Scans and returns BSS list for selected wireless adapter
    Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId);

    // Triggers hardware scan cycle
    Task TriggerHardwareScanAsync(Guid interfaceId);

    // Queries connection status, speed, and IP details for Ethernet adapter
    Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId);

    // Event raised when Windows notifies a PNP hardware change
    event Action HardwareChanged;
    
    // Internal callback invoked when WndProc captures WM_DEVICECHANGE
    void NotifyHardwareChange();
}

public record WiredInterfaceStats(long SpeedBitsPerSecond, string IpAddress, string LinkStatus, double AvgPingLatencyMs);
```

### 1.2 Channel Data Communication contract
```csharp
public record TelemetrySample
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string SurveyPoint { get; init; } = "Default";
    public string BSSID { get; init; } = string.Empty;
    public string SSID { get; init; } = string.Empty;
    public string Band { get; init; } = "2.4 GHz";
    public int Channel { get; init; }
    public int RSSI { get; init; }
    public int SNR { get; init; }
    public int Quality { get; init; }
    public double Jitter { get; init; }
}
```

### 1.3 SQLite DbContext configuration (Infrastructure Layer)
```csharp
public class WlanDbContext : DbContext
{
    public DbSet<DbSessionRecord> Sessions { get; set; }
    public DbSet<DbSampleRecord> Samples { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=wireless_scanner.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSessionRecord>()
            .HasKey(s => s.SessionId);
            
        modelBuilder.Entity<DbSessionRecord>()
            .HasIndex(s => new { s.Name, s.FacilityName, s.StartTime });

        modelBuilder.Entity<DbSampleRecord>()
            .HasKey(s => s.SampleId);

        modelBuilder.Entity<DbSampleRecord>()
            .HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId)
            .OnDelete(DeleteBehavior.Cascade); // Cascade purge child records

        modelBuilder.Entity<DbSampleRecord>()
            .HasIndex(s => s.SessionId);
    }
}

public class DbSessionRecord
{
    public Guid SessionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public string InterfaceName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Mode { get; set; }
    public int Status { get; set; }
}

public class DbSampleRecord
{
    public int SampleId { get; set; }
    public Guid SessionId { get; set; }
    public DbSessionRecord Session { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public string SurveyPoint { get; set; } = string.Empty;
    public string BSSID { get; set; } = string.Empty;
    public string SSID { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public int Channel { get; set; }
    public int RSSI { get; set; }
    public int SNR { get; set; }
    public int Quality { get; set; }
    public double Jitter { get; set; }
}
```

---

## 2. Core Logical Pseudocode

### 2.1 Background Scan Worker Loop
This worker runs in the Infrastructure layer on a dedicated thread, publishing to an application-wide Channel.

```csharp
public class ScanWorkerService
{
    private readonly INetworkAdapterProvider _adapterProvider;
    private readonly ChannelWriter<TelemetrySample> _channelWriter;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;
    private CancellationTokenSource _cts;

    public ScanWorkerService(INetworkAdapterProvider provider, ChannelWriter<TelemetrySample> writer, ISessionManager sessionManager, ILogger logger)
    {
        _adapterProvider = provider;
        _channelWriter = writer;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public void StartScanning(NetworkInterfaceInfo device, TimeSpan interval)
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ScanLoopAsync(device, interval, _cts.Token));
    }

    private async Task ScanLoopAsync(NetworkInterfaceInfo device, TimeSpan interval, CancellationToken token)
    {
        _logger.LogInfo($"Starting capture loop for adapter {device.Name} ({device.DeviceType}) at interval {interval.TotalSeconds}s");
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                var currentPoint = _sessionManager.ActiveSurveyPoint;

                if (device.DeviceType == NetworkDeviceType.Wired_Ethernet)
                {
                    // Wired Ethernet Fallback: Poll interface speed and ping latency
                    var stats = await _adapterProvider.GetWiredStatsAsync(device.Id);
                    
                    var sample = new TelemetrySample
                    {
                        BSSID = "Wired Link",
                        SSID = $"Ethernet ({stats.LinkStatus})",
                        Band = "Wired",
                        Channel = 0,
                        RSSI = stats.LinkStatus == "Up" ? 0 : -100, // RSSI representation
                        SNR = 0,
                        Quality = stats.LinkStatus == "Up" ? 100 : 0,
                        SurveyPoint = currentPoint,
                        Jitter = stats.AvgPingLatencyMs // Map latency metrics to Jitter field
                    };

                    await _channelWriter.WriteAsync(sample, token);
                }
                else
                {
                    // Wireless scan update trigger (requires admin token checked at startup)
                    await _adapterProvider.TriggerHardwareScanAsync(device.Id);
                    await Task.Delay(250, token); 

                    // Fetch fresh access points list
                    var apList = await _adapterProvider.GetAccessPointsAsync(device.Id);
                    
                    foreach (var ap in apList)
                    {
                        var sample = new TelemetrySample
                        {
                            BSSID = ap.BSSID,
                            SSID = ap.SSID,
                            Band = ap.Band,
                            Channel = ap.Channel,
                            RSSI = ap.RSSI,
                            SNR = ap.SNR,
                            Quality = ap.Quality,
                            SurveyPoint = currentPoint,
                            Jitter = _sessionManager.CalculateJitterFor(ap.BSSID, ap.RSSI)
                        };
                        
                        await _channelWriter.WriteAsync(sample, token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error encountered in background scan loop", ex);
                _sessionManager.HandleScanError(ex);
            }

            await Task.Delay(interval, token);
        }
        
        _logger.LogInfo("Scan loop stopped cleanly.");
    }

    public void StopScanning()
    {
        _cts?.Cancel();
    }
}
```

### 2.2 Thread-Safe Channel Consumer (Application Layer)
This consumer digests incoming samples from the background thread and updates the thread-safe Presentation ViewModel collections via the Dispatcher.

```csharp
public class TelemetryConsumer
{
    private readonly ChannelReader<TelemetrySample> _channelReader;
    private readonly IMainViewModel _mainViewModel;
    private readonly ILogger _logger;

    public TelemetryConsumer(ChannelReader<TelemetrySample> reader, IMainViewModel viewModel, ILogger logger)
    {
        _channelReader = reader;
        _mainViewModel = viewModel;
        _logger = logger;
    }

    public async Task StartConsumingAsync(CancellationToken token)
    {
        while (await _channelReader.WaitToReadAsync(token))
        {
            while (_channelReader.TryRead(out var sample))
            {
                // Process and route sample to current capture session state
                _mainViewModel.ProcessNewSample(sample);

                // Safely marshal state updates to UI thread
                Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    _mainViewModel.UpdateUiViewAndCharts(sample);
                });
            }
        }
    }
}
```

### 2.3 Startup Administrator Elevation Enforcement
This logic runs at the entry point of the application (`App.xaml.cs` or `Program.cs`) before the main window is created.

```csharp
public static class SecurityHelper
{
    public static bool IsRunningAsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void CheckElevationAndRun(Action runAction)
    {
        if (IsRunningAsAdministrator())
        {
            runAction();
        }
        else
        {
            MessageBox.Show(
                "Administrator privileges are required to access raw Wi-Fi telemetry via the Windows WLAN API.\n\n" +
                "Please restart the application by right-clicking the executable and choosing 'Run as Administrator'.",
                "Privilege Elevation Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            
            Application.Current.Shutdown();
        }
    }
}
```

### 2.4 Historical Query & Database Purge Service
This service in the Application layer coordinates database retrieval and deletes.

```csharp
public class DatabaseService
{
    private readonly WlanDbContext _dbContext;

    public DatabaseService(WlanDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Fetches sessions within date bounds
    public async Task<List<DbSessionRecord>> GetSessionsByDateAsync(DateTime start, DateTime end)
    {
        return await _dbContext.Sessions
            .Where(s => s.StartTime >= start && s.StartTime <= end)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
    }

    // Fetches unique SSIDs and APs seen in selected sessions
    public async Task<(List<string> SSIDs, List<string> APs)> GetUniqueTargetsForSessionsAsync(List<Guid> sessionIds)
    {
        var samples = await _dbContext.Samples
            .Where(s => sessionIds.Contains(s.SessionId))
            .Select(s => new { s.SSID, s.BSSID })
            .Distinct()
            .ToListAsync();

        var ssids = samples.Select(s => s.SSID).Distinct().ToList();
        var aps = samples.Select(s => s.BSSID).Distinct().ToList();
        
        return (ssids, aps);
    }

    // Age-based purging
    public async Task PurgeSessionsByAgeAsync(TimeSpan ageLimit)
    {
        var thresholdDate = DateTime.UtcNow - ageLimit;
        var targets = await _dbContext.Sessions
            .Where(s => s.StartTime < thresholdDate)
            .ToListAsync();

        _dbContext.Sessions.RemoveRange(targets);
        await _dbContext.SaveChangesAsync();
        
        // Vacuum database to recover disk space immediately
        await _dbContext.Database.ExecuteSqlRawAsync("VACUUM;");
    }

    // Granular purge
    public async Task PurgeSessionAsync(Guid sessionId)
    {
        var target = await _dbContext.Sessions.FindAsync(sessionId);
        if (target != null)
        {
            _dbContext.Sessions.Remove(target);
            await _dbContext.SaveChangesAsync();
            await _dbContext.Database.ExecuteSqlRawAsync("VACUUM;");
        }
    }
}
```

---

## 3. Edge Case Matrix

| Edge Case | Impact | Mitigation Strategy | Code Action |
|-----------|--------|---------------------|-------------|
| **No adapters found** | App UI is blank, cannot start scan. | Show friendly placeholder overlay: "No Wi-Fi Adapters Detected". Disables "Start" button. | If list of interfaces is empty, trigger UI overlay state and log warning. |
| **Adapter lost mid-session** | Scan loop throws P/Invoke exceptions. UI freezes if unhandled. | Capture loop catches exception, pauses session, attempts to re-enumerate adapter, displays banner. | Catch specific Win32 errors in `ScanLoopAsync`, transition state to `PausedAdapterLost`, alert user. |
| **Driver limits polling rate** | Hardware fails to scan faster than 10s. | Limit polling tick slider in UI to $\ge$ 2 seconds. Cache and return last known BSS list if driver throttles. | Check timestamp of retrieved AP list. If identical to previous tick, log debug notice, do not re-insert duplicate timestamps. |
| **150+ APs in dense area** | Chart memory grows exponentially, UI lags. | Store raw samples in session buffer on disk or array. Limit UI charts to top 10 strongest or explicitly tracked targets. | Use a rolling window of 60 seconds for visual line charts. Render full APs in standard sorted list without layout updates. |
| **Invalid characters in SSID** | Serialization or UI crashes. | Clean/escape SSID string. If null or contains invalid binary characters, represent as `<Hidden SSID>` or hex string. | Perform UTF-8 sanity check on raw byte array before converting SSID to string. |

---

## 4. Logical Error Paths

### 4.1 Native API P/Invoke Failure Path
```
[Application Startup]
        │
        ├──► Is Elevated? (SecurityHelper.IsRunningAsAdministrator)
        │         ├──► Yes ──► Launch Main Windows App ──► Open WlanHandle
        │         └──► No  ──► Prompt User Warning dialog ──► Exit cleanly
        │
[WlanGetNetworkBssList] 
        │
        ├──► Returns ERROR_SUCCESS (0x0) ──► Parse list cleanly
        │
        └──► Returns ERROR_INVALID_PARAMETER / Device Disconnected
                  └──► Log critical error ──► Trigger IWlanAdapter.Reset() ──► Pause Session ──► Prompt User
```

### 4.2 PDF Export I/O Locked Path
```
[User triggers PDF export] 
        │
        ├──► Target file write successful ──► Display "Export Complete" popup
        │
        └──► Target file locked (e.g. open in Acrobat)
                  └──► Catch IOException 
                  └──► Generate alternative filename: "Report_ConferenceRoom_A_1.pdf"
                  └──► Attempt write ──► If succeeds, notify user of new name
                  └──► If fails ──► Display modal requesting file close + path picker
```

---

## 5. System Test Plan

### 5.1 Unit Test Coverage Checklist
- **[UT-01] RSSI to Quality Formula**: Verify `Quality = 2 * (RSSI + 100)` clamped to `[0, 100]` operates correctly for inputs like -30dBm (100%), -50dBm (100%), -85dBm (30%), -100dBm (0%).
- **[UT-02] Jitter Algorithm Accuracy**: Verify Jitter calculation logic with a predetermined sequence: `[-60, -62, -59, -60]` yields expected standard mathematical variance values.
- **[UT-03] Serializer Integrity**: Validate JSON and CSV serializers with mock session data containing empty, special characters, and null SSID/Vendor fields.
- **[UT-04] Dependency Direction Compiler Check**: Domain assemblies must not import Presentation assemblies or reference UI modules.

### 5.2 Integration Harness Tests
- **[IT-01] Mock Walk Simulator**: Launch application utilizing `MockWlanAdapter` running on a thread that feeds variable sinusoidal signal data. Run for 5 minutes. Verify session timer, average calculations, and charts operate without memory leaks or race conditions.
- **[IT-02] PDF Generation Integration**: Execute `ExportService.GeneratePdf` with 200 mock samples. Verify file generates, is valid PDF, and charts are visual layout matches.

---

## 6. Architecture & Enterprise Readiness Audit

- **Separation of Concerns**: Verified. Domain contains no dependency on WPF or QuestPDF. Infrastructure manages P/Invoke and file writes. Application holds state.
- **Security Boundaries**: No inbound ports opened. Local filesystem operations are sandboxed to user-selected export paths and the application startup directory (`logs/` and `appsettings.json`).
- **Telemetry Limits**: Maximum memory footprint of `CaptureSession` memory buffer restricted by limiting session samples to $100,000$ points (approx. 30 hours of continuous scanning of 50 APs at 2-second intervals). Displays error if size limit reached.
