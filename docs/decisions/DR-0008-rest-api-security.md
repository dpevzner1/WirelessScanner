# Decision DR-0008: REST API — header-only constant-time keys, no wildcard CORS, actionable start errors

Status: approved (implemented, T08)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
Finding F05:
- the live API key was published in the README, and therefore in GitHub history (confirmed by attestation step F05-KEY; the value was never printed);
- keys were accepted in the query string;
- CORS was `*`;
- exception text was echoed in 500 responses;
- port conflicts were only logged.

Engine C-03 lists query-string secrets and unauthenticated metrics as mandatory pushback items.

## Decision
- **Authentication:** `X-API-Key` header only. A query-string key returns 401 with guidance. Comparison uses `CryptographicOperations.FixedTimeEquals`.
- **CORS:** headers only for an explicitly configured `AllowedOrigin` (none by default).
- **Responses:** `Cache-Control: no-store`; 500 responses are generic.
- **Start errors:** `IRestApiServer.LastError` is shown on the REST API tab, e.g. "Port N is already in use…".
- **Binding:** stays `localhost`-only.
- **Docs:** the key is removed from all documentation; the owner is told to regenerate it.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| `ApiTests` (10) | 108/108 | Query key rejected, CORS not wildcard, constant-time match edge cases, port-conflict message | Key rotation is an owner action |

## Alternatives

### Keep query-string keys for convenience
Rejected (C-03).

### Windows authentication
Deferred: it would change the integration model for scripts.

## Consequences
- Integrations that used `?api_key=` must switch to the header (CHANGELOG, breaking).

## Exit and rollback criteria
- Success gate: tests pass, and the owner regenerates the key (Blocked: owner action).

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Security | demitri.pevzner | approved via "begin" (2026-10-15) | 2026-10-15 | session transcript |

## Supersession rule
Write a new decision for remote (non-localhost) exposure, TLS, or multi-key / role-based access.
