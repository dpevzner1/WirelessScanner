# WirelessScanner — Project Scope

> **Version:** 1.0  
> **Date:** 2026-05-27  
> **Status:** Planning  
> **DevOps Engine Validation:** Pending implementation

---

## 1. Problem Statement

Enterprise and facility network teams need a portable, self-contained wireless survey tool that captures real RF telemetry from Wi-Fi access points across a physical space. Current commercial tools (Ekahau, NetSpot, Acrylic) are expensive, cloud-dependent, or lack the real-time telemetry depth required for industrial/warehouse environments.

This project delivers a **native Windows executable** that provides live wireless scanning, signal-strength tracking, mesh coverage analysis, and exportable survey reports — all from a single portable tool.

---

## 2. User Story

> *As a network engineer or facilities technician, I need to walk through a facility with a laptop and capture detailed wireless signal data — SSIDs, APs, MAC addresses, signal strength, band utilization, mesh coverage — so that I can identify dead zones, interference, signal degradation, roaming issues, and generate professional survey reports for remediation or documentation.*

### Primary Users
- Network engineers performing wireless site surveys
- Facilities/IT staff validating AP deployment and coverage
- Network operations teams monitoring wireless performance
- Contractors validating installation quality

### Secondary Users
- Management receiving PDF survey reports
- Help desk referencing coverage data for troubleshooting

---

## 3. Functional Requirements

### FR-01: Network Adapter & Device Manager Integration
- **Multi-Adapter Enumeration**: Enumerate all available network interfaces on the host machine, including built-in Wi-Fi, external USB Wi-Fi adapters, and wired Ethernet adapters.
- **Dynamic Device Manager Detection**: Listen for Windows PnP hardware changes (`WM_DEVICECHANGE`). Real-time plug/unplug of USB network cards automatically refreshes the selection dropdown without restarting the app.
- **Wired Fallback Mode**: If an Ethernet adapter is selected, disable SSID/AP radio features and display wired network details (interface speed, link status, IP configuration, and ping latency) instead.
- **Active Selection**: Allow the user to select which active network adapter is used for the capture session.

### FR-02: SSID / AP Scanning Modes
- **SSID Mode**: Scan and group all visible SSIDs; show all APs broadcasting each SSID
- **AP Mode**: Scan and list individual access points by BSSID/MAC
- Toggle between modes via a slider/segmented control
- In either mode, the user can select a specific SSID or AP to track

### FR-03: Signal Telemetry Capture
For each visible AP, capture and display:
- **BSSID** (MAC address)
- **SSID** (network name)
- **AP hostname** (if resolvable/visible)
- **Band** (2.4 GHz / 5 GHz / 6 GHz where available)
- **Channel** and channel width
- **RSSI** (signal strength in dBm)
- **SNR** (signal-to-noise ratio)
- **Signal quality** (percentage)
- **Security type** (Open, WEP, WPA, WPA2, WPA3)
- **Network type** (Infrastructure, Ad-hoc, Mesh)
- **Supported rates** (where available)
- **Vendor OUI** (manufacturer from MAC prefix)
- **Last seen** timestamp

### FR-04: Dual-Band & Multi-Band Filtering
- Filter scan results by 2.4 GHz, 5 GHz, or all bands
- Display band-specific metrics and comparisons

### FR-05: Capture Session Management
- **Start/Stop/Pause** capture sessions with timestamp logging
- Each session records a time-bounded capture window
- **Searchable Metadata**: User can name each session (NAME), assign what it is tracking (SCOPE), and define the location (FACILITY NAME). All fields are fully searchable in the database.
- Running timer showing capture duration
- Displays the local SQLite database file size in the interface

### FR-06: Roaming Survey Mode
- User physically walks through a facility while scanning
- All visible SSIDs/APs are captured continuously across the walk
- Captures air mesh strength, single AP broadcast strength
- Measures signal ubiquity/elasticity across facility zones
- Timestamped samples tied to user-labeled survey points

### FR-07: Stationary Performance Monitoring
- User stands in one spot and captures signal strength over time
- Monitors for signal dips, jitter, and degradation
- Captures performance behavior patterns in wireless APs/routers
- Statistical analysis: min, max, mean, standard deviation, jitter

### FR-08: Real-Time Telemetry Visualization
- **Signal Strength Timeline**: Live-updating line chart of RSSI over time per AP
- **Band Balance**: Comparative bar chart of 2.4 GHz vs 5 GHz quality
- **Mesh Shape Radar**: Radar/polar chart showing AP quality distribution
- **Channel Utilization**: Channel map showing congestion/overlap
- **AP Detail Cards**: Per-AP cards with full telemetry data
- **Health Dashboard Metrics**: Avg quality, AP count, weak links, best signal
- All graphics update in real-time during active capture

### FR-09: Export — CSV
- Full timestamped capture data as CSV
- All telemetry fields per sample
- Compatible with Excel, Google Sheets, analysis tools

### FR-10: Export — JSON
- Structured JSON with session metadata + sample array
- Machine-readable for integration with other tools

### FR-11: Export — PDF Report
- Professional survey report with:
  - Custom Cover Sheet: Surveyor can input custom "Client Name", "Surveyor Name", and "Notes/Description" in the export dialog to print on the cover page.
  - Session metadata (interface, duration, facility, scope, date)
  - Signal strength graphs (timeline, band balance, mesh)
  - AP detail tables
  - Summary statistics
  - Visual representation of telemetry throughout the session
  - Timestamped capture data in tabular format (pulling historical sessions if compared)

---

## 4. Non-Functional Requirements

### NFR-01: Native Executable
- Must compile to a standalone Windows `.exe`
- No installation dependencies (self-contained runtime)
- No browser dependency — native desktop application
- **Language & Framework**: C# (.NET 8 LTS) targeting `win-x64`
- **Primary Theme**: High-contrast dark mode by default for premium visibility in warehouse/facility conditions

### NFR-02: Reflexive/Responsive Interface
- Modern UI that scales with device resolution and orientation
- DPI-aware rendering on high-DPI displays
- Supports landscape and portrait orientations
- Graceful layout adaptation from 1080p to 4K+

### NFR-03: Performance
- Scan tick interval ≤ 2 seconds
- UI must remain responsive during active scanning
- Background scanning thread must not block UI rendering
- Handle 100+ visible APs without degradation

### NFR-04: Portability & Elevation
- Single executable, no install wizard required
- Can run from USB drive or network share
- **Administrator Elevation Enforcement**: Enforces administrator privileges at startup. Prompts the user with an elevation request dialog and exits cleanly if not run as administrator. This guarantees full raw access to the Windows WLAN API (`WlanScan` triggers).

### NFR-05: Data Integrity
- No telemetry data loss during capture
- Atomic file writes for exports (write to temp, then rename)
- Capture buffer survives UI interactions

---

## 5. Scope Boundaries

### In Scope
- Windows desktop application (native executable)
- Wi-Fi scanning via Windows WLAN API (Wlanapi.dll)
- SSID and AP discovery and tracking
- Real-time signal telemetry visualization
- CSV, JSON, and PDF export
- Dual-band (2.4/5 GHz) scanning
- Survey point labeling and session management
- Roaming and stationary capture modes

### Out of Scope (v1.0)
- Linux/macOS support (future consideration)
- Floor plan / heatmap overlay
- GPS/indoor positioning integration
- Cloud sync or multi-user features
- Packet capture / deep inspection
- Monitor mode (requires special drivers on Windows)
- 6 GHz / Wi-Fi 6E scanning (depends on adapter/driver support)
- Active probing (sending probe requests)
- Network vulnerability scanning

---

## 6. Risks & Constraints

| Risk | Impact | Mitigation |
|------|--------|------------|
| Windows WLAN API limitations | May not expose all AP metadata (e.g., hostname) | Document which fields come from WLAN API vs. require vendor APIs |
| Adapter driver differences | Some telemetry (SNR, channel width) varies by adapter | Gracefully handle missing fields; show "N/A" |
| Admin rights requirements | Active scanning requires elevated privileges | Enforce administrator elevation at startup; exit cleanly with prompt if elevation is denied |
| High-DPI rendering complexity | UI scaling bugs on mixed-DPI setups | Use framework-native DPI handling (WPF/WinUI or DX-backed rendering) |
| PDF generation with embedded charts | Complex to render charts to PDF from native code | Leverage a charting library that supports bitmap export for PDF embedding |

---

## 7. Success Criteria

1. User can launch a single `.exe` on any Windows 10/11 machine and begin scanning
2. All visible Wi-Fi networks appear within 3 seconds of starting a scan
3. Real-time charts update smoothly at ≤2-second intervals
4. SSID/AP mode toggle works without losing capture history
5. Exported CSV contains all captured telemetry with timestamps
6. Exported PDF contains embedded charts and is professionally formatted
7. UI scales correctly from 1080p to 4K displays
8. Application handles 100+ APs without UI lag or data loss
