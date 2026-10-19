# Decision DR-0003: Fresh-scan cycles, and one ordered background writer for persistence

Status: approved (implementation tracked as T03/T04)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
- **F01:** `WlanScan` is never called, so every 2 s the app re-reads Windows' cached BSS list and stores it as new samples.
- **F02:** fire-and-forget `Task.Run` writes for the session and its samples race. The 2026-05-31 log shows `FOREIGN KEY constraint failed`, i.e. lost samples.
- **F03:** WLAN calls run synchronously on the UI dispatcher.
- **Engine consultation C-03:** do not poll faster than the source updates; keep timestamps and freshness; isolate providers so a blocking call cannot stall the UI.

## Decision
1. **Scan cycle API.** `INetworkAdapterProvider.ScanAsync(interfaceId, timeout, ct)` returns a `ScanCycleResult`:
   - **status:** fresh, stale or failed;
   - **timing:** request and completion times;
   - **error:** error code and reason;
   - **results:** the BSS list, each entry with a beacon age derived from `ullHostTimestamp`.

   `WlanAdapter` implements it with `WlanRegisterNotification` (ACM source). It waits for `wlan_notification_acm_scan_complete` (7) or `scan_fail` (8), up to the timeout, then reads the list. All native calls run off the UI thread. One WLAN client handle is kept per adapter instance and disposed at shutdown.
2. **Live tab.** The 2 s `DispatcherTimer` is replaced by an async background loop that runs fresh-scan cycles at a configurable interval (default 5 s) and marshals results to the UI.
3. **Persistence.** A single background writer (`System.Threading.Channels`, one consumer) persists work items in order. The session row is enqueued first, so a session's samples can never be written before it. Each cycle's samples are batched into one transaction. `SessionManager` keeps its public API; writes go through the writer.
4. **Schema upgrades.** `SchemaMigrator` uses `PRAGMA user_version` and idempotent `CREATE TABLE IF NOT EXISTS` / guarded `ALTER TABLE` statements. `EnsureCreated` alone cannot evolve existing databases.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Baseline audit F01–F03 | BL-2026-10-15 | No scan trigger; race visible in the log; UI-thread WLAN calls | Code reading plus a single log |
| Microsoft Native Wi‑Fi API contract (WlanScan, WlanRegisterNotification, WLAN_BSS_ENTRY) | Windows 10/11 SDK documentation, as known to the implementer | Scans are asynchronous and complete via notification; drivers must finish within 4 s | Not consulted live this session; verify on hardware (SAT-AC-10) |
| Engine C-03 | 2026-10-15 | Freshness, missing ≠ zero, isolation | General telemetry guidance, not WLAN-specific |

## Alternatives

### Poll the BSS list until the newest `ullHostTimestamp` changes
Rejected as the primary method: it is ambiguous when nothing new is heard. It is kept as a fallback freshness signal (beacon age).

### Fix the race by awaiting each write inline
Rejected:
- it blocks the scan loop on disk I/O;
- it still opens one transaction per sample.

## Consequences
- Benefits: true sampling, no lost samples, a responsive UI, and freshness that can be audited.
- Costs:
  - a native callback delegate must stay rooted for the adapter's lifetime;
  - writer shutdown must drain the queue on exit.
- New tests: a fake provider scan cycle, writer ordering (session before samples), and schema-upgrade idempotency.

## Exit and rollback criteria
- Success gate: tests prove ordering and status handling. A manual run shows cycle statuses that are mostly `fresh`, with RSSI values that change between cycles.
- Stop condition: the notification registration fails on target hardware. Then fall back to WlanScan plus a 4 s wait plus beacon age, label the cycles `stale` when unsure, and record a finding.
- Rollback: revert the changed files from the Git history in the mirror.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Architecture | demitri.pevzner | approved via "begin" (chat 2026-10-15) | 2026-10-15 | session transcript |
| Security | pending |  |  |  |
| Operations | pending |  |  |  |

## Supersession rule
Write a new decision if Windows changes the scan or notification contract, or if a different capture provider is added (e.g. a monitor-mode adapter).
