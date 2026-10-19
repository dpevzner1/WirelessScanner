# Decision DR-0006: Persistent data in %ProgramData%\WirelessScanner, with one-time copy-only migration

Status: approved (implemented, T07)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
Finding F04: the database, logs and API key used paths relative to the working directory, and the 1.0 uninstaller deleted the install folder, taking user data with it.

## Decision
- **Canonical location:** `AppPaths.DataDirectory` = `%ProgramData%\WirelessScanner`. It can be overridden with the `WIRELESSSCANNER_DATA_DIR` environment variable (for portable use and tests).
- **Contents:** `wireless_scanner.db`, `logs\`, `API\`. Reports default to `Documents\WirelessScanner\Reports`.
- **Migration:** `AppPaths.MigrateLegacyData()` runs at startup. It **copies** 1.0 data from the exe folder or working directory, never moves or overwrites it, and logs every action. The installer applies the same rule (`DataSafety`).
- **Security:** the app runs elevated, and ProgramData's default ACLs suit a machine-wide tool.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| `AppPathsTests` (2), `InstallerCoreTests.PreserveLegacyData…` | suite 108/108 | Paths are independent of the working directory; no overwrite; originals kept | Real upgrade from 1.0 on a machine: Blocked until the installer is run |

## Alternatives

### `%LOCALAPPDATA%` (per user)
Rejected: the app is elevated and machine-wide, and per-user data would split surveys between accounts.

### Next to the exe
Rejected: Program Files is not a data location, and uninstall removes it.

## Consequences
- Data survives upgrades and uninstalls. Tests must isolate the data directory (`TestEnvironment` sets the override).

## Exit and rollback criteria
- Success gate: an upgraded 1.0 machine shows its old sessions (Blocked: needs an owner run).
- Rollback: set `WIRELESSSCANNER_DATA_DIR` to the old folder.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Architecture | demitri.pevzner | approved via "begin" (2026-10-15) | 2026-10-15 | session transcript |

## Supersession rule
Write a new decision for a multi-user or roaming-profile data model, or for cloud sync.
