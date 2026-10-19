# Decision DR-0011: Saturation test inside Live Survey, shared selection, one scan scheduler, mixed scope

Status: approved · implemented in 1.2.0 (2026-10-16; evidence: docs/PARITY_CHECKLIST.md, RV-2026-10-16-REDESIGN)  
Date: 2026-10-16  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: DR-0005 (the separate-tab part only; its UX rules, states and accessibility criteria remain)

## Context
- The owner said the separate saturation tab's own scope menu and network table were "too confusing" next to Live Survey, and approved moving the test into Live Survey (2026-10-16).
- Finding **F18**: the live loop, the saturation runner and DISCOVER each called `ScanAsync` on the same `WlanAdapter` singleton. Its single pending-scan slot per interface is overwritten by a concurrent call, so the first caller times out and records a STALE cycle.

## Decision
1. **`ScanScheduler`** (Application):
   - one scan loop per adapter, and **single-flight**: concurrent requests share the in-flight scan;
   - consumers subscribe to `CycleCompleted`, and `ScanNowAsync()` serves FORCE SCAN and the tests;
   - the loop runs at the shortest interval any consumer needs;
   - the saturation runner takes the first cycle at or after each of its due times (decimation), so a 30 s test does not slow the 5 s live view;
   - `WlanAdapter.ScanAsync` additionally coalesces concurrent calls per interface (defence in depth).
2. **Live Survey layout:**
   - a mode bar: **Monitor / Capture session / Saturation test**;
   - capture and saturation can run at the same time (as before), sharing the scheduler;
   - the selected mode opens its drawer: capture controls, or the saturation setup / run panel;
   - the centre gains **Test** (tiles and timeline) and **Results** views.
3. **The explorer selection is the test scope:**

   | Selection | Scope |
   | --- | --- |
   | Whole SSIDs only | SingleSsid / MultipleSsids |
   | Fixed BSSIDs only | SingleAp / MultipleAps |
   | Both | **Mixed** (new) |
   | Nothing | AllVisible, after an explicit confirmation |

   The tracker registers explicit BSSIDs from the start and SSID members on sight. SSID aggregates are computed for every target SSID.
4. **Selection lock:** while a test runs the selection is locked, so the scope cannot drift. Capture filtering then uses the same locked selection.
5. **Removed:** the SATURATION TEST tab, its adapter picker, DISCOVER and its candidate table. The view model keeps a standalone mode (DISCOVER and candidates) for tests and headless use.
6. **History:** HISTORY / LOGS gets **Capture sessions | Saturation tests**. Saturation history (open, re-export, delete) moves there unchanged.
7. **Analytics:** `SAT-ANALYTICS-1` definitions are unchanged. Mixed scope only adds target registration and SSID aggregates where target SSIDs exist; outputs for the existing scopes are identical, as proven by regression tests.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Code reading (2026-10-15) | `WlanAdapter.cs:268`, `SaturationTestRunner.cs:108`, `MainWindowViewModel.cs:880`, `SaturationTestViewModel.cs:171` | Three callers share one singleton; the pending slot is overwritten | Not yet reproduced; T13 adds a reproducing test |
| Approved proposal | v1 | Flow, scope mapping, mockups | — |

## Alternatives

### Keep the tab but read the Live Survey selection
Offered as an alternative to Q1; the owner chose the merge.

### Per-consumer adapter instances
Rejected: Windows scans the radio, not the handle, so parallel scans would still contend.

## Consequences
- **Benefits:** one place to select, no duplicate scans, F18 fixed, mixed scope available.
- **Costs:**
  - a larger Live Survey surface;
  - a selection lock during tests;
  - a feature-parity checklist (`docs/PARITY_CHECKLIST.md`) is mandatory.

## Exit and rollback criteria
- Success gate: every row of the parity checklist is Validated; the F18 reproduction test fails on the old behaviour and passes after the fix.
- Rollback: restore the tab from git history; the scheduler stays (it fixes F18).

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Product/UX | demitri.pevzner | approved (chat 2026-10-16: Q1–Q5 as proposed) | 2026-10-16T14:30Z | session transcript |

## Supersession rule
Write a new decision for changes to scope semantics, concurrency of capture and test, or the scheduler cadence policy.
