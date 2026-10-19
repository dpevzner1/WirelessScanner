# Decision DR-0001: Adopt the DevOps engine's full governance model for WirelessScanner

Status: approved  
Date: 2026-10-15  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Supersedes: none

## Context

The 2026-10-15 baseline audit (`governance/evidence/baseline/BASELINE_AUDIT_2026-10-15.md`, finding F07) found:
- `PLAN/` was never updated after implementation;
- there was no changelog, decision record, lesson record or test-results record;
- the README claimed features that do not exist.

The owner asked for a live breadcrumb trail of what was implemented, tested, succeeded and failed, so future similar projects can reuse it. Offered a lightweight trail or the engine's full model, the owner chose "heavy and full".

## Decision

Adopt the engine's governance model in full, scoped to this project:
- **Canonical authority:** `config/pipeline/wirelessscanner-remediation-control.json` (schema `project_remediation_control.v2`), validated by the vendored engine validator with its negative self-tests.
- **Lessons:** `LessonsLearned/` following the engine's iterative documentation standard (verbatim copy), with a register validated by the vendored `validate_lessons_learned.py`.
- **Reviews:** hash-chained attestation ledgers (`tools/review_attestation.py`, verbatim copy) for every cited review or test run.
- **Decisions:** engine `DECISION_RECORD_TEMPLATE.md` under `docs/decisions/`.
- **Projections:** `docs/STATUS.md` and `CHANGELOG.md` are human-readable views of the control file and must agree with it.
- **Security boundary:** none affected.
- **Publication:** GitHub publication still needs explicit owner approval for each push.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| `governance/evidence/baseline/BASELINE_AUDIT_2026-10-15.md` | BL-2026-10-15 | No trail exists; documents and code diverge | Single non-independent auditor |
| Engine `AGENTS.md`, `LessonsLearned/ITERATIVE_DOCUMENTATION_STANDARD.md`, `docs/PROJECT_REMEDIATION_AUDIT_STANDARD.md`, `docs/REVIEW_ATTESTATION_STANDARD.md` | read 2026-10-15 | The engine says new projects should copy and fill in the v2 templates | Built for engine remediation, so some fields (e.g. `engine_sha256`) need reinterpretation (see Consequences) |
| `governance/evidence/vendored-engine-manifest.json` | captured 2026-10-15 | 15 artifacts copied byte-identically | Copies, not live links |

## Alternatives

### Lightweight trail (STATUS + short lessons + decisions)
Rejected by the owner in favour of full rigour.

### Reference the engine tools in place, without copying
Rejected:
- the engine validators resolve evidence paths relative to the engine root, so they cannot validate files in this project;
- running them inside the engine folder would break the read-only boundary.

## Consequences

### Benefits
- Every finding maps to tasks, work packages, verification plans, iterations and lessons, and the validator checks this.
- Failed attempts and reruns stay visible. Hash continuity detects accidental drift.

### Costs and obligations
- Each task needs at least 3 work packages, a verification plan with negative and regression checks, and a risk/resume map.
- Every iteration that has started needs reproduction hashes. The reproduction fields are interpreted for this project as:
  - `input_sha256`: the primary changed source file, or the test input;
  - `engine_sha256`: the build output assembly under test (or the vendored validator, for governance work);
  - `context_sha256`: the governing spec or decision record;
  - `test_sha256`: the test source file.
- Closure needs an owner, a reviewer, regression evidence and lesson evidence.

## Exit and rollback criteria
- Success gate: `python tools/ws_control.py validate` reports `control_valid: true`, with every negative control rejected, and `validate_lessons_learned.py` passes.
- Stop condition: the validator cannot represent a needed record without falsifying data. If so, record a finding and amend this decision.
- Rollback trigger: the owner withdraws the "heavy and full" choice.
- Rollback action: freeze the control file as historical and continue with `docs/STATUS.md` plus lessons only, via a superseding decision.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Architecture | demitri.pevzner | approved (chat: "for decisions, heavy and full") | 2026-10-15 | session transcript |
| Data/corpus | n/a — no corpus | n/a |  |  |
| Security | pending |  |  |  |
| Operations | pending |  |  |  |

## Supersession rule
Write a new decision when the engine's v2 schema, validator or standards change materially, when this project's ownership changes, or when CI enforcement replaces manual checkpoints.
