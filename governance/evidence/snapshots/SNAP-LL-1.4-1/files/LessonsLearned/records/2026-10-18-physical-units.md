# Lesson LL-2026-10-18-PHYSICAL-UNITS: Inferring physical access points from passive evidence (1.4.0)

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-18-PHYSICAL-UNITS` |
| Task/change reference | Control task `T23` (finding F28); decision DR-0013; engine consultation C-08 |
| Project | WirelessScanner |
| Components | WlanAdapter (TSF, WPS), AccessPoint, PhysicalUnitGrouper, NetworkSelection, SaturationTargetTracker, SaturationTestViewModel, SaturationAnalyzer (SAT-ANALYTICS-3), SaturationExportService (schema v3), NetworkExplorer(View/ViewModel), MainWindowViewModel, MockNetworkAdapterProvider, documentation |
| Observed period (UTC) | `2026-10-18T18:46:25Z` (after the 1.3.1 seal; owner request, probe, proposal) to `2026-10-18T21:17:14Z` (documentation and trail; release build and seal follow in the same ledger) |
| Local date/time zone | 2026-10-18, America/Chicago |
| Recorded/updated (UTC) | 2026-10-18T21:17:14Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `RV-2026-10-18-RELEASE-1.4.0` (TEST-1.4-FULL-1 209/209, DOC-AUDIT-1.4-1, release build); explicit probe `HardwareIeInventoryProbe` (console; raw output local-only) |
| Source/evidence scope | Fixtures in `PhysicalUnitTests` keep the owner's BSSIDs with SSIDs replaced; raw probe output with real SSIDs stays in `governance/evidence/local-only/` |

## Executive outcome
- **What is now true:**
  - The app groups BSSIDs into inferred physical units and shows confidence and evidence.
  - The user can correct the grouping; corrections persist and win.
  - Whole units can be selected and saturation-tested.
  - Unit tests report on-air %, per-radio SSID consistency and SSID-only outages.
  - Reports carry the unit section (schema v3).
- **Owner's environment (fixtures):**
  - the router groups as 1 unit with 2 radios and 4 SSIDs;
  - nothing merges across unrelated OUIs.
- **Not proven:**
  - behaviour in the installed app on the owner's networks;
  - mesh-system metrics (deferred);
  - independent review.
- **Authority:** no GitHub push.

## Expected behavior
The owner asked to select by hardware: the AP or router, not only the SSID; to test one AP across all its SSIDs; and to test mesh quality. The agreed MVP: *"at the bare minimum identify that several SSIDs are coming from the same physical unit"*.

## Initial conditions and boundaries
- 1.3.1 sealed.
- Windows reports BSSIDs only.
- The DevOps engine is read-only. Its C-08 consultation returned no Wi‑Fi topology sources, so design rested on measurement.
- The probe ran read-only on the owner's adapter.

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-18 (after 18:46Z) | C-08 | Engine consultation | Only cloud service-mesh hits; no Wi‑Fi topology sources | consultation log | Design from measurement |
| 2026-10-18 | PROBE-1 | `HardwareIeInventoryProbe`: IE inventory of 20 BSSIDs | No Multiple-BSSID / EasyMesh / Mesh ID; WPS on several APs; two MAC schemes | local-only | Standards-based grouping unavailable → heuristics + confidence |
| 2026-10-18 | PROBE-2 | TSF offsets in two scans | Same radio within 1 ms; radios of one box seconds apart (Arris ~45 s) | local-only | TSF = vendor-independent **radio** key, not a device key |
| 2026-10-18 | MASK-1 | Analysis with masked MAC octets | **Near-false "duplicate BSSID" claim** caused by masking | console | Re-ran with full MACs: no duplicates. Never reason on masked identifiers |
| 2026-10-18 | PROTO-1 | Offline prototype (`scratchpad/unit_mvp_prototype.py`) | Router 1 unit / 2 radios; 3 SSIDs on one radio; mesh candidate | console | Rules carried into C# unchanged |
| 2026-10-18 | BUILD-1 | Grouper | `Min(selector, comparer)` overload does not exist (CS0029) | build | `Select(...).Min(comparer)` |
| 2026-10-18 | PATCH-1 | Python patch with `\n` in a non-raw string | **Newlines injected into a C# string literal** (17 errors) | build | Raw strings or the Edit tool for C# containing escapes |
| 2026-10-18 | HEREDOC-4 | Append tests via a bash heredoc | **Aborted** (unbalanced quote in C#) | console | Fourth repeat of LF-S2: Write tool only |
| 2026-10-18 | TEST-1 | New tests | 1 failure: test selected a CSV row by a BSSID that also appears in another row's findings text | test | Match on delimited columns |
| 2026-10-18 | TEST-2 | Full suite | 1 failure: demo AP count 5 → 11 (intended demo change) | test | Assertion updated with reason |
| 2026-10-18 | RENDER-1 | Render the hardware view | Badge clipped ("MED"); combo showed "AccessPoint" | render | Badge on its own line; display labels |
| 2026-10-18T21:11Z | TEST-1.4-FULL-1 | Attested full suite | 209/209 | ledger | — |
| 2026-10-18 | DOC-AUDIT-1.4-1 | Every identifier cited in 1.4 docs exists | 55 checked; none missing; no stale version strings | ledger | Reuse the audit script pattern |

## Actual behavior and deviations
- **Deviations from the DR design:**
  - no new tables: corrections live in Settings, and each test's grouping is stored in its config JSON;
  - RSSI co-movement is not used as evidence;
  - mesh-system metrics are deferred.
- **Accepted behaviour change:** the demo AP count changed from 5 to 11.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| PU-1 | Near-false duplicate-BSSID claim | Reasoning on masked MACs | Privacy masking applied before analysis | Analyse full identifiers locally; mask only in output | Re-run; corrected |
| PU-2 | 17 compile errors | `\n` in a Python non-raw string became a literal newline in C# | Patch tooling | Raw strings / Edit tool | Build clean |
| PU-3 | Heredoc abort | Quote balance in shell | Known (LF-S2) repeated | Write tool only | — |
| PU-4 | A wrong grouping would silently skew unit results | Inference, not measurement | — | Confidence + evidence everywhere; SSID-consistency finding detects misgrouping; overrides | `AnalyzeUnit_DivergingSsids_…` |
| PU-5 | Regrouping could change a running/recorded test | Live resolver | — | Resolve at start; freeze on lock | `Selection_LockFreezesUnitMembership` |

## Root-cause narrative
The product modelled what the OS reports (BSSIDs), not the physical system the user manages. The engine had no Wi‑Fi topology knowledge, and the access points advertise no standard topology elements, so a passive design had to be grounded in measurement:
- the TSF behaviour;
- the MAC schemes;
- WPS.

Measuring first avoided a design built on standards these APs do not use.

## Corrections and changed artifacts
- **Domain:** `PhysicalUnits.cs`, plus additions to `INetworkAdapterProvider.cs` and `Saturation.cs`.
- **Infrastructure:**
  - capture: `WlanAdapter.cs`, `AccessPointExtensions.cs`;
  - reports: `SaturationExportService.cs`;
  - demo data: `MockNetworkAdapterProvider.cs`.
- **Application:** `PhysicalUnitGrouper.cs`, `NetworkSelection.cs`, `SaturationObservationBuilder.cs`, `SaturationAnalyzer.cs`.
- **Presentation:** `NetworkExplorerViewModel.cs`, `NetworkExplorerView.xaml(.cs)`, `MainWindowViewModel.cs`, `SaturationTestViewModel.cs`.
- **Tests:** `PhysicalUnitTests.cs`, `PhysicalUnitIntegrationTests.cs`; updated `LiveSurveyTests.cs`, `ReportPrecisionTests.cs`, `SaturationViewModelTests.cs`.
- **Version:** `Directory.Build.props` 1.4.0; `tools/api_smoke.py`.
- **Documentation:** README, CHANGELOG, USER_GUIDE, TECHNICAL_REFERENCE, SATURATION_TEST_SPEC, STATUS, PARITY_CHECKLIST, DR-0013, `docs/images/ui_ls_explorer_access_points.png`.

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `PhysicalUnitTests` (18) | n/a | pass | fixtures from the owner's environment |
| `PhysicalUnitIntegrationTests` (10) | n/a | pass | selection → config → analysis → exports |
| Full suite | 181 (1.3.1) | 209/209 | Debug, attested |
| Render review | badge clipped | fixed | synthetic environment |

## Engine capability impact
Not applicable: no source ingestion or capability update. A read-only consultation (C-08) found a gap: no Wi‑Fi topology sources. It is recorded in the consultation log for the engine owner.

## What worked
- Measuring the real environment before designing.
- A throwaway prototype on real data before writing C#.
- Freezing test scope at start.
- Printing the inference definition with every result.

## What did not work
- Masked identifiers during analysis (PU-1).
- Escapes inside Python patch strings (PU-2).
- Heredocs (PU-3, repeated).

## Resume and anti-duplication map
- **Do not re-run** the IE inventory to re-derive the rules: the fixtures encode them.
- **Next stages (DR-0013):**
  - the IEEE OUI registry with update (needs owner approval for outbound HTTPS);
  - the AP-family rule pack;
  - mesh analytics;
  - the node-position survey.
- **Reuse:** `HardwareIeInventoryProbe` for new environments.

## Reusable prevention
- Every inferred identity carries confidence + evidence + user override, and is frozen per measurement.
- Analyse unmasked identifiers locally; mask only what leaves the machine.
- Patch C# with the Write/Edit tools or raw strings.

## Follow-up tasks
- T23-WP6: the owner confirms the grouping in the installed build.
- T22-WP3: live API re-check.
- Owner decision on the later DR-0013 stages.

## Residual risk
- Vendors with other MAC schemes may group only by radio clock (several units per device). This is correctable manually and flagged by confidence.
- Two boxes with coincident TSF offsets on the same band are outvoted over scans, but could link briefly in the first scan.

## Supersession
None.

## Completeness attestation
All material attempts in this session are listed. The time of each console-only step was not captured and is not estimated.
