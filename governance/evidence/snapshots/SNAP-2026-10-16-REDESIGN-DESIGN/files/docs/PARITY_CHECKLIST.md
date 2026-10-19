# Feature parity checklist — Live Survey redesign (DR-0010, DR-0011)

The owner's requirement (2026-10-16): *"ensure no current features, capabilities, or capacity is lost"*.

Every capability of the 1.1 **LIVE SURVEY** and **SATURATION TEST** tabs is listed below. Each row was inventoried **before** implementation and records where the capability lives afterwards and what proves it. Status uses the evidence labels in `PROJECT_CONVENTIONS.md` §3.

## Live Survey (1.1)

| # | Capability (1.1) | New location | Proof | Status |
| --- | --- | --- | --- | --- |
| L1 | Adapter combo (name, description, MAC/IP) | Explorer header (unchanged combo template) | UI render `ui_ls_monitor.png` | pending |
| L2 | Adapter diagnostics: class, link status, PnP ID | Collapsible "Adapter details" under the combo | UI render | pending |
| L3 | PnP hot-plug refreshes the adapter list | Unchanged (`WM_DEVICECHANGE`) | code unchanged | pending |
| L4 | SSID view / AP view switch | Replaced by the grouped tree; the legacy `IsSsidView` API is kept as a bridge | `FilteredActiveSessionLogs_FiltersLogsBasedOnCheckedKeys`, `PerformScanAsync_FiltersCapturedTelemetry_BasedOnCheckedKeys` | pending |
| L5 | Tick networks to filter graph, grid, log and capture | Explorer selection (`NetworkSelection.Matches`) | `NetworkSelectionTests` + legacy tests | pending |
| L6 | Per-row SSID, BSSID, vendor, channel, RSSI, quality, SNR | Explorer rows (vendor/security/band in the group line; channel/width per AP; dBm + tier) + tooltip with quality and SNR est. | Explorer tests + render | pending |
| L7 | FORCE SCAN TICK | Explorer footer button → `ScanScheduler.ScanNowAsync` | `ScanSchedulerTests.ScanNow…` | pending |
| L8 | BEGIN SESSION CAPTURE (dialog: name, facility, scope, zone, notes, mode) | Capture drawer → same dialog | `ViewModelTests` + render | pending |
| L9 | PAUSE / STOP capture; STOP offers export | Capture drawer | existing lifecycle tests + render | pending |
| L10 | ● RECORDING / ⏸ PAUSED indicator | Mode bar status + log header | render | pending |
| L11 | Session metadata banner (session, facility, scope, zone, mode, notes) | Capture drawer | render | pending |
| L12 | Live captured telemetry log (timestamp, SSID, BSSID, ch, band, RSSI, fluctuation, SNR est., quality, VAR dB), 200 rows | Centre, below the spectrum while recording | render + existing tests | pending |
| L13 | Spectrum graph 2.4 GHz and 5 GHz shown together | Band selector **All** (default) stacks 2.4 / 5 (/ 6 when present) | `SpectrumLayoutTests` + render | pending |
| L14 | Telemetry grid (SSID, BSSID, band, channel, RSSI, SNR est., quality, security, vendor) | View selector **Grid** (same columns) | render | pending |
| L15 | Channel width drawn | Width-true masks | `SpectrumLayoutTests` | pending |
| L16 | Labels with SSID + dBm on the graph | Focused networks labelled; **Label all** option restores labels for every AP | render | pending |
| L17 | Tier colours (green/amber/red) | Tier is shown by label, tooltip and bands; identity colour replaces tier colour on shapes (DR-0010) | render | pending |
| L18 | Status bar: status text, last scan cycle, database size | Unchanged | render | pending |
| L19 | Live scan every 5 s, fresh-scan rules | `ScanScheduler` (5 s) | `ScanSchedulerTests` | pending |
| L20 | Live APs pushed to REST `/api/live` | Unchanged (on each cycle) | `ApiTests` | pending |

## Saturation Test tab (1.1)

| # | Capability (1.1) | New location | Proof | Status |
| --- | --- | --- | --- | --- |
| S1 | Adapter picker | Live Survey adapter (shared) | `SaturationViewModelTests` (attached selection) | pending |
| S2 | DISCOVER (two fresh scans), candidate table, search, select shown / clear | Explorer (always live), search, chips, Clear. The standalone DISCOVER mode is kept in the VM for headless use and tests | existing `SaturationViewModelTests` + new attached tests | pending |
| S3 | Scopes: single/multiple AP, single/multiple SSID, all visible | Derived from the selection; **Mixed** added | `NetworkSelectionTests.Scope…`, `SaturationMixedScopeTests` | pending |
| S4 | Hidden SSID only by BSSID | Group checkbox disabled for hidden SSIDs | `NetworkSelectionTests` | pending |
| S5 | Duration presets, custom 1–1440 min, until stopped; interval 5–60 s | Saturation drawer | VM tests | pending |
| S6 | Location (required), name, facility, operator, client, notes | Saturation drawer | VM tests | pending |
| S7 | Geolocation lat/long/accuracy, DETECT, building/floor/zone, validation | Saturation drawer | `GeoTagTests` | pending |
| S8 | Advanced thresholds with validation | Saturation drawer | VM tests | pending |
| S9 | Keep PC awake; battery warning | Saturation drawer / run status | VM tests | pending |
| S10 | Summary line and first validation problem; START enabled only when valid | Saturation drawer (pinned) | VM tests + render | pending |
| S11 | Running: progress, elapsed/remaining, cycle counts, warnings | Drawer (run status) | render | pending |
| S12 | Live tiles with text labels; live timeline with bands, missing ticks, markers | Centre **Test** view | `Tile_StatusAlwaysHasTextLabel` + render | pending |
| S13 | Add marker; event feed | Drawer | VM tests + render | pending |
| S14 | Pause / Resume; Stop with confirmation | Drawer | `Stop_RequiresConfirmation` | pending |
| S15 | Results: target table, selected-target timeline, events, PDF / CSV / JSON export, NEW TEST | Centre **Results** view (+ History) | `Flow_SetupRunningResults…` + render | pending |
| S16 | History: list (date, name, location, geo, scope, cycles, status), open results, delete with confirmation, refresh | HISTORY / LOGS → **Saturation tests** | VM tests + render | pending |
| S17 | Selection cannot change mid-test | Selection lock | `NetworkSelectionTests.Lock…` | pending |
| S18 | Test runs at its own interval independently of the live view | Scheduler decimation | `SaturationRunnerSchedulerTests` | pending |

## Capacity

| # | Capacity | Proof | Status |
| --- | --- | --- | --- |
| C1 | Capture and saturation test can run at the same time | Scheduler shared; runner + capture test | pending |
| C2 | No extra scans: one in-flight scan per adapter (F18) | `ScanSchedulerTests.SingleFlight…` | pending |
| C3 | Test durations up to 24 h and until stopped | unchanged runner | pending |
| C4 | 100+ APs listed without losing selection or expansion state on refresh | `NetworkExplorerTests.Reconcile…` | pending |
