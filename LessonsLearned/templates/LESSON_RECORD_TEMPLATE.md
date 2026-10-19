# Lesson <ID>: <short title>

| Field | Required value |
| --- | --- |
| Lesson ID | `<stable unique ID>` |
| Task/change reference | `<task, ticket, incident, or change ID>` |
| Project | `<project identity>` |
| Components | `<affected source, engine, model, database, documentation, or operational components>` |
| Observed period (UTC) | `<start timestamp>` to `<end timestamp or in-progress>` |
| Local date/time zone | `<YYYY-MM-DD and IANA time zone>` |
| Recorded/updated (UTC) | `<timestamps>` |
| Status | observed \| corrected \| validated \| superseded |
| Owners/reviewers | `<accountable identities or pending>` |
| Related run/profile/snapshot IDs | `<immutable identities>` |
| Source/evidence scope | `<paths, hashes, reports, and exclusions>` |

## Executive outcome

State what is now true, what is not yet proven, and whether any publication or
production authority was granted.

## Expected behavior

What was expected and why?

## Initial conditions and boundaries

Record the starting state, approved scope, immutable/read-only boundaries,
baseline identities, and explicit non-goals.

## Iteration log

Record every material attempt in chronological order, including diagnostic and
aborted work. Do not remove failed attempts when a later attempt succeeds.

| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| `<timestamp>` | `<ID>` | `<what was attempted>` | `<pass/fail/abort/observation>` | `<path/error/metric>` | `<what may be reused and what must not be repeated>` |

## Actual behavior and deviations

What happened? Include exact error, metric, case ID, or report path.

## Failure, root-cause, and remediation register

Use one row per distinct failure or control gap. Separate a genuine quality
failure from a deliberate controlled abort.

| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| `<ID>` | `<exact observation>` | `<cause>` | `<why controls did not prevent it>` | `<change/profile/version>` | `<test/report/status>` |

## Root-cause narrative

Distinguish direct cause, contributing conditions, and why existing controls
did not catch it earlier.

## Corrections and changed artifacts

Describe code, schema, process, prompt, data, or operational changes. Identify
new version/profile IDs and list every material changed path.

## Validation

| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| <path/case> | <failure> | <result> | <limitations> |

## Engine capability impact

Required for every source ingestion or material capability update; write
`Not applicable` with rationale for unrelated work.

| Field | Required evidence |
| --- | --- |
| Capability iteration ID | `<CAP-YYYY-MM-DD-SHORT-ID>` |
| Change classification | source ingestion \| capability update \| mixed |
| Process-update manifest | `<immutable capability-update.json path/hash, or not applicable for ingestion-only work>` |
| Engine before/after | `<documents, pages, concepts, cross-references, corpus hashes>` |
| Source contribution | `<every selected source, domain/rank, pages, hydration, concepts, cross-document relationships, visual state>` |
| Domain/subdomain effect | `<introduced/strengthened/preserved/reduced plus measured score deltas and controlled subdomains>` |
| Cross-layer/adjacent effect | `<capability families, crosswalk behavior, adjacent domain evidence>` |
| Validation layers | `<structural retrieval, consultation behavior, independent response judging, target-device/HIL, regression tests>` |
| Honest limits | `<pending, blocked, or unproven claims>` |
| Description reconciliation | `<capability register, README, affected domain/GPT descriptions>` |
| Vector/publication state | `<not updated/candidate/projected/published plus explicit authorization evidence>` |
| Product/enterprise go-live state | `<development/pilot/candidate/qualified plus evidence and accountable approval, or not applicable>` |

## What worked

Record successful practices and the evidence that supports reusing them.

## What did not work

Record rejected approaches, why they failed, and the evidence that prevents
future repetition.

## Resume and anti-duplication map

Identify durable checkpoints, canonical versus diagnostic artifacts, completed
work that must not be repeated, the exact safe resume point, and invalidation
conditions.

## Reusable prevention

- New invariant:
- New positive test:
- New false-positive/negative test:
- New monitoring signal:
- New release gate:
- New documentation/control:

## Follow-up tasks

| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| `<action>` | `<component>` | `<identity or pending>` | `<date/event>` | pending \| active \| complete \| blocked | `<path or pending>` |

## Residual risk

What is still unproven, and what future evidence would close it?

## Supersession

Which future changes invalidate this conclusion? Name the record that
supersedes this one; never rewrite historical evidence to appear current.

## Completeness attestation

- [ ] Every task request and project component is represented.
- [ ] Every attempt/run is recorded, including failures and controlled aborts.
- [ ] Every material claim points to durable evidence.
- [ ] Successful and unsuccessful approaches are both retained.
- [ ] Checkpoints and anti-duplication instructions are explicit.
- [ ] Residual risks, owners, and review triggers are explicit.
- [ ] Dates use UTC plus the project's local date/time zone.
- [ ] Publication and production authority are stated separately.
- [ ] Capability iteration impact is reconciled, or explicitly not applicable.
- [ ] Local links, JSON, and registered-record validation pass.
