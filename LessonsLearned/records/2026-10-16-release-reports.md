# Lesson LL-2026-10-16-RELEASE-REPORTS: Report precision, installer detection, icon and API screen (1.3.0 and 1.3.1)

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-16-RELEASE-REPORTS` |
| Task/change reference | Control tasks `T18`–`T22` (findings F21–F27); decision DR-0012 |
| Project | WirelessScanner |
| Components | NetworkSelection legacy bridge, Setup.Core InstallDetector, Setup wizard, branding pipeline, WlanAdapter capture, SaturationAnalyzer (SAT-ANALYTICS-2), SaturationExportService (schema v2), ExportService, SchemaMigrator, REST API settings screen, documentation |
| Observed period (UTC) | `2026-10-16T20:06:57Z` (ledger RV-2026-10-16-RELEASE-1.3 opened) to `2026-10-18T18:46:25Z` (RV-2026-10-18-RELEASE-1.3.1 sealed) |
| Local date/time zone | 2026-10-16 to 2026-10-18, America/Chicago |
| Recorded/updated (UTC) | 2026-10-18T21:17:14Z (written late: this record was referenced by F21–F26 since 2026-10-16 but not written until the 1.4.0 trail; see LR-6) |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | Sealed `RV-2026-10-16-RELEASE-1.3` (tests, regeneration of the owner's live report from a DB copy, release build, live API check 12/12); sealed `RV-2026-10-18-RELEASE-1.3.1` (release build) |
| Source/evidence scope | `governance/reviews/RV-2026-10-16-RELEASE-1.3/`, `RV-2026-10-18-RELEASE-1.3.1/`; live report and RF data stay in `governance/evidence/local-only/` (sync-excluded) |

## Executive outcome
- **What is now true:**
  - Saturation reports meet the data contract in SPEC §8 (SAT-ANALYTICS-2, schema v2).
  - Setup detects existing installs from both registry sources.
  - The REST API screen reports the real state and exports safely.
  - All projects share one version number.
- **Live checks:**
  - 1.3.0 was installed by the owner; the REST API passed 12/12 live checks.
  - 1.3.1 was built but never installed; it is superseded by 1.4.0.
- **Not proven:**
  - independent review;
  - an end-to-end installer run observed by the implementer (the owner runs setup);
  - the app icon embedded in the exe (on hold, F23).
- **Authority:** no GitHub push.

## Expected behavior
- **Live-test bug.** The owner's first live 1.2.0 test was to work as selected; the whole-SSID scope was recorded as Mixed instead (F21).
- **Owner request 2026-10-16:**
  - setup must use existing registry entries for update or repair;
  - the app icon must appear everywhere;
  - reports must contain "all relevant data … with clarity, granularity, and absolute percision".
- **Owner 2026-10-17:** the REST API looked disabled and EXPORT did nothing.

## Initial conditions and boundaries
- 1.2.0 was installed; the owner ran one live saturation test.
- **Boundaries:**
  - work on a **copy** of the live database;
  - no antivirus changes;
  - never print the API key;
  - setup runs by the owner only;
  - no push.

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-16 (before 20:06Z) | LIVE-1.2 | Review the owner's live report and stored config | 6 report defects (active time, part > whole exposure, 5-min trend, cached neighbours, no provenance, 2 pt labels) + scope echo (F21) | DB copy, local-only | Derive a report data contract first, then fix |
| 2026-10-16 | ICON-1 | Embed the new icon via `ApplicationIcon` | **Test host FileNotFound**: Malwarebytes ML quarantined the Presentation DLL (9 detections, RtpDetections log) | AV log (read only) | Icon kept in setup + `branding/`; embedding on hold; no AV change |
| 2026-10-16T20:07Z | TEST-1.3 | Full suite + regeneration from the DB copy | pass; regenerated report: active 300.000 s, longest-below 298.9 s, trend withheld | `EV-1.3-TEST`, `EV-1.3-REGEN` | Keep the regeneration test (explicit category) |
| 2026-10-16T20:10Z | RELEASE-1.3 | Release build | pass | ledger | — |
| 2026-10-17T17:54Z | API-LIVE-1 | `tools/api_smoke.py check` against the installed 1.3.0 | 12/12; version 1.3.0 | ledger | API itself fine: defects were in the screen |
| 2026-10-17 | API-SCREEN-1 | Fix status label, export, masking | old tests opened a real SaveFileDialog and **hung the test host** | console | Dialog hook + refuse without `Application.Current` |
| 2026-10-18T18:15Z | RELEASE-1.3.1 | Release build incl. `ApiSettingsScreenTests` | pass; sealed 18:46Z | ledger | Not installed; folded into 1.4.0 |

## Actual behavior and deviations
- **1.2.0 live report:**
  - "00:04:56 of planned 00:05:00";
  - "longest below −80: 301 s" in a 300 s test;
  - "+35.6 dB/h" from 5 minutes;
  - co-channel counts included cached entries;
  - PDF/API version 1.1.0 on 1.2.0.
- **Setup** read only its own registry key.
- **API screen** showed "Stopped" while the server answered.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| LR-1 | Scope Mixed for whole SSIDs (F21) | Legacy list setter fired the user-check callback | Mirroring UI state through a setter with side effects | `SetCheckedSilently` + bridge guard | Regression test; validated |
| LR-2 | Part > whole, extrapolated trend, cached neighbours (F24) | Definitions never audited against a report contract | No bounds checks or validity gates | SAT-ANALYTICS-2, contract tests | `ReportPrecisionTests`; regenerated live report |
| LR-3 | Version 1.1.0 on 1.2.0 (F25) | Per-project versions | Version set in several places | `Directory.Build.props` | validated |
| LR-4 | Install not detected via Apps & features (F22) | Single detection source | Two-key registration, one reader | `InstallDetector` | `InstallDetectorTests` |
| LR-5 | Icon-embedded build quarantined (F23) | AV machine-learning heuristic | Unsigned binary | Embedding on hold; owner decision | Blocked |
| LR-6 | This lesson referenced from the control but not written for two days | Release work continued before the trail step | No validator check that referenced lesson IDs exist | Written now; prevention below | validator passes |
| LR-7 | API "Stopped" while running; export silent (F27) | Status read once; result in an overwritten status bar | Async server start; shared transient message channel | Event-bound status; inline result | `ApiSettingsScreenTests` |

## Root-cause narrative
- **Direct causes:** each defect had a direct implementation cause (see the register).
- **Common pattern:** numbers and states were *produced* without a contract saying what a correct report or screen must state.
  - A report could contradict itself (part > whole).
  - A label could be stale.
- **Why tests did not catch it:** they checked the computations they were written for, not the claims a reader would make from the output.

## Corrections and changed artifacts
- **Analytics and reports:** `SaturationAnalyzer` (SAT-ANALYTICS-2), `SaturationObservationBuilder`, `SaturationExportService` (schema v2, `_info.csv`, charts in points).
- **Capture and storage:** `WlanAdapter` (PHY, beacon, BSS Load, connection, driver), `ExportService`, `SchemaMigrator` schema 3.
- **Installer and branding:** `InstallDetector` with the setup wizard; `branding/` and `tools/make_icons.py`.
- **API screen:** `IRestApiServer.StateChanged`; MainWindowViewModel API panel.
- **Version:** `Directory.Build.props`.
- **Documentation:** DR-0012; documentation for 1.3.0 and 1.3.1.

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `ReportPrecisionTests` | live defects reproduced | pass | synthetic + regenerated live report |
| `InstallDetectorTests` | n/a | pass | pure logic + read-only registry probe |
| `ApiSettingsScreenTests` | status stale, silent export | pass | headless VM |
| API live check | n/a | 12/12 on 1.3.0 | owner's machine |

## Engine capability impact
Not applicable: no engine source ingestion or capability update. The DevOps engine was consulted read-only for DR-0012 (C-07 reuse).

## What worked
- Regenerating the owner's real report from a **copy** of the database made every defect concrete and testable.
- Reading the AV log instead of guessing explained the "FileNotFound".

## What did not work
- Embedding the icon (AV quarantine).
- Writing the lesson later instead of at release (LR-6).
- Tests that opened real dialogs.

## Resume and anti-duplication map
- **Do not retry embedding the icon** until the owner adds an exclusion or the binary is signed.
- **Do not hand-edit report definitions** without bumping the analytics version and a DR.
- **Reuse:**
  - the regeneration test (`WS_REGEN_DB` / `WS_REGEN_OUT`);
  - `tools/api_smoke.py` for live API checks.

## Reusable prevention
- Write a **report data contract** (questions a report must answer) before changing analytics; test part ≤ whole and validity gates.
- Bind UI state to events; never read a background service's state once.
- Show action results next to the action, not in a shared status bar.
- Write the lesson record in the same session as the release ledger, before sealing.

## Follow-up tasks
- Live re-check of the API screen on 1.4.0 (T22-WP3).
- Owner decision on icon embedding (T20).
- Independent review.

## Residual risk
Unsigned binaries remain exposed to AV heuristics. Report consumers of v1 needed migration (documented as breaking in 1.3.0).

## Supersession
1.3.1 is superseded by 1.4.0 (same fixes plus DR-0013). This record is not superseded.

## Completeness attestation
All material attempts known from the ledgers and the session are listed. The time of each console-only step was not captured and is not estimated.
