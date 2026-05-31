# WirelessScanner — Architecture & Technology Decisions

> **Version:** 1.0  
> **Date:** 2026-05-27  
> **DevOps Engine Domains Applied:** `software_architecture`, `language_tooling`, `coding_best_practices`, `security_reliability`, `observability_incident_response`

---

## 1. Architecture Decision: C# (.NET 8+) with WPF/WinUI

### Decision
**C# with .NET 8 (or 9) and WPF** is the recommended path for the native executable.

### Rationale (DevOps Engine Analysis)

| Factor | C# (.NET 8 + WPF) | C++ (Win32/DX) |
|--------|-------------------|----------------|
| **Windows WLAN API access** | ✅ Direct P/Invoke to Wlanapi.dll — clean, well-documented | ✅ Native access, but manual memory management |
| **UI framework** | ✅ WPF/WinUI: XAML-based, DPI-aware, data-binding, GPU-accelerated | ⚠️ Requires Win32 + DirectX or third-party (ImGui, Qt) |
| **Charting/visualization** | ✅ LiveCharts2, OxyPlot, ScottPlot — production-grade, GPU-accelerated | ⚠️ Limited native options; ImGui charting is basic |
| **PDF generation** | ✅ QuestPDF, iTextSharp, PdfSharp — rich layout + embedded graphics | ⚠️ libharu/PoDoFo work but require more boilerplate |
| **Single-file exe** | ✅ `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | ✅ Naturally produces single binary |
| **Development velocity** | ✅ High — strong tooling, NuGet ecosystem, async/await | ⚠️ Slower iteration, manual resource management |
| **Maintenance / modularity** | ✅ Namespace/project boundaries, DI, testability patterns | ⚠️ Requires disciplined header/module structure |
| **Thread safety** | ✅ `Task`, `Channel<T>`, `ConcurrentQueue` built-in | ⚠️ Manual threading, mutexes, message passing |
| **DevOps Engine coverage** | ✅ PowerShell (10,225 mentions), .NET ecosystem well-covered | ⚠️ No dedicated C++ domain briefs in engine |

### DevOps Engine Standards Applied
Per `03_operating_model.json` → `engineering_standards`:
- **Small cohesive modules with explicit ownership** → C# projects map cleanly to module boundaries
- **Domain logic separate from adapters, IO, UI, persistence** → Clean Architecture pattern via dependency injection
- **Dependencies directional and intentional** → Project references enforce direction
- **Errors explicit, actionable, observable** → Exception hierarchy + structured logging
- **Test seams for business logic** → Interface-based DI enables unit testing

---

## 2. System Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        WirelessScanner.exe                       │
│                     (Self-Contained .NET 8)                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │                    PRESENTATION LAYER                     │   │
│  │          WPF / WinUI — XAML + MVVM ViewModels            │   │
│  │                                                           │   │
│  │  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌─────────────┐  │   │
│  │  │ Scanner │ │ Charts   │ │ AP Detail│ │ Export      │  │   │
│  │  │ View    │ │ View     │ │ View     │ │ View        │  │   │
│  │  └─────────┘ └──────────┘ └──────────┘ └─────────────┘  │   │
│  └──────────────────────┬───────────────────────────────────┘   │
│                          │ Data Binding / Commands               │
│  ┌──────────────────────┴───────────────────────────────────┐   │
│  │                   APPLICATION LAYER                       │   │
│  │              ViewModels + Services + State                │   │
│  │                                                           │   │
│  │  ┌─────────────┐ ┌────────────┐ ┌────────────────────┐   │   │
│  │  │ ScanService │ │ Session    │ │ ExportService      │   │   │
│  │  │ (orchestr.) │ │ Manager    │ │ (CSV/JSON/PDF)     │   │   │
│  │  └──────┬──────┘ └────────────┘ └────────────────────┘   │   │
│  └─────────┼────────────────────────────────────────────────┘   │
│             │ Interface contracts                                 │
│  ┌─────────┴────────────────────────────────────────────────┐   │
│  │                    DOMAIN LAYER                           │   │
│  │           Models + Business Logic + Contracts             │   │
│  │                                                           │   │
│  │  ┌──────────┐ ┌───────────┐ ┌──────────┐ ┌───────────┐  │   │
│  │  │ AP Model │ │ SSID Model│ │ Session  │ │ Telemetry │  │   │
│  │  │          │ │           │ │ Model    │ │ Sample    │  │   │
│  │  └──────────┘ └───────────┘ └──────────┘ └───────────┘  │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │                 INFRASTRUCTURE LAYER                      │   │
│  │             Adapters + Platform Integration               │   │
│  │                                                           │   │
│  │  ┌─────────────┐ ┌──────────┐ ┌────────────────────┐    │   │
│  │  │ WlanAdapter │ │ PDF      │ │ File I/O           │    │   │
│  │  │ (Wlanapi)   │ │ Renderer │ │ (Export Writer)     │    │   │
│  │  └──────┬──────┘ └──────────┘ └────────────────────┘    │   │
│  └─────────┼────────────────────────────────────────────────┘   │
│             │                                                    │
│  ╔══════════╧════════════════════════════════════════════════╗   │
│  ║              WINDOWS WLAN API (Wlanapi.dll)               ║   │
│  ║         Native Wi-Fi → P/Invoke → Managed Types           ║   │
│  ╚═══════════════════════════════════════════════════════════╝   │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Module Boundaries

Per DevOps Engine `engineering_standards.module_boundaries`:

| Module (Project) | Responsibility | Dependencies |
|---|---|---|
| `WirelessScanner.Domain` | Models, enums, interfaces, business logic contracts | None (core) |
| `WirelessScanner.Application` | Services, session management, scan orchestration, export logic | Domain |
| `WirelessScanner.Infrastructure` | WLAN API adapter (P/Invoke), file I/O, PDF rendering | Domain, Application |
| `WirelessScanner.Presentation` | WPF/WinUI views, XAML, ViewModels (MVVM) | Domain, Application |
| `WirelessScanner.Tests` | Unit + integration tests | All above |

### Dependency Direction
```
Presentation → Application → Domain ← Infrastructure
```
Infrastructure implements interfaces defined in Domain (Dependency Inversion Principle per Clean Architecture).

---

## 4. Data Models

### AccessPoint
```
AccessPoint {
    BSSID: string              // MAC address (e.g., "8C:5A:25:04:91:B2")
    SSID: string               // Network name
    Hostname: string?          // AP hostname if resolvable
    Band: enum                 // Band_2_4GHz, Band_5GHz, Band_6GHz
    Channel: int               // Operating channel number
    ChannelWidth: int?         // 20/40/80/160 MHz
    RSSI: int                  // Signal strength in dBm
    NoiseFloor: int?           // Noise level in dBm
    SNR: int                   // Signal-to-noise ratio
    Quality: int               // 0-100 percentage
    SecurityType: enum         // Open, WEP, WPA, WPA2, WPA3
    NetworkType: enum          // Infrastructure, AdHoc, Mesh
    VendorOUI: string?         // Manufacturer from MAC prefix
    SupportedRates: string[]?  // Advertised data rates
    LastSeen: DateTime         // Timestamp of last detection
}
```

### TelemetrySample
```
TelemetrySample {
    Timestamp: DateTime
    SurveyPoint: string        // User-labeled location
    BSSID: string
    SSID: string
    Band: enum
    Channel: int
    RSSI: int
    SNR: int
    Quality: int
    Jitter: double             // Signal variance over short window
    NoiseFloor: int?
}
```

### CaptureSession
```
CaptureSession {
    SessionId: Guid
    Name: string               // User-assigned searchable name
    Scope: string              // Searchable scope (e.g., "All SSIDs", "Specific AP")
    FacilityName: string       // Searchable location/facility name
    InterfaceId: string
    InterfaceName: string
    StartTime: DateTime
    EndTime: DateTime?
    Mode: enum                 // Roaming, Stationary
    ScanMode: enum             // SSID, AP
    TrackedTarget: string?     // SSID or BSSID being tracked
    SurveyPoints: string[]
    Samples: TelemetrySample[]
    Status: enum               // Active, Paused, Completed
}
```

### NetworkInterfaceDevice
```
NetworkDeviceType {
    Wireless_WiFi,
    Wired_Ethernet,
    Other
}

NetworkInterfaceDevice {
    Id: Guid
    Name: string               // E.g., "Intel Wi-Fi 6E AX211" or "Realtek PCIe GbE"
    PnpDeviceId: string        // Plug-and-Play unique ID
    DeviceType: NetworkDeviceType
    IsConnected: bool
}
```

---

## 5. Windows WLAN API Integration

### API Surface (Wlanapi.dll P/Invoke)

| Function | Purpose |
|----------|---------|
| `WlanOpenHandle` | Open client handle to WLAN service |
| `WlanEnumInterfaces` | List available wireless adapters |
| `WlanGetAvailableNetworkList` | Get visible SSIDs with metadata |
| `WlanGetNetworkBssList` | Get individual AP/BSSID list with RSSI, channel, rates |
| `WlanScan` | Trigger a fresh scan (may require elevation) |
| `WlanRegisterNotification` | Register for scan-complete callbacks |
| `WlanCloseHandle` | Release client handle |

### Key Implementation Notes
- `WlanGetNetworkBssList` is the primary data source — returns per-BSSID telemetry
- RSSI values from this API are in dBm (typically -30 to -100)
- SNR must be calculated: `SNR = RSSI - NoiseFloor` (noise floor may need estimation if not provided)
- Channel width and supported rates come from the BSS Information Element (IE) data
- Vendor OUI resolved from first 3 octets of BSSID against an IEEE OUI database

---

## 6. UI Framework Decision: WPF

### Rationale
- **DPI-aware**: Native high-DPI support via `PerMonitorV2` awareness
- **MVVM**: Clean separation of view and logic — aligns with DevOps Engine's "domain logic separate from UI"
- **Data Binding**: Real-time telemetry updates via `ObservableCollection` and `INotifyPropertyChanged`
- **GPU Rendering**: WPF uses DirectX for rendering — handles complex charts smoothly
- **Mature Charting**: LiveCharts2 or ScottPlot integrate natively with WPF
- **Single-file publishing**: .NET 8 AOT or self-contained single-file works with WPF

### Responsive/Reflexive Design Strategy
- Use `Grid` with `*` and `Auto` sizing for proportional layouts
- `ViewBox` wrapping for chart panels that scale with container
- `VisualStateManager` for orientation-aware layout switching
- `MediaQuery`-equivalent via `SizeChanged` event handlers for breakpoints
- Vector icons (SVG/XAML paths) for resolution-independent graphics

### High-Contrast Dark Theme Specification
- **Backgrounds**: Deep obsidian (`#0F0F12` main background, `#16161A` card background)
- **Accents**: Neon Cyan (`#00F2FE` for primary highlights/charts), Electric Purple (`#4FACFE` secondary accent)
- **Typography**: Crisp white/light-gray text (`#F3F4F6` for content, `#9CA3AF` for labels/captions)
- **Signal Indicators**: High-intensity Green (`#10B981` strong), Yellow (`#F59E0B` moderate), Red (`#EF4444` weak) for optimal contrast on dark layout grids
- **Charts**: Customized gridlines (`#2D2D34` opacity 0.5) with glowing solid series fills (SkiaSharp shadow effect)

---

## 7. Charting Library: LiveCharts2

| Chart Type | Use Case |
|------------|----------|
| `LineSeries` | Signal strength timeline (RSSI over time per AP) |
| `ColumnSeries` | Band balance (2.4 GHz vs 5 GHz quality comparison) |
| `PolarLineSeries` | Mesh shape radar (AP quality distribution) |
| `HeatSeries` | Channel utilization heatmap (future) |
| `ScatterSeries` | Signal jitter scatter plot |

LiveCharts2 supports:
- Real-time data push via `ObservableCollection`
- GPU-accelerated rendering via SkiaSharp
- Automatic axis scaling
- Tooltip/crosshair for inspection
- Bitmap export for PDF embedding

---

## 8. Database Architecture (SQLite)

The application utilizes an in-process **SQLite** database (`wireless_scanner.db`) located in the application directory to persist sessions and telemetry data. SQLite operates serverless, zero-configuration, and is fully managed via **Microsoft.Data.Sqlite** or Entity Framework Core.

### Entity Relationships & Schema Diagram

```
┌─────────────────────────────────┐
│        CaptureSessions          │
├─────────────────────────────────┤
│ PK  SessionId    TEXT (Guid)    │
│     Name         TEXT           │◄─── Searchable Index
│     Scope        TEXT           │◄─── Searchable Index
│     FacilityName TEXT           │◄─── Searchable Index
│     InterfaceName TEXT          │
│     StartTime    TEXT (DateTime)│
│     EndTime      TEXT (DateTime)│
│     Mode         INTEGER        │
│     Status       INTEGER        │
└────────────────┬────────────────┘
                 │ 1
                 │
                 │ 1..N
┌────────────────▼────────────────┐
│        TelemetrySamples         │
├─────────────────────────────────┤
│ PK  SampleId     INTEGER (Auto) │
│ FK  SessionId    TEXT (Guid)    │
│     Timestamp    TEXT (DateTime)│
│     SurveyPoint  TEXT           │
│     BSSID        TEXT           │
│     SSID         TEXT           │
│     Band         TEXT           │
│     Channel      INTEGER        │
│     RSSI         INTEGER        │
│     SNR          INTEGER        │
│     Quality      INTEGER        │
│     Jitter       REAL           │
└─────────────────────────────────┘
```

### SQLite Optimization Patterns
- **WAL Mode**: Enable Write-Ahead Logging (`PRAGMA journal_mode=WAL`) to support simultaneous background thread inserts and UI thread reading.
- **Indexes**: Explicit B-Tree indexes on `TelemetrySamples(SessionId, BSSID)` and `CaptureSessions(Name, FacilityName, StartTime)` to optimize historical comparison queries.
- **Parametric Purging**: Deletion utilizes age boundaries:
  ```sql
  DELETE FROM CaptureSessions WHERE StartTime < date('now', '-7 days'); -- 1 Week
  DELETE FROM CaptureSessions WHERE StartTime < date('now', '-30 days'); -- 1 Month
  DELETE FROM CaptureSessions WHERE StartTime < date('now', '-90 days'); -- 1 Quarter
  ```
  Deleting a session cascade-deletes all its child `TelemetrySamples` via Foreign Key rules.

---

## 9. Export Architecture

### CSV Export
- Standard RFC 4180 compliant CSV
- One row per telemetry sample
- All fields from `TelemetrySample` + session metadata header

### JSON Export
```json
{
  "session": { /* CaptureSession metadata including Name, Scope, FacilityName */ },
  "interface": { /* adapter info */ },
  "samples": [ /* TelemetrySample[] */ ],
  "summary": {
    "duration_seconds": 300,
    "total_samples": 1500,
    "unique_aps": 12,
    "unique_ssids": 4,
    "avg_rssi": -62,
    "weakest_rssi": -87,
    "strongest_rssi": -41
  }
}
```

### PDF Export
- Generated via **QuestPDF** (C# layout engine, no external dependencies)
- Cover page supports user inputs: Client Name, Surveyor Name, Description.
- Page structure:
  1. Title cover sheet with session metadata and client/surveyor details
  2. Executive summary with key metrics
  3. Signal strength timeline chart (bitmap from LiveCharts2)
  4. Band balance chart
  5. Mesh shape radar chart
  6. AP detail table (all APs with full telemetry)
  7. Raw sample data table (paginated)
  8. Statistical summary (min/max/mean/stddev per AP)

---

## 10. Threading Model

```
┌──────────────────────────┐
│     UI Thread (STA)      │ ← WPF Dispatcher, ViewModels, rendering
│     - Chart updates      │
│     - AP list updates    │
│     - User interactions  │
└──────────┬───────────────┘
           │ Dispatcher.Invoke
┌──────────┴───────────────┐
│   Scan Worker Thread     │ ← Background thread with Timer
│   - WlanScan trigger     │
│   - WlanGetNetworkBssList│
│   - Sample creation      │
│   - Push to Channel<T>   │
└──────────┬───────────────┘
           │ Channel<TelemetrySample>
┌──────────┴───────────────┐
│   Data Processing Thread │ ← Consumes samples, computes statistics
│   - Jitter calculation   │
│   - Rolling averages     │
│   - History buffer mgmt  │
└──────────────────────────┘
```

Per DevOps Engine `coding_best_practices` → thread safety:
- Use `System.Threading.Channels.Channel<T>` for producer/consumer between scan and UI
- Use `ConcurrentDictionary` for AP state tracking
- Use `Dispatcher.InvokeAsync` for UI updates from background threads
- No `lock` on hot paths — lock-free data structures preferred

---

## 10. Security Considerations

Per DevOps Engine `security_reliability` domain:

| Concern | Mitigation |
|---------|------------|
| Scanning captures network data | All data stays local — no network transmission |
| WLAN API handle management | Proper `WlanCloseHandle` in `IDisposable` pattern |
| Exported files may contain MACs | User responsibility — document in export UI |
| Elevation for `WlanScan` trigger | Graceful degradation: use passive scan if not elevated |
| OUI database bundling | Ship static OUI lookup table — no runtime downloads |
