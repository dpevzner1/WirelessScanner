# Lesson LL-2026-10-15-SATURATION-RELEASE: Saturation test, geolocation, data/API/installer hardening, documentation

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-15-SATURATION-RELEASE` |
| Task/change reference | Control tasks `T06`–`T12` (findings F04, F05, F06, F07, F10, F11, F12) |
| Project | WirelessScanner |
| Components | Saturation domain/analyzer/runner/repository/exports/UI; geolocation; AppPaths; RestApiServer; Setup + Setup.Core; build_release; documentation |
| Observed period (UTC) | `2026-10-15T22:25:00Z` to `2026-10-15T23:50:00Z` |
| Local date/time zone | 2026-10-15, America/Chicago |
| Recorded/updated (UTC) | 2026-10-15T23:50:00Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `RV-2026-10-15-IMPL` (TEST-T06-1, HW-SAT-1, TEST-T06T09-1, RELEASE-1, TEST-FINAL-1); analytics `SAT-ANALYTICS-1` |
| Source/evidence scope | Snapshot `SNAP-T06-T12-CODE`; `governance/evidence/hardware/SAT-HW-1-redacted.json`; `docs/images/*` (synthetic) |

## Executive outcome
- **What is now true:**
  - the signal saturation test exists end to end: setup → running → results → history → PDF / CSV bundle / JSON;
  - geolocation is on every report;
  - data lives in ProgramData with migration;
  - the API is hardened;
  - the installer supports update, repair, downgrade, modify and uninstall with data retention and integrity checks;
  - every feature is documented with evidence labels.
- **Test results:** suite 108/108. A 15-minute real-adapter saturation test passed. The UI renders were reviewed.
- **Not proven:**
  - an installer end-to-end run (prohibited without owner approval);
  - DETECT against the real Windows Location service;
  - a usability study;
  - independent review.
- **Authority:** no GitHub push.

## Expected behavior
The owner asked for a comprehensive, user-friendly saturation test with fully granular reports, designed using the DevOps engine. Mid-course the owner added: geolocation must be in the reports.

## Initial conditions and boundaries
- Starting point: T03–T05 done (52 tests).
- The DevOps engine was used read-only.
- Not allowed without the owner: running the installer, deleting files in `publish/`, pushing to GitHub.

## Iteration log

| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 22:30Z | SAT-TEST-1 | First saturation suite run | **3 failures**: (a) determinism test built two configs with different TestIds — test bug; (b) baseline test expectation was wrong — the analyzer was correct, and a step change is an unrecovered drop; (c) **real defect**: a ~1-minute series produced "Trending up 11 dB/hour" | console | (c) fixed: trend finding requires ≥ 15 min span (spec §6 amended pre-release). Lesson: per-hour extrapolation needs a minimum span |
| 22:36Z | TEST-T06-1 | Full suite after T06 | pass 83/83 | `executions/TEST-T06-1.*` | — |
| 22:40Z | PRIVACY-1 | Owner objected to the proposed redaction ("we do need the geo location in the report") | clarified: reports were never redacted; only public-repo evidence is. Added structured `GeoTag` + DETECT (DR-0009) | chat | **When proposing a privacy boundary, state explicitly which artifact it applies to** |
| 22:42Z | PATCH-GEO-1 | Geolocation patch through a heredoc | **abort**: same heredoc parse failure as PATCH-1 (LL-CORE); nothing applied (verified with grep) | console | The rule from the previous lesson was not followed — **always** use script files for multi-line patches |
| 22:43–22:59Z | HW-SAT-1 | 15-min real saturation test from copied binaries (so builds could continue) | pass: 175 cycles (159 fresh, 16 stale = Windows throttling while connected), 0 failed; 1 BSSID, availability 100%, σ 0.86 dB, grade A, tier Fair | `governance/evidence/hardware/SAT-HW-1-redacted.json` (raw exports `local-only`) | Grade = stability, not strength: documented in the guide |
| 23:00Z | PDF-PREVIEW-1 | Render PDF pages via QuestPDF images | **fail**: license exception (the static license setter in the exporter was bypassed) | console | Set the license wherever documents are generated directly |
| 23:01Z | PDF-PREVIEW-2 | Rendered 17 pages; reviewed visually | found 3 defects (clipped y label, clipped x label, orphan section heading) → fixed and re-rendered | `docs/images/report_page*.png` | Visual review catches layout defects tests cannot |
| 23:05Z | UI-CU-1 | Screenshot the running app via computer-use | **declined by the owner** (the request also resolved to the *installed* 1.0 app) | — | Do not retry; use in-process rendering |
| 23:07Z | UI-RENDER-1 | STA test renders the real XAML of all three steps to PNG | pass; found 4 UX defects (START below the fold, legend over markers, event feed scrolls sideways, stale security labels in the mock) → fixed | `docs/images/ui_*.png` | The render test also guards against XAML runtime errors permanently |
| 23:20Z | INSTALLER-1 | Setup.Core + net48 wizard | build error (`Text` helper vs `Form.Text`) → renamed; System.Text.Json added 9 DLLs → switched to DataContractJsonSerializer (2 files) | build logs | — |
| 23:25Z | INSTALLER-TEST-1 | Installer core tests | 1 failure: test split a highly compressible zip at a fixed 1000 bytes → part 2 empty (test bug) | console | Split test data at the midpoint |
| 23:33Z | RELEASE-0 | First release build | payload 108 MB (60 MB in 1.0): an 84 MB `libSkiaSharp.pdb` and unused LiveCharts | `artifacts/release` | Removed LiveCharts; publish target strips `.pdb` |
| 23:35Z | RELEASE-1 | Release build | pass: 84.6 MB in 5 parts ≤ 20 MB; the remaining growth is the Windows SDK projection (geolocation) | `artifacts/release/release-summary.json` | — |
| 23:40Z | DOC-AUDIT-1 | Checked User Guide claims against the code | 2 false claims: "shortcuts start elevated" and API version "1.0" → **fixed the code** (UAC self-relaunch, assembly version) rather than the docs | diff | Writing docs is a verification step |
| 23:43Z | TEST-FINAL-1 | Final regression | pass 108/108; warnings 37 → 6 | `executions/TEST-FINAL-1.*` | — |

## Actual behavior and deviations
- Requirement NFR-03 asked for a "scan tick ≤ 2 s". It is deliberately not met (DR-0003): Windows cannot deliver fresh data faster. Measured scans took 1.0–3.5 s, and the interval is 5 s.
- Several PLAN features were never built: band filter, band-balance and radar charts, wired fallback, purge UI. They are now marked **Not implemented** in STATUS instead of being claimed.

## Failure, root-cause, and remediation register

| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| LF-S1 | False "trending" finding on short tests | Per-hour slope from a ~1 min span | Spec lacked a minimum span | ≥ 15 min gate | Validated (test) |
| LF-S2 | Heredoc patch aborted twice | Bash heredoc parsing of long Python | Rule from LL-CORE not applied | Script files only | Observed |
| LF-S3 | Release payload bloat | Package-shipped PDB; unused dependency | No size gate in the release process | Strip PDBs; remove LiveCharts; size printed in `release-summary` | Validated (84.6 MB) |
| LF-S4 | Docs claimed behaviour the code lacked | Docs written from intent | — | Doc-audit step; code fixed | Validated |
| LF-S5 | Privacy proposal misread as report redaction | Ambiguous scope statement | — | DR-0009 states both boundaries | Observed |

## Root-cause narrative
Most defects were caught because each layer had its own check:
- synthetic known-answer tests for the analytics;
- visual renders for the PDF and UI;
- a hardware run for acquisition;
- a doc-versus-code audit for claims.

The process failures (heredocs, PDF licensing) came from tooling assumptions that were not re-checked.

## Corrections and changed artifacts
- **New:**
  - Domain: `Saturation.cs` (incl. `GeoTag`, `IGeoLocationProvider`)
  - Application: `SaturationAnalyzer`, `SaturationObservationBuilder`, `SaturationTestRunner`
  - Infrastructure: `SaturationRepository`, `SaturationExportService`, `AppPaths`
  - Presentation: `SaturationTestView(.xaml/.cs)`, `SaturationTestViewModel`, `WindowsGeoLocationProvider`
  - Setup: `WirelessScanner.Setup.Core`, rewritten `WirelessScanner.Setup/Program.cs` (net48)
  - Tools: `tools/build_release.py`
  - Tests: Saturation*, GeoTag, AppPaths, InstallerCore, UiRender, ReportPreview, SaturationHardwareRun
- **Changed:** DbContext (4 tables); RestApiServer; App (DI, elevation, demo mode, migration, shutdown drain); MainWindow (tab, status bar); csproj targets and versions; `publish.bat`; `sync_github_export.py`; `.gitignore`.
- **Removed:** `pack_installer.py`, `assemble_installer.py`, `Class1.cs` ×3, the LiveCharts reference.
- **Docs:** README, CHANGELOG, USER_GUIDE, TECHNICAL_REFERENCE, STATUS, SATURATION_TEST_SPEC (amended), DR-0006…0009, PLAN banners, PROJECT_CONVENTIONS (Not-implemented state).

## Validation

| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `dotnet test` | 52 | 108/108 | Unit/integration/UI-render |
| HW-SAT-1 | — | 15 min, 175 cycles, grade A | One adapter, one location |
| RELEASE-1 | 60 MB (1.0) | 84.6 MB, 5 parts | Installer not executed |

## Engine capability impact
Not applicable. Consultation only (C-02…C-06). The C-01 gap (no RF coverage) persists.

## What worked
- Pure analytics with known-answer and negative tests.
- Running the long hardware test from copied binaries in the background while development continued.
- In-process WPF rendering for UI review when desktop control was not available.
- A netstandard core library to make installer logic testable from the .NET 10 suite.

## What did not work
- Heredoc patches (twice).
- The first PDF preview (license).
- The computer-use screenshot (declined; it also resolved to the wrong, installed app).
- Assuming dependency size without measuring it.

## Resume and anti-duplication map
- **Canonical:** snapshot `SNAP-T06-T12-CODE`; ledger `RV-2026-10-15-IMPL` (sealed); `SAT-HW-1-redacted.json`.
- **Do not repeat:** HW-SAT-1 for unchanged acquisition and analytics code; RELEASE-1 unless code changes.
- **Resume:** owner actions (installer VM run, DETECT check, API key regeneration, reviewer assignment), then backlog T-13.

## Reusable prevention
- New invariant: every user-facing doc claim maps to a STATUS row with an evidence label.
- New positive test: `UiRenderTests` (XAML loads, all steps render).
- New false-positive/negative test: noise produces no events; short series give no trend finding.
- New monitoring signal: `release-summary.json` size and largest part.
- New release gate: `build_release.py` runs the tests first; parts must be ≤ 20 MB.
- New documentation/control: a doc-audit step before closing a docs task.

## Follow-up tasks

| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| Installer end-to-end run (fresh, upgrade from 1.0, repair, modify, uninstall ± data) on a VM | Setup | demitri.pevzner | before publishing 1.1 | pending | pending |
| Verify DETECT with Windows Location | Presentation | demitri.pevzner | before publishing 1.1 | pending | pending |
| Regenerate the API key published in 1.0 | Operations | demitri.pevzner | now | pending | pending |
| Assign reviewers; independent review | Governance | demitri.pevzner | before closure | pending | pending |
| T-13 backlog: atomic session exports, band filter, charts, purge UI, wired view | App | pending | owner prioritisation | pending | pending |

## Residual risk
- Single adapter and location for hardware evidence.
- Installer unexecuted.
- Owner-set thresholds are heuristics.
- The stability grade could be misread as signal quality (mitigated in the docs).

## Supersession
Analytics changes need `SAT-ANALYTICS-2` and a new decision. Later releases get new records.

## Completeness attestation
- [x] Every task request and project component is represented.
- [x] Every attempt/run is recorded, including failures and controlled aborts.
- [x] Every material claim points to durable evidence.
- [x] Successful and unsuccessful approaches are both retained.
- [x] Checkpoints and anti-duplication instructions are explicit.
- [x] Residual risks, owners, and review triggers are explicit.
- [x] Dates use UTC plus the project's local date/time zone.
- [x] Publication and production authority are stated separately.
- [x] Capability iteration impact is reconciled, or explicitly not applicable.
- [x] Local links, JSON, and registered-record validation pass.
