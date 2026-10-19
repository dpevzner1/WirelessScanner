# Review and audit attestation standard

Version 1.0.0, effective 2026-10-14 (America/Chicago). Requested by the user as a control for all reviews and audits in this project, whatever tool performs them. Authored by Claude Code (Anthropic, model claude-opus-5-5).

This standard sits beside the remediation control (`docs/PROJECT_REMEDIATION_AUDIT_STANDARD.md`); it does not replace it. The remediation control records what the project decided. This standard records who did each review or audit step, when, and on which bytes.

## 1. Purpose

Every review, audit, verification or evidence recap must leave a ledger that answers four questions for each material step:

- **When.** UTC and local time, written by the tool at the moment of the step.
- **Who.** The tool or person that performed it: tool, tool version, model, operator, role.
- **What.** The step's phase, kind and description, with every input and output file and its SHA-256.
- **How.** The exact command and exit code when something was executed, with hashes of its captured output.

The ledger is tool-agnostic. Claude Code, Codex, any other assistant, a CI job or a person uses the same command and the same record format.

## 2. Applicability

Mandatory for:
- independent or third-party verifications;
- implementation-owner evidence audits and recaps;
- qualification and closure reviews;
- holdout authoring and freezing;
- any review whose result will be cited in the remediation control.

Optional for exploratory reading that produces no cited artifact. If a later decision cites such reading, it must be redone under a ledger.

## 3. Components

| Component | Path | Role |
| --- | --- | --- |
| Command-line tool | `tools/review_attestation.py` | `init`, `record`, `run`, `verify`, `seal` |
| Record schema | `config/pipeline/review-attestation-record.v1.schema.json` | Shape of each ledger line |
| Tests | `tests/test_review_attestation.py` | Positive cycle, alteration, removal, drift, coverage, write roots, seal, identity, lock, protected manifest, signatures |
| Ledger | `<review dir>/ATTESTATION-LEDGER.jsonl` | One JSON record per line, hash-chained |
| Executions | `<review dir>/executions/` | Captured stdout and stderr of attested commands |

## 4. Record content

Each record contains:

- `schema`, `sequence`, `record_id`, `review_id`;
- `recorded_at_utc` and `recorded_at_local`;
- `actor`: `tool`, `tool_version`, `model`, `operator`, `role`, and `identity_basis` (`self_declared`, or `signature_verified` once verified);
- `step`: `phase`, `id`, `kind` and `description`;
- `command` (or null), `inputs`, `outputs`, `limits`;
- `previous_record_sha256`, `record_sha256` and `signature`.

Step kinds are `init`, `read`, `hash_check`, `execute`, `write`, `judgment`, `decision`, `freeze`, `note` and `seal`.

The `init` record also holds the session:
- purpose, authorized scope, prohibited actions and approved exceptions;
- write roots and coverage exclusions;
- platform and Python version;
- the state of an optional protected manifest, such as the engine source manifest under review.

The `seal` record holds:
- the inventory of every file in the review directory, with its hash;
- protected-manifest drift at the end of the review;
- the verification result taken immediately before sealing.

`record_sha256` is the SHA-256 of the canonical record without `record_sha256` and `signature`: JSON with sorted keys, compact separators, UTF-8. `previous_record_sha256` links each record to the one before.

## 5. Identity and signatures

Unknown values are written as `unknown`, never guessed. A tool must not copy another tool's or an earlier session's identity.

| Level | Method | Proves | Does not prove |
| --- | --- | --- | --- |
| L0 | Self-declared actor plus hash chain (default) | Records are complete and in order since they were written; files have not changed since attested; outputs stayed within the declared write roots | Who wrote a record. Anyone with write access can rewrite the whole ledger consistently. |
| L1 | `--sign-key` with `ssh-keygen -Y sign`, namespace `review-attestation`; verify with `--allowed-signers` | That the holder of a registered key wrote each signed record and that the record has not changed since | That the key holder was the declared tool or model, or that the review's judgments are correct |
| L2 | The project's existing signed governance, or an external notarisation of the sealed ledger hash | Organisational acceptance | Semantic correctness |

Keys belong to the accountable operator. A tool must never generate, store or use a signing key on its own initiative. The tests create a throwaway key only inside a temporary directory.

## 6. Mandatory procedure

1. **Before any work**, run `init` in a new review directory. Declare:
   - review ID, purpose and authorized scope;
   - prohibited actions and approved exceptions, each with who approved it and when;
   - write roots, normally the review directory only;
   - a protected manifest when an artifact under review must not change during the review.
2. **Execute only through `run`** so the command, exit code and captured output are attested. Commands outside the approved exceptions are not allowed.
3. **Record every material step** with `record`: evidence reads that a conclusion depends on (`read` or `hash_check`); each batch of judgments (`judgment`); each decision about interpretation or applicability (`decision`); each artifact written (`write`); frozen material (`freeze`). Record the step before the next step that depends on it.
4. **When a file is rewritten, attest it again.** The latest attestation supersedes the earlier one, and both stay in the ledger.
5. **Record failed attempts too.** Failed commands and rejected drafts stay attested; do not delete them.
6. **Verify at checkpoints**: before relying on earlier work, after interruptions, and before sealing.
7. **Seal at the end.** After the seal, the directory is closed. Further work needs a new review directory that cites the sealed ledger's final hash.
8. **Hand off the seal.** The custodian records the sealed ledger's final `record_sha256` in the remediation control as evidence. The custodian, not the reviewer, changes control state.

## 7. Verification checks (`verify`)

- schema, sequence and chain continuity;
- record hashes recomputed;
- timestamps non-decreasing;
- actor tool and role present and valid;
- no record after a seal;
- every output inside the declared write roots;
- every attested output still matching its latest attested hash;
- every file in the review directory attested, apart from declared exclusions;
- signatures verified when an allowed-signers file is given; unverifiable signatures are reported as warnings.

`--require-sealed` fails an unsealed ledger. Exit code 0 means valid, 1 means checks failed, 2 means the tool refused an operation.

## 8. Commands

```powershell
.venv/Scripts/python.exe tools/review_attestation.py init --dir <review dir> --review-id <id> --purpose "<text>" --actor-tool "<tool>" --actor-model "<model or unknown>" --actor-role reviewer --authorized-scope "<scope>" --prohibited "<action>" --exception "<approved exception, approver, date>" --protected-manifest <manifest.json>
.venv/Scripts/python.exe tools/review_attestation.py record --dir <review dir> --phase <phase> --step-id <id> --kind judgment --description "<what was decided>" --input <file> --output <file> --actor <actor.json>
.venv/Scripts/python.exe tools/review_attestation.py run --dir <review dir> --phase <phase> --step-id <id> --description "<why>" --actor <actor.json> -- <command> <args>
.venv/Scripts/python.exe tools/review_attestation.py verify --dir <review dir> [--allowed-signers <file>] [--require-sealed]
.venv/Scripts/python.exe tools/review_attestation.py seal --dir <review dir> --actor <actor.json> --protected-manifest <manifest.json>
```

Actor identity can come from a JSON profile (`--actor`), from `REVIEW_ACTOR_TOOL`, `REVIEW_ACTOR_TOOL_VERSION`, `REVIEW_ACTOR_MODEL`, `REVIEW_ACTOR_OPERATOR` and `REVIEW_ACTOR_ROLE`, or from individual flags. Flags override the environment, which overrides the profile.

## 9. Relationship to independence

The ledger shows who acted; it does not make a review independent.

- A review is independent only if the reviewer is not the implementer of the reviewed revision.
- Reviewer tooling and outputs live inside the reviewer's directory.
- Scores are produced by reading the current answers, not by rule or carry-forward.
- The implementer may act as custodian (storing, registering, running later controlled executions). Each custodian step must appear in the ledger with role `custodian`, so readers can see which conclusions depended on the implementer.

## 10. Limits

- Without L1 or L2 signatures, the ledger is accidental-drift and sequence evidence, not proof of authorship.
- A ledger cannot show that a tool performed no unrecorded action outside the review directory. Use a protected manifest for the artifacts that matter, and compare the workspace before and after.
- Correct attestation says nothing about whether a judgment is right.
