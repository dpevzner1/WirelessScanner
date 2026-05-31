# WirelessScanner — Acceptance Criteria

> **Version:** 1.0  
> **Date:** 2026-05-27  
> **DevOps Engine Gate:** `gate.acceptance.criteria_complete`  
> **Source Requirements:** [01_PROJECT_SCOPE.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/01_PROJECT_SCOPE.md)

---

## 1. Functional Acceptance Criteria (FAC)

### FAC-01: Network Adapter & Device Manager Integration
*Refers to FR-01*
- **Criteria 1.1**: The application detects all active, enabled Wi-Fi network adapters and wired Ethernet adapters on the host machine.
- **Criteria 1.2**: Adapters are displayed by user-friendly names in a dropdown selection box, categorized by device type (Wireless vs. Wired).
- **Criteria 1.3**: When an external USB Wi-Fi adapter is plugged in or unplugged, the dropdown list updates dynamically in real-time within 2 seconds of device arrival/removal.
- **Criteria 1.4**: Selecting an Ethernet adapter disables Wi-Fi SSID/AP scanning and loads the Wired Fallback UI showing interface speed, IP address, and ping latency metrics.
- **Validation Method**: 
  - Run on test host with one built-in Wi-Fi adapter. Plug in an external USB Wi-Fi card. Verify it appears automatically in the dropdown. Unplug it, verify it disappears automatically.
  - Select a wired Ethernet adapter; verify the Wi-Fi scan loop pauses, and wired interface statistics load cleanly.

### FAC-02: SSID / AP Scanning Modes
*Refers to FR-02*
- **Criteria 2.1**: The user toggle (slider/segmented control) allows switching between SSID Mode and AP Mode.
- **Criteria 2.2**: **SSID Mode** aggregates all APs sharing the same SSID string. The interface displays unique SSIDs, expanding a tree view or table row reveals the BSSIDs (MACs) broadcasting that SSID.
- **Criteria 2.3**: **AP Mode** lists each physical access point individually by BSSID.
- **Criteria 2.4**: Selecting an SSID or AP and clicking "Track" pins it to the tracking UI panel, showing real-time signal level metrics.
- **Validation Method**: Switch modes on active scans, verify UI list reorganizes without data loss, and verify tracked target is highlighted and plotted.

### FAC-03: Signal Telemetry Completeness
*Refers to FR-03*
- **Criteria 3.1**: The application extracts and displays: SSID, BSSID (MAC format `XX:XX:XX:XX:XX:XX`), Band (2.4 GHz or 5 GHz), Channel (1–13 for 2.4 GHz, 36–165 for 5 GHz), RSSI (dBm), Quality (0-100%), Security Type (e.g., WPA2-Personal), Network Type (Infrastructure/Mesh), and Vendor OUI.
- **Criteria 3.2**: Hostname, SNR, and Channel Width are resolved and displayed if provided by the adapter; otherwise, they display "N/A" or estimated metrics gracefully.
- **Validation Method**: Inspect the grid views in AP Mode and verify all fields are populated or fallback gracefully.

### FAC-04: Dual-Band Filtering
*Refers to FR-04*
- **Criteria 4.1**: User-selectable buttons/checkboxes filter the active AP/SSID grid by: "All Bands", "2.4 GHz Only", or "5 GHz Only".
- **Criteria 4.2**: Filtering updates the list within 100ms and does not stop background capture.
- **Validation Method**: Select "2.4 GHz Only" and verify no 5 GHz APs (channels 36+) are visible. Switch to "5 GHz Only" and verify no 2.4 GHz APs (channels 1-13) are visible.

### FAC-05: Capture Session Management
*Refers to FR-05*
- **Criteria 5.1**: Start, Stop, and Pause buttons correctly transition the session state.
- **Criteria 5.2**: Active session duration is displayed on a clock/timer updating every 1 second.
- **Criteria 5.3**: User can input custom searchable metadata fields in the interface: session NAME, SCOPE, and FACILITY NAME. These must save to the SQLite database and be index-searchable.
- **Criteria 5.4**: The UI displays the current SQLite database file size in MB.
- **Validation Method**: Start a session, let it run for 10 seconds, pause it, verify the duration timer stops, input custom tags (e.g. NAME: "Morning Scan", SCOPE: "IoT Mesh", FACILITY: "Building C"), stop session, and verify records are saved with these values in SQLite.

### FAC-06: Roaming Survey Mode
*Refers to FR-06*
- **Criteria 6.1**: During roaming, all visible APs are polled and recorded in the session buffer.
- **Criteria 6.2**: The application calculates "Ubiquity" (number of unique APs seen) and "Elasticity" (signal strength distribution/range) per survey point.
- **Validation Method**: Run a survey session while moving. Verify the session buffer accumulates samples across multiple distinct BSSIDs, and exports correctly.

### FAC-07: Stationary Performance Monitoring
*Refers to FR-07*
- **Criteria 7.1**: The application displays stationary metrics for a tracked AP: Minimum, Maximum, Mean, Standard Deviation, and Jitter.
- **Criteria 7.2**: **Jitter** is calculated as the average absolute difference between consecutive RSSI samples: $\text{Jitter} = \frac{1}{N-1}\sum |RSSI_t - RSSI_{t-1}|$.
- **Validation Method**: Select a single AP, start stationary logging for 30 seconds. Verify the statistics dashboard computes values in real-time.

### FAC-08: Real-Time Telemetry Visualization
*Refers to FR-08*
- **Criteria 8.1**: **Signal Strength Timeline**: Live line chart showing RSSI vs. Time (up to last 60 seconds) for the tracked AP.
- **Criteria 8.2**: **Band Balance**: A bar chart displaying the count of 2.4 GHz vs 5 GHz APs visible.
- **Criteria 8.3**: **Mesh Shape Radar**: Radar chart mapping signal strength of the top 6 APs by direction/identity.
- **Criteria 8.4**: **Health Dashboard**: Real-time display of Average Quality, Active AP Count, Weakest Link, and Best Signal.
- **Validation Method**: Verify charts update concurrently with every scan tick (≤ 2-second intervals) without UI lag.

### FAC-09: Export — CSV
*Refers to FR-09*
- **Criteria 9.1**: Output is an RFC-4180 compliant CSV file.
- **Criteria 9.2**: Schema: `Timestamp`, `SurveyPoint`, `BSSID`, `SSID`, `Band`, `Channel`, `RSSI`, `Quality`, `SNR`, `Jitter`, `SecurityType`.
- **Validation Method**: Export a session and parse the output using an automated CSV parser to verify schema alignment and row integrity.

### FAC-10: Export — JSON
*Refers to FR-10*
- **Criteria 10.1**: Exported JSON conforms to the schema defined in [02_ARCHITECTURE.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/02_ARCHITECTURE.md#L243-L258).
- **Validation Method**: Export a session, run it through a JSON validator to confirm formatting correctness.

### FAC-11: Export — PDF Report
*Refers to FR-11*
- **Criteria 11.1**: The PDF contains a formatted cover page (supporting Surveyor Name, Client Name, and Notes), summary widgets, and rendered charts (bitmap exports).
- **Criteria 11.2**: AP detail table and full sample logs are displayed in paginated grids with alternating row backgrounds.
- **Validation Method**: Generate a PDF for a 2-minute capture session, open in Adobe Acrobat / Edge, and verify charts are clear and columns are aligned.

### FAC-12: Historical Diagnostics & Comparison Dashboard
- **Criteria 12.1**: The "Historical Comparison" tab is structured in a 3-column layout:
  - **Left Column**: Two calendar date-pickers (Start Date, End Date) filter the list of completed sessions. The session list displays checkboxes next to each run.
  - **Middle Column**: Dynamically lists all unique SSIDs and APs/MACs discovered *within* the selected sessions, providing checkboxes for granular target filtering.
  - **Right Column**: Displays LiveCharts2 line/bar series plotting signal strength, quality, and jitter over time comparing the selected sessions with color-coded markers.
- **Validation Method**: Select two separate dates, check one session from each, filter by a shared SSID, and verify the line chart overlays both runs on a single relative timeline.

### FAC-13: Database Purging & Retention
- **Criteria 13.1**: The Settings page exposes a "Database Size" metric in megabytes.
- **Criteria 13.2**: A DB Purge dialog offers age-based deletion options: "Older than 1 Week", "Older than 1 Month", "Older than 1 Quarter", "Older than 1 Year", "Total Purge (All Data)", and granular selective check-purging.
- **Validation Method**: Click "Older than 1 Month" on a DB containing old test sessions, confirm size reduction, and verify old sessions are completely deleted.

---

## 2. Non-Functional Acceptance Criteria (NFAC)

### NFAC-01: Standalone Native Executable
*Refers to NFR-01*
- **Criteria**: The final build must output a single `.exe` file containing all dependencies (WPF, SkiaSharp, QuestPDF, LiveCharts2, SQLite library). It must run on a clean Windows 10/11 system without requiring installation of a separate .NET Runtime (self-contained publish targeting `win-x64`). The application is styled in a high-contrast dark theme by default.
- **Validation Method**: Execute `dotnet publish` with single-file arguments. Copy output to a clean VM without .NET 8 SDK and launch. Confirm default dark theme loads.

### NFAC-02: Reflexive & Responsive UI
*Refers to NFR-02*
- **Criteria**: The UI must scale dynamically when resized. Panel grid ratios must adjust to preserve visualization legibility between 1280x720 and 3840x2160 resolutions. Charts must resize dynamically.
- **Validation Method**: Manually resize window, maximize on a 4K screen, and verify no controls overlap or go off-screen.

### NFAC-03: Performance and Concurrency
*Refers to NFR-03*
- **Criteria 3.1**: The scan loop operates on a background thread. Blocking operations (P/Invoke `WlanGetNetworkBssList`) must not cause the UI thread to drop below 60 frames per second during user interaction.
- **Criteria 3.2**: RAM usage must not exceed 250 MB after 1 hour of continuous scanning at 2-second intervals (approx. 1800 scans).
- **Validation Method**: Run diagnostic profiling during active scan. Check memory footprint and monitor thread blocking.

### NFAC-04: Portability
*Refers to NFR-04*
- **Criteria**: The application runs successfully from a standard USB flash drive. It requires zero Windows Registry writes or local app data installations to run.
- **Validation Method**: Run executable from a FAT32-formatted USB drive on a test machine. Confirm basic scanning functionality works.

### NFAC-05: Data Integrity & Fault Tolerance
*Refers to NFR-05*
- **Criteria**: Telemetry data is stored in memory using thread-safe collection models. Export writers must perform atomic file operations (writing to `.tmp` first, then replacing). If the scan thread crashes due to adapter removal, the UI displays an error but remains running.
- **Validation Method**: Pull out a USB Wi-Fi adapter during active scan; confirm UI reports adapter lost but doesn't crash, and user can reconnect adapter and resume scanning.

---

## 3. Security Acceptance Criteria (SAC)

- **SAC-01: Local Data Bound**: No telemetry data, MAC addresses, or location data collected during surveys may be transmitted over any network connection. The application must not create outbound HTTP/S or TCP sockets except for optional, explicit user-triggered updates.
- **SAC-02: Safe Handles**: All native Windows WLAN API handles must be opened via `SafeHandle` wrappers or closed reliably in `finally` blocks (implementing `IDisposable`).
- **SAC-03: Privilege Elevation Enforcement**: The application enforces administrator privileges at startup. On launch, the manifest or startup code checks for administrative tokens; if absent, a dialog is shown to the user requesting they restart with administrator privileges, and the application exits. This guarantees raw physical access to the WLAN API scan triggers.
- **SAC-04: Static Dependency Integrity**: OUI manufacturer resolution must use an embedded static mapping file compiled into the executable to prevent arbitrary external HTTP requests at runtime.

---

## 4. Observability Acceptance Criteria (OAC)

- **OAC-01: Structured Operational Logging**: All adapter events, scan errors, database transactions, and file exports must write structured log entries (e.g., Serilog) to a rolling file `logs/wireless_scanner_log.txt`. Log file recycling is configured by duration/iteration parameters (retaining logs for up to 4 weeks) with a manual "Purge Logs" button in Settings.
- **OAC-02: Logging Format**: Logs must follow a strict schema: `[Timestamp] [LogLevel] [ThreadId] [SourceContext] Message {ExceptionDetail}`.
- **OAC-03: Debug Diagnostic Dashboard**: A hidden or user-accessible "System Logs" tab in the application must display real-time database transactions, polling statistics, and P/Invoke warning codes.
- **OAC-04: Telemetry State Consistency**: UI state dashboards (AP count, Average quality) must accurately match the active SQLite session database.

---

## 5. Maintainability Acceptance Criteria (MAC)

- **MAC-01: Clean Module Boundaries**: The source code must strictly maintain the project separations specified in [02_ARCHITECTURE.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/02_ARCHITECTURE.md#L95-L113). No WPF UI references are permitted in Application or Domain layers.
- **MAC-02: Adapter Mocking**: The `WlanAdapter` must implement a domain-defined `IWlanAdapter` interface. This interface must be fully mockable to allow complete application testing without relying on physical wireless hardware.
- **MAC-03: Dependency Injection**: All service-to-service dependencies must be resolved via a dependency injection container (e.g., `Microsoft.Extensions.DependencyInjection`) initialized at startup.
- **MAC-04: Zero Static State**: No global static variables or singletons containing mutable runtime state (such as the current active session or list of APs) are allowed. State must live in scoped or singleton services managed by the DI container.

---

## 6. Test Acceptance Criteria (TAC)

- **TAC-01: Unit Test Coverage**: Unit tests must cover:
  - Signal quality percentage math (Wlanapi signal quality 0-100 mapped to quality metrics)
  - Jitter calculation algorithms
  - CSV/JSON exporting serializers
  - Dynamic Vendor OUI prefix extraction
- **TAC-02: Mock Adapter Test Harness**: A test harness using a mock `IWlanAdapter` must simulate a 5-minute roaming walk and verify that:
  - Average, min, max signal values are correctly calculated.
  - No concurrent collection modification exceptions are thrown when the UI thread reads the collection during updates.
- **TAC-03: PDF Export Validation**: The PDF generation unit test must successfully write a multi-page PDF document to disk with mock data within 3 seconds, throwing no exceptions.

---

## 7. Definition of Done (DoD)

A task or feature is considered "Done" when:
1. **Implementation**: Code meets the requirements and complies with defined Architecture & Coding Standards.
2. **Review**: Code has been verified against the DevOps validation checklists (Stage 5 pseudocode & logic review completed).
3. **Tests**: Unit tests pass successfully. Code coverage for business logic in `WirelessScanner.Application` is $\ge 80\%$.
4. **Verification**: Manual validation checklists are executed and pass.
5. **No Static Analysis Errors**: The C# code compiles with zero compiler warnings (treated as errors) and passes Roslyn analyzer rules.
6. **Documentation**: Code is documented with XML comments for public members, and changes are summarized in [walkthrough.md](file:///C:/Users/demit/.gemini/antigravity/brain/63802bd8-8ee4-4751-8a37-891b63e11fa1/walkthrough.md).
