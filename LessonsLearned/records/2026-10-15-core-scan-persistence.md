# Lesson LL-2026-10-15-CORE-SCAN-PERSISTENCE: Fresh scan cycles, ordered persistence and honest measurement semantics

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-15-CORE-SCAN-PERSISTENCE` |
| Task/change reference | Control tasks `T03`, `T04`, `T05` (findings F01, F02, F03, F09, F13, F14, F15, F16, F17) |
| Project | WirelessScanner |
| Components | `WirelessScanner.Domain` (ScanCycle, interfaces), `WirelessScanner.Infrastructure` (WlanAdapter, SchemaMigrator, SessionRepository, MockNetworkAdapterProvider, ExportService), `WirelessScanner.Application` (OrderedPersistenceWriter, SessionManager), `WirelessScanner.Presentation` (MainWindowViewModel, MainWindow.xaml), `WirelessScanner.Tests` |
| Observed period (UTC) | `2026-10-15T22:06:00Z` to `2026-10-15T22:20:00Z` |
| Local date/time zone | 2026-10-15, America/Chicago |
| Recorded/updated (UTC) | 2026-10-15T22:25:00Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `RV-2026-10-15-IMPL` steps TEST-T03T05-1, MUT-F02, TEST-T03T05-2, HW-PROBE-1 |
| Source/evidence scope | `governance/reviews/RV-2026-10-15-IMPL/executions/*`, snapshot `SNAP-T03-T05-CODE` |

## Executive outcome
- **What is now true:**
  - live scanning requests a fresh Windows scan every cycle and waits for the scan-complete notification;
  - only demonstrably fresh entries become samples;
  - every write goes through a single ordered queue, so the foreign-key race can no longer occur, as proved by a mutation check;
  - existing databases are upgraded in place;
  - the noise floor is no longer presented as measured, and RSSI variation is labelled in dB;
  - the 6 GHz channel, channel-width and security parsing were corrected.
- **Test results:** the suite passes 52/52. A real-adapter probe returned 4/4 `Fresh` cycles.
- **Not proven:**
  - a long-duration soak run;
  - behaviour on other adapters or drivers;
  - independent review.

## Expected behavior
Each live cycle should reflect new RF observations, and every captured sample should be persisted, ordered after its session. Values that are not measured must not look measured.

## Initial conditions and boundaries
- **Baseline:** `BL-2026-10-15`.
- **Boundaries:**
  - no GitHub push;
  - the installer was not run;
  - the DevOps engine was used read-only;
  - the hardware probe performed only ordinary WLAN scans (implementer judgement; the owner may object).

## Iteration log

| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-15T22:08Z | PATCH-1 | Patch `WlanAdapter` through a bash heredoc containing a Python script | **abort**: the bash heredoc failed to parse ("unexpected EOF"); verified the file was unchanged via `git show` in the mirror | console | **Write multi-line patch scripts to files** (scratchpad); don't inline them in heredocs |
| 2026-10-15T22:09Z | PATCH-2 | Same patch from a file | pass | build 0 errors | — |
| 2026-10-15T22:14Z | TESTS-NEW | While writing parser tests, checked the formulas against the IEEE 802.11 element layouts | found F15 (6 GHz base), F16 (capability vs operation), F17 (AKM) | `CoreFixTests.cs` | Write test vectors before trusting any parser |
| 2026-10-15T22:17Z | TEST-T03T05-1 | Full suite | pass 52/52 | `executions/TEST-T03T05-1.*` | — |
| 2026-10-15T22:18Z | MUT-F02 | Restored the baseline fire-and-forget session insert; ran the ordering test | **failed as intended** (FkViolations > 0) | `executions/MUT-F02.*` | Shows the ordering test detects F02 |
| 2026-10-15T22:18Z | TEST-T03T05-2 | Full suite after restoring the fix | pass 52/52 | `executions/TEST-T03T05-2.*` | Justified rerun: the code changed back after the mutation |
| 2026-10-15T22:19Z | HW-PROBE-1 | 4 `ScanAsync` cycles on an Intel AC 7265 | 4/4 Fresh, 1.0–3.5 s; beacon-age medians 0.6–3.4 s, max 42.6 s (a cached entry); 37 BSSIDs, 15 with RSSI change; 6 Enterprise BSSIDs | `executions/HW-PROBE-1.*` | Confirms the notification path and the FILETIME beacon-age reading on one adapter |

## Actual behavior and deviations
- Windows dropped entries between cycles: the AP count went 32 → 26 → 23 → 20 within about 20 s.
- **Design consequence (for the saturation test):** a target missing from a single fresh cycle is weak evidence. Requiring N ≥ 2 consecutive misses before declaring a dropout (SPEC-SAT-001 §6) is justified.

## Failure, root-cause, and remediation register

| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| F01 | Cached list stored as samples | Scan never requested | No freshness model | `ScanAsync` with notifications; beacon-age filter | Validated (FreshScanCaptureTests, HW-PROBE-1) |
| F02 | FK constraint failure | Unordered writes | Fire-and-forget pattern | `OrderedPersistenceWriter` | Validated (MUT-F02 shows detection) |
| F03 | UI-thread WLAN calls | Timer on the dispatcher calling synchronous APIs | — | Async loop; native calls in `Task.Run`; no overlapping scans (semaphore) | Observed (code review; no UI profiling) |
| F09 | Noise floor shown as measured | Constant −95 | No quality model | `NoiseFloor = null`; SNR labelled estimated; wired sentinels removed | Validated (FreshCycle test) |
| F13 | Generic scan-failure text | No error model | — | `ScanCycleResult.Failed` + `WlanErrorText` | Validated (FailedCycle test) |
| F14 | dB value labelled ms | Unit not modelled | — | Relabelled in UI and exports | Validated (UnitLabelTests) |
| F15 | 6 GHz channel off by 2 | Wrong base frequency | No vectors | Base 5950 MHz | Validated |
| F16 | Width from capability | Wrong element | No vectors | Operation elements; null when unknown | Validated (unit vectors); Observed on hardware (widths known for all) |
| F17 | Enterprise labelled PSK | AKM not parsed | No vectors | RSN AKM parsing | Validated (unit vectors + hardware) |

## Root-cause narrative
The baseline code was written to make the UI work rather than to measure. No tests covered parsers, freshness or persistence ordering, so plausible-looking but wrong behaviour was never caught. All three parser defects (F15–F17) surfaced only once test vectors were written from the IEEE 802.11 element layouts.

## Corrections and changed artifacts
- **Added:**
  - `WirelessScanner.Domain/ScanCycle.cs`
  - `WirelessScanner.Application/OrderedPersistenceWriter.cs`
  - `WirelessScanner.Infrastructure/SchemaMigrator.cs`
  - `WirelessScanner.Tests/CoreFixTests.cs`, `WirelessScanner.Tests/HardwareProbeTests.cs`
- **Changed:**
  - `INetworkAdapterProvider` (`ScanAsync` with a default implementation; `AccessPoint.BeaconAgeMs`)
  - `ISessionRepository.SaveSamplesAsync` (default implementation)
  - `ISessionManager` (`SaveSamples`, `FlushAsync`)
  - `WlanAdapter` (notifications, persistent handle, `IDisposable`, `ReadBssList`, `BeaconAgeMs`, parsers)
  - `SessionManager` (writer; rejects starting a new session while one is paused)
  - `SessionRepository` (batch insert, migrator)
  - `MockNetworkAdapterProvider` (`ScanAsync`, fault injection, no constant noise floor)
  - `MainWindowViewModel` (scan loop, freshness capture, `LastCycleText`)
  - `ExportService` and `MainWindow.xaml` labels; `.csproj` `InternalsVisibleTo`
- **Breaking changes for CSV/JSON consumers:**
  - CSV headers `SNR` → `SNR_Estimated_dB`, `Jitter` → `RSSI_Variation_dB`
  - JSON keys `SNR` → `SnrEstimatedDb`, `Jitter` → `RssiVariationDb`
  - security labels now `WPA2-Personal`, `WPA2-Enterprise`, and so on

## Validation

| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `dotnet test` | 20/20 | 52/52 | Unit/integration with mocks |
| MUT-F02 | n/a | ordering test fails on baseline behaviour | Mutation detection |
| HW-PROBE-1 | never exercised | 4/4 Fresh | One adapter (Intel AC 7265), about 20 s |

## Engine capability impact
Not applicable. Consultation only (C-03 informed the freshness, quality and status semantics).

## What worked
- Default interface methods let the provider contract evolve without breaking existing implementations.
- A single FIFO consumer for persistence is simpler than locks and gives ordering for free.
- Taking DDL from EF's `GenerateCreateScript()` lets the migrator follow the model automatically.
- An explicit hardware probe test, with its output captured in the attestation ledger.

## What did not work
- Inline heredoc patch scripts (PATCH-1).
- Trusting existing parser code without test vectors.

## Resume and anti-duplication map
- **Canonical:** the code snapshot `SNAP-T03-T05-CODE` and the executions listed above.
- **Do not repeat:** HW-PROBE-1 for the same code (reuse it). The mutation check, unless the persistence code changes.
- **Resume point:** T06, the saturation test.

## Reusable prevention
- New invariant: no sample without a confirmed-fresh cycle or a known beacon age within the limit.
- New positive test: `FreshCycle_StoresOneSamplePerAp_WithUnavailableNoiseFloor`.
- New false-positive/negative test: `StaleCycle_WithUnknownBeaconAge_IsNotStoredAsSample`; MUT-F02.
- New monitoring signal: `LastCycleText` (cycle status, duration, AP count) shown to the operator.
- New release gate: the hardware probe passes on the release adapter.
- New documentation/control: none beyond this record.

## Follow-up tasks

| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| Long soak (≥ 15 min) on hardware | saturation test | demitri.pevzner / Claude Code | After T06 | pending | pending |
| UI responsiveness profiling | Presentation | pending | Before qualification | pending | pending |
| Independent review of T03–T05 | all | pending | Before closure | pending | pending |

## Residual risk
- Tested on one adapter only.
- `ScanTimeout` (6 s) may need tuning for slow drivers.
- Windows scan throttling while connected was not observed in this short run.

## Supersession
Superseded for the saturation-specific behaviour by the T06 lesson. Fresh-scan semantics change only through a new decision record.

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
