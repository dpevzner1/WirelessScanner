# Changelog

All notable changes. Evidence for each item is in `config/pipeline/wirelessscanner-remediation-control.json` and `LessonsLearned/`.

## [1.2.0] — 2026-10-16

Live Survey redesign (DR-0010, DR-0011). Every 1.1 feature is kept; the row-by-row proof is in [`docs/PARITY_CHECKLIST.md`](docs/PARITY_CHECKLIST.md).

### Added
- **Network explorer** replaces the flat SSID/AP list:
  - networks grouped by SSID with their access points underneath;
  - a three-state box per network: a whole network follows access points that appear later; single APs are a fixed list;
  - search, sort (signal / name / channel), filter chips (2.4 / 5 / 6 GHz, ≥ −70 dBm, Secured, Selected);
  - signal bars with tier text, and a 12-scan sparkline per AP;
  - the selection described in words, plus **Select shown** and **Clear**.
- **Spectrum redesign:**
  - width-true flat-top channel masks on the standard bonded channels;
  - identity colours for at most 3 networks (validated colour-blind-safe palette; the 3 strongest are coloured automatically when nothing is selected), with dashed context outlines for the rest;
  - −67/−70/−80 dBm reference lines and DFS shading;
  - a channel load strip with the **least busy** channel;
  - band selector **All / 2.4 / 5 / 6 GHz** (6 GHz is now drawn); **Label all**; tooltips; click a shape to select it.
- **Waterfall view:** the strongest signal per channel over the last 60 fresh scans.
- **Mode bar** in Live Survey: **MONITOR · CAPTURE · SATURATION TEST**, with a status line. Capture and a saturation test can run at the same time.
- **Saturation test inside Live Survey:**
  - targets come from the network explorer, with a new **Mixed** scope (whole SSIDs plus single APs);
  - the selection is locked while a test runs;
  - an empty selection asks before testing every network;
  - the centre **TEST** view shows live tiles, then results.
- **HISTORY / LOGS → SATURATION TESTS** sub-tab: past tests, with results and exports inline.
- **▶ RESUME** button for paused capture sessions (the command existed in 1.1 but had no button).
- `tools/mutation_check.py`: re-introduces a defect temporarily to prove that a test detects it.

### Changed
- One **scan scheduler** serves the live view, capture and the saturation test. Each test records one scan per its own interval from the shared scans.
- Spectrum shapes are coloured by network identity rather than signal tier. Tier stays visible as text, bars and reference lines.
- The separate **SATURATION TEST** tab is removed (moved into Live Survey and History).
- The telemetry grid gains a **WIDTH** column.
- The app is version 1.2.0.

### Fixed
- **F18:** concurrent scan requests on one adapter clashed. A saturation test running beside the live view could turn cycles STALE, because the second request overwrote the first one's completion slot. Requests are now coalesced in the scheduler and in `WlanAdapter`. Verified on hardware: 3 concurrent requests → 1 WLAN scan; live + test → 8/8 fresh, 0 failed.
- The live capture log header never showed **⏸ PAUSED** (a local value blocked the style trigger).

## [1.1.0] — 2026-10-15

### Added
- **Signal Saturation Test** tab (SPEC-SAT-001):
  - five target scopes;
  - duration presets, custom duration or until stopped;
  - pause/resume and timestamped markers;
  - live tiles, timeline and event feed;
  - drop, peak, dropout, channel-change and strongest-BSSID detection;
  - statistics, threshold exposure, trend, co-channel load and stability grade;
  - PDF / CSV bundle / JSON reports, and history with re-analysis.
- **Geolocation** for saturation tests: latitude/longitude/accuracy, entered manually or with **DETECT** (Windows Location), plus building, floor and zone. Shown in the PDF (with a map link), in JSON and on every CSV row (DR-0009).
- **Fresh-scan acquisition:** `WlanScan` plus the scan-complete notification; cycle status FRESH/STALE/FAILED shown in the status bar; beacon age per entry.
- **Demo mode** (`WIRELESSSCANNER_DEMO=1`) with simulated networks.
- **Self-elevation:** the app requests administrator rights through the UAC prompt instead of exiting.
- **Installer:**
  - update, repair, downgrade (with warning), modify shortcuts, and uninstall with an option to keep or delete data;
  - SHA-256-verified payload;
  - stale-file removal through an install manifest;
  - a maintenance copy registered in Apps & features;
  - all-users shortcuts;
  - setup logs;
  - .NET Framework 4.8, so no prerequisites (DR-0007).
- `tools/build_release.py` (release pipeline) and `tools/sync_github_export.py` (repository mirror).
- Full governance trail: remediation control, lessons, decisions, attestation ledgers, engine consultation log.
- Documentation: User Guide, Technical Reference, Status, Saturation Test Specification.

### Changed
- **Data location:** the database, logs and API key moved to `%ProgramData%\WirelessScanner`. 1.0 data is copied there automatically on first start; originals are kept (DR-0006).
- **Persistence:** all writes go through one ordered background queue, batched per scan cycle. This fixes samples lost to a foreign-key race at session start.
- **Scanning** runs on a background loop, so WLAN calls no longer block the UI thread.
- **Security labels** are derived from AKM suites: `WPA2-Personal`, `WPA2-Enterprise`, `WPA3-Personal`, `WPA2/WPA3-Personal`, `WPA3-Enterprise-192`, `OWE (Enhanced Open)`, …
- **Channel width** comes from HT/VHT/HE *operation* elements; blank when unknown.
- "Stationary Saturation" capture mode renamed to **Stationary Capture**; the dedicated saturation test replaces it for analysis.
- The installer targets .NET Framework 4.8 and the app is version 1.1.0.

### Fixed
- 6 GHz channel numbers were off by +2 (base frequency 5940 → 5950 MHz).
- Enterprise (802.1X) networks were labelled as PSK.
- RSSI variation was labelled "ms"; it is dB.
- The noise floor was shown as measured (a constant −95); it is now *unavailable* and SNR is labelled *estimated*.
- The wired-adapter path recorded fake RSSI/quality values; it now reports link details only.
- REST API port conflicts were only logged; they are now shown with guidance.

### Security
- The REST API **no longer accepts keys in the URL** (`?api_key=`). Use the `X-API-Key` header.
- Keys are compared in constant time; CORS is no longer `*`; 500 responses no longer leak exception text; `Cache-Control: no-store`.
- The 1.0 README contained a working API key. It was removed; **regenerate your key** if you used 1.0.

### Breaking changes for data consumers
- Session CSV headers: `SNR` → `SNR_Estimated_dB`, `Jitter` → `RSSI_Variation_dB`.
- Session JSON keys: `SNR` → `SnrEstimatedDb`, `Jitter` → `RssiVariationDb`.
- Security label strings changed (see above).
- Minimum OS: Windows 10 2004 (build 19041).

### Removed
- Unused LiveCharts package (and its OpenTK and SkiaSharp WPF dependencies). Native debug symbols are no longer shipped.
- `pack_installer.py` and `assemble_installer.py` (replaced by `tools/build_release.py`); empty `Class1.cs` stubs.

## [1.0.0] — 2026-05-31
Initial release: live survey, capture sessions, CSV/JSON/PDF export, REST API, split-file installer.
