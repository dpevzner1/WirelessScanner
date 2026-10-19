# WirelessScanner 1.1 — Technical Reference

For maintainers. The user-facing behaviour is in [`USER_GUIDE.md`](USER_GUIDE.md), feature status in [`STATUS.md`](STATUS.md), and process rules in [`PROJECT_CONVENTIONS.md`](PROJECT_CONVENTIONS.md).

## 1. Solution layout

```
WirelessScanner.slnx
├── WirelessScanner.Domain           net10.0     records, enums, interfaces (no dependencies)
├── WirelessScanner.Application      net10.0     SessionManager, OrderedPersistenceWriter, StatisticsCalculator,
│                                                SaturationAnalyzer, SaturationObservationBuilder/TargetTracker, SaturationTestRunner
├── WirelessScanner.Infrastructure   net10.0     WlanAdapter (wlanapi.dll P/Invoke), WlanDbContext + SchemaMigrator,
│                                                Session/Saturation/Settings repositories, ExportService, SaturationExportService,
│                                                RestApiServer, AppLogger (Serilog), AppPaths, MockNetworkAdapterProvider
├── WirelessScanner.Presentation     net10.0-windows10.0.19041.0  WPF app: App (DI, elevation, migration), MainWindow(+ViewModel),
│                                                SaturationTestView(+ViewModel), dialogs, WindowsGeoLocationProvider, NativePower
├── WirelessScanner.Setup.Core       netstandard2.0  installer lifecycle logic (unit-tested)
├── WirelessScanner.Setup            net48 WinForms  setup / maintenance wizard
└── WirelessScanner.Tests            NUnit, net10.0-windows10.0.19041.0 (108 tests + explicit hardware/preview categories)
```

**Dependency direction:** Presentation → Application → Domain ← Infrastructure (Infrastructure also references Application for the analyzer types). Setup → Setup.Core.

**Other folders:**
- `docs/`: guides, specification, decisions (`decisions/DR-*.md`), the consultation log, vendored engine standards, images.
- `PLAN/`: the original May 2026 design baseline (historical; see the banner in each file).
- `LessonsLearned/`, `config/pipeline/`, `governance/`, `tools/`: the evidence trail and tooling (see `PROJECT_CONVENTIONS.md`).
- `src/`, `index.html`, `package.json`: an early React/Vite UI prototype using simulated data. It is **archived**: not built, not shipped, kept for reference.

## 2. Runtime architecture and threading

```
UI thread (WPF dispatcher)
  ├─ MainWindowViewModel.RunScanLoopAsync ─ every ScanInterval (5 s), never overlapping (SemaphoreSlim)
  │     └─ await INetworkAdapterProvider.ScanAsync()  ──►  thread pool: WlanScan → wait for ACM scan_complete/fail (≤ 6 s)
  │                                                                    → WlanGetNetworkBssList → ScanCycleResult
  │     └─ fresh entries → SessionManager.SaveSamples(batch) ──► OrderedPersistenceWriter (Channel<T>, 1 consumer)
  └─ SaturationTestViewModel ◄─ Progress/Completed events (marshalled via Dispatcher)
        └─ SaturationTestRunner (background Task): scan cycle → ObservationBuilder → cycle → writer → analyzer → progress
```

- **One WLAN client handle per adapter instance**, registered for ACM notifications. The callback delegate is rooted for the lifetime of the registration, and the handle is disposed at app exit.
- **`OrderedPersistenceWriter`** is a singleton FIFO with one consumer: a parent row (session or test) is always enqueued before its children. On exit: stop the runner, flush the writer, dispose the adapter (`App.OnExit`).
- **Freshness rule:** a sample or observation counts as fresh if the cycle was confirmed `Fresh`, or if the entry's beacon age (`ullHostTimestamp`, a FILETIME) is ≤ interval + 5 s.

## 3. Measurement semantics (DR-0004)

| Field | Source | Quality |
| --- | --- | --- |
| RSSI | `WLAN_BSS_ENTRY.lRssi` | measured |
| Link quality | `uLinkQuality` | measured (driver) |
| Channel / band | `ulChCenterFrequency`: 2.4 GHz `(f−2407)/5` (2484 → 14); 5 GHz `(f−5000)/5`; 6 GHz `(f−5950)/5` | derived |
| Channel width | HT Operation (61), VHT Operation (192, CCFS rules), HE Operation 6 GHz info (255/36); `null` = unknown | derived |
| Security | Privacy bit + RSN AKM suites (1, 2, 8, 12, 18, …) + WPA vendor element | derived |
| Noise floor | not available from the API → `null` | unavailable |
| SNR | RSSI − (−95), labelled *estimated* | estimated |
| Beacon age | now − `ullHostTimestamp` (FILETIME); null when implausible | measured |
| RSSI variation (stored column `Jitter`) | \|ΔRSSI\| between consecutive samples, in dB | derived |

## 4. Data model (SQLite, schema version 2)

| Table | Key columns |
| --- | --- |
| `Sessions` | SessionId (PK), Name, Scope, FacilityName, InterfaceName, StartTime, EndTime, Mode, Status |
| `Samples` | SampleId (PK), SessionId (FK, cascade), Timestamp, SurveyPoint, BSSID, SSID, Band, Channel, RSSI, SNR, Quality, Jitter |
| `Settings` | Key (PK), Value (`ApiEnabled`, `ApiPort`, `ApiKey`) |
| `SaturationTests` | TestId (PK), Name, Scope, ConfigJson (full `SaturationTestConfig`, including `Geo`), InterfaceName, Location, Facility, StartTime, EndTime, Status |
| `SaturationCycles` | Id, TestId (FK, cascade), Sequence (unique per test), WallUtc, ElapsedMs, Status, DurationMs, BssCount, ErrorCode, ErrorMessage |
| `SaturationObservations` | Id, TestId (FK, cascade), CycleSequence, Bssid, Ssid, Status, Rssi?, LinkQuality?, Channel?, Band?, ChannelWidth?, BeaconAgeMs?, CoChannelCount?, OverlapCount? |
| `SaturationMarkers` | Id, TestId (FK, cascade), WallUtc, ElapsedMs, Text |

- **Schema management.** `EnsureCreated()` builds new databases. `SchemaMigrator.Upgrade()` creates any missing tables and indexes in existing ones (taken from EF's `GenerateCreateScript()`, applied with `IF NOT EXISTS`), then sets `PRAGMA user_version = 2`. It never drops or alters existing data. WAL journal mode.
- **Canonical vs derived.** Saturation cycles and observations are canonical. Reports (summaries and events) are always re-derived by the pure `SaturationAnalyzer` (`SAT-ANALYTICS-1`).

## 5. File locations (DR-0006)

| Item | Location |
| --- | --- |
| Data directory | `AppPaths.DataDirectory` = `%ProgramData%\WirelessScanner`, or `WIRELESSSCANNER_DATA_DIR` |
| Database | `<data>\wireless_scanner.db` |
| Logs | `<data>\logs` (Serilog, daily, 28 retained; setup logs `setup_*.log`) |
| API key export | `<data>\API\api_key.md` |
| Reports default | `Documents\WirelessScanner\Reports` |
| Demo mode | `%LOCALAPPDATA%\WirelessScanner\demo` |

`AppPaths.MigrateLegacyData()` copies 1.0 data from the exe folder or working directory once, never overwriting. The installer's `DataSafety.PreserveLegacyData` does the same before changing or removing an install folder.

## 6. REST API (DR-0008)

- **Listener:** `HttpListener` on `http://localhost:<port>/` (localhost only). GET only.
- **Authentication:** `X-API-Key` header, compared in constant time (`CryptographicOperations.FixedTimeEquals`). A query-string `api_key` returns 401 with a message.
- **CORS:** headers only for `RestApiServer.AllowedOrigin` when it is set (default: none).
- **Responses:** `Cache-Control: no-store`. 500 responses never echo exception text.
- **Errors:** `IRestApiServer.LastError` carries actionable start errors (port in use), which the REST API tab displays.
- **Endpoints:** `/api/status`, `/api/live`, `/api/sessions`, `/api/sessions/{id}`, `/api/stats`.

## 7. Saturation test pipeline (SPEC-SAT-001)

`SaturationTestViewModel.BuildConfig()` → `SaturationTestRunner.StartAsync`:
1. Enqueue the `SaturationTests` row.
2. Loop: `ScanAsync` → `SaturationObservationBuilder.Build` (target tracking, statuses, co-channel) → `SaturationCycle` → enqueue the cycle (one transaction) → raise `Progress` (the UI re-runs the analyzer on the snapshot).
3. Duration counts **active** time (a Stopwatch stopped while paused).
4. Finish: update the status, flush, analyze, then raise `Completed`.

`SaturationExportService` produces the PDF (QuestPDF, with SkiaSharp-drawn charts), the CSV bundle and the JSON (schema `wirelessscanner.saturation_report.v1`). Writes are atomic.

## 8. Installer (DR-0007)

**Release layout:**
- `WirelessScanner.Setup.exe` (+ `.config`, `WirelessScanner.Setup.Core.dll`);
- `WirelessScanner.payload.json` (version, bytes, SHA-256, parts);
- `WirelessScanner01..NN.bin` (≤ 20 MB parts of the self-contained app zip).

**Flow:**
1. Detect: `HKLM\SOFTWARE\WirelessScanner` (InstallDir, Version, DataDir).
2. Classify: `VersionPolicy` → Fresh, Upgrade, Reinstall or Downgrade.
3. Offer: Install / Modify / Uninstall.
4. On install:
   - stop a running app;
   - preserve legacy data;
   - assemble and verify the payload;
   - `PackageExtractor` (zip-slip guard; removes files listed in the previous `install-manifest.json` that the new package no longer contains);
   - copy the maintenance setup to `<install>\Setup`;
   - shortcuts (all-users Desktop and Start Menu; 1.0's per-user shortcuts are removed);
   - register in Apps & features (`UninstallString`/`ModifyPath` → the maintenance copy with `/uninstall` / `/modify`; `NoRepair=1`, because repair needs the payload).
5. Uninstall: data is kept unless the delete box is ticked (double confirmation). When running from the maintenance copy, the folder is removed after exit by a delayed `rmdir`.

## 9. Build, test, release

```powershell
dotnet build WirelessScanner.slnx
dotnet test WirelessScanner.Tests/WirelessScanner.Tests.csproj                     # 108 tests
dotnet test WirelessScanner.Tests --filter "Category=Hardware"                       # real-adapter scan probe (manual)
$env:WS_SAT_MINUTES=15; dotnet test WirelessScanner.Tests --filter "Category=HardwareSaturation"   # 15-min real test (manual)
$env:WS_PREVIEW_OUT="C:\tmp\p"; dotnet test WirelessScanner.Tests --filter "Category=ReportPreview" # PDF page PNGs
$env:WS_UI_RENDER_OUT="C:\tmp\ui"; dotnet test WirelessScanner.Tests --filter "FullyQualifiedName~UiRenderTests"  # UI PNGs
.\publish.bat              # = python tools\build_release.py : tests, self-contained publish, setup, split, EXE INSTALLER
python tools\sync_github_export.py --check
```

- **SDK:** .NET 10 SDK; it builds the net48 setup through reference assemblies.
- **Release size:** the 1.1 payload is about 85 MB in 5 parts. It grew from about 60 MB in 1.0 because of the Windows SDK projection needed for geolocation. Debug symbols are excluded from publish.

## 10. Configuration reference

| Setting | Where | Default |
| --- | --- | --- |
| Live scan interval / timeout | `MainWindowViewModel.ScanInterval` / `ScanTimeout` | 5 s / 6 s |
| Saturation thresholds | `SaturationThresholds` (UI: Advanced) | drop/peak 8 dB, window 12, min baseline 3, dropout 2, hysteresis 3 dB, −67/−70/−80 dBm, freshness +5 s |
| Data directory | env `WIRELESSSCANNER_DATA_DIR` | `%ProgramData%\WirelessScanner` |
| Demo mode | env `WIRELESSSCANNER_DEMO=1` | off |
| API | Settings table `ApiEnabled`, `ApiPort`, `ApiKey` | off, 5005 |
