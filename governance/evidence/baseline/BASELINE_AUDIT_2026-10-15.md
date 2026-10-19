# WirelessScanner baseline audit — 2026-10-15

| Field | Value |
| --- | --- |
| Baseline ID | `BL-2026-10-15` |
| Captured | 2026-10-15 (America/Chicago), ~21:00–21:50 UTC |
| Auditor | Claude Code (claude-opus-5-5), role implementer, operator demitri.pevzner |
| Attestation ledger | `governance/reviews/RV-2026-10-15-BASELINE/ATTESTATION-LEDGER.jsonl` |
| Code state | Main project folder as found (no edits before this report); GitHub mirror HEAD `9e10314` |
| Build | `dotnet build WirelessScanner.slnx` (SDK 10.0.301): 0 errors, 37 warnings → `build-20261015.txt` |
| Tests | 20/20 passed (NUnit) → `test-20261015.txt`, re-run under the ledger as step `B-TEST` |
| Independence | Not independent: the auditor is also the implementer. Independent review is pending. |

Evidence labels follow `LessonsLearned/ITERATIVE_DOCUMENTATION_STANDARD.md`: **Validated** (exercised by a test or harness), **Observed** (seen in code, logs or a run, but not under an approved test), **Designed** (documented or coded, not exercised), **Blocked**.

## Findings

### F01 — Windows is never asked to scan; cached lists are recorded as new samples (P0, Observed)
- `WlanAdapter.TriggerHardwareScanAsync` (`WirelessScanner.Infrastructure/WlanAdapter.cs:239`) has no caller in Presentation, Application or Infrastructure (grep, 2026-10-15).
- `MainWindowViewModel.PerformScanAsync` (`MainWindowViewModel.cs:825`) runs every 2 s from a `DispatcherTimer` (`:778`) and calls only `GetAccessPointsAsync`, which returns Windows' cached BSS list (`WlanGetNetworkBssList`).
- Impact: duplicate, stale values are written as fresh samples. Jitter, fluctuation and every statistic are biased toward zero change or show step changes. The engine's telemetry source says this directly: polling faster than native updates "creates duplicate values and false resolution" (`DevOpsAgent/Source/Telemetry-Capture-Architecture-Engineering.md`, Time/sampling section).
- Freshness data is available but discarded: `WLAN_BSS_ENTRY.ullHostTimestamp` / `ullTimestamp` are marshalled (`WlanAdapter.cs:128-129`) but never used.

### F02 — Session and sample writes race; samples are lost (P0, Observed in the runtime log)
- `SessionManager.StartSession` starts the session insert without awaiting it (`SessionManager.cs:59`). `SaveSample` does the same for each sample (`:159`). Nothing orders the two.
- `publish/logs/wireless_scanner_log20260531.txt`: `SQLite Error 19: 'FOREIGN KEY constraint failed'` at `SessionRepository.SaveSampleAsync` from `SessionManager.SaveSample`, which matches a sample insert landing before its session row.
- Each sample opens its own DbContext and transaction, so there is no batching and no backpressure.

### F03 — The threading model differs from the README and plan (P1, Observed)
- The README says "STA Thread Scan Worker" and a "System.Threading.Channels queue". Neither exists: the scan runs on the UI dispatcher timer, and `GetAccessPointsAsync` is synchronous (`Task.FromResult`), so WLAN calls block the UI thread.

### F04 — Data and log locations depend on the working directory and are deleted on uninstall (P1, Observed)
- `Data Source=wireless_scanner.db` (`App.xaml.cs` ConfigureServices; `WlanDbContext.cs` OnConfiguring) and `logs/` (`AppLogger.cs`) are relative paths. `/api/stats` reads `new FileInfo("wireless_scanner.db")`.
- The installer deletes the whole install directory on uninstall (`WirelessScanner.Setup/Program.cs:569`), which destroys sessions, logs and the API key without warning.

### F05 — REST API security (P1, Observed)
- A live-looking key (`ws_live_f1cf…`) appears in README examples. `publish/API/api_key.md` holds a key in plaintext.
- The key is accepted from the query string (`RestApiServer.cs`, `request.QueryString["api_key"]`). CORS is `*`. The key comparison is not constant-time.
- Port conflicts (`HttpListener` 5005, three occurrences in the 2026-05-30 log) surface only as log errors.

### F06 — Installer lifecycle defects (P1, Observed)
- The `UninstallString` points at the setup exe wherever it was run from (`Program.cs:615`).
- `NoModify` and `NoRepair` are set (`:616-617`), so there is no Repair option. The version is hard-coded to `1.0.0` and there is no version comparison, so downgrades are silent.
- Update leaves stale files behind. Changing the folder during an update orphans the old install. Unticking a shortcut does not remove it.
- After an uninstall, the finish page says "successfully installed" (`FinishedPage`).
- `pack_installer.py` publishes Setup with `--self-contained false` (requires the .NET 8 Desktop Runtime), which contradicts the "no prerequisites" claim.
- There are no data-retention options and no confirmation before deleting.

### F07 — Documentation and code disagree, and no trail exists (P1, Observed)
- `PLAN/` is dated 2026-05-27 and still says Status: Planning, with Stage 5/6 gates Pending and every validation checkbox unchecked, although the app shipped (GitHub commit `5e18a55`).
- Claims with no implementation: LiveCharts2 charts (the charts are hand-drawn on WPF `Canvas`), band filter, SSID/AP toggle as specified, band-balance and radar charts, OUI database, wired fallback (the adapter list filters to Wi-Fi only, `MainWindowViewModel.cs:798`), PNP refresh of the adapter list.
- The plan says .NET 8; the code targets net10.0. There is no changelog, no decision record and no lessons record.

### F08 — Duplicated source without a sync process (P2, Observed)
- `GitHub Export/` is a full second copy of the source. It already differs: Setup, `*.py`, README and `.slnx` (`diff -rq`, 2026-10-15).

### F09 — Missing values are recorded as numbers; derived values are not labelled (P1, Observed)
- The noise floor is a hard-coded constant −95 dBm presented as a measured field (`WlanAdapter.cs:333`). SNR is computed from that constant. `SessionRepository` maps NoiseFloor back as −95.
- The wired path encodes "down" as RSSI −100 and "up" as 0, with Quality 0/100 (`MainWindowViewModel.cs:844-867`).
- An AP missing from a scan writes nothing, so absence is indistinguishable from not being in scope. This matches the engine's anti-pattern "missing values conflated with zero" (`TelemetryApp-ReadOnly-Case-Study.md`).

### F10 — No signal saturation test (P1, user request 2026-10-15)
- "Stationary Saturation" exists only as a mode label (`SessionCaptureDialog`, `SessionModeText`). There is no configured duration, target scope, peak/drop detection, channel-hop detection, availability or dropout accounting, or saturation report.

### F11 — Repository clutter (P3, Observed)
- `Class1.cs` stubs in Domain, Application and Infrastructure; `publish/WirelessScanner.Presentation.exe.old`; an 84 MB `libSkiaSharp.pdb` in publish; an unused LiveCharts package reference; a React/Vite prototype (`src/`, `index.html`, `package.json`, `dist/`, `node_modules/`) with unpinned `latest` dependencies.

### F12 — Test gaps (P2, Observed)
- No tests cover IE parsing (channel width, security, rates), frequency-to-channel mapping, the race in F02, scan freshness, the installer, or the saturation analytics.

### F13 — Adapter loss and scan errors (P2, Observed in the runtime log)
- `WlanGetNetworkBssList` returned error 1168 (ERROR_NOT_FOUND) five times on 2026-05-28. The UI shows only "Scan failure: …". Nothing re-enumerates the adapter or backs off, and nothing records the failure as a gap in an active session.

## Build warnings worth tracking
- `NU1701` for `OpenTK.GLWpfControl` / `SkiaSharp.Views.WPF` (pulled in by the LiveCharts package that the UI does not use).
- `CS0414` `_selectedSessionSampleCount` assigned but never used.

## Engine consultation summary (read-only)
See `governance/evidence/consultation/` (raw query JSON) and `docs/consultation/ENGINE_CONSULTATION_LOG.md`. The engine's keyword index has no concepts for RSSI, jitter, roaming, percentile, WLAN or interference. Its RF and Wi-Fi coverage is a gap. Its telemetry-capture and UI/UX sources apply.
