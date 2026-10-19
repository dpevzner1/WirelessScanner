# Release approval checklist

Release candidate: `<ID>`  
Corpus snapshot: `<ID>`  
Embedding profile: `<ID>`  
Generator profile: `<ID or none>`  
Target environment: `<environment>`

## Required evidence

| Gate | Status | Evidence reference | Approver | UTC timestamp |
| --- | --- | --- | --- | --- |
| Corpus ownership and rights | pending |  |  |  |
| Classification, privacy, and ACL review | pending |  |  |  |
| Taxonomy and competency preservation | pending |  |  |  |
| Independent blind retrieval evaluation | pending |  |  |  |
| Embedding/profile promotion | pending |  |  |  |
| Vector-store/server conformance | pending |  |  |  |
| Generator compatibility, if serving | pending |  |  |  |
| Backup, restore, and disaster recovery | pending |  |  |  |
| SLO, capacity, and cost | pending |  |  |  |
| Change, cutover, and rollback | pending |  |  |  |

## Non-waivable assertions

- [ ] No unauthorized retrieval or forbidden output.
- [ ] Citation resolution is 100%.
- [ ] No critical exact-token/domain/capability regression.
- [ ] No unexplained drift.
- [ ] Distribution integrity passes.
- [ ] Target server and isolated restore pass.
- [ ] Production monitor has no diagnostic skips.
- [ ] Prior production alias/snapshot is available for rollback.
- [ ] Known limitations are accepted by accountable owners.

## Final authorization

Production publication authorized: `false | true`  
Authorized by: `<named authority>`  
UTC timestamp: `<timestamp>`  
Change record: `<ID>`  
Promotion command/result evidence: `<immutable reference>`

If any required row is pending or missing evidence, authorization remains
false. Do not use verbal approval as a substitute for this record.
