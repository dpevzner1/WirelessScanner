# Lesson LL-2026-10-15-BASELINE-GOVERNANCE: Baseline audit and adoption of full engine governance

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-15-BASELINE-GOVERNANCE` |
| Task/change reference | Control task `T01` (finding `F07`); baseline `BL-2026-10-15` |
| Project | WirelessScanner |
| Components | Whole repository; `config/pipeline/`; `LessonsLearned/`; `docs/`; `governance/`; `tools/` |
| Observed period (UTC) | `2026-10-15T20:30:00Z` to in-progress |
| Local date/time zone | 2026-10-15, America/Chicago |
| Recorded/updated (UTC) | 2026-10-15T22:10:00Z |
| Status | observed |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending |
| Related run/profile/snapshot IDs | `BL-2026-10-15`, `RV-2026-10-15-BASELINE`, GitHub mirror HEAD `9e10314` |
| Source/evidence scope | `governance/evidence/baseline/*`, `governance/evidence/consultation/*`, `governance/evidence/vendored-engine-manifest.json` |

## Executive outcome
- **What is now true:**
  - the project has a validated v2 remediation control: 14 findings, 12 tasks, 37 work packages, hash-chained events, and all 12 negative self-tests rejected;
  - it has a lesson register, decision records DR-0001 to DR-0005, an engine consultation log, conventions, and a saturation test specification.
- **Not yet proven:** all code findings F01–F06 and F08–F14 are open. No independent reviewer is assigned.
- **Authority:** no GitHub publication or production authority was granted.

## Expected behavior
- The project documents should reflect the code.
- Each change should leave a trail of what was attempted, tested, passed and failed.
- The DevOps engine should be consulted read-only, with every consultation cited.

## Initial conditions and boundaries
- **Starting state:**
  - the main folder and `GitHub Export/` were divergent copies;
  - `PLAN/` was dated 2026-05-27 and never updated;
  - build: 0 errors, 37 warnings; tests: 20/20 passed.
- **Read-only boundary:** the DevOps engine folder. Only `query` and file reads were used, with `PYTHONDONTWRITEBYTECODE=1`.
- **Non-goals:** pushing to GitHub; running the installer on this machine; hardware RF validation.

## Iteration log

| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-15T21:20Z | B-BUILD | Baseline Debug build | pass (0 errors, 37 warnings) | `governance/evidence/baseline/build-20261015.txt` | Reuse as the baseline; do not rebuild just to re-observe |
| 2026-10-15T21:22Z | B-TEST-1 | Baseline tests (unattested) | pass 20/20 | `governance/evidence/baseline/test-20261015.txt` | Superseded by the attested B-TEST |
| 2026-10-15T21:30Z | Q-1 | Engine queries over 20 UX/RF terms | partial: 7 queries failed with `UnicodeDecodeError` (cp1252 console output) | console transcript | **Do not repeat without `PYTHONIOENCODING=utf-8`** |
| 2026-10-15T21:35Z | Q-2 | Re-ran the failed queries with UTF-8 | pass | `governance/evidence/consultation/query_*.json` | RF terms absent from the engine (C-01) |
| 2026-10-15T21:49Z | ATT-init-1 | Attestation `init` with role `implementation_owner` | fail: invalid role | console | The tool's roles are reviewer, implementer, custodian, auditor, human_reviewer, automation, other |
| 2026-10-15T21:49Z | ATT-init-2 | `init` with role `implementer` | pass | `governance/reviews/RV-2026-10-15-BASELINE/ATTESTATION-LEDGER.jsonl` | — |
| 2026-10-15T21:50Z | B-TEST | Attested baseline tests | pass 20/20 | same ledger, step B-TEST | Canonical baseline test evidence |
| 2026-10-15T21:58Z | CTRL-1 | Bootstrap and validate the control | pass; 12/12 negative controls rejected | `config/pipeline/wirelessscanner-remediation-control.json` | Superseded by CTRL-3 (never published) |
| 2026-10-15T21:59Z | CTRL-2 | Re-bootstrap with runtime-log evidence copied into `governance/evidence/baseline/runtime-log-copies/` | pass | control file | Evidence outside mirrored roots makes the mirror's control invalid. **Keep all evidence under `governance/`** |
| 2026-10-15T22:00Z | SYNC-CHECK-1 | Attested `sync --check` | **fail (exit 1)** | `governance/reviews/RV-2026-10-15-BASELINE/executions/SYNC-CHECK.*` | Root cause: an open attestation ledger is inside a mirrored root and changes with every attested step. Fix: mirror review ledgers only after `seal` |
| 2026-10-15T22:00Z | SYNC-CHECK-2 | Attested `sync --check` after the fix | pass | `executions/SYNC-CHECK-2.*` | — |
| 2026-10-15T22:00Z | SYNC-NEG | Planted `docs/planted.db` and `docs/api_key.md`; dry-run plan | pass (neither planned) | ledger ATT-00009 | — |
| 2026-10-15T22:01Z | F05-KEY | Compared the README example key with `publish/API/api_key.md` (value not printed) | **confirmed identical**: the live key is public in GitHub history | ledger ATT-00010 | The owner must rotate the key; T08 removes it from the docs |
| 2026-10-15T22:05Z | CTRL-3 | Re-bootstrap citing living documents through the immutable snapshot `SNAP-2026-10-15-DESIGN` | pass | `governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/MANIFEST.json` | Evidence hashes of living files would drift and invalidate the control. **Cite living files only through snapshots** (`ws_control.py snapshot`) |

## Actual behavior and deviations
- Baseline findings F01–F14 are in `governance/evidence/baseline/BASELINE_AUDIT_2026-10-15.md`.
- The engine's README inventory and its validation figures differ from what `PLAN/03` cites. The plan figures are historical.

## Failure, root-cause, and remediation register

| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| LF-01 | Docs claim features that don't exist; PLAN never updated | No post-planning documentation process | No checkpoints and no status projection | DR-0001 governance; T12 reconciliation | Designed |
| LF-02 | Engine query output failed to decode | Windows console encoding cp1252 | No encoding set | `PYTHONIOENCODING=utf-8` | Validated (Q-2) |
| LF-04 | Sync check failed right after a sync | Open review ledger mirrored while still being appended | Self-referential evidence location | Mirror sealed ledgers only | Validated (SYNC-CHECK-2) |
| LF-05 | Evidence on living files would drift | Docs and code are edited after capture | Evidence immutability not designed in | Snapshot evidence | Validated (CTRL-3) |
| LF-06 | Live API key published in README and GitHub history | Docs written from a real configuration | No secret scanning or doc review | T08: remove from docs, header-only auth; owner rotation | Observed; remediation pending |
| LF-03 | Engine validators could not validate project-local evidence | Validators resolve paths relative to their own repository root | Engine tooling assumes engine-local use | Verbatim vendoring under the same relative layout (`tools/`, `config/pipeline/`, `LessonsLearned/`) | Validated (CTRL-1) |

## Root-cause narrative
The project followed a strong plan-first method (6-stage engine model), but nothing carried that discipline into implementation and release. The plan stayed frozen while the code took a simpler path (UI-timer scanning, fire-and-forget writes). No status projection or checkpoint existed to expose the divergence, and the README was written from the plan rather than from the code.

## Corrections and changed artifacts
- **Added:**
  - `config/pipeline/wirelessscanner-remediation-control.json`
  - the vendored `tools/*.py` and `config/pipeline/*.schema.json`
  - `tools/ws_control.py`
  - `governance/bootstrap/build_initial_control.py`
  - `docs/PROJECT_CONVENTIONS.md`, `docs/SATURATION_TEST_SPEC.md`, `docs/consultation/ENGINE_CONSULTATION_LOG.md`, `docs/decisions/DR-0001…0005`, `docs/standards/*`
  - `LessonsLearned/*`
  - `governance/evidence/**`, `governance/reviews/RV-2026-10-15-BASELINE/`
- **Code:** none changed in this iteration.

## Validation

| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| `python tools/ws_control.py validate` | no control | valid; 12/12 mutations rejected | Record completeness only |
| `python tools/validate_lessons_learned.py` | no register | see the T01 iteration result in the control | Structure only |
| `dotnet test` | 20/20 | 20/20 (no code change) | Regression |

## Engine capability impact
Not applicable. This project consults the engine read-only and does not ingest sources or change engine capability. Consultation gaps are recorded (C-01: no RF/Wi‑Fi concepts) for the engine owner to consider.

## What worked
- Vendoring the engine tools verbatim, in the same relative layout, let the engine's own validators run unmodified against this project.
- Specifying the saturation test before writing code, structured by the engine's UX rules (roles, jobs to be done, state inventory, acceptance criteria).

## What did not work
- Engine queries without UTF-8 output (LF-02).
- Assuming the attestation tool accepts any role string.
- The plan's engine figures could not be reused; they were stale.

## Resume and anti-duplication map
- **Canonical:** the control file, the baseline audit, the vendored-manifest hashes.
- **Diagnostic only:** console transcripts.
- **Do not repeat:** the baseline build and test (reuse EV-BL-BUILD / EV-BL-TEST); the RF engine queries (C-01 is a recorded gap).
- **Resume point:** task T02 (sync) and T03 (fresh scan).

## Reusable prevention
- New invariant: no README claim without an evidence label in `docs/STATUS.md`.
- New positive test: `ws_control.py validate` at every checkpoint.
- New false-positive/negative test: the validator self-test mutations.
- New monitoring signal: `ws_control.py drift`.
- New release gate: `sync_github_export.py --check` must be clean before any push.
- New documentation/control: `docs/PROJECT_CONVENTIONS.md`.

## Follow-up tasks

| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| Assign verification and independent reviewers | governance | demitri.pevzner | Before any task closure | pending | pending |
| T02–T12 | see control | Claude Code | per control sequence | pending | pending |

## Residual risk
The implementer and auditor are the same agent. Hardware behaviour is unverified. The engine has no RF coverage, so RF thresholds rely on industry practice.

## Supersession
Superseded per task by later records. This record is updated by appending iterations, never by rewriting earlier rows.

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
- [ ] Local links, JSON, and registered-record validation pass. (Checked in the T01 iteration.)
