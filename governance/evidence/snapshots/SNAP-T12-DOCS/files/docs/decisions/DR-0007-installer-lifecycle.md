# Decision DR-0007: Installer lifecycle — .NET Framework 4.8 wizard with a tested core, verified payload and maintenance copy

Status: approved (implemented, T09; end-to-end run Blocked pending owner approval)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
- The owner asked for installer, update, modify, repair and uninstall behaviour.
- The baseline (F06) had:
  - an uninstaller path pointing at the download folder;
  - no version comparison, repair or modify;
  - stale files and orphaned folders after updates;
  - data deleted on uninstall;
  - the wrong finish text;
  - a dependency on the .NET 8 Desktop Runtime.

## Decision
- **Wizard on .NET Framework 4.8**, which is part of Windows 10 and 11, so there are no prerequisites. It is small: the exe, a config file and one DLL.
- **Lifecycle logic** lives in `WirelessScanner.Setup.Core` (netstandard2.0) and is unit-tested from the .NET 10 suite:
  - `VersionPolicy`: Fresh, Upgrade, Reinstall or Downgrade; a downgrade needs confirmation;
  - `PayloadAssembler`: joins the parts and verifies size and SHA-256 from `WirelessScanner.payload.json`;
  - `PackageExtractor`: zip-slip guard, plus an install manifest so files dropped by a newer version are removed, while files the installer never placed are not touched;
  - `DataSafety`: preserves legacy in-folder data before any change;
  - `ShortcutPlanner`: the desired state wins; unticking removes a shortcut;
  - `InstallPaths.ValidateInstallDir`: refuses drive roots, system folders, and non-empty foreign folders.
- **Maintenance copy:** setup copies itself to `<install>\Setup`. Apps & features `UninstallString` and `ModifyPath` point there (`/uninstall`, `/modify`). `NoRepair=1`, because repair needs the payload: running the downloaded setup again performs it.
- **Install folder:** cannot be changed during maintenance (prevents orphans). Moving means uninstall, then reinstall.
- **Uninstall:** keeps `%ProgramData%\WirelessScanner` unless the user ticks *delete data* (double confirmation). A maintenance copy removes its own folder after exit with a delayed `rmdir`.
- **Shortcuts:** all-users (`CommonDesktopDirectory` / `CommonPrograms`); 1.0's per-user shortcuts are removed.
- **Logging:** setup logs go to `<data>\logs\setup_*.log`.
- **Release pipeline:** `tools/build_release.py` (tests → self-contained single-file publish without PDBs → zip → net48 setup → 20 MB parts + payload manifest → `GitHub Export/EXE INSTALLER`).

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| `InstallerCoreTests` (12) | 108/108 | Version classes, integrity (corrupt/missing part), stale removal, zip-slip, data preservation, shortcut plan, folder validation | Registry, shortcuts and UI not automated |
| RELEASE-1 (`artifacts/release/release-summary.json`) | 1.1.0 | Payload 84.6 MB, 5 parts ≤ 20 MB | The installer was **not executed** (prohibited without owner approval) |
| Engine C-06 | 2026-10-15 | Plan and test install/update/rollback | — |

## Alternatives

### MSIX / WiX MSI
Deferred: they need signing and toolchain decisions. Could be revisited for enterprise deployment.

### Self-contained .NET 10 setup
Rejected: an extra ~70 MB file, larger than the GitHub-friendly 20 MB parts.

## Consequences
- Benefits: safe upgrades, truthful Apps & features entries, no data loss, integrity-checked installs.
- Obligation: the end-to-end run on a clean VM must happen before calling the installer production-ready. It covers fresh install, upgrade from 1.0 (data migration), repair, modify, and uninstall with and without data.

## Exit and rollback criteria
- Success gate: the owner's VM run passes the checklist in `docs/USER_GUIDE.md` §1.
- Stop condition: any data loss on upgrade.
- Rollback: the 1.0 installer remains in the git history of `GitHub Export`.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Architecture | demitri.pevzner | approved via "begin" (2026-10-15) | 2026-10-15 | session transcript |
| Operations | pending (end-to-end run) |  |  |  |

## Supersession rule
Write a new decision for a code-signing or MSI/MSIX move, an auto-update channel, or a change of payload format.
