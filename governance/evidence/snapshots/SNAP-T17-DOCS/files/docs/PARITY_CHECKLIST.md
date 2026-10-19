# Feature parity checklist — Live Survey redesign (DR-0010, DR-0011)

The owner's requirement (2026-10-16): *"ensure no current features, capabilities, or capacity is lost"*.

Every capability of the 1.1 **LIVE SURVEY** and **SATURATION TEST** tabs is listed below. Each row was inventoried **before** implementation (snapshot `SNAP-2026-10-16-REDESIGN-DESIGN`) and records where the capability lives in 1.2 and what proves it. Status uses the evidence labels in `PROJECT_CONVENTIONS.md` §3.

The proofs are cited as follows:

- Test names refer to `WirelessScanner.Tests`. The full suite ran attested as `TEST-REDESIGN-FULL-2` (148/148) in `governance/reviews/RV-2026-10-16-REDESIGN`.
- Images are renders of the real XAML with synthetic networks, in `docs/images/` (attested step `UI-RENDER-REDESIGN-2`).
- `HW-F18-1` is the real-adapter probe.

## Live Survey (1.1)

| # | Capability (1.1) | Location in 1.2 | Proof | Status |
| --- | --- | --- | --- | --- |
| L1 | Adapter combo (name, description) | Explorer header combo | `ui_ls_monitor_auto.png` | Validated |
| L2 | Adapter diagnostics: class, link status, PnP ID | Collapsible **Adapter details** under the combo (adds the MAC/IP details line) | XAML loads in `LiveSurvey_AllModesAndViews_LoadAndRender`; bindings unchanged | Validated |
| L3 | PnP hot-plug refreshes the adapter list | Unchanged (`WM_DEVICECHANGE` → `LoadAdaptersAsync`) | code unchanged in `MainWindow.xaml.cs` | Validated |
| L4 | SSID view / AP view switch | Replaced by the grouped tree (a group = SSID, its rows = APs). The `IsSsidView` API and item check bridge are kept | `LegacyBridges_StillFilterLiveViewAndCapture`, `FilteredActiveSessionLogs_FiltersLogsBasedOnCheckedKeys`, `PerformScanAsync_FiltersCapturedTelemetry_BasedOnCheckedKeys` | Validated |
| L5 | Tick networks to filter graph, grid, log and capture | Explorer selection (`NetworkSelection.Matches`), whole SSIDs or single APs | `NetworkSelectionTests`, `LegacyBridges_…`, `ui_ls_monitor_selected.png`, `ui_ls_grid.png` | Validated |
| L6 | Per-row SSID, BSSID, vendor, channel, RSSI, quality, SNR | Group line: APs · security · bands · vendor; AP row: BSSID, channel · band · width, signal bars + dBm, 12-scan sparkline; tooltip: quality, SNR est., security | `NetworkExplorerTests`, `ui_ls_monitor_selected.png` | Validated |
| L7 | FORCE SCAN TICK | Explorer footer **FORCE SCAN** → `ScanScheduler.ScanNowAsync` | `SingleFlight_ConcurrentRequestsShareOneProviderScan`, `Waterfall_KeepsSixtyFreshScans_NewestFirst` | Validated |
| L8 | BEGIN SESSION CAPTURE (dialog: name, facility, scope, zone, notes, mode) | **CAPTURE** mode drawer → same `StartSessionCommand` and dialog | `ui_ls_capture_drawer.png`; existing session tests | Validated |
| L9 | PAUSE / STOP capture; STOP offers export | Capture drawer. **RESUME** added (1.1 had no resume button although the command existed) | `ui_ls_capture_recording.png`, `ui_ls_capture_paused.png`; existing lifecycle tests | Validated |
| L10 | ● RECORDING / ⏸ PAUSED indicator | Mode bar status, drawer and log header. Fixed a 1.1 defect: the log header never showed PAUSED (a local value blocked the trigger) | `ui_ls_capture_paused.png` | Validated |
| L11 | Session metadata banner (session, facility, scope, zone, mode, notes) | Unchanged banner above the capture log; summary line in the drawer | `ui_ls_capture_recording.png` | Validated |
| L12 | Live captured telemetry log (timestamp, SSID, BSSID, ch, band, RSSI, fluctuation, SNR est., quality, VAR dB), 200 rows | Centre, below the view while recording (same columns, same `FilteredActiveSessionLogs`) | `ui_ls_capture_recording.png`; existing log tests | Validated |
| L13 | Spectrum graph 2.4 GHz and 5 GHz shown together | Band selector **All** (default) stacks 2.4 / 5, plus 6 GHz when anything is heard there | `SpectrumBands_AllShows6GhzOnlyWhenHeard`, `ui_ls_monitor_auto.png` | Validated |
| L14 | Telemetry grid (SSID, BSSID, band, channel, RSSI, SNR est., quality, security, vendor) | View selector **GRID**: same columns plus WIDTH | `ui_ls_grid.png` | Validated |
| L15 | Channel width drawn | Width-true masks on standard bonded channels (5/6 GHz) | `BlockCenter_UsesStandardBondedChannels` (12 cases), `Occupancy_And_Load_ReflectWidthAndSignal` | Validated |
| L16 | Labels with SSID + dBm on the graph | Coloured networks are labelled; **Label all** labels every AP (labels that would overlap are moved up or dropped) | `ui_ls_spectrum_5ghz_labelall.png` | Validated |
| L17 | Tier colours (green/amber/red) | Tier is a text label + bars in the explorer, a tooltip and −67/−70/−80 reference lines; shapes carry identity colour (DR-0010) | `ui_ls_monitor_auto.png` | Validated |
| L18 | Status bar: status text, last scan cycle, database size | Unchanged; last scan is also shown in the mode bar | all `ui_ls_*.png` | Validated |
| L19 | Live scan every 5 s, fresh-scan rules | `ScanScheduler` at 5 s; the existing fresh/stale rules are unchanged in `ProcessCycleAsync` | `Loop_RunsAtShortestRequestedCadence_AndStopsWhenReleased`; existing `FreshScanCaptureTests` | Validated |
| L20 | Live APs pushed to REST `/api/live` | Unchanged, on every processed cycle | `RestLiveFeed_IsUpdatedOnEveryProcessedCycle`, `ApiTests` | Validated |

## Saturation Test tab (1.1)

| # | Capability (1.1) | Location in 1.2 | Proof | Status |
| --- | --- | --- | --- | --- |
| S1 | Adapter picker | The Live Survey adapter (shared). A wired adapter is refused with a message | `AttachedTest_ScopeComesFromSharedSelection_IncludingMixed` | Validated |
| S2 | DISCOVER (two fresh scans), candidate table, search, select shown / clear | Explorer: always live, with search, chips, **Select shown** and **Clear**. Standalone DISCOVER mode is kept in the view model and its screen for headless use and tests | `SelectShown_SelectsWholeVisibleGroups_AndHiddenPerBssid`, `SaturationView_AllThreeSteps_LoadAndRender` | Validated |
| S3 | Scopes: single/multiple AP, single/multiple SSID, all visible | Derived from the selection; **Mixed** (SSIDs + fixed APs) added | `ScopeDerivation_CoversAllCasesIncludingMixed`, `MixedScope_…`, `ExistingScopes_ProduceIdenticalReports_AfterMixedSupport` | Validated |
| S4 | Hidden SSID only by BSSID | Hidden groups cannot be ticked as a whole; their APs can | `HiddenSsid_GroupSelectionRefused_ButApSelectionAllowed`, `Groups_TriState_Hidden_Filters_Search_Sort` | Validated |
| S5 | Duration presets, custom 1–1440 min, until stopped; interval 5–60 s | Saturation drawer, section 2 | `Validation_ScopeRulesCustomDurationAndThresholds` | Validated |
| S6 | Location (required), name, facility, operator, client, notes | Saturation drawer, section 3 + **Report details** expander | `Validation_BlocksStartUntilAdapterTargetsAndLocationAreValid`, `ui_ls_saturation_setup_invalid.png` | Validated |
| S7 | Geolocation lat/long/accuracy, DETECT, building/floor/zone, validation | Saturation drawer, section 3 | `GeoTagTests`, `ui_ls_saturation_setup_ready.png` | Validated |
| S8 | Advanced thresholds with validation | Saturation drawer, **Advanced thresholds** | `Validation_ScopeRulesCustomDurationAndThresholds` | Validated |
| S9 | Keep PC awake; battery warning | Drawer checkbox; warning in the run status | `KeepAwakeAndBatteryWarning_WorkInAttachedMode` | Validated |
| S10 | Summary line and first validation problem; START enabled only when valid | Pinned at the bottom of the drawer | `AttachedTest_…` (summary), `ui_ls_saturation_setup_invalid.png` / `_ready.png` | Validated |
| S11 | Running: progress, elapsed/remaining, cycle counts, warnings | Drawer (run status) | `ui_ls_saturation_running.png` | Validated |
| S12 | Live tiles with text labels; live timeline with bands, missing ticks, markers | Centre **TEST** view | `Tile_StatusAlwaysHasTextLabel`, `ui_ls_saturation_running.png` | Validated |
| S13 | Add marker; event feed | Drawer | `Flow_SetupRunningResults_WithMarkersAndExports`, `ui_ls_saturation_running.png` | Validated |
| S14 | Pause / Resume; Stop with confirmation | Drawer | `Stop_RequiresConfirmation`, `Flow_…` | Validated |
| S15 | Results: target table, selected-target timeline, events, PDF / CSV / JSON export, NEW TEST | Centre **TEST** view shows results; exports and NEW TEST in the drawer. NEW TEST returns to the spectrum | `Flow_…`, `RunningTest_LocksSelection_DrivesModeAndCentreView_AndCaptureKeepsWorking`, `ui_ls_saturation_results.png` | Validated |
| S16 | History: list (date, name, location, geo, scope, cycles, status), open results, delete with confirmation, refresh | HISTORY / LOGS → **SATURATION TESTS** (same columns, results and exports inline). Opening is refused while a test runs | `SaturationHistory_OpenDeleteRefresh_AndOpenRefusedWhileATestRuns`, `ui_history_saturation_tests.png` | Validated |
| S17 | Selection cannot change mid-test | `NetworkSelection.Lock()` while the test runs; explorer shows **LOCKED** | `Lock_RejectsEveryMutation`, `RunningTest_LocksSelection_…` | Validated |
| S18 | Test runs at its own interval independently of the live view | Scheduler decimation (tolerance = half the effective interval) | `Runner_DecimatesSharedCadenceToItsOwnInterval_AndLiveViewKeepsItsPace` | Validated |

## Capacity

| # | Capacity | Proof | Status |
| --- | --- | --- | --- |
| C1 | Capture and saturation test can run at the same time | `RunningTest_LocksSelection_DrivesModeAndCentreView_AndCaptureKeepsWorking`; real adapter `HW-F18-1` (live cadence + test: 8/8 fresh, 0 failed) | Validated |
| C2 | No extra scans: one in-flight scan per adapter (F18) | `SingleFlight_ConcurrentRequestsShareOneProviderScan`; real adapter `HW-F18-1` (3 concurrent requests → one WLAN scan) | Validated |
| C3 | Test durations up to 24 h and until stopped | Runner duration logic unchanged; `DurationOptions` unchanged | Validated (code unchanged; no new long run) |
| C4 | 100+ APs listed without losing selection or expansion state on refresh | `Reconcile_KeepsItemInstancesExpansionAndSparklines_Over120Aps` | Validated |

## Changes in behaviour (intentional, owner-approved proposals)

- The standalone **SATURATION TEST** tab is gone. The test is a Live Survey mode, and past tests are under HISTORY / LOGS (DR-0011, Q1).
- Spectrum shapes are coloured by **network identity** (max 3, validated palette), not by signal tier. Tier stays visible as text, bars and reference lines (DR-0010, Q3).
- Labels default to the coloured networks only; **Label all** restores the 1.1 behaviour.
- The **Waterfall** view is new (Q4). The **Mixed** scope is new (Q2).
- In 1.1, switching the SSID/AP view cleared the selection. The legacy API keeps that behaviour; the UI no longer needs a switch.
