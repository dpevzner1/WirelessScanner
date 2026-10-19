# WirelessScanner 1.4 — Technical Reference

For maintainers. The user-facing behaviour is in [`USER_GUIDE.md`](USER_GUIDE.md), feature status in [`STATUS.md`](STATUS.md), and process rules in [`PROJECT_CONVENTIONS.md`](PROJECT_CONVENTIONS.md).

## 1. Solution layout

```
WirelessScanner.slnx
├── WirelessScanner.Domain           net10.0     records, enums, interfaces (no dependencies)
├── WirelessScanner.Application      net10.0     SessionManager, OrderedPersistenceWriter, StatisticsCalculator, ScanScheduler,
│                                                NetworkSelection (+IUnitResolver), PhysicalUnitGrouper, SaturationAnalyzer,
│                                                SaturationObservationBuilder/TargetTracker, SaturationTestRunner
├── WirelessScanner.Infrastructure   net10.0     WlanAdapter (wlanapi.dll P/Invoke), WlanDbContext + SchemaMigrator,
│                                                Session/Saturation/Settings repositories, ExportService, SaturationExportService,
│                                                RestApiServer, AppLogger (Serilog), AppPaths, MockNetworkAdapterProvider
├── WirelessScanner.Presentation     net10.0-windows10.0.19041.0  WPF app: App (DI, elevation, migration), MainWindow(+ViewModel),
│                                                NetworkExplorerView(+NetworkExplorerViewModel), SpectrumView + SpectrumLayout,
│                                                SaturationDrawerView, SaturationStageView, SaturationTestViewModel,
│                                                SaturationTestView (standalone, not shown), Themes/HudTheme.xaml, dialogs,
│                                                WindowsGeoLocationProvider, NativePower
├── WirelessScanner.Setup.Core       netstandard2.0  installer lifecycle logic (unit-tested)
├── WirelessScanner.Setup            net48 WinForms  setup / maintenance wizard
└── WirelessScanner.Tests            NUnit, net10.0-windows10.0.19041.0 (209 tests + explicit hardware/preview/regeneration/IE-inventory categories)
```

**Dependency direction:** Presentation → Application → Domain ← Infrastructure (Infrastructure also references Application for the analyzer types). Setup → Setup.Core.

**Other folders:**
- `docs/`: guides, specification, decisions (`decisions/DR-*.md`), the consultation log, vendored engine standards, images.
- `PLAN/`: the original May 2026 design baseline (historical; see the banner in each file).
- `LessonsLearned/`, `config/pipeline/`, `governance/`, `tools/`: the evidence trail and tooling (see `PROJECT_CONVENTIONS.md`).
- `src/`, `index.html`, `package.json`: an early React/Vite UI prototype using simulated data. It is **archived**: not built, not shipped, kept for reference.

## 2. Runtime architecture and threading

```
ScanScheduler (singleton IScanScheduler, DR-0011)
  loop per adapter at min(requested cadences) ── single flight: concurrent ScanNow/loop requests join the in-flight scan
     └─ INetworkAdapterProvider.ScanAsync ──► WlanAdapter: coalesced per interface (one WlanScan at a time)
            thread pool: WlanScan → wait for ACM scan_complete/fail (≤ 6 s) → WlanGetNetworkBssList → ScanCycleResult
     └─ CycleCompleted(ScheduledCycle{Sequence, AdapterId, Result}) ─┬─► MainWindowViewModel.ProcessCycleAsync (UI thread)
                                                                     │     dedupe by Sequence → AccessPoints, explorer, waterfall,
                                                                     │     REST /api/live, capture: SessionManager.SaveSamples
                                                                     │     ──► OrderedPersistenceWriter (Channel<T>, 1 consumer)
                                                                     └─► SaturationTestRunner (background Task)
                                                                           decimate to the test interval → ObservationBuilder
                                                                           → cycle → writer → Progress
UI thread: SaturationTestViewModel ◄─ Progress/Completed (marshalled via Dispatcher) → re-analyse → tiles, timeline, results
```

- **Cadence requests:** the live view requests 5 s while an adapter is selected, and a test requests its own interval. The scheduler loops at the shortest active request and stops when nobody needs scans. FORCE SCAN calls `ScanNowAsync`, which joins a scan in flight instead of starting a second one.
- **Decimation:** the runner records a cycle when `now ≥ nextDue − tolerance`, where the tolerance is half the effective interval. `nextDue` advances by the test interval and is re-anchored after a gap, so a 5 s test on a 5 s live cadence records every cycle, and a 60 s test records one in twelve.
- **Without the app's DI** (tests, headless), `MainWindowViewModel` creates a scheduler with `AutoScan = false`. Scans then happen only on demand, which keeps the older tests deterministic.

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

## 4. Data model (SQLite, schema version 3)

| Table | Key columns |
| --- | --- |
| `Sessions` | SessionId (PK), Name, Scope, FacilityName, InterfaceName, StartTime, EndTime, Mode, Status |
| `Samples` | SampleId (PK), SessionId (FK, cascade), Timestamp, SurveyPoint, BSSID, SSID, Band, Channel, RSSI, SNR, Quality, Jitter |
| `Settings` | Key (PK), Value (`ApiEnabled`, `ApiPort`, `ApiKey`, `PhysicalUnitOverrides` — JSON `UnitOverrides`, 1.4) |
| `SaturationTests` | TestId (PK), Name, Scope (int; 1.4 adds `SingleUnit` = 6, `MultipleUnits` = 7), ConfigJson (full `SaturationTestConfig`, including `Geo`, `Environment` and, 1.4, `TargetUnits`), InterfaceName, Location, Facility, StartTime, EndTime, Status, ActiveSeconds |
| `SaturationCycles` | Id, TestId (FK, cascade), Sequence (unique per test), WallUtc, ElapsedMs, Status, DurationMs, BssCount, ErrorCode, ErrorMessage |
| `SaturationObservations` | Id, TestId (FK, cascade), CycleSequence, Bssid, Ssid, Status, Rssi?, LinkQuality?, Channel?, Band?, ChannelWidth?, BeaconAgeMs?, CoChannelCount?, OverlapCount? |
| `SaturationMarkers` | Id, TestId (FK, cascade), WallUtc, ElapsedMs, Text |

- **Schema management.** `EnsureCreated()` builds new databases. `SchemaMigrator.Upgrade()` creates any missing tables and indexes in existing ones (taken from EF's `GenerateCreateScript()`, applied with `IF NOT EXISTS`), adds missing nullable columns (schema 3), then sets `PRAGMA user_version` to `CurrentSchemaVersion` (3; 1.4 adds no schema change). It never drops or alters existing data. WAL journal mode.
- **Canonical vs derived.** Saturation cycles and observations are canonical. Reports (summaries and events) are always re-derived by the pure `SaturationAnalyzer` (`SAT-ANALYTICS-3` since 1.4).

## 5. File locations (DR-0006)

| Item | Location |
| --- | --- |
| Data directory | `AppPaths.DataDirectory` = `%ProgramData%\WirelessScanner`, or `WIRELESSSCANNER_DATA_DIR` |
| Database | `<data>\wireless_scanner.db` |
| Logs | `<data>\logs` (Serilog, daily, 28 retained; setup logs `setup_*.log`) |
| API key export | User-chosen (Save dialog; default `%USERPROFILE%\Documents\WirelessScanner\API\`). 1.3.1 no longer writes `<data>\API\api_key.md` (machine-wide readable); DELETE removes that legacy file |
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

`SaturationTestViewModel.BuildConfig()` → `SaturationTestRunner.StartAsync`. In the app the view model is **attached** to Live Survey (`MainWindowViewModel.AttachSaturation`): scope, targets and adapter come from the shared `NetworkSelection`, and the selection is locked from start until `Completed`.
1. Enqueue the `SaturationTests` row.
2. Loop: next `ScheduledCycle` from the shared scheduler (decimated, see §2) → `SaturationObservationBuilder.Build` (target tracking, statuses, co-channel) → `SaturationCycle` → enqueue the cycle (one transaction) → raise `Progress` (the UI re-runs the analyzer on the snapshot).
3. Duration counts **active** time (a Stopwatch stopped while paused).
4. Finish: update the status, flush, analyze, then raise `Completed`.

`SaturationExportService` produces the PDF (QuestPDF, with SkiaSharp-drawn charts), the CSV bundle and the JSON (schema `wirelessscanner.saturation_report.v3` since 1.4; v2 in 1.3, v1 before). Writes are atomic.

## 7a. Live Survey UI (DR-0010, DR-0011)

| Component | Role | Tests |
| --- | --- | --- |
| `NetworkSelection` (Application) | The one selection. **Whole SSIDs** follow new BSSIDs; **fixed BSSIDs** are exact. Empty means everything. It also provides `Lock`/`Unlock`, `DeriveScope` (incl. `Mixed`), `Describe`, and focus order for colours (max 3; auto-focus on the 3 strongest when empty) | `NetworkSelectionTests` |
| `NetworkExplorerViewModel` | SSID groups (`NetworkGroupItem`) with AP rows (`NetworkApItem`). Items are reconciled in place by key, so expansion, selection and scroll survive refreshes. Also: filters, search, sort, a 12-value sparkline history from fresh entries only, and scope text | `NetworkExplorerTests` |
| `SpectrumLayout` (pure) | Band axes (2.4/5/6 GHz, DFS 5250–5730, preferred channels), bonded block centres (5 GHz 40/80/160 tables; 6 GHz block formula; 2.4 GHz HT40± assumed by channel), occupancy, channel load (Σ clamp((RSSI+95)/45)), least busy channel, waterfall bins, intensity | `SpectrumLayoutTests` |
| `SpectrumView` | Draws spectrum or waterfall on one canvas, one strip per band. Redraws are coalesced at render priority. Short strips get fewer labels | `LiveSurvey_AllModesAndViews_LoadAndRender` |
| `SaturationDrawerView` / `SaturationStageView` | The test's setup and controls (right) and its live tiles or results (centre, and inline in History). Charts use `SaturationCharts` | render tests, `LiveSurveyIntegrationTests` |
| `Themes/HudTheme.xaml` | Keyed styles (panel, chips, segments, tri-state box). Implicit control styles still come from `MainWindow` | XAML load tests |

**Colour (validated with the dataviz palette validator, dark surface):**
- identity s1 `#3987E5`, s2 `#D95926`, s3 `#199E70` (all pairs separable, hence max 3);
- context `#5B5246` (lightened for outlines);
- status good `#0CA30C`, warning `#FAB219`, serious `#EC835A`, critical `#D03B3B` (always paired with a text label).

**Mode and view state** live in `MainWindowViewModel`:
- `SurveyMode`: Monitor / Capture / Saturation;
- `CenterView`: Spectrum / Waterfall / Grid / Test / Results;
- `SpectrumBand`, `LabelAll`;
- `IsTestRunning`, `ModeStatusText`;
- `WaterfallHistory` (60 fresh cycles, newest first).

The legacy `IsSsidView` / `DisplayAccessPoints` / item-check API is kept as a bridge onto `NetworkSelection`.

## 7b. Report pipeline precision (DR-0012)

- **Provenance.** `SaturationTestViewModel.CaptureEnvironmentAsync` builds `SaturationEnvironment` at start:
  - the app version (informational version), OS, machine and time zone;
  - adapter and MAC;
  - `INetworkAdapterProvider.GetDriverInfo`, which reads the NetCfgInstanceId-matched network-class registry key, read-only;
  - `GetConnectionInfoAsync`: `WlanQueryInterface`, opcode 7 `current_connection`.

  The environment is serialised inside `ConfigJson`.
- **Capture.** `WlanAdapter` maps `dot11BssPhyType`, `usBeaconPeriod` and `ulChCenterFrequency`, and parses BSS Load (element 11) with `ParseBssLoad`. All null when absent.
- **Geometry.** `Domain.ChannelMath` (block centres, occupancy, overlap, DFS) is shared by `SpectrumLayout` and `SaturationObservationBuilder`.
- **Analytics SAT-ANALYTICS-2.**
  - `Analyze(..., activeSeconds)`: the runner stores the measured active time (`SaturationTests.ActiveSeconds`). Otherwise `SaturationAnalyzer.ActiveSeconds` reconstructs it.
  - The last cycle's weight is `min(interval, active − lastElapsed)`.
  - The trend is null below `MinTrendSpanSeconds`.
- **Exports.** `SaturationExportService`:
  - JSON schema v2, the `_info.csv` key/value file (`InfoRows`), `LocalWithOffset` and `LocalIso`, and `PrecisionStatement` and `NeighbourDefinition` printed everywhere.
  - Charts are rendered in points: the scale is the raster width divided by the A4 content width (`ContentWidthPt`).
- **Schema 3.** `SchemaMigrator` adds missing nullable columns for every EF entity (`PRAGMA table_info` compared with the model) and refuses non-nullable additions.
- **Version.** `Directory.Build.props` is the only place the version lives. `tools/build_release.py` reads it.
- **Regeneration.** `dotnet test ... --filter Category=ReportRegeneration` with `WS_REGEN_DB` (a **copy** of a database) and `WS_REGEN_OUT` re-exports every test with the current code.

## 7e. Physical access points (DR-0013, 1.4)

Windows reports BSSIDs only. Device identity is **inferred** and always carried with its confidence and evidence.

**Capture (`WlanAdapter.ReadBssList`, Infrastructure):**
- `AccessPoint.TsfOffsetMs = WlanAdapter.TsfOffsetMs(ullTimestamp, ullHostTimestamp)`: the TSF in µs / 1000 minus the host FILETIME / 10 000. BSSIDs of one radio share one TSF timer, so their offsets agree within milliseconds. Measured on the owner's equipment: within 1 ms on one radio; seconds apart between radios and between boxes.
- `WlanAdapter.ParseWps(ies)`: element 221, OUI `00:50:F2`, type 4, big-endian TLVs. It reads `0x1021` manufacturer, `0x1023` model, `0x1024` model number, `0x1011` device name and `0x1047` UUID-E (16 bytes, read as a big-endian `Guid`). `AccessPointExtensions.WithWps` copies them onto `AccessPoint.Wps*`.
- Everything is null when absent: never guessed.

**Inference (`Application.PhysicalUnitGrouper`, pure apart from in-memory evidence):**

| Step | Rule |
| --- | --- |
| Input | `Observe(aps, nowUtc)` once per scan cycle (`MainWindowViewModel.ProcessCycleAsync`, before `Explorer.Update`). BSSIDs unseen for `Retention` (10 min) are forgotten with their evidence |
| Radio clock | For every same-band pair in one scan: `|Δ TsfOffsetMs| ≤ SameRadioToleranceMs` (5) counts *agree*, otherwise *disagree*. A pair is linked when agree > 0 and agree ≥ disagree, so a one-scan coincidence is outvoted |
| WPS | Equal `WpsUuid` links. Different UUIDs **block** any clock or family link between the two |
| MAC family (`SameFamily`) | `(o1 & ~0x06)`, o2, o3 and o5 equal, and the high nibble of o6 equal. o4 must be equal **unless both addresses are locally administered** (radio index in o4, e.g. `9A:3C:5E:1F/26`) |
| Union | Union-find over the three links. Automatic unit id `U-<smallest BSSID without colons>`, stable while that BSSID is heard |
| Overrides | `UnitOverrides.Assignments` (BSSID → unit id; `M-<BSSID>` for a split) are applied after the union. A unit that gains or loses a member by override becomes `Manual`. `Names` rename units. Persisted as JSON in the setting `PhysicalUnitOverrides` (`MainWindowViewModel.UnitOverridesSettingKey`) through `OverridesChanged` |
| Confidence | Manual > Single (1 member) > High (radio clock, WPS UUID or a WPS model present) > Medium (MAC family only) |
| Radios | Clock-linked members form a radio (`<band>#<bssid>`). Members without clock evidence group by band + channel (`<band>@<bssid>`) |
| Mesh candidates | A visible SSID (not hidden) broadcast by more than one unit sets `SameSsidUnitIds` on each, with an evidence line |
| Name | WPS manufacturer + model, else the OUI vendor, else "Access point", plus the last 5 characters of the root BSSID. A custom name replaces it |

**Selection (`NetworkSelection`):**
- **Third entry kind:** unit ids, resolved through `IUnitResolver` (implemented by the grouper; set as `Selection.UnitResolver`).
- **Methods:** `ToggleUnit`, `StateOfUnit`, `IsUnitSelected` and `UnitBssids()`.
- **`ToggleAp` on a member of a selected unit** converts the unit to fixed BSSIDs minus that one.
- **`Lock()`** snapshots the unit BSSIDs; `Matches` uses the snapshot until `Unlock()`.
- **`DeriveScope`:**
  - units only → `SingleUnit` / `MultipleUnits`;
  - units plus anything else → `Mixed`;
  - no units → unchanged 1.3 behaviour.

**Test configuration:**
- **Resolution at start.** `SaturationTestViewModel.BuildConfig` resolves units through `ResolveUnits`: `SaturationUnitTarget` carries the id, name, confidence and evidence, plus members with their SSID, band and radio key.
- **Members become fixed BSSIDs.** They are added to `TargetBssids`, so `SaturationTargetTracker` tracks them from cycle 1, exactly like the AP scopes, using the SSIDs taken from the unit members.

**Analytics (`SaturationAnalyzer.AnalyzeUnit`, SAT-ANALYTICS-3):**
- **Inputs.** Fresh cycles only.
- **Per unit:**
  - on-air % (≥ 1 member Observed);
  - best mean (mean of the per-cycle maximum).
- **Per radio key:**
  - consistency: the worst pair's mean |ΔRSSI| and the max |ΔRSSI| over cycles where both were Observed; a finding above `UnitConsistencyFindingDb` (3);
  - SSID-only outage per BSSID: cycles where another member of the radio was Observed but this one was not; a finding at or above `SsidOnlyOutageFindingPct` (5).
- **Findings** also cover Medium/Single confidence and a whole unit off air.
- `SaturationReport.Units` (optional trailing record parameter; `UnitSummaries` is never null).

**Exports:**
- **`UnitDefinition`** is printed with every unit result.
- **CSV:**
  - `_info.csv` gains `TargetUnits` and `UnitInference`;
  - `_units.csv` (one row per member, only when units were targeted).
- **JSON:** `units` and `unitDefinition`; `test.targetUnits`.
- **PDF:** section **6b**.
- No database change. Units travel inside `ConfigJson`, and summaries are re-derived.

**Explorer (`NetworkExplorerViewModel`, `NetworkExplorerView`):**
- **Grouping.** `Grouping` (`ExplorerGrouping.Network | AccessPoint`; `GroupingIndex` for the combo box).
- **Unit rows.** `UnitGroups` of `UnitGroupItem`, reconciled by id like SSID groups, with the same filters and sort. A unit's name also matches the search box.
- **Commands.** `ToggleUnitCommand` and `ToggleUnitExpandCommand`.
- **Corrections.** `RenameUnit`, `SplitAp`, `MoveAp`, `ResetUnit` and `MoveTargets`, all refused while locked (`CanEditUnits`).
- **Context menus.** Built in code-behind (`UnitRow_ContextMenuOpening`, `UnitMember_ContextMenuOpening`); the name prompt is a small modal window.

**Tests:**
- `PhysicalUnitTests`: parsers; MAC rules; grouping on fixtures derived from the owner's live inventory; coincidence and band guards; retention; overrides with persistence.
- `PhysicalUnitIntegrationTests`: selection, lock, tracker, explorer → config, settings round trip, analyzer, all three exports.
- The Live Survey render test adds `ui_ls_explorer_access_points.png`.
- Explicit `HardwareIeInventoryProbe` (read-only): prints the IE inventory, WPS and TSF offsets on real hardware.

**Not in 1.4 (DR-0013, later stages):**
- the IEEE OUI registry update;
- the AP-family rule pack;
- mesh-system analytics (best-node margin, node changes);
- guided node-position survey;
- active per-node tests;
- management-plane adapters.

## 7c. Installer detection

`Setup.Core.InstallDetector.Detect(snapshots, fileExists, loadManifest)` is pure and unit-tested.
- **Sources, in order of trust:** the install manifest on disk, `HKLM\SOFTWARE\WirelessScanner`, then the Apps & features entry (`InstallLocation`, else the folder of `UninstallString` / `DisplayIcon`; a `\Setup` maintenance folder is mapped to its parent). The 64-bit view is read before the 32-bit one.
- **Disagreements** become notes on the maintenance page. A missing `WirelessScanner.Presentation.exe` sets `NeedsRepair`.
- **Writes** use the 64-bit view explicitly and delete stale WOW6432Node copies. After registration setup calls `SHChangeNotify(SHCNE_ASSOCCHANGED)` so Windows drops its cached icons.

## 7d. Branding

`branding/` holds the master icon and all generated sizes (`tools/make_icons.py`; `--master <png>` swaps the design).
- Outputs: `windows/WirelessScanner.ico` (10 sizes), `png/`, and `android/` (mipmap sets, round, adaptive foreground with the background colour XML, and the Play Store 512 px icon).
- The setup embeds the icon. The app's `ApplicationIcon` is **on hold**: Malwarebytes ML heuristics quarantine the icon-embedded build. See CHANGELOG 1.3.0, Known issues.

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
dotnet test WirelessScanner.Tests/WirelessScanner.Tests.csproj                     # 177 tests
dotnet test WirelessScanner.Tests --filter "Category=Hardware"                       # real-adapter scan probe (manual)
$env:WS_SAT_MINUTES=15; dotnet test WirelessScanner.Tests --filter "Category=HardwareSaturation"   # 15-min real test (manual)
$env:WS_PREVIEW_OUT="C:\tmp\p"; dotnet test WirelessScanner.Tests --filter "Category=ReportPreview" # PDF page PNGs
$env:WS_UI_RENDER_OUT="C:\tmp\ui"; dotnet test WirelessScanner.Tests --filter "FullyQualifiedName~UiRenderTests|FullyQualifiedName~LiveSurveyRenderTests"  # UI PNGs (absolute path)
python tools\mutation_check.py --file <src> --find "<text>" --replace "<mutant>" --filter "<tests>"   # prove a test detects a defect
.\publish.bat              # = python tools\build_release.py : tests, self-contained publish, setup, split, EXE INSTALLER
python tools\sync_github_export.py --check
```

- **SDK:** .NET 10 SDK; it builds the net48 setup through reference assemblies.
- **Release size:** the 1.1 payload is about 85 MB in 5 parts. It grew from about 60 MB in 1.0 because of the Windows SDK projection needed for geolocation. Debug symbols are excluded from publish.

## 10. Configuration reference

| Setting | Where | Default |
| --- | --- | --- |
| Live scan interval / timeout | `MainWindowViewModel.ScanInterval` / `ScanTimeout` (requested from `ScanScheduler`) | 5 s / 6 s |
| Waterfall depth | `MainWindowViewModel.WaterfallDepth` | 60 fresh scans |
| Spectrum identity colours | `NetworkSelection.MaxFocusColors`, `NetworkExplorerViewModel.IdentityColors` | 3 |
| Saturation thresholds | `SaturationThresholds` (UI: Advanced) | drop/peak 8 dB, window 12, min baseline 3, dropout 2, hysteresis 3 dB, −67/−70/−80 dBm, freshness +5 s |
| Data directory | env `WIRELESSSCANNER_DATA_DIR` | `%ProgramData%\WirelessScanner` |
| Demo mode | env `WIRELESSSCANNER_DEMO=1` | off |
| API | Settings table `ApiEnabled`, `ApiPort`, `ApiKey` | off, 5005 |
| Access-point grouping | `PhysicalUnitGrouper.SameRadioToleranceMs`, `Retention`; corrections in Settings `PhysicalUnitOverrides` | 5 ms, 10 min, none |
| Unit findings | `SaturationAnalyzer.UnitConsistencyFindingDb`, `SsidOnlyOutageFindingPct` | 3 dB, 5 % |
