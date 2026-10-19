# Signal Saturation Test — specification

| Field | Value |
| --- | --- |
| Spec ID | `SPEC-SAT-001` |
| Version | 1.0 (2026-10-15, America/Chicago) |
| Status | **Designed** (the implementation state is tracked in `docs/STATUS.md`) |
| Requested by | Project owner, 2026-10-15 |
| Decision records | `docs/decisions/DR-0004-saturation-test-measurement-model.md`, `DR-0005-saturation-test-ux.md` |
| Engine consultation | `docs/consultation/ENGINE_CONSULTATION_LOG.md` (consultations C-01 to C-06) |
| Control tasks | `T06` (finding `F10`) in `config/pipeline/wirelessscanner-remediation-control.json` |

## 1. Purpose

A **signal saturation test** leaves the test laptop stationary in one test area for a chosen period. It repeatedly measures one or more selected Wi‑Fi targets and reports how stable they were: peaks, drops, dropouts, channel changes, scan-to-scan variation, trend, threshold exposure, and the RF environment around them.

It answers questions such as:
- "Is AP X stable at this desk over a working day?"
- "Does SSID Y hold a usable signal in the stock room for an hour?"
- "Does the AP change channels (DFS or auto-channel) during the shift?"
- "When does coverage dip, how deep, and for how long?"

## 2. Users and jobs to be done

| Role | Job | What they need from the test |
| --- | --- | --- |
| Network engineer | Prove or disprove an AP stability complaint | Exact event timestamps, depths, durations, channel changes, raw samples |
| Field surveyor or technician | Leave a laptop in an area and come back | Clear setup, keep-awake, unattended reliability, progress view from a distance |
| Facilities or IT lead | Decide whether remediation is needed | A plain-language executive summary and grades, with evidence behind them |
| Help desk | Correlate user tickets with Wi‑Fi events | An event list by time, and markers the tester added |

## 3. What the platform can and cannot measure (measurement boundary)

Measurements come from the Windows Native Wi‑Fi API (`wlanapi.dll`) on the test laptop's adapter. This is the **client-side receive view** of AP beacons and probe responses.

| Quantity | Source | Quality label | Notes |
| --- | --- | --- | --- |
| RSSI (dBm) | `WLAN_BSS_ENTRY.lRssi` | measured | Driver-reported; resolution and averaging vary by adapter and driver |
| Link quality (%) | `WLAN_BSS_ENTRY.uLinkQuality` | measured (driver-derived) | Driver mapping from RSSI; not comparable across adapters |
| Channel, band, centre frequency | `ulChCenterFrequency` | derived | Derived by a versioned frequency-to-channel formula |
| Channel width | HT/VHT information elements | derived | Best effort; `null` when no element is present |
| Beacon receive time | `ullHostTimestamp` | measured | Used for freshness and age |
| Noise floor | — | **unavailable** | The WLAN API does not expose it. Never shown as measured |
| SNR | RSSI − assumed noise floor (−95 dBm) | **estimated** | Always labelled "estimated" with the assumption shown |
| Latency or packet jitter | — | out of scope | This is RF observation, not traffic. "Variation" means scan-to-scan RSSI change in **dB** |

These rules are taken from the engine's telemetry-capture source (C-03): never record missing as zero, keep quality and status separate, keep wall-clock and monotonic time separate, and never treat duplicate cached values as extra resolution.

## 4. Sampling model

1. **Scan cycle.** Each cycle asks Windows for a fresh scan (`WlanScan`), waits for the `scan_complete` or `scan_fail` notification (default timeout 6 s), then reads the BSS list.
2. **Cycle status.** One of `fresh`, `stale` (no completion notification arrived, so the cached list was used), `failed` (an API error, or the adapter is missing), or `skipped` (paused).
3. **Observation status per target per cycle.** One of:
   - `observed`: seen in this cycle, beacon age ≤ the freshness limit;
   - `stale`: seen, but the beacon age is over the limit, so it is not counted in statistics;
   - `missing`: not in the list during a fresh cycle (counts toward dropouts);
   - `unknown`: the cycle failed, so absence cannot be judged.
4. **Interval.** User-selectable from 5 s (default), 10 s, 15 s, 30 s or 60 s. Windows typically needs 2–4 s per scan, and the driver may throttle more frequent requests. The engine's guidance: never poll faster than the source updates.
5. **Freshness limit.** By default, the scan interval + 5 s.
6. **Two clocks.** Each cycle records `wall_utc` (for correlation) and `elapsed_ms` from a monotonic `Stopwatch` (for durations and rates).
7. **Unattended operation.** The test keeps the PC awake (`SetThreadExecutionState`) and warns when the laptop is on battery or the adapter is disconnected.

## 5. Target scope (user-selected)

| Scope | Selection | Analysis unit |
| --- | --- | --- |
| Single AP | One BSSID | That BSSID |
| Multiple APs | Two or more BSSIDs | Each BSSID |
| Single SSID | One SSID name | Each BSSID broadcasting it, plus an SSID aggregate (best BSSID per cycle) |
| Multiple SSIDs | Two or more SSIDs | Per SSID aggregate, plus each BSSID |
| All visible | Everything seen | Each BSSID (the environment-survey variant) |

Hidden SSIDs can be targeted only by BSSID.

## 6. Analytics (definitions — implemented in `SaturationAnalyzer`, version `SAT-ANALYTICS-1`)

All RSSI statistics use **observed** samples only. Defaults are configurable under *Advanced*.

| Metric | Definition |
| --- | --- |
| Cycles (total, fresh, stale, failed) | Count by cycle status |
| Availability % | observed ÷ (observed + missing) × 100. Stale and unknown cycles are excluded and reported separately |
| RSSI min / max / mean / median | Over observed samples |
| RSSI σ (std dev) | Population standard deviation |
| P5 / P25 / P75 / P95, IQR | Nearest-rank percentiles |
| Range | max − min |
| Scan-to-scan variation (dB) | \|RSSI(n) − RSSI(n−1)\| over consecutive observed samples: mean, P95, max |
| Baseline | Rolling median of the previous *W* observed samples (default *W* = 12) |
| **Drop event** | Starts when RSSI ≤ baseline − *D* (default *D* = 8 dB). Ends at the first sample ≥ baseline − *D*/2 (hysteresis). Records start, end, duration, minimum RSSI, depth (baseline − min) and recovery time |
| **Peak event** | Mirror of a drop: RSSI ≥ baseline + *P* (default 8 dB) |
| **Dropout** | *N* or more consecutive `missing` cycles (default *N* = 2). Records start, end, missed cycles and duration |
| **Channel change** | The channel or band of a BSSID differs from its previous observation. Records time, from → to, and width change |
| **Strongest-BSSID change** (SSID scope) | The best BSSID for an SSID changes and the new one is ≥ 3 dB stronger (hysteresis). Indicates steering or roaming candidates |
| Threshold exposure | % of observed time below −67 dBm (voice/video grade), below −70 dBm (fair) and below −80 dBm (unusable), plus the longest continuous stretch below each |
| Trend | Least-squares slope of RSSI against elapsed time, in dB/hour |
| Co-channel load | Per cycle: the number of other BSSIDs on the same channel, and (2.4 GHz only) on overlapping channels within ±4. Mean and max |
| Event rate | Events per hour, by type |
| Stability score (0–100, *derived heuristic*) | `100 − 4·σ − 0.5·(100 − availability%) − 3·drops/h − 5·dropouts/h − 5·channel changes/h`, clamped to 0–100. Grades: A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, F < 40. The formula is printed in every report |

**Markers.** While a test runs, the tester can add a timestamped note (e.g. "microwave on", "door closed"). Markers appear on charts and in reports.

## 7. UI and UX design

Engine sources (C-04, C-05): require roles, jobs to be done, task flow, screen and state inventory, accessibility, interaction feedback, error/empty/loading states, usability validation and UX acceptance criteria. Apply Hick's law (limit choices per step; presets first, *Advanced* collapsed), Fitts's law (large primary actions), Gestalt grouping (setup grouped into What / How long / Where), and visible system status and feedback.

### 7.1 Task flow

`Saturation Test tab → 1 SETUP (choose adapter → Discover → pick scope and targets → duration and interval → location and notes → Start) → 2 RUNNING (live status, markers, pause or stop) → 3 RESULTS (summary, events, export) → history (reopen or re-export)`

### 7.2 Screen and state inventory

| Screen / state | Content | Feedback, error and empty states |
| --- | --- | --- |
| Setup — no adapter | Adapter selector | Empty state: "No Wi‑Fi adapter found. Connect one or check Device Manager." Start disabled |
| Setup — discovering | Progress text "Scanning… (fresh scan n/3)" | Loading state; the list fills as results arrive |
| Setup — targets | Search box, scope selector (Single AP / Multiple APs / SSID(s) / All visible), list showing SSID, BSSID, band, channel, current RSSI and a strength label | Empty: "No networks found. Move closer or rescan." The selection count is always visible |
| Setup — test settings | Duration presets (5 m, 15 m, 30 m, 1 h, 4 h, 8 h, 24 h, Custom, Until stopped), interval, location, facility, operator, notes; *Advanced* expander with thresholds | Inline validation ("Select at least one target", "Custom duration must be 1–1440 minutes"). Start is enabled only when valid, and a summary line reads "Test 3 BSSIDs every 5 s for 1 h (≈720 cycles)" |
| Running | Progress bar, elapsed / remaining, cycles fresh / stale / failed, keep-awake state; one tile per target (current RSSI, label, min/max, availability, events, mini status); live timeline with threshold bands; event feed; Add marker; Pause; Stop | Warnings: "On battery", "Adapter missing — retrying", "Scans are being throttled (stale cycles)". Status always has a text label as well as colour |
| Stop confirmation | "Stop the test? Results so far will be analysed and saved." | Prevents accidental loss |
| Results | Per-target summary cards (grade, availability, mean, σ, worst drop); tabs or sections for Timeline, Distribution, Events, Channel changes, Environment, Scan health, Raw samples | Empty event tables show "No drops detected (threshold 8 dB)", never a blank grid |
| Export | PDF report, CSV bundle (samples, events, summary, cycles), JSON | Shows the saved path and "Open folder". Errors (file locked) are shown inline with a retry option |
| History | Past saturation tests (date, location, scope, duration, grade) | Reopen results, re-export, delete (with confirmation) |

### 7.3 Accessibility and layout rules
- Colour is never the only signal. Every RSSI tier also shows a text label: Excellent ≥ −55, Good ≥ −67, Fair ≥ −70, Weak ≥ −80, Unusable < −80.
- Primary actions are large (Fitts's law). Destructive actions need confirmation. Everything is reachable by keyboard (Tab order follows the visual flow).
- The layout follows the app's existing amber-on-obsidian theme and stays readable from a distance while running: large current-RSSI numbers and a high-contrast status line.

## 8. Report output

| Output | Content |
| --- | --- |
| **PDF** | Cover (test ID, location, facility, operator, client, dates, duration, adapter, scope) → Executive summary (grades plus plain-language findings, e.g. "AP 8C:5A:… dropped below −80 dBm 4 times, longest 42 s") → Methodology (interval, freshness limit, thresholds, analytics version, measurement boundary and limits from §3) → one section per target (statistics table, timeline with threshold bands, event markers and tester markers, RSSI histogram, drop / peak / dropout / channel-change tables) → SSID aggregates (strongest-BSSID changes) → Environment (co-channel load) → Scan health (cycle statuses, failures with error codes, gaps) → Appendix (complete per-cycle observation log) |
| **CSV bundle** | `<test>_samples.csv` (one row per cycle per target, **including missing and stale rows**, with status and quality columns), `<test>_events.csv`, `<test>_summary.csv`, `<test>_cycles.csv` |
| **JSON** | `schema: wirelessscanner.saturation_report.v1`: config, analytics version, cycles, observations, events, markers, summaries |

Every export states the analytics version, thresholds and measurement limits, so results stay interpretable later.

## 9. Storage

New SQLite tables, created by the schema upgrader (`SchemaMigrator`, `PRAGMA user_version`):
- `SaturationTests`: config, status, start/end, location and metadata
- `SaturationCycles`: per cycle: sequence, wall UTC, elapsed ms, status, error, BSS count
- `SaturationObservations`: per cycle per BSSID: status, RSSI, quality, channel, band, width, beacon age

Writes are batched once per cycle in a single transaction through one background writer, so they are ordered and avoid the race behind F02.

## 10. Acceptance criteria (`SAT-AC`)

| ID | Criterion | Verification |
| --- | --- | --- |
| SAT-AC-01 | Every cycle requests a fresh scan and records the cycle status | Unit test with a fake scan engine; manual hardware check |
| SAT-AC-02 | Missing and stale targets are recorded as statuses, never as numeric RSSI | Analyzer and export tests |
| SAT-AC-03 | Drops, peaks, dropouts, channel changes and strongest-BSSID changes are detected per §6 | Synthetic-series unit tests with known answers, including negative cases (noise below threshold must not trigger) |
| SAT-AC-04 | Statistics (percentiles, σ, availability, slope, exposure) match hand-computed values | Unit tests |
| SAT-AC-05 | The test runs for the chosen duration without blocking the UI, and keeps the PC awake | Runner test with a fake clock; manual soak |
| SAT-AC-06 | PDF, CSV bundle and JSON contain every section in §8; CSV includes missing rows | Export tests |
| SAT-AC-07 | Setup validation prevents starting without an adapter or targets, or with invalid settings | ViewModel tests |
| SAT-AC-08 | Results survive restart (reopen from history) | Repository test |
| SAT-AC-09 | Status colour always has a text label; the stop action needs confirmation | UI review checklist |
| SAT-AC-10 | Hardware validation on a real adapter for ≥ 15 minutes with a known AP | Manual. Stays **Blocked** until the owner runs it |

## 11. Known limits
- RSSI accuracy depends on the adapter and driver; there is no calibration against an RF instrument. Results are comparable within one adapter and driver, not across them.
- Windows may throttle scans, especially while connected. These cycles are reported as `stale`, not hidden.
- 6 GHz visibility depends on the adapter, driver and region.
- This test does not measure throughput, latency or packet loss.
