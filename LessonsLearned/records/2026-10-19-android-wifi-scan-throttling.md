# Lesson LL-2026-10-19-ANDROID-SCAN-THROTTLING: Android throttles app Wi‑Fi scans, and no permission or install option lifts it

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-19-ANDROID-SCAN-THROTTLING` |
| Task/change reference | Control task `T30` (finding **F33**); decision DR-0015 (detect, guide and fall back; addendum 2026-10-19); reference `AndroidApp/docs/WIFI_SCAN_THROTTLING.md` |
| Project | WirelessScanner (Android port) |
| Components | `AndroidApp/app/.../platform/Wifi.kt` (WifiProvider), `AppGraph.kt` (live cadence, status text), `core/runtime/Runtime.kt` (ScanScheduler), Live Survey banner and badge, SETTINGS → WI‑FI SCANNING guide, reports |
| Observed period (UTC) | `2026-10-19T02:42:03Z` (DR-0015 records the 4-per-2-min rule as a design input) to `2026-10-19T18:15:20Z` (read-only device probe) |
| Local date/time zone | 2026-10-19, America/Chicago |
| Recorded/updated (UTC) | 2026-10-19T18:30:00Z |
| Status | observed (platform rule confirmed on the device; app follow-ups open) |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Question raised by the owner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `deploy-20261019T175335Z` (b4), `deploy-20261019T180210Z` (b5), `deploy-20261019T180550Z` (b6); probe `governance/evidence/android/scan-throttle-probe-20261019T181519Z.txt` |
| Source/evidence scope | The probe used read-only adb commands; no device setting was changed. The device serial is masked. Screenshots showing real SSIDs stay in `governance/evidence/local-only/android-screens/` |

## Executive outcome
- **What happened.**
  - On the owner's tablet, the Android app shows `STALE (THROTTLED)` for most live cycles.
  - The owner asked whether this can be fixed in code, with a permission, or at install time.
- **What is now true.**
  - On this device the answer is **no**. Android 9+ allows a foreground app 4 scans per 2 minutes, and all background apps together 1 scan per 30 minutes.
  - The only exemptions are signature or privileged permissions, which a sideloaded APK cannot hold. Granting a permission at install time or by `pm grant` does not help.
  - On Android 14 the setting is not in `Settings.Global` (measured: `null`), so even `WRITE_SECURE_SETTINGS` would not reach it.
  - The supported fix is the owner switching off **Developer options → Wi‑Fi scan throttling**, once.
  - App-side mitigations are listed and partly built.
- **Defect found while probing.** The app keeps requesting a scan every **5 s** while throttled, although `AppGraph.liveIntervalMs()` selects 30 s (logcat 13:13:05 to 13:14:10 local). Open as T30-WP2.
- **Not proven.** The tablet has not yet been run with throttling OFF, so the 5 s cadence on Android has not been measured.
- **Authority.** No publication or production authority is granted. The device setting is the owner's to change.

## Expected behavior
- **Requirement:** the Windows app scans every 5 s, and the port was required to keep features one for one.
- **Expected:** reach the same cadence on Android, or state the platform limit clearly in the app, in reports and in project documents. Never show cached data as fresh.

## Initial conditions and boundaries
- **Device:** Samsung Galaxy Tab A8 SM-X200, Android 14 / API 34, build X200XXS6DYG6. Throttling at its factory default (on). Tablet not connected to any Wi‑Fi network.
- **App:** 1.5.0 builds b3 to b6, sideloaded with the project's own signing key (not platform-signed).
- **Boundaries:**
  - No changes to system or security settings on the device by the assistant.
  - No root.
  - The reference projects are read-only.

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-19T02:42Z | DESIGN | DR-0015 records the 4-per-2-min rule; chooses "detect, guide and fall back" | Design input | DR-0015 §platform, mapping table | Keep the design; this lesson adds measured facts |
| 2026-10-19 (b1–b2) | BUILD | `WifiProvider`: refused `startScan` → STALE `scan_throttled`; `isScanThrottleEnabled`; THROTTLED badge, banner, guide; 30 s cadence when throttled | Built | `platform/Wifi.kt`, `AppGraph.kt:150` | — |
| 2026-10-19T17:43Z–18:07Z | DEVICE | Smoke tests b3–b6 on the tablet | Live view alternates FRESH and `STALE (THROTTLED)`; the banner takes two lines | local-only `smoke-*.png` | Banner to one line (T30-WP5) |
| 2026-10-19T18:11Z | PROBE-1 | `settings get global wifi_scan_throttle_enabled`; `dumpsys wifi \| grep throttl`; `cmd wifi help` | `null`; `wifi_scan_throttle_enabled=true` in the Wi‑Fi store; 75 foreground requests throttled; no shell switch | conversation; repeated in PROBE-2 | `Settings.Global` route rejected |
| 2026-10-19T18:15:20Z | PROBE-2 | Same probe saved as evidence, plus the logcat cadence | 114 throttled; one request every 5 s | `governance/evidence/android/scan-throttle-probe-20261019T181519Z.txt` | Cadence defect → T30-WP2 |
| 2026-10-19T18:2xZ | ANSWER | Owner told: no permission or install route; developer option is the fix; app mitigations proposed | Owner asked to document it thoroughly for future projects | conversation | This record + `WIFI_SCAN_THROTTLING.md` |

## Actual behavior and deviations
- `startScan()` returns false for about 3 of 4 requests in each 2-minute window. The app then shows cached results labelled `STALE (THROTTLED)`, as designed.
- **Deviation:** the request cadence is 5 s rather than 30 s while throttled. The refused requests don't use up the budget (Android counts only accepted scans), but they produce log noise, extra work and a status line that is mostly STALE.
- **Deviation:** the throttle banner takes two lines on the 1280 × 800 dp landscape layout. That leaves only three rows visible in the network slide-over.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| R1 | Most live cycles show `STALE (THROTTLED)` | Android scan throttling (platform rule) | Factory default on; the exemptions are privileged-only | Owner turns off the developer option; app detects, guides and falls back | Platform limit; documented |
| R2 | Request every 5 s while throttled (logcat) | Not yet diagnosed. Hypotheses: (a) a cadence request held elsewhere (FORCE SCAN, capture, or the scheduler default) overrides the 30 s request; (b) `liveIntervalMs()` is evaluated before `isScanThrottleEnabled` / `recentlyRefused` is known and never re-applied | No device check of the request cadence in the gate | T30-WP2: find which cadence holder wins; re-apply on the throttle state change; add a logcat cadence check to the smoke test | open |
| R3 | Banner takes two lines; only three list rows visible | Long text in a full-width banner | UI reviewed in landscape only after the slide-over change | T30-WP5: one-line banner with ⓘ | open |
| R4 | Risk that a future project tries permission or install-time fixes | No project-level reference existed | Platform knowledge was only in DR-0015 as one line | `AndroidApp/docs/WIFI_SCAN_THROTTLING.md`, this record, memory rule | documented |

## Root-cause narrative
- **Direct cause:** an Android platform policy that exists to save battery and protect location privacy. It is not an app defect.
- **Contributing conditions:**
  - Wi‑Fi survey tools assume they control the scan rate, as on Windows.
  - Android's exemptions are all signature or privileged permissions.
  - On Android 11+ the setting moved out of `Settings.Global`, so the older adb tricks found online no longer work.
- **Control gap:** the device gate checked install, launch and rendering but not the scan-request cadence, so R2 was only found when the owner asked about STALE readings.

## Corrections and changed artifacts
- Added `AndroidApp/docs/WIFI_SCAN_THROTTLING.md`: the reference for future projects (limits, measured probe, what does and doesn't lift it, the app pattern, checklist).
- Added `governance/evidence/android/scan-throttle-probe-20261019T181519Z.txt`: read-only probe.
- Added this record and its register entry.
- Added a DR-0015 addendum line pointing to the reference.
- Added `AndroidApp/docs/PROGRESS_LOG.md` entry AP-17.
- Added control file finding F33 and task T30.
- No app code changed in this step. The mitigations are planned in T30.

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| Probe (read-only) | Unknown where the setting is stored | `Settings.Global` = `null`; Wi‑Fi store `true`; 114 refused | One device, Android 14 |
| App status text | — | `STALE (THROTTLED)` shown, never FRESH, for refused scans | Screenshots b4–b6 |
| Throttling OFF run | not run | pending (owner action) | — |

## Engine capability impact
Not applicable. No engine source ingestion or capability update. The DevOps engine has no Android Wi‑Fi API sources (consultation C-10 gap); this record is project knowledge.

## What worked
- Detecting a refused `startScan()` and labelling the cycle STALE with the reason. Users are never shown cached data as fresh.
- Read-only probes (`dumpsys wifi`, `settings get`, `logcat`) answer "where is the setting and is it enforced" without changing the device.

## What did not work
- **Install-time or permission routes:** not possible. `NETWORK_SETTINGS` and the related permissions are privileged; `pm grant` refuses them.
- **`Settings.Global.wifi_scan_throttle_enabled`:** absent on Android 14, so writing it would do nothing.
- **Trusting the cadence code without a device log check:** it hid R2.

## Resume and anti-duplication map
- **Canonical reference:** `AndroidApp/docs/WIFI_SCAN_THROTTLING.md`. Do **not** research permission or install routes again; section 3 lists every route and why it fails.
- **Safe resume point:** T30-WP2 (cadence defect), then WP3–WP5. Run the throttle-OFF measurement after the owner switches the developer option off.
- **Invalidation:** a future Android release that changes the budget, or a device whose `Settings.Global` key is non-null. Re-run the probe on every new device or OS version.

## Reusable prevention
- **New invariant:** a refused or cached scan is never shown or exported as fresh. Every report states the effective scan interval and the refused count.
- **New positive test:** with throttling OFF, the 5 s cadence gives FRESH on every cycle on the device.
- **New false-positive/negative test:** with throttling ON, the logcat throttled count over 2 minutes is ≤ 1, which proves the app paces itself instead of hammering.
- **New monitoring signal:** `WifiScanRequestProxy … throttled` lines per minute in the smoke test; the diagnostics export includes the refused count.
- **New release gate:** the Android device smoke test includes the read-only throttle probe and the cadence check.
- **New documentation/control:**
  - `WIFI_SCAN_THROTTLING.md` §6 is a checklist for new Android Wi‑Fi projects;
  - a memory rule points to it;
  - DR-0017 records throttling as a platform limit, so Windows takes no action.

## Follow-up tasks
| Task | Component | Owner | Due/review trigger | Status | Closure evidence |
| --- | --- | --- | --- | --- | --- |
| T30-WP1 Turn off Developer options → Wi‑Fi scan throttling, then re-run the probe | Tablet | demitri.pevzner | Next device session | pending | probe shows `false`; FRESH every 5 s |
| T30-WP2 Fix the 5 s request cadence while throttled (R2) | `AppGraph`, `ScanScheduler` | Claude Code | Next Android build | pending | logcat ≤ 4 accepted plus ≤ 1 refused per 2 min |
| T30-WP3 Per-AP age from `ScanResult.timestamp` | `WifiProvider`, explorer rows | Claude Code | Next Android build | pending | rows show age; parity row added |
| T30-WP4 Process system-initiated results; sample the connected link for tests on that network | `WifiProvider`, test runners | Claude Code | Next Android build | pending | device log + test report |
| T30-WP5 One-line throttle banner with ⓘ | `MainActivity` Banner | Claude Code | Next Android build | pending | screenshot landscape + portrait |

## Residual risk
- Other devices or OEM builds may store or enforce the limit differently.
- Throttling OFF has not been measured on this tablet.
- Windows parity is not affected (platform limit under DR-0017).

## Supersession
Superseded if Android changes the scan budget or adds an app-grantable exemption. Record the new rule in a new record; do not edit the measured facts above.

## Completeness attestation
- [x] Every task request and project component is represented.
- [x] Every attempt/run is recorded, including failures and controlled aborts.
- [x] Every material claim points to durable evidence.
- [x] Successful and unsuccessful approaches are both retained.
- [x] Checkpoints and anti-duplication instructions are explicit.
- [x] Residual risks, owners, and review triggers are explicit.
- [x] Dates use UTC plus the project's local date/time zone.
- [x] Publication and production authority are stated separately.
- [x] Capability iteration impact is reconciled, or explicitly not applicable.
- [x] Local links, JSON, and registered-record validation pass (`tools/validate_lessons_learned.py`, 2026-10-19).
