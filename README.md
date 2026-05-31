# WirelessScanner

A high-performance Windows desktop application built in C# .NET 10 and WPF to perform Wi-Fi signal analysis, live channel spectrum mapping, telemetry logging, and reporting. 

Designed for network engineers, DevOps administrators, and field site surveyors, this utility wraps the native Windows WLAN API (`wlanapi.dll`) to scan, analyze, persist, and export raw wireless network telemetry.

---

## Installation & Repository

The official source code repository is hosted on GitHub:  
👉 **[https://github.com/dpevzner1/WirelessScanner](https://github.com/dpevzner1/WirelessScanner)**

### Installation from GitHub via PowerShell

#### Option A: Build and Run from Source
To clone, build, and launch this application on a new system using Git in an Administrator PowerShell console, execute the following:

1. **Clone the repository**:
   ```powershell
   git clone https://github.com/dpevzner1/WirelessScanner.git
   cd WirelessScanner
   ```

2. **Restore dependencies & build the solution**:
   ```powershell
   dotnet build
   ```

3. **Run the WPF desktop application**:
   ```powershell
   dotnet run --project WirelessScanner.Presentation/WirelessScanner.Presentation.csproj
   ```

4. **Package as a self-contained executable (optional)**:
   ```powershell
   ./publish.bat
   ```

#### Option B: Download and Run Pre-compiled Executable Directly
A pre-compiled, standalone, self-contained single-file executable (`WirelessScannerCompiled.zip`) is included directly in this repository. No .NET SDK installation is required.

1. **Clone the repository**:
   ```powershell
   git clone https://github.com/dpevzner1/WirelessScanner.git
   cd WirelessScanner
   ```

2. **Extract the compiled executable**:
   ```powershell
   Expand-Archive -Path .\WirelessScannerCompiled.zip -DestinationPath .\WirelessScannerApp -Force
   cd WirelessScannerApp
   ```

3. **Launch with Administrator privileges** (required to access raw Wi-Fi adapter hardware telemetry):
   ```powershell
   Start-Process .\WirelessScanner.Presentation.exe -Verb RunAs
   ```

#### Option C: Guided Setup Wizard (Recommended for end users)
A full Windows installer wizard is provided in the **`EXE INSTALLER/`** subfolder of this repository.
It reassembles the split application payload, installs to `Program Files`, creates Desktop and Start Menu
shortcuts, and registers the application in Windows Add/Remove Programs — with full uninstall support.

1. **Clone the repository**:
   ```powershell
   git clone https://github.com/dpevzner1/WirelessScanner.git
   cd WirelessScanner
   ```

2. **Navigate to the installer folder**:
   ```powershell
   cd "EXE INSTALLER"
   ```

3. **Right-click** `WirelessScanner.Setup.exe` → **Run as Administrator** and follow the wizard.

> The wizard merges the split `.bin` files, extracts all application files, creates shortcuts,
> and registers WirelessScanner in **Settings → Apps** for clean uninstall support.

---

## About the Application

WirelessScanner functions as a professional-grade site survey and diagnosis tool. It allows users to execute real-time wireless environment scans to analyze channel allocation, signal congestion, and signal decay trends.

The application operates in two scan modes:
* **Stationary Saturation Mode**: Continuous background scanning of a fixed location to analyze signal stability, jitter, and interference over time.
* **Roaming Survey Mode**: Multi-point telemetry capture where surveyors walk a facility and label measurements with specific location/zone tags to build signal coverage maps.

---

## How It Functions

1. **Direct Hardware Querying**: Accesses raw Wi-Fi adapter frame metrics directly via the Windows Native Wifi API (WLAN API).
2. **STA Thread Scan Worker**: Launches scans inside a dedicated Single-Threaded Apartment (STA) thread, preventing the main UI or processing threads from blocking during hardware polling.
3. **Thread-Safe Telemetry Queue**: Dispatches captured frame samples via a high-performance C# `System.Threading.Channels` queue to background database-writing tasks.
4. **WAL SQLite Storage**: Stores telemetry samples in an SQLite database. Write-Ahead Logging (WAL) is enabled by default to prevent database locks and ensure concurrent, low-latency writes.
5. **Decimated Rendering**: To handle massive timeline graphs efficiently, the charting UI downsamples data points if the capture session exceeds 1,000 records, maintaining UI responsiveness.

---

## Key Benefits

* **Native & Lightweight**: Packaged as a self-contained, single-file release with zero dependencies or third-party framework prerequisites.
* **DevOps HUD Styling**: Designed with a high-contrast, amber-tinted Obsidian HUD dashboard optimized for high readability in field settings or server closets.
* **API Integration Ready**: Includes a lightweight HTTP background API, making it easy to script, pull metrics, or stream live data to external monitoring consoles.
* **Interactive Legending & Filtering**: Allows users to filter graphs and data grids dynamically by checking or unchecking specific SSIDs/APs in the UI or report compiler.

---

## Telemetry Capture & Export Capabilities

### 1. Telemetry Metrics Captured
The scanner records and logs the following detailed attributes for every scanned frame:
* **SSID**: Service Set Identifier (supporting hidden networks).
* **BSSID**: Access Point MAC address.
* **RSSI**: Received Signal Strength Indicator (measured in dBm).
* **Frequency Band**: Active band (2.4 GHz or 5 GHz) and channel width (20/40/80/160 MHz).
* **Channel**: The specific operating channel index.
* **SNR**: Signal-to-Noise Ratio (calculated in dB).
* **Signal Quality**: Overall connection quality percentage (0% to 100%).
* **Jitter**: High-precision variation in packet transmission times (calculated in milliseconds).
* **Survey Zone**: Location-tagged labels assigned during roaming survey captures.
* **Timestamp**: High-precision localized time marking.

### 2. Export Capabilities & Formats
Users can selectively filter telemetry by Access Points and export data into multiple standard formats:
* **CSV**: Generates clean, spreadsheet-compatible CSV spreadsheets containing session metadata headers and raw telemetry rows.
* **JSON**: Compiles structured JSON payloads including duration, unique client counts, signal thresholds, and raw metrics arrays.
* **PDF**: Exports a formal executive-summary report containing metadata cards, average metrics tables, custom-painted SkiaSharp timeline graphs (with translucent signal-strength color overlays), and a complete tabular sample log appendix.

---

## Getting Started

### Prerequisites
* **Windows OS** (WLAN API dependency)
* **.NET 10 SDK**
* **Administrator Privileges** (required to access raw Wi-Fi adapter hardware telemetry)

### How to Run Locally (Alternative)
If you already have the source code downloaded, you can build and run locally using:
```powershell
dotnet run --project WirelessScanner.Presentation/WirelessScanner.Presentation.csproj
```

### Running Unit Tests
To run the automated test suite:
```powershell
dotnet test WirelessScanner.Tests/WirelessScanner.Tests.csproj
```

---

## REST API Interface

The application runs a lightweight background REST API server (`RestApiServer`) to expose live Wi-Fi scanner metrics, past sessions, and telemetry logs to developer tools and scripts.

### 1. Configuration
The API server auto-starts on application launch if configured in the SQLite settings database (`wireless_scanner.db`) with the following fields:
* `ApiEnabled`: Set to `"true"` to start the server.
* `ApiPort`: The port to listen on (defaults to `5005` if unspecified).
* `ApiKey`: The static secret key used to authenticate requests.

*Note: Your API key and port mappings are stored locally in the SQLite settings database (`wireless_scanner.db`) on first launch.*

### 2. Authentication
All API requests must authenticate using one of the following methods:
* **HTTP Header**: Include `X-API-Key: YOUR_API_KEY` in the request headers.
* **Query Parameter**: Append `?api_key=YOUR_API_KEY` to the URL query string.

### 3. API Endpoints
* **`GET /api/status`**
  Returns server health status and port.
* **`GET /api/live`**
  Returns a real-time list of all currently scanned Wi-Fi Access Points.
* **`GET /api/sessions`**
  Returns a list of all historical capture sessions stored in the local database.
* **`GET /api/sessions/{id}`**
  Retrieves metadata and all telemetry timeline samples for a specific capture session ID.
* **`GET /api/stats`**
  Returns database telemetry statistics, including total sessions, total samples, and database file size.

### 4. Integration Examples

#### cURL:
```bash
curl -H "X-API-Key: ws_live_f1cf3cf16f25a3dd6e060c9db085a27a" http://localhost:5005/api/live
```

#### Python:
```python
import requests

url = "http://localhost:5005/api/live"
headers = {
    "X-API-Key": "ws_live_f1cf3cf16f25a3dd6e060c9db085a27a"
}

response = requests.get(url, headers=headers)
print(response.json())
```

---

## Visual Signal Strength Mapping (RSSI Tiers)

To prevent color confusion and blockage overlap in live capture graphs and exported reports, the application implements a standard, high-contrast, semi-transparent color overlay scheme:

* **Excellent Signal (RSSI >= -55 dBm)**: Emerald Green (`#00FF66`)
* **Fair/Good Signal (-55 dBm to -70 dBm)**: Vibrant Amber/Gold (`#FFBB33`)
* **Weak/Poor Signal (RSSI < -70 dBm)**: Crimson Red (`#FF3300`)

### Graph Transparency & Blending
* **Live Curves**: Overlapping active channel spectrum curves are drawn with low-opacity fills (`alpha = 0x18`) so they blend where they intersect instead of blocking other charts.
* **Timeline Timelines (WPF & PDF Report)**: Historical timeline line charts calculate average session RSSI to color their trace lines, and draw matching semi-transparent area shapes underneath with low opacity (`alpha = 0x15`). When lines intersect, their background shadings stack additively to show transparent overlays.
