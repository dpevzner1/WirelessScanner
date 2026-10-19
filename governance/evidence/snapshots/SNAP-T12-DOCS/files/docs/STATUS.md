# Status — WirelessScanner 1.1 (2026-10-15)

Evidence labels follow [`PROJECT_CONVENTIONS.md` §3](PROJECT_CONVENTIONS.md):
- **Validated**: automated test or recorded harness;
- **Observed**: seen in a real run, not under an approved test;
- **Designed**: implemented or documented, awaiting environment, review or approval;
- **Blocked**: requires evidence or authority that doesn't exist yet;
- **Not implemented**: backlog.

This file is a human-readable projection of `config/pipeline/wirelessscanner-remediation-control.json`. When they disagree, the control file wins and this file must be fixed.

**Test suite:** 108/108 passing (`RV-2026-10-15-IMPL`, step TEST-T06T09-1). **Independent review:** Blocked (no reviewer assigned).

## Original requirements (PLAN/01_PROJECT_SCOPE.md)

| ID | Requirement | State | Evidence / note |
| --- | --- | --- | --- |
| FR-01 | Enumerate adapters; PnP refresh; active selection | Validated (enumeration, selection) · Observed (PnP refresh in code; not exercised) | `WM_DEVICECHANGE` → reload |
| FR-01 | Wired fallback mode | **Not implemented** | Wired adapters are filtered out of the list; the 1.0 fake-RSSI path was removed (F09). Backlog |
| FR-02 | SSID / AP modes, tracking selection | Validated | `ViewModelTests` filtering tests |
| FR-03 | BSSID, SSID, band, channel, width, RSSI, quality, security, vendor, last seen | Validated (parsers, vectors) · Observed (hardware HW-PROBE-1) | F15–F17 fixed |
| FR-03 | SNR | Validated as **estimated** | The noise floor is unavailable from the API |
| FR-03 | Hostname; network type (mesh/ad-hoc) | **Not implemented** | Not exposed by the WLAN API: hostname is always empty and network type is always "Infrastructure" |
| FR-03 | Supported rates | Validated (parser) | Available in `/api/live`; not shown in the UI grid |
| FR-04 | Band filter (2.4 / 5 / all) | **Not implemented** | Backlog |
| FR-05 | Start / pause / stop sessions, searchable metadata, database size | Validated (lifecycle, search) · Observed (database size display) | — |
| FR-06 | Roaming survey with survey points | Validated (capture) | The zone is set at session start |
| FR-07 | Stationary monitoring: min, max, mean, σ, jitter | Validated, superseded by the **Saturation Test** | See SAT-AC below |
| FR-08 | Signal timeline, channel map, AP detail, health metrics | Validated (history timeline, spectrum render) · Observed (UI) | — |
| FR-08 | Band balance chart; mesh radar chart | **Not implemented** | Backlog |
| FR-09/10 | CSV / JSON export | Validated | Headers renamed in 1.1 (see CHANGELOG) |
| FR-11 | PDF with cover details, statistics, timeline, sample table | Validated | Band-balance and radar pages not implemented |
| NFR-01 | Standalone executable, no prerequisites | Validated (self-contained publish, net48 setup builds) · **Blocked** (installer end-to-end run needs owner approval) | RELEASE-1 |
| NFR-02 | DPI-aware, responsive layout | Designed | No DPI matrix test (100–200%) yet |
| NFR-03 | Scan cycle ≤ 2 s, responsive UI, 100+ APs | **Deliberate deviation (DR-0003):** a 5 s interval, because Windows cannot deliver fresh data faster · Observed: scans take 1.0–3.5 s · Designed: 100+ APs (37 seen on hardware) | — |
| NFR-04 | Portable; administrator elevation prompt | Validated (data-directory override) · Designed (UAC relaunch, not run in automation) | — |
| NFR-05 | No data loss; atomic exports | Validated (ordering + mutation check; saturation exports atomic) · **Designed** (1.0 session exports still write directly) | Follow-up T-13 |
| Retention | Purge sessions by age | Designed (repository method only) · **Not implemented** (no UI) | Backlog |

## Saturation test acceptance (SPEC-SAT-001 §10)

| ID | State | Evidence |
| --- | --- | --- |
| SAT-AC-01 Fresh scan per cycle; cycle status recorded | Validated · Observed | Runner tests; HW-SAT-1 (159 fresh / 16 stale / 0 failed in 15 min) |
| SAT-AC-02 Missing and stale never numeric | Validated | Builder and analyzer tests; CSV includes missing rows |
| SAT-AC-03 Event detection, including negatives | Validated | Drop, peak, dropout, channel change, strongest-AP; noise produces no events |
| SAT-AC-04 Statistics match hand-computed values | Validated | Percentiles, σ, availability, exposure, slope |
| SAT-AC-05 Runs for its duration without blocking the UI; keep-awake | Validated (runner, pause excludes time) · Observed (15-min hardware run) | — |
| SAT-AC-06 Exports complete | Validated | PDF, 5 CSV files, JSON; visual review of the PDF render |
| SAT-AC-07 Setup validation | Validated | `SaturationViewModelTests` |
| SAT-AC-08 Results survive restart | Validated | Repository round-trip and re-analysis |
| SAT-AC-09 Text labels with colour; confirmation on stop | Validated | Tile labels and stop-confirmation tests; UI render review |
| SAT-AC-10 Hardware ≥ 15 min with a known AP | **Observed** | HW-SAT-1: 15 min on Intel AC 7265, grade A, σ 0.86 dB, availability 100% (redacted summary in `governance/evidence/hardware/`) |
| Geolocation in all exports | Validated | `GeoTagTests` |
| Geolocation DETECT with the real Windows Location service | **Blocked** | Owner to verify on their machine (location permission) |
| Usability study with real users | **Blocked** | Owner to arrange |

## Remediation findings (baseline 2026-10-15)

| Finding | State |
| --- | --- |
| F01 fresh scans · F02 write race · F03 UI-thread scanning | Validated (implemented; awaiting independent review) |
| F04 data location | Validated |
| F05 API security | Validated (code). **Owner action:** regenerate the API key published in 1.0 |
| F06 installer lifecycle | Validated (core logic, 12 tests) · Blocked (end-to-end run) |
| F07 docs vs code | Validated (this documentation set); continuous |
| F08 repository sync | Validated |
| F09 sentinel values · F14 units · F15 6 GHz · F16 width · F17 security | Validated |
| F10 saturation test | Validated / Observed (above) |
| F11 clutter | Validated (stubs, LiveCharts and PDB removed) · `publish/*.exe.old` and `publish/libSkiaSharp.pdb` remain in the owner's local folder (not deleted without approval) |
| F12 test gaps | Validated (20 → 108 tests) |
| F13 adapter loss | Validated |

## Open follow-ups
- Assign a verification reviewer and an independent qualification reviewer.
- Owner: run the installer end to end on a test machine or VM; verify DETECT; regenerate the 1.0 API key.
- T-13: make 1.0 session exports atomic; band filter; band-balance and radar charts; purge UI; wired-adapter view (backlog).
