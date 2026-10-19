# Project remediation audit and improvement standard

Version 2.0.0, effective 2026-10-11 America/Chicago. Authority: user request for a highly structured, granular control/harness that prevents repeated work and supports audit, self-improvement and optimization. Applies to implementation, investigation, controlled assessment, recovery and qualification. This standard supplements the existing iterative lesson standard.

## 1. Purpose and operating boundary

The control is a working record, not a retrospective narrative alone. A successor must be able to determine what was attempted, reproduce the finding, understand why a choice was made, inspect actual results and resume at the next safe action without repeating invalid assumptions. It must also let an auditor distinguish evidence from belief and evaluate whether a correction reduced recurrence or merely moved the problem.

Current execution enforcement is a manual validation checkpoint, with machine-checked records and explicit event append. It is not wired automatically into every engine command or CI job. An operator must run it before work, before retests, after changes/failures and before closure/qualification. Do not imply automatic runtime enforcement. Automated integration may be a separately scoped follow-up.

## 2. Canonical artifacts and authority

| Artifact | Authority and use |
| --- | --- |
| `config/pipeline/vector-engine-remediation-control.json` | Active project scope, known findings, ordered tasks, evidence, event history, iteration records and qualification state |
| `config/pipeline/project-remediation-control.v2.schema.json` | Required record shapes; cross-field evidence and semantics checked separately |
| `tools/validate_project_remediation_control.py` | Structural, link/hash, dependency, closure and event validation; callable checkpoint |
| `tools/remediation_audit_rules.py` | Granular reproducibility, recurrence, effort and qualification rules |
| `LessonsLearned/templates/project-remediation-control.v2.json` | Future-project scaffold; placeholders must be populated and deliberately fail validation |
| `LessonsLearned/templates/REMEDIATION_ITERATION.v2.json` | One attempt's hypothesis, alternatives, environment, change, tests, outcomes, cost, learning and resume fields |
| Frozen suite and raw run receipts | Actual baseline evidence; preserved, never overwritten by successful successors |
| Registered 18-heading lesson records | Explain deviations, outcomes, transferability and supersession |

If a prose summary conflicts with raw evidence, investigate and correct the summary in a new recorded revision. Never silently change the historical evidence. Current VECTOR baseline remains not qualified.

## 3. Record model and traceability

Use stable IDs for projects, findings, tasks, work packages, tests, iterations, events, evidence and lessons. Every finding must have evidence and one or more mapped tasks; task/finding links must agree in both directions. Every task has prerequisites, acceptance, three or more work packages, a verification plan, risks and a next action. Each verified work package has supporting evidence; a task cannot close while a work package or prerequisite remains unverified.

Traceability chain:

`authorized scope -> preserved baseline -> observed symptom -> cause/hypothesis -> alternative decision -> targeted change -> actual tests -> regression comparison -> review -> closure or reopened finding -> transferable lesson -> next action`

Task granularity is determined by independently verifiable outputs. Avoid a task named only “improve quality”; define the exact behavior, artifact, boundary and failure case.

## 4. Finding record: what an auditor must understand

Record symptom, reproducible conditions, exact evidence, impact, root cause and confidence, contributing conditions, rejected explanations and what would disconfirm the cause. Include recurrence signature and applicable prior lessons. Distinguish a demonstrated cause in one path from an assumption about all paths. Tag priority and the affected task/domain/control. New defects discovered during retest become new finding IDs; do not hide them in a generic failure count.

Known facts, assumptions, unknowns and proposed investigations remain distinct. Current nine findings are supported by the engine assessment; they do not prove exhaustive discovery of every defect.

## 5. Roles and accountability

| Role | Required responsibility |
| --- | --- |
| Implementation owner | Proposes bounded change, identifies artifacts and writes raw execution record |
| Verification reviewer | Reviews exact acceptance checks, failed attempts, regressions and closure evidence |
| Independent qualification reviewer | Judges whole-scenario semantics and claim support; cannot be substituted by the implementation author |
| Evidence custodian / recorder | Captures paths/hashes, timestamps, actual tool/model attribution and scope limits; preserves originals |
| Project decision owner | Resolves tradeoffs or approval decisions that genuinely require human authority |

Roles may be pending during planning. Task closure requires a named accountable owner/reviewer. Independent qualification requires a distinct reviewer and evidence. Assigning a name or checking a Boolean is not proof of reviewer competence; auditors inspect the actual signed/governed review where applicable.

## 6. Required checkpoints and safe execution

Before starting: read scope, current state, prior lessons, baseline and dependencies; validate the control; verify active run ownership before any engine processing. Do not restart completed or failed runs merely because a monitor woke up.

Before a rerun: record the hypothesis, disconfirming observation, exact input/engine/context/test identity and planned positive/negative/regression checks. Search prior iteration fingerprints. If nothing relevant changed, either reuse the evidence or state a specific reason for repeatability, environmental investigation or independent replication. “Try again” is not a sufficient explanation.

After a material change or failure: retain raw commands, outputs, errors, timestamps and partial results; record changed artifacts, what worked/did not, cause confidence and safe resume. Separate environment/discovery/tooling errors from engine answer failures. Preserve failed attempts even when the next attempt passes.

Before closure: verify every work package, prerequisites, change evidence, actual verification, matched regression results, review and lesson. Reopen a finding if a later matched run contradicts its acceptance result.

Before qualification: check the complete answer/rubric gates and independent evidence. Conversion, routing, schema checks or correct arithmetic cannot stand in for enterprise scenario answers.

## 7. Iteration record: granular fields and meaning

| Group | Minimum contents | Audit question |
| --- | --- | --- |
| Identity/scope/state | Stable ID, task/finding links, authorized track, start/end UTC, verdict | Was this the intended operation, and is it terminal? |
| Hypothesis | Proposed cause/correction, disconfirming result, prior lessons | Could this attempt falsify the hypothesis? |
| Decision | Feasible options, chosen option, why, rejected alternatives | Why was this the smallest justified next step? |
| Reproduction | Baseline refs, OS/runtime/environment/config, input/engine/context/test hashes, fingerprint | Can another operator reproduce and compare it? |
| Change | Exact modified artifacts/evidence, authority, scope | What actually changed relative to the failed baseline? |
| Test | Commands, positive/negative/regression checks, raw result refs, reviewer/independence | Was the claimed behavior exercised rather than inferred? |
| Outcome | Pass/fail/abort/inconclusive, worked/failed, new findings, residual risks | What did the attempt establish and fail to establish? |
| Effort | Elapsed/person/rework seconds, measurement source or explicit unknown | Can efficiency be calculated honestly? |
| Reuse | Similar iteration IDs, prior result used, repeat justification | Was prior knowledge used to prevent duplication? |
| Lessons | Registered lesson IDs, transferable rule, applicability limits | What should a future project do differently? |
| Resume | Next action, accountable owner, conditions | Where can work continue safely? |

An unexecuted template uses planned state and null measurements. Terminal attempts need actual raw result references, commands, outcomes and learning. Aborted and inconclusive attempts remain visible. The fingerprint binds input, engine, context and test hashes; additional environment facts must be compared by the reviewer. A changed environment can justify repeating a fingerprint, but must be recorded.

## 8. State and completion rules

| State | Meaning | Does not establish |
| --- | --- | --- |
| Planned | Action, scope and acceptance designed | Implementation or execution |
| In progress / running | Owned work underway with recorded boundaries | Passing result |
| Implemented | Change exists | Verified behavior |
| Verified | Actual acceptance and regression evidence reviewed | Universal expertise or production authority |
| Blocked | Specific dependency/decision absent | Approval, or an excuse to suppress the failed result |
| Failed / aborted / inconclusive iteration | Terminal attempt with actual outcome and resume evidence | Successful correction |
| Qualified | All applicable release/answer gates reviewed independently | Production authorization unless separately granted |

No empty evidence lists, unresolved work packages, pending owners or generic passing reports can close a task. Qualification evidence must agree with counters and include both-phase, family, mandatory-obligation and critical-integrity gates. The current exposed 100-case VECTOR criterion is 98 whole passes, >=90% within each family rounded up, >=85 per answer phase, all mandatory obligations and zero critical violations. Hidden-case generalization remains separate; future projects must set their own approved thresholds.

## 9. Evidence hygiene and audit retention

Each evidence record has ID, workspace path, byte SHA256, producer, capture time, kind, claim scope and claim limit. Capture time denotes when this snapshot was recorded; inspect the source's original timestamps for execution timing. Actual attribution must not imply a model when the code was deterministic. Private evaluation oracles never enter engine context.

Evidence types include design, observation, implementation change, actual test, review, lesson and measurement. A design artifact is not a benchmark; a validator report is not semantic expertise. Never manufacture acceptance thresholds, performance numbers, citations or saved human effort. Retain originals, exact config/source versions, failed outputs and all attempts. Do not include secrets; identify any redaction and preserve a governed pointer when appropriate.

Hashes and linked chronological events catch accidental drift. They are not a cryptographic signature or tamper-proof ledger: someone can rewrite all hashes. Use the project's existing signed governance for trusted attestation; do not replace it with this control.

## 10. Anti-duplication and recurrence audit

For each attempt inspect similar input/engine/context/test fingerprints, finding recurrence signatures and prior lessons. Require a recorded reason for an unchanged rerun. Reuse prior evidence when still applicable; invalidate it when source, code, config, workload, context, authorization or rubric changes. Reusing a result requires matching conditions, not simply a similar title.

At review ask: Did the prior lesson alter this attempt? Is the same failure recurring because its root cause is uncorrected? Were generic metrics or optional tracks mistaken for the actual goal? Which assumptions drove wasted work? Was a smaller discriminating test available? Do not count duplicated logs as independent evidence.

## 11. Optimization metrics and calculation boundaries

| Metric | Calculation / required observations | Interpretation limit |
| --- | --- | --- |
| Duplicate reexecution | Count repeated fingerprint attempts without relevant changed hypothesis; inspect justification | Repeatability checks can be justified; not every repeat is waste |
| Rework fraction | Recorded corrective person-seconds / total person-seconds in same scope | Unknown if human effort not measured; wall time is not person time |
| Time to reproduce | Observation/investigation start to first reproducible proof | Requires comparable start definitions and actual timestamps |
| Time to verified closure | Finding observation to reviewed verification | Includes waiting time; split it from active effort when measured |
| Regression rate | New failures among previously passing matched cases / eligible matched cases | Changing corpus/rubric requires a new comparison basis |
| Clarification recurrence | Requests for already supplied facts / evaluated requests | Requires semantic review of what was truly supplied/ambiguous |
| Independent case success | Independent whole-case passes / all required cases | Missing answers stay in denominator; exposed set is not universal accuracy |

Current values are null because these improvement measurements have not been captured. Never fill them with plausible estimates. Require measurement evidence and units; fractions stay between0 and1. Record matching workload/config/time scope, confounders and missing data before comparing iterations or projects.

Optimization proposals need an ID, triggering evidence, hypothesis, expected benefit, potential harm/regression, controlled comparison, decision owner, acceptance criterion and outcome. Keep expected benefits separate from achieved improvements. Prefer one bounded correction at a time so cause/effect remains understandable.

## 12. Audit procedure and self-improvement review

An audit checks all twelve dimensions: scope/authority; fact/claim provenance; cause/hypothesis; task/dependency/acceptance; change/config identity; reproducibility/coverage; raw failures/recovery; independent review/closure; drift/regressions; duplication/effort; optimization/lessons; residual risk/resume.

For each dimension record reviewer/date, evidence references, conclusion, exceptions and next actions. Trace a sample from finding to raw output and a sample from closure back to changed code and matched tests. Examine reopened and aborted work, not only successful runs. Check owners and scope before recommending automatic execution. Record improvement proposals separately; an audit that identifies a problem does not itself remediate it.

Review after each completed correction and before final qualification. For future projects perform the realistic native-interface smoke test early; the present lesson is that structured calculators/context routing did not establish complete expert answering.

## 13. Commands and future-project onboarding

```powershell
.venv/Scripts/python.exe tools/validate_project_remediation_control.py --self-test
.venv/Scripts/python.exe -m pytest tests/test_remediation_audit_control.py -q
.venv/Scripts/python.exe tools/validate_lessons_learned.py
```

Record a new event using `--record-event <event.json>` with ID, timezone-aware at, kind, details and valid evidence_ids. The tool derives continuity hashes, validates the proposed record and writes only on success. Use a single writer. Do not edit an old event to make the history look successful.

Copy the v2 project and iteration templates for another project. Replace placeholders with actual authorized scope, preserved baseline, evidence, findings and dependencies. Empty scaffolds fail intentionally. Run `--control <project-control.json>` from the project's workspace, with the corresponding schema/rules available. Validate after each record update. Prior v1 records remain historical and do not have the v2 granular audit guarantee.

## 14. Harness validation and known limitations

The live v2 control passes. Twelve adverse fixtures reject orphan findings, evidence drift, event breakage, unsupported closure/qualification, dependency cycles, missing work packages, missing negative tests, fabricated measured metrics, missing causes, inconsistent mapping and duplicate sequence. Sixteen focused tests additionally check valid iteration records, justified/unjustified repeats, missing raw outcome evidence, invalid chronology, unsupported effort, bad fractions, work-package evidence, disconfirmation, fingerprints and misuse of failed baseline as qualification.

Those tests exercise the control, not the engine. The harness cannot determine semantic truth from JSON structure, guarantee reviewer competence, measure unrecorded effort or guarantee universal defect discovery. All nine engine findings remain open and zero remediation tasks verified. No model/vector tests, automatic monitoring or publication are introduced.

## 15. How to judge whether this control helped

A later project should demonstrate fewer unreasoned reruns, faster reproducible findings, fewer reopened defects, stable regression performance and evidence-backed task closure under comparable scope. Until such measurements exist, report that the control is implemented and tested, not that a quantity of time or cost was saved.
