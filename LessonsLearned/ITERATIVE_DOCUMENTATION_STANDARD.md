# Iterative lessons-learned documentation standard

Effective date: 2026-07-30  
Local time zone: America/Chicago  
Applies to: DevOpsAgent tasks, incidents, controlled builds, pipeline changes,
evaluations, migrations, and release/readiness work

## Purpose

Lessons learned are part of the project's control system, not an optional
retrospective. Their purpose is to preserve the full path from request through
evidence: what was attempted, what failed, what was deliberately stopped, what
worked, how remediation was validated, where work can resume, and what remains
unproven. This prevents a later task from repeating expensive analysis or
mistaking diagnostic artifacts for accepted evidence.

## When a record is mandatory

Create or update a registered record when any of these occurs:

- a controlled run fails, is manually stopped, or is resumed;
- a quality, drift, security, provenance, recovery, or release gate changes;
- a model, parser, prompt, schema, semantic profile, source class, or backend
  is introduced or materially changed;
- an ingestion introduces, strengthens, preserves, reduces, or otherwise
  changes an engine capability, primary domain, subdomain, cross-layer family,
  consultant behavior, or publication boundary;
- investigation produces a reusable correction, rejected approach, benchmark,
  checkpoint, or operational constraint;
- work spans multiple attempts, tasks, dates, project components, or evidence
  bundles;
- a result could otherwise be misread as production-ready or publication
  authority.

Small corrections may share one task record when they have the same scope and
evidence chain. Unrelated tasks require separate lesson IDs.

## Record identity and location

- Use `LL-YYYY-MM-DD-<SHORT-TASK-ID>` as the stable lesson ID.
- Store chronological records under `LessonsLearned/records/`.
- Register every record in `LessonsLearned/lesson-register.json`.
- Use `LessonsLearned/templates/LESSON_RECORD_TEMPLATE.md`.
- Retain thematic syntheses in numbered documents, but link them to the
  chronological records instead of replacing the source history.

## Required content

Every record must identify:

1. task/change reference, project, affected components, UTC period, local date
   and time zone, owners/reviewers, and immutable run/profile/snapshot IDs;
2. initial scope, authority, read-only/immutable boundaries, baseline, and
   non-goals;
3. a chronological iteration log covering successful, failed, diagnostic, and
   aborted attempts;
4. symptoms, exact evidence, direct root cause, contributing conditions, the
   missing or insufficient control, remediation, and validation state;
5. every material code, configuration, data, test, and documentation artifact
   changed;
6. explicit "worked", "did not work", resume, checkpoint, invalidation, and
   anti-duplication guidance;
7. follow-up tasks by component with owner or `pending`, trigger/date, status,
   and closure evidence;
8. residual risk, production/publication authority, and supersession rules;
9. a completeness attestation and machine validation result.
10. for every source ingestion or material capability update, the stable
    capability iteration ID; before/after engine state; per-source
    contribution; domain/subdomain and cross-layer effects; validation layers;
    unproven limits; updated descriptions; and vector/publication state.
    A process-only update must use an immutable, schema-validated
    `artifacts/capability-updates/<iteration-id>/capability-update.json`
    manifest and explicitly prove whether the engine corpus changed; it must
    never fabricate an ingestion intake or handoff.

Use `validated`, `observed`, `designed`, and `blocked` precisely. Do not promote
an observation into a validated conclusion or merge engineering completion,
production qualification, and publication authorization.

## Strict capture procedure

1. Open the record at task start or immediately after the first material
   deviation; mark it `observed` or `in_progress` in the register.
2. Append each material iteration before starting a materially different
   remediation. Include the evidence path and state what is reusable.
3. Preserve immutable run reports and logs. Correct their status through the
   governed tool; never rewrite a failed or aborted run as successful.
4. Update the failure/remediation register and tests when the root cause is
   known. A retry without a changed hypothesis or control is not remediation.
5. At task closure, reconcile the lesson with final reports, update follow-up
   tasks and residual risks, complete the attestation, validate the pack, and
   update the record status. Historical attempts remain.
6. For an ingestion or capability update, add or update the canonical entry in
   `config/pipeline/engine-capability-register.v1.json`, update
   `docs/ENGINE_CAPABILITY_REGISTER.md`, the root README, and each affected
   domain/GPT description, then record the same capability iteration ID in the
   lesson. A qualifying handoff without this accounting is technically
   complete but not project-iteration complete.
   For a process-only update, first create its immutable capability-update
   manifest, bind the affected controlled artifacts and version, and retain
   development/pending/blocked maturity wording until higher evidence passes.
7. If later evidence changes the conclusion, create a new record or append a
   dated supersession section. Never silently rewrite history.

## Evidence and anti-duplication rules

- Prefer exact report, log, manifest, test, hash, source, model, and profile
  paths over conversational recollection.
- Distinguish canonical artifacts from checkpoints and diagnostics.
- State which completed stages are hash-compatible and reusable.
- State the earliest safe resume point and every condition that invalidates it.
- Record attempted commands only when they explain a failure, an operating
  constraint, or a reproducible recovery step.
- Do not duplicate large logs in Markdown; cite them and quote only the
  minimal identifying error.
- When a task is still active, mark incomplete results as interim and update
  the same record at final gate closure.

## Validation and review

Run:

```powershell
uv run python tools/validate_lessons_learned.py
uv run python tools/validate_engine_capability_harness.py
```

The validator checks registry structure, unique IDs, dates, component/task
metadata, registered file coverage, and mandatory headings. Human review must
still check factual completeness, evidence interpretation, root-cause quality,
owners, and publication wording. A passing validator is necessary but not
sufficient for lesson closure.

For ingestion and capability work, both validators are mandatory. The
capability harness discovers every qualifying handoff and verifies its
source-level metrics, domains/subdomains, validation limits, documentation,
and publication state against immutable run evidence.
It also discovers process-only capability-update manifests and verifies their
schema, hashes, unchanged-or-changed engine state, validation limitations,
documentation, and publication/go-live boundaries. Run any capability-specific
validator named by the project pipeline contract before the general closure
validators.

## Ownership and maturity

The task owner maintains the record while work is active. Component owners
review lessons affecting their controls; the release owner reviews any claim
about qualification or publication. Where names or service ownership are not
yet assigned, record `pending` and keep the associated readiness claim
blocked.


Control iteration `CAP-2026-10-06-COMPETENCY-PROPAGATION-V1`: source contributions and behavioral/runtime competency propagation must reconcile before project closure. See [competency control](../docs/COMPETENCY_PROPAGATION.md).


## 2026-10-11: reusable finding-to-verification trail

Use the [remediation trail guide](../docs/PROJECT_REMEDIATION_TRAIL.md) and `config/pipeline/vector-engine-remediation-control.json` to follow evidence, nine known gaps, eight ordered tasks, dependencies, acceptance, actual retests, reviewer closure and lessons. Run `tools/validate_project_remediation_control.py --self-test` before resuming and after each iteration. All engine corrections remain open; trail validation is not expert qualification.


## V2 granular audit control — 2026-10-11

The [audit and improvement standard](../docs/PROJECT_REMEDIATION_AUDIT_STANDARD.md) extends the trail with twelve audit dimensions, 24 work packages, per-task verification plans, reproducible iteration records, repeat-run justification, measured-effort rules and independent closure/qualification gates. Use the v2 project/iteration templates for new work. Run the control validator at its five checkpoints; this is a manual guard, not automatic runtime or CI integration. Engine remediation remains open.
