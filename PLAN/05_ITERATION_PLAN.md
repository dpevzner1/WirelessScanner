# WirelessScanner — Iteration Plan

> **Version:** 1.0  
> **Date:** 2026-05-27  
> **DevOps Engine Gate:** `gate.implementation.production_ready`  
> **Iteration Model:** 7-stage gated workflow (`03_operating_model.json`)

---

## 1. Overview of Iteration Framework

Following the DevOps Engine's strict engineering standards, the project will be built in **seven progressive iterations**. Each iteration represents a functional step with its own deliverables, validation gate, and test suite. We do not advance to the next iteration until the current iteration's gate criteria are fully met.

```
┌────────────────────────────────────────────────────────┐
│           ITERATION 1: MVP VERTICAL SLICE              │  ← Bootstrap WPF, DI, Mock Grid, Single-File Pub
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│           ITERATION 2: CORE DOMAIN LOGIC               │  ← Models, Sessions, Stats, Jitter math
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│         ITERATION 3: ADAPTERS AND INTERFACES           │  ← P/Invoke Wlanapi, CSV, JSON, QuestPDF export
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│        ITERATION 4: VALIDATION AND ERROR HANDLING       │  ← Adapter loss fallback, input validation, tests
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│       ITERATION 5: OBSERVABILITY & AUDITABILITY        │  ← Rolling file logs, diagnostic view, metadata
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│      ITERATION 6: PERFORMANCE & RESILIENCE             │  ← Channel<T> threading, memory limit, profiling
└───────────┬────────────────────────────────────────────┘
            ▼
┌────────────────────────────────────────────────────────┐
│     ITERATION 7: DOCUMENTATION & ACCEPTANCE            │  ← Responsive styling, responsive DPI, walkthrough
└────────────────────────────────────────────────────────┘
```

---

## 2. Detailed Iteration Slices

### Iteration 1: MVP Vertical Slice
- **Objective**: Establish the C# WPF solution structure, setup Dependency Injection, create the Mock Data source, and verify compilation into a single self-contained executable.
- **Deliverables**:
  - `WirelessScanner.sln` containing the 5 projects: `Domain`, `Application`, `Infrastructure`, `Presentation`, `Tests`.
  - Service provider initialization (`App.xaml.cs`) with Microsoft DI.
  - A mock implementation of `IWlanAdapter` generating 5 hardcoded access points.
  - A basic main window with a data grid displaying SSID, BSSID, RSSI from the mock adapter.
  - A working `publish` command script to verify single-file `.exe` generation.
- **Validation Gate**: 
  - [ ] App launches and displays the mock AP list.
  - [ ] Compiles successfully with zero warnings.
  - [ ] The generated single-file executable runs on a clean Windows environment.

### Iteration 2: Core Domain Logic & Local Storage
- **Objective**: Define core data models, state machines, business logic for scanning sessions, and configure the local SQLite database schema.
- **Deliverables**:
  - Strongly typed data models in `Domain` project: `AccessPoint`, `TelemetrySample`, `CaptureSession`.
  - SQLite database schemas, indexing setup on session tags, and DbContext/PInvoke database migrations setup.
  - `SessionManager` in `Application` project to track session state (`Active`, `Paused`, `Completed`), session duration, and save metadata (NAME, SCOPE, FACILITY NAME).
  - Statistics calculator to compute running Min, Max, Mean, Standard Deviation, and Jitter on RSSI samples.
  - Unit test suite validating stats calculations, Jitter algorithms, and SQLite model insertion.
- **Validation Gate**:
  - [ ] Unit tests for math/calculations show 100% pass rate.
  - [ ] Session transitions and DB insertions verified via unit tests.
  - [ ] All domain models are immutable and separate from Presentation/UI layers.

### Iteration 3: Adapters, Interfaces & Comparisons
- **Objective**: Bridge the application to physical hardware via native Windows P/Invoke, implement export engines, and create historical query providers.
- **Deliverables**:
  - Physical `WlanAdapter` in `Infrastructure` invoking `Wlanapi.dll` (opening handle, listing interfaces, querying BSS list).
  - P/Invoke struct definitions matching Windows headers.
  - CSV export engine conforming to RFC-4180.
  - JSON export engine utilizing `System.Text.Json` with schema mapping.
  - PDF export engine utilizing `QuestPDF` containing custom metadata, tables, spacing, and customizable cover sheet inputs.
  - Historical query provider to retrieve past sessions matching date filters for comparison.
- **Validation Gate**:
  - [ ] WLAN API handle is successfully opened and closed (no leaks verified by memory profiles).
  - [ ] Device scans pull real physical APs in range of the host.
  - [ ] CSV/JSON/PDF exports successfully write complete data sets to the local folder.
  - [ ] SQLite database queries successfully filter sessions by calendar date ranges.

### Iteration 4: Validation and Error Handling
- **Objective**: Ensure the application degrades gracefully under failure scenarios, validates all user inputs, and enforces startup security elevation.
- **Deliverables**:
  - Startup check validating administrator elevation; displays prompt and exits if not elevated.
  - Exception handling wrapper for all `Wlanapi` calls; catches device removals and hardware errors.
  - UI validation rules for export target paths, custom zone labels, and track criteria.
  - Comprehensive unit and integration testing of error paths.
- **Validation Gate**:
  - [ ] Running without administrator elevation successfully triggers warning dialog and exits.
  - [ ] Pulling Wi-Fi adapter during active scan triggers warning message in UI without crashing application.
  - [ ] Attempting to write exports to locked system folders reports a clean permission failure message to the user.

### Iteration 5: Observability, Auditability & Purging
- **Objective**: Implement structured logging, Diagnostic dashboards, and data purging interfaces.
- **Deliverables**:
  - Integrated Serilog logger writing to a rolling file `logs/wireless_scanner_log.txt` (retention capped by duration).
  - Structured log statements for: adapter select, session start/stop, database transactions, exports, P/Invoke failures.
  - A "Diagnostics Console" view inside the Presentation layer that mirrors log events for real-time troubleshooting.
  - Database management dashboard in Settings showing SQLite file size, log purging, and age-based database purge controls (1 week, 1 month, 1 quarter, 1 year, Total Purge).
- **Validation Gate**:
  - [ ] Log entries format matches required `[Timestamp] [LogLevel] [ThreadId]` structure.
  - [ ] System Console page displays scan ticks, database queries, and errors live during a session.
  - [ ] Purging database logs and session tables successfully updates database file size on screen.

### Iteration 6: Performance and Resilience Hardening
- **Objective**: Decouple the UI thread from the scanning thread, manage memory footprint, and optimize charts.
- **Deliverables**:
  - Thread-isolated worker thread using `System.Threading.Channels.Channel<TelemetrySample>` to queue and process scan packets.
  - LiveCharts2 line chart performance optimization (limiting plotting window to latest 60 seconds of history, downsampling if active points count exceeds 300).
  - Memory analysis and leak verification using diagnostic tools during a 1-hour scan simulation.
- **Validation Gate**:
  - [ ] UI frame rate remains stable (constant 60 FPS) when loading 100+ simulated APs.
  - [ ] Memory footprint does not exceed 250 MB after 1 hour of continuous scanning.
  - [ ] Thread safety analysis confirms no cross-thread collection exceptions.

### Iteration 7: Documentation and Final Acceptance
- **Objective**: Polish UI styling in dark mode, implement responsive scaling, finalize documentation, and package the release.
- **Deliverables**:
  - Custom high-contrast dark theme styles (`#0F0F12` background, neon cyan accent, glowing charts).
  - 3-column "Historical Comparison" dashboard (calendars, reactive checklist filters, comparative LiveCharts2 overlay).
  - Responsive visual design styling in WPF (grid alignments, layout breakpoints, scaling vector icons).
  - High-DPI scaling validation (`PerMonitorV2` configurations).
  - Production build release (`dotnet publish` script execution).
  - Comprehensive user [walkthrough.md](file:///C:/Users/demit/.gemini/antigravity/brain/63802bd8-8ee4-4751-8a37-891b63e11fa1/walkthrough.md) containing setup instructions, usage guides, and system limits.
- **Validation Gate**:
  - [ ] UI displays perfectly on High-DPI screens without layout clipping in dark mode.
  - [ ] Standalone `.exe` is packaged and running.
  - [ ] All acceptance criteria in `04_ACCEPTANCE_CRITERIA.md` are verified and pass.

---

## 3. Transition checklist (Moving to Stage 5: Pseudocode Review)

Before code implementation begins in Iteration 1, the following gates must be cleared:
1. **Scope and Requirements Alignment**: Verified in [01_PROJECT_SCOPE.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/01_PROJECT_SCOPE.md).
2. **Architecture and Data Model Approved**: Verified in [02_ARCHITECTURE.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/02_ARCHITECTURE.md).
3. **Acceptance Criteria Defined**: Verified in [04_ACCEPTANCE_CRITERIA.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/04_ACCEPTANCE_CRITERIA.md).
4. **DevOps Validation Done**: Verified in [03_DEVOPS_VALIDATION.md](file:///c:/Users/demit/Documents/Antigrav/WirelessScanner/PLAN/03_DEVOPS_VALIDATION.md).
5. **Logic Review Preparation**: Next step is to draft the Pseudocode and Data Contracts document (`06_PSEUDOCODE_REVIEW.md` or similar) for Stage 5 review.
