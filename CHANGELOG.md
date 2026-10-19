# Changelog

All notable changes. Evidence for each item is in `config/pipeline/wirelessscanner-remediation-control.json` and `LessonsLearned/`.

## [Android 1.5.0 — preview builds b1–b6] — 2026-10-19

**Android tablet port** (DR-0015). WirelessScanner 1.5.0 is ported to an Android tablet as a native Kotlin + Compose app (`AndroidApp/`). It uses the tablet's own Wi‑Fi and is sideloaded; it is not on the Play Store.

### Added
- **Shared logic ported one for one** to a pure Kotlin `core` module. It covers channel math, selection and physical units, saturation analytics (SAT-ANALYTICS-3), connection analytics (CONN-ANALYTICS-1), reports, the REST API DTOs and the responder protocol (WSR1).
  - The C# code generates parity fixtures (`AndroidParityFixtures.cs`, `Category=AndroidParity`), and Kotlin checks against them: 18/18 tests, 7 parity suites.
- **Tablet features:**
  - Live Survey: Monitor, Capture, Saturation test and Connection test;
  - spectrum, waterfall and grid views;
  - history;
  - PDF/CSV/JSON exports through *Share or send* (email or any installed app) or *Save a copy* (any cloud provider);
  - the REST API (localhost by default) and the responder role;
  - a foreground service for long tests.
- **Adaptive layout:**
  - three panes when the screen is at least 1360 × 760 dp;
  - on smaller screens, one main view with a selection bar and a NETWORKS slide-over from the right. It stays open for multi-select and closes on CLOSE ▸, a tap outside, or Back. Its row order is pinned while it is open.
- **Release APK** `WirelessScanner-Android-1.5.0.apk`:
  - in the repository folder `ANDROID APK`, with SHA-256 and the signer certificate;
  - built by `tools/build_android_release.py`;
  - byte-identical to the b6 build that passed the device gate and smoke test.

  User install guide: `AndroidApp/docs/INSTALL_GUIDE.md`.
- **Signing secrets removed from source.** The sideload keystore passwords now come from `local.properties` or environment variables, and are never committed.
- **Device gate** `tools/android_deploy.ps1`: preflight, deploy copy with a read-back SHA-256 check, install, launch, foreground check and screenshot.

### Platform limits (stated in the app and in reports)
- **Wi‑Fi scan throttling.** Android allows an app 4 scans per 2 minutes; while throttled the app shows `STALE (THROTTLED)` and scans every 30 s. No permission or install option lifts the limit; the owner can turn it off in Developer options. See `AndroidApp/docs/WIFI_SCAN_THROTTLING.md`.
- **No driver frame-retry counters.** Android has no equivalent of the Windows driver counters; the related tiles say so.

### Process
- **Two-way Windows ↔ Android parity** is now a hard control (DR-0017): every change to either app is carried to the other or recorded as a limit or exception.
- **Lessons:**
  - LL-2026-10-19-ANDROID-DEVICE-AUTH;
  - LL-2026-10-19-ANDROID-PORT;
  - LL-2026-10-19-ANDROID-SCAN-THROTTLING.

## [1.5.0] — 2026-10-18

**Connection test.** WirelessScanner can now measure the quality of the connection this PC is already on, end to end, and show **where** problems happen (DR-0014, stages A, B and C). This release also contains everything from 1.4.0 (physical access points), which was built but never installed.

### Added
- **⇄ CONNECTION TEST**, a fourth Live Survey mode built like the saturation test:
  - a setup panel (what / how long / where);
  - a **CONNECTION** centre view;
  - results;
  - a **CONNECTION TESTS** history sub-tab.

  One line under the mode bar now says what each mode does.
- **What it does.** It probes each part of the path from the Wi‑Fi address only:
  - the Wi‑Fi link: signal, PHY rate, driver frame retries, roams, disconnects;
  - the router;
  - the first ISP hop, found automatically;
  - the internet: Cloudflare, Google and Quad9 by TCP 443 and ICMP (editable);
  - DNS;
  - a small HTTPS request.

  It never connects, disconnects or changes the connection, and refuses to start without a Wi‑Fi connection.
- **Analytics `CONN-ANALYTICS-1`:**
  - loss, availability, round trip p50/p95/p99, jitter and delay variation per target and segment;
  - **outages and latency spikes attributed to the first failing part of the path** (Wi‑Fi link, router, ISP, internet, DNS, web);
  - ISP-router ping rate-limiting is not counted as a fault;
  - verdicts for browsing, streaming, video calls and gaming;
  - an E-model voice estimate;
  - unmeasured gaps (sleep, stalls) recorded, never invented.
- **Reports:**
  - PDF with timelines and the method, definitions and limits;
  - CSV bundle (`_probes`, `_link`, `_segments`, `_events`, `_info`, …);
  - JSON `wirelessscanner.connection_report.v1`;
  - REST `GET /api/connection` and `GET /api/connection/tests`.
- **Run with a saturation test:** one option starts a saturation test of the connected access point (its whole unit) on the same timeline.
- **● CONNECTED** badge in the network list, in both grouping views.
- **Stage B, speed and responsiveness.**
  - Measures download and upload capacity, latency under load, the **bufferbloat grade** (A+ … F) and approximate responsiveness.
  - Runs on demand (with a confirmation), at the start, or every 15, 30 or 60 minutes.
  - Server: the LAN responder or an HTTP server you control. No public provider is preset.
- **Stage C, two-way Wi‑Fi tests.**
  - New **`WirelessScanner.Responder`** (cross-platform console; `WirelessScanner.Responder.zip` in the release) to run on a wired machine.
  - Measures per-direction loss, reordering, delay variation and MOS estimate, plus LAN capacity.
  - Traffic is authenticated with a pairing key (HMAC); the responder never answers unsigned packets or amplifies.
- **SETTINGS tab** (was REST API CONFIG): **REST API** and the new **TEST SERVERS** page. The responder key is stored with Windows DPAPI.
- **Wi‑Fi link reading** (`ReadLinkAsync`) with driver frame counters. RSSI falls back to the associated BSS entry when the driver lacks the RSSI query.
- **Demo mode** simulates a connection: no traffic, occasional loss, a brief ISP outage every 5 minutes.
- **Database schema 4:** connection-test tables, added in place.
- **Docs:** [`docs/CONNECTION_TEST_SPEC.md`](docs/CONNECTION_TEST_SPEC.md); user guide §5a and §8b; technical reference §7f.

### Changed
- The saturation centre-view button reads **SATURATION** (was TEST); the mode-bar status reads *● TESTING SIGNAL* / *● TESTING CONNECTION*.
- The `tools/api_smoke.py` live check now also checks the connection endpoints (default expected version 1.5.0).

### Known issues
- Round trip only. One-way delay needs synchronised clocks; stage C gives per-direction variation and loss.
- Stage B against a real server and stage C on real Wi‑Fi have not yet been run on the owner's network.
- The app icon is still not embedded (see 1.3.0).

## [1.4.0] — 2026-10-18

**Physical access points.** You can now see, select and test a router or AP as **one device**, with every SSID and radio it broadcasts, and spot mesh nodes and extenders (DR-0013, stage 1). This release also contains the 1.3.1 fixes; 1.3.1 was built but never installed.

### Added
- **Explorer: GROUP BY Network | Access point.** In *Access point* view each row is one inferred physical unit, showing:
  - its name: WPS manufacturer and model when advertised, otherwise the vendor, plus the last digits of its first BSSID;
  - a **confidence badge**: HIGH, MEDIUM, SINGLE or MANUAL;
  - radios, SSIDs, BSSIDs and bands;
  - the evidence, in a tooltip;
  - a **mesh / extender candidate** hint when another unit broadcasts the same SSID.

  Member rows list each SSID (hidden ones included) with BSSID, channel, sparkline and signal.
- **How units are inferred (passive):**
  - **radio clock:** the TSF offsets of same-band BSSIDs agree within 5 ms and keep agreeing over scans;
  - **WPS device identity:** UUID-E;
  - **MAC-address family:** the locally-administered bit, the SSID index in the last digit, and the radio index in the 4th byte for locally-administered schemes. Different WPS identities are never merged.

  The rules come from a read-only inventory of 20 real BSSIDs in the owner's environment.
- **Corrections.** Right-click options: *Rename access point…*, *Reset to automatic grouping*, *This is a separate device*, *Belongs to access point ▸*. Corrections are saved in settings, survive restarts and always win.
- **Select a whole access point.** Ticking a unit selects every SSID and radio it broadcasts, for the live view, capture and saturation tests.
- **New test scopes:**
  - *Single access point* (`SingleUnit`);
  - *Multiple access points* (`MultipleUnits`), e.g. the nodes of a mesh.

  Units also combine with SSIDs and single APs as *Mixed*. Membership is **fixed at start** and stored with the test, including confidence and evidence.
- **Analytics SAT-ANALYTICS-3**: SAT-ANALYTICS-2 plus a summary for each unit:
  - on-air %;
  - best mean signal;
  - per radio, **SSID consistency**: the RSSI gap between SSIDs of one radio, flagged above 3 dB;
  - per radio, **SSID-only outage**: an SSID missing while its radio is on air, flagged at 5 % or more;
  - a finding when grouping confidence is only Medium or Single.
- **Reports, schema v3.** Only fields are added:
  - PDF section **6b Access points (physical units)**;
  - `_units.csv`, one row per member, written only for unit tests;
  - `_info.csv` keys `TargetUnits` and `UnitInference`;
  - JSON `units`, `unitDefinition` and `test.targetUnits`.
- **Capture:**
  - `AccessPoint.TsfOffsetMs`;
  - WPS manufacturer, model, model number, device name and UUID (`WlanAdapter.ParseWps`).
- **Demo mode** now includes a 4-SSID, 2-radio router and a mesh node with the same SSID.
- `HardwareIeInventoryProbe` (explicit, read-only) prints each BSSID's information elements, WPS fields and TSF offsets on real hardware.

### Changed
- `analyticsVersion` is now `SAT-ANALYTICS-3` and the JSON `schema` is `wirelessscanner.saturation_report.v3`. Tests without units give the same numbers as with -2.
- The demo adapter lists 11 APs instead of 5.

### Included from 1.3.1 (never installed separately)
- The REST API status label follows the server.
- EXPORT saves to your Documents and shows the result.
- APPLY writes no key file.
- The key is masked, with SHOW and COPY.

See [1.3.1] below.

### Known issues
- Grouping is an **inference**. A wrong grouping appears as an SSID-consistency finding in unit tests; correct it in the explorer.
- Backhaul links between mesh nodes cannot be measured from a client.
- The app icon is still not embedded in the executable (Malwarebytes ML quarantine; see 1.3.0).

## [1.3.1] — 2026-10-17

Fixes from the owner's first live use of 1.3.0. The API itself passed 12/12 live checks; these were problems in the settings screen.

### Fixed
- **REST API status stayed "Stopped" while the API was running.** The label was read once, and the API starts in the background at launch. The server now raises a state event and the label always shows the real state: RUNNING with its address, enabled but not running, disabled, or NOT RUNNING with the reason.
- **EXPORT appeared to do nothing.** It wrote silently to `C:\ProgramData\WirelessScanner\API\api_key.md`, a folder every user of the PC can read, and APPLY overwrote its only message in the status bar. EXPORT now opens a Save dialog (default: your own Documents) and shows the result with an **OPEN FOLDER** button on the settings page.
- **APPLY no longer writes the key to a file.** DELETE removes the old shared file and this session's export.
- A modal dialog could block forever when no WPF application is running (it hung the test run). The default dialog now refuses to open without one.

### Changed
- **The API key is masked** in the settings screen, with **SHOW** and **COPY** buttons (the full key had appeared in a screenshot).

## [1.3.0] — 2026-10-16

Reports audited for diagnostics and performance tracking: complete, precise, and with provenance (DR-0012). Installer detection hardened. First live-test fix included (the 1.2.1 hotfix was never released separately).

### Added
- **Report provenance.** Every saturation report states:
  - the app, analytics and schema version;
  - the computer, OS and time zone;
  - the adapter, MAC and **driver** version, date and provider (RSSI is comparable only per adapter and driver);
  - **this PC's own Wi-Fi connection** at the start (SSID, BSSID, channel, 802.11 standard, rx/tx rate, signal).
- **New diagnostics per access point.** From data Windows already provides:
  - the 802.11 standard (PHY), beacon interval and centre frequency, and DFS;
  - the AP's own **BSS Load**: connected clients and channel-busy %;
  - link quality (mean/min) and estimated SNR;
  - an **interference margin**: own signal minus the strongest co-channel or overlapping neighbour.
- `_info.csv` in the CSV bundle (every setting, version and definition as key/value rows). Local time columns (`WallLocal`) next to UTC.
- PDF: a "Measurement environment" section, tier and margin in the executive summary, local times **with UTC offset** plus UTC, and precision and neighbour definitions.
- **Installer detection** (`InstallDetector`). Setup finds an existing install from **both** the app key and the Apps & features entry, in the 64- and 32-bit registry views. It cross-checks them against the installed files, reports disagreements, and offers **Repair** when program files are missing. It also refreshes the Windows icon cache and removes stale 32-bit entries.
- **Icon pipeline** (`tools/make_icons.py`, `branding/`): an original WirelessScanner icon and every size for Windows (.ico) and for a future Android port (mipmap sets, adaptive foreground, Play Store 512 px). The setup now uses it; a borrowed "NEXUS Terminal" icon was found in the setup and is kept only in `branding/legacy/`. **Embedding it in the app is on hold**: see Known issues.
- Tools:
  - `tools/api_smoke.py`: live smoke test through the REST API;
  - `tools/mutation_check.py`;
  - an explicit test that regenerates reports from a **copy** of a real database.

### Changed
- **Analytics SAT-ANALYTICS-2:**
  - **active time is measured** (1.2 used the last scan's time: "00:04:56 of planned 00:05:00");
  - exposure stops at the end of the test (1.2 reported "longest 301 s" in a 300 s test);
  - **trend is withheld under 15 minutes** (1.2 printed +35.6 dB/h for 5 minutes);
  - neighbours count **fresh** entries only and use bonded 40/80/160 MHz blocks in every band (1.2 counted cached entries and ignored bonding).
- PDF charts are drawn at true print size: labels at 7.5 pt with units (1.2's were about 2 pt). Each chart stays on the same page as its caption.
- Tests recorded before 1.3 say **"not recorded"** for data 1.2 did not capture, instead of "not advertised" or "none".
- **One version number** for all projects (`Directory.Build.props`); the window header reads it from the program.
- Database schema 3: new nullable columns are added in place (`ALTER TABLE ... ADD COLUMN`); existing data is untouched.

### Fixed
- **REST API `/api/status` and PDF footers reported version 1.1.0** on 1.2.0, because the library had a stale per-project version.
- **Whole-network selections were turned into "Mixed".** Found in the first live test of 1.2.0. After each scan, the hidden legacy list copied the selection back and added every AP of a ticked network as a fixed BSSID. The scope was recorded as *Mixed*, and unticking could leave APs selected. Regression test `WholeSsidSelection_SurvivesScans_WithoutLegacyListEchoingBssids`.
- **Capture CSV used the Windows number format.** A comma-decimal locale wrote `1,500` and broke the file. Now invariant, with a `Timestamp_UTC` column and labelled start and end times.
- A stored test whose saved configuration lacked fields came back with a random new test ID; the database ID is now authoritative.

### Breaking changes for data consumers
- Saturation `_samples.csv`: `Overlap24_Count` → `Overlap_Count` (now all bands, bonding-aware, fresh only); new columns added.
- Saturation JSON schema `wirelessscanner.saturation_report.v2`.
- The CSV bundle has 6 files (adds `_info.csv`).
- Capture CSV: `Timestamp` → `Timestamp_UTC`.

### Known issues
- **App icon not embedded yet.** Malwarebytes real-time protection on the development PC quarantines the app whenever the icon is embedded (`MachineLearning/Anomalous.93%`, a heuristic false positive, 9 detections). The icon ships in the setup and in `branding/`. Embedding it waits for the owner's decision: an antivirus exclusion for development, and code signing and/or a false-positive report for distribution.

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
