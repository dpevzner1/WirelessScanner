# Vector/RAG quality gate checklist

## Contract and integrity

- [ ] Runtime and dependencies are pinned and reproducible.
- [ ] Controlled distribution manifest verifies with no differences.
- [ ] Schemas validate all canonical and preservation records.
- [ ] Source and page hash coverage is 100% for admitted data.
- [ ] Profile/model/artifact/quantization identities are verified.

## Corpus and knowledge

- [ ] Intake passes and approved evidence count is nonzero.
- [ ] Duplicates, versions, failures, quarantine, and exclusions are enforced.
- [ ] Domain taxonomy is approved.
- [ ] Critical capabilities have owners and independent tests.
- [ ] Policy/reference/evaluation channels cannot become proof.

## Text and citations

- [ ] Raw canonical text is unchanged.
- [ ] Clean projection is versioned and offset-preserving.
- [ ] Titles/authors/page furniture are absent from model-visible payloads.
- [ ] RFCs, OIDs, commands, versions, IPs, dates, code, tables, and headings are preserved.
- [ ] Every chunk/citation resolves to a valid page/span.
- [ ] Demoted metadata reconstructs and hashes correctly.

## Retrieval and security

- [ ] Exact/lexical baseline passes.
- [ ] Dense and hybrid metrics pass macro and critical-domain floors.
- [ ] Exact identifier non-regression passes.
- [ ] Hard negatives and near-duplicate cases pass.
- [ ] Unauthorized/forbidden hits equal zero.
- [ ] Authorization occurs before ranking and expansion.
- [ ] Result citations are authorized, relevant, and compatible with abstention.
- [ ] Unchanged vectorization rerun performs zero work.
- [ ] Unexplained drift equals zero.

## Vector store

- [ ] Local adapter semantics pass.
- [ ] Target server TLS/authentication/RBAC pass.
- [ ] Required payload/SQL indexes exist and are used.
- [ ] Exact search is retained as the oracle.
- [ ] ANN recall versus exact meets threshold.
- [ ] Persistence, snapshots, isolated restore, and alias reconstruction pass.
- [ ] Load, concurrency, tail latency, memory, storage, and cost meet limits.
- [ ] Upgrade, failure injection, and rollback pass.

## Generator

- [ ] Model digest and quantization are pinned.
- [ ] Structured-output schema is enforced.
- [ ] Required/forbidden answer patterns pass.
- [ ] Exact citations pass.
- [ ] Insufficient-evidence abstention passes.
- [ ] Reference-not-proof and ACL-negative cases pass.
- [ ] Retrieved prompt-instruction resistance passes.
- [ ] Architecture/completeness cases pass.
- [ ] Cold and warm latency meet SLO.
- [ ] Repeated same-profile runs meet stability policy.
- [ ] Model remains outside the quality-judge role.

## Recovery and operations

- [ ] Backup is encrypted and integrity-verified.
- [ ] Isolated restore succeeds.
- [ ] Retrieval, citation, and ACL replay are equivalent after restore.
- [ ] RTO/RPO are measured and approved.
- [ ] SLOs, alerts, retention, on-call, incident, capacity, and cost are approved.
- [ ] Dual-run, shadow, canary, and rollback drills pass.

## Release

- [ ] Independent sequestered evaluation passes.
- [ ] Every gate has immutable evidence.
- [ ] Every required role has a named approver and UTC timestamp.
- [ ] Production authorization flag is explicit.
- [ ] Candidate alias promotion is explicit and prior alias is retained.
- [ ] Post-cutover observation is inside the error budget.
