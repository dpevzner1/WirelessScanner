# Lesson LL-2026-10-16-LIVE-SURVEY-REDESIGN: Live Survey redesign without losing features

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-16-LIVE-SURVEY-REDESIGN` |
| Task/change reference | Control tasks `T13`–`T17` (findings F18, F19, F20); decisions DR-0010, DR-0011 |
| Project | WirelessScanner |
| Components | ScanScheduler, WlanAdapter coalescing, SaturationTestRunner, NetworkSelection, NetworkExplorer(View/ViewModel), SpectrumLayout/SpectrumView, Saturation drawer/stage, MainWindow, History, documentation |
| Observed period (UTC) | `2026-10-16T14:38:02Z` (owner approval, E-0008) to `2026-10-16T15:35:20Z` (record written; sealing and sync follow) |
| Local date/time zone | 2026-10-16, America/Chicago |
| Recorded/updated (UTC) | 2026-10-16T15:35:20Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `RV-2026-10-16-REDESIGN` (TEST-REDESIGN-FULL-1/2, UI-RENDER-REDESIGN-1/2, HW-F18-1, MUT-F18-1, DOC-AUDIT-REDESIGN-1); snapshots `SNAP-2026-10-16-REDESIGN-DESIGN`, `SNAP-T13-T16-CODE`, `SNAP-T14-T16-UI`, `SNAP-T17-DOCS` |
| Source/evidence scope | `docs/PARITY_CHECKLIST.md` (42 rows); `docs/images/ui_ls_*.png`, `ui_history_*.png` (synthetic networks) |

## Executive outcome
- **What is now true:**
  - Live Survey has one shared network selection: a grouped explorer with whole SSIDs that follow new APs, or fixed BSSIDs.
  - The spectrum is width-true and coloured by identity (max 3 colours), with DFS shading, a load strip and the least-busy channel. A 60-scan waterfall is new.
  - The saturation test is a Live Survey mode (new Mixed scope; the selection is locked while it runs), and its history is in HISTORY / LOGS.
  - One scan scheduler serves every consumer (F18 fixed).
- **Every 1.1 capability is accounted for:** 20 Live Survey rows, 18 Saturation rows and 4 capacity rows, all Validated in `PARITY_CHECKLIST.md`.
- **Test results:**
  - suite 108 → 148/148;
  - the hardware probe coalesced 3 concurrent requests into 1 scan, and live + test gave 8/8 fresh with 0 failed;
  - the mutation check proved the F18 test detects the old behaviour.
- **Not proven:**
  - use by the owner on real networks;
  - a usability study;
  - independent review;
  - a 1.2 release build (needs owner approval because it replaces `EXE INSTALLER`).
- **Authority:** no GitHub push.

## Expected behavior
The owner approved the proposal ("love it, implement these and ensure no current features, capabilities, or capacity is lost"), with Q1–Q5 as proposed:
- merge the tab;
- mixed scope;
- 3-colour focus;
- waterfall now;
- fix F18 first.

## Initial conditions and boundaries
- 1.1 complete (108 tests).
- The parity inventory (`PARITY_CHECKLIST.md`, all *pending*) was written **before** any code, together with DR-0010/0011 and the scope bootstrap (F18–F20, T13–T17).
- Not allowed without the owner: pushing, running the installer, deleting `publish/` files.
- The DevOps engine was used read-only.

## Iteration log
Times marked *ledger* come from `RV-2026-10-16-REDESIGN`. Other steps ran from the console between the bounds given; their exact clock times were not captured, so none are estimated.

| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 14:39Z (ledger ATT-00001) | INIT | Open the review ledger | ok | ledger | — |
| 14:39–15:20Z | SCHED-1 | `ScanScheduler`, runner on the scheduler (decimation), `WlanAdapter` coalescing, Mixed scope | build ok; suite 108/108 | console | — |
| 14:39–15:20Z | RACE-1 | Scheduler's first scan ran asynchronously in older capture tests | **would race** the deterministic capture tests → `AutoScan` flag (headless VM scans on demand only) | console | Keep headless paths deterministic; inject timing |
| 14:39–15:20Z | REDESIGN-TEST-1 | 29 new unit tests | **1 failure**: wrong test expectation (6 GHz ch 37 @160 MHz sits in block 33–61, centre 6185, not 6025) | console | Code right; test fixed and a case added for each block |
| 14:39–15:20Z | HEREDOC-3 | Multi-line Python patch via a bash heredoc (twice) | **hung/aborted** (a `cat >` reading stdin; an unterminated heredoc); nothing applied | console | Third repeat of LF-S2: **only** the Write tool + script files for patches |
| 14:39–15:20Z | VIEWS-1 | Theme, explorer, spectrum, drawers, stage, MainWindow restructure | build ok | console | — |
| 14:39–15:20Z | RENDER-REVIEW-1 | Rendered all modes and reviewed them | **6 visual defects:** −67/−70 labels overlapping; ▲ covering the ch 1 label; AP rows truncating the BSSID; results grid crushed beside the drawer; label crowding on short strips; **capture log header never showing PAUSED** (a 1.1 defect: a local `Text` value blocks the style trigger) | renders | All fixed and re-rendered. Rendering finds defects that tests cannot |
| 14:39–15:20Z | PARITY-GAP-1 | Checked that each parity row's proof exists | **3 rows lacked a direct test** (history open/delete, keep-awake/battery, REST live feed) | grep | Wrote the tests instead of claiming them |
| 15:21Z (ledger ATT-00002) | TEST-REDESIGN-FULL-1 | Full suite | pass 145/145 | `executions/TEST-REDESIGN-FULL-1.*` | — |
| 15:21Z (ledger ATT-00003) | UI-RENDER-REDESIGN-1 | Renders to `docs/images` | **wrong folder**: the relative path resolved against the test runner's working directory | stdout | Use absolute output paths; stray output removed |
| 15:21Z (ledger ATT-00004) | HW-F18-1 | Real-adapter concurrency probe | pass: burst coalesced; 8/8 fresh, 0 failed | `executions/HW-F18-1.*` | — |
| 15:22Z (ledger ATT-00005) | UI-RENDER-REDESIGN-2 | Re-render with absolute path, outputs declared | pass | `executions/UI-RENDER-REDESIGN-2.*`, `SNAP-T14-T16-UI` | — |
| 15:24Z (ledger ATT-00006) | TEST-REDESIGN-FULL-2 | Full suite after the parity tests | pass 148/148 | `executions/TEST-REDESIGN-FULL-2.*` | — |
| 15:28Z (ledger ATT-00007) | MUT-F18-1 | Remove single-flight, run the F18 test, restore | mutant **killed**; source restored byte-identical | `executions/MUT-F18-1.*` | `tools/mutation_check.py` is reusable |
| 15:30–15:33Z | DOCS-1 | Docs; a shell edit used backticks inside a double-quoted string | **one name silently erased** by command substitution; found by grep and fixed | console | Never put Markdown backticks in double-quoted shell strings |
| 15:33Z (ledger ATT-00008) | DOC-AUDIT-REDESIGN-1 | Doc-versus-code audit | pass; stale backlog label renamed | ledger | — |

## Actual behavior and deviations
- 1.1 tier colouring of shapes was replaced by identity colouring (approved, DR-0010). Tier remains as text, bars and reference lines.
- The standalone `SaturationTestView` is kept in code (headless use and tests) but no longer shown. This is a deliberate choice, so the standalone view-model mode stays tested.
- FR-04 (band filter), backlog in 1.1, is delivered as a side effect: explorer chips plus the spectrum band selector.

## Failure, root-cause, and remediation register

| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| LR-1 (F18) | Concurrent consumers produced STALE cycles | One pending-scan slot per interface, overwritten by a second caller | Features added as separate tabs, each owning its scan loop | Single-flight scheduler + adapter coalescing + runner decimation | Validated (test, mutation, hardware) |
| LR-2 | PAUSED never shown in the capture log header (since 1.1) | Local property value outranks style triggers in WPF | No render of the paused state existed | Values moved into the style; render of the paused state added | Validated (render) |
| LR-3 | Parity rows citing tests that did not exist | Inventory written from intent | — | A "proof exists" check per row; missing tests written | Validated |
| LR-4 | Renders in the wrong folder | Relative env path under the test runner | — | Absolute path; outputs declared in the ledger | Validated |
| LR-5 | Heredoc hang/abort (3rd recurrence) | Shell heredoc/stdin handling in this environment | Rule known (LF-S2) but the shell was reached for out of habit | Write tool + script files only | Observed |
| LR-6 | Text silently lost in a doc edit | Backticks inside a double-quoted shell string | — | Edit tool for Markdown | Observed |

## Root-cause narrative
- **The functional defect (F18)** was architectural: three features each owned a scan loop on a shared adapter. A single owner of scanning, with consumers decimating its output, removes the race and the extra scans.
- **The presentation defects** were only visible in renders. The 1.1 PAUSED bug had shipped because no test rendered that state.
- **The process defects** were tool-handling repeats. They are cheap to avoid once the rule is applied every time.

## Corrections and changed artifacts
- **New:**
  - Application: `ScanScheduler`, `NetworkSelection`
  - Presentation: `NetworkExplorerView(+ViewModel)`, `SpectrumLayout`, `SpectrumView`, `SaturationDrawerView`, `SaturationStageView`, `Themes/HudTheme.xaml`
  - Tests: `RedesignTests`, `LiveSurveyTests` (integration + render)
  - Tools: `tools/mutation_check.py`
  - Hardware probe: `ConcurrentConsumers_OnRealAdapter_ShareScans_WithoutFailures`
- **Changed:**
  - `SaturationTestRunner` (scheduler + decimation), `SaturationObservationBuilder`/`SaturationAnalyzer` (Mixed), `Saturation.cs` (Mixed), `WlanAdapter` (coalescing)
  - `MainWindowViewModel` (scheduler, selection, modes, views, waterfall, `AttachSaturation`), `SaturationTestViewModel` (attached mode, lock, history guard)
  - `MainWindow.xaml` (Live Survey restructured, History sub-tabs, SATURATION TEST tab removed), `MainWindow.xaml.cs` (canvas drawing moved to `SpectrumView`)
  - `App.xaml.cs` (shared scheduler in DI); version 1.2.0
  - Test doubles: `MockRestApiServer` counts live updates; `MemorySaturationRepository` deletes
- **Docs:** README, CHANGELOG 1.2.0, USER_GUIDE §3–6, TECHNICAL_REFERENCE §1/2/7/7a/9/10, STATUS, SATURATION_TEST_SPEC §5/§7.1, PARITY_CHECKLIST, DR-0010/0011 status.

## Validation

| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `dotnet test` | 108 | 148/148 | Unit / integration / XAML render |
| HW-F18-1 | — | 3→1 scan; 8/8 fresh, 0 failed | One adapter, 35 s |
| MUT-F18-1 | — | mutant killed | Scheduler single flight |
| Parity checklist | 42 pending | 42 Validated | 1.1 Live Survey + Saturation + capacity |

## Engine capability impact
Not applicable: consultation only (C-07 for the redesign). The C-01 gap (no RF coverage in the engine) persists.

## What worked
- The parity inventory written **before** coding, then filled with one proof per row.
- A pure layout module (`SpectrumLayout`) tested directly, with the renderer kept thin.
- In-process renders of the real `MainWindow` for every mode; they found 6 defects.
- A mutation check to prove the regression test.
- A shared scheduler with decimation instead of per-feature loops.

## What did not work
- Bash heredocs (again).
- Relative render paths.
- Backticks in double-quoted shell strings.
- Citing proofs before checking that they exist.

## Resume and anti-duplication map
- **Canonical:** `SNAP-T13-T16-CODE`, `SNAP-T17-DOCS`, ledger `RV-2026-10-16-REDESIGN` (sealed), `PARITY_CHECKLIST.md`.
- **Do not repeat:**
  - HW-F18-1, unless the scheduler or adapter changes;
  - the renders, unless the XAML changes. Re-run `LiveSurveyRenderTests` with an absolute `WS_UI_RENDER_OUT`.
- **Resume:**
  1. Owner trial on real networks.
  2. Owner approves the 1.2 release build.
  3. Reviewers are assigned.

## Reusable prevention
- **New invariant:** every parity or STATUS proof names an existing test, render or ledger step (checked by grep before closing).
- **New positive tests:** `LiveSurveyIntegrationTests`, `LiveSurveyRenderTests` (all modes, History).
- **New false-positive/negative tests:**
  - an empty selection asks before testing everything;
  - opening history is refused while a test runs;
  - the locked selection refuses changes;
  - stale entries never extend sparklines.
- **New release/verification gate:** `tools/mutation_check.py` for regression tests of concurrency fixes.
- **New documentation/control:** a parity checklist for any redesign that removes or moves UI.

## Follow-up tasks

| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| Trial 1.2 Live Survey on real networks | App | demitri.pevzner | before release | pending | pending |
| Approve a 1.2.0 release build (`publish.bat`) | Release | demitri.pevzner | after trial | pending | pending |
| Usability walkthrough (explorer, colours, saturation flow) | UX | demitri.pevzner | before release | pending | pending |
| Assign reviewers; independent review of T13–T17 | Governance | demitri.pevzner | before closure | pending | pending |

## Residual risk
- 2.4 GHz HT40 side (±) and bonded-centre tables are assumptions where the API reports only the primary channel. They are documented, and a misdrawn block affects only the drawing, not measurements.
- Context outlines `#5B5246`/`#8A7F72` are deliberately dim on black; a usability check may ask for more contrast.
- Renders use synthetic networks; real dense environments (100+ APs, many 6 GHz) have not been seen in the new UI.

## Supersession
This record supersedes nothing. DR-0011 supersedes the separate-tab part of DR-0005.

## Completeness attestation
- [x] Every task request and project component is represented.
- [x] Every attempt/run is recorded, including failures and controlled aborts.
- [x] Every material claim points to durable evidence.
- [x] Successful and unsuccessful approaches are both retained.
- [x] Checkpoints and anti-duplication instructions are explicit.
- [x] Residual risks, owners, and review triggers are explicit.
- [x] Dates use UTC plus the project's local date/time zone (console steps are bounded, not estimated).
- [x] Publication and production authority are stated separately.
- [x] Capability iteration impact is reconciled, or explicitly not applicable.
- [x] Local links, JSON, and registered-record validation pass.
