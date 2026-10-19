# Lesson LL-2026-10-19-ANDROID-PORT: Porting WirelessScanner to an Android tablet (core, parity, first device builds)

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-19-ANDROID-PORT` |
| Task/change reference | Control task `T27` (finding F30); `T28` (F31, deferred); `T29` (F32, device gate); decisions DR-0015, DR-0016; consultations C-10, C-11 |
| Project | WirelessScanner (Android port) |
| Components | `AndroidApp/core` (domain, analytics, selection, runtime, responder, report, api, ie, net, forms), `AndroidApp/app` (preview), `WirelessScanner.Tests/AndroidParityFixtures.cs`, `tools/android_deploy.ps1` |
| Observed period (UTC) | `2026-10-19T02:42:03Z` (DR-0015) to `2026-10-19T13:27:33Z` (preview2 on the tablet) |
| Local date/time zone | 2026-10-18/19, America/Chicago |
| Recorded/updated (UTC) | 2026-10-19T13:40:00Z |
| Status | observed (port in progress; the record is updated per milestone) |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | Core tests (18/18), C# fixture run (`Category=AndroidParity`, 7/7), `deploy-20261019T131727Z`, `deploy-20261019T132423Z`, `deploy-20261019T132733Z` |
| Source/evidence scope | Synthetic addresses in fixtures; tablet serial masked; no real SSID or BSSID in this repository |

## Executive outcome
- **What is now true:**
  - The Windows 1.5.0 logic is ported to a pure Kotlin `core` module.
  - The C# code generates reference answers and the Kotlin code reproduces them exactly (7 parity suites): channel maths and spectrum, saturation builder + analytics in 4 scopes, physical units (including split, rename, merge and reset), connection analytics, responder bytes, selection, and 802.11 element parsing.
  - Reports (JSON, CSV, PDF model), REST API, forms and the explorer model are in core.
  - The device gate exists, and a visible preview build is installed on the owner's tablet.
- **Not proven:** the Android platform layer and the screens are not built. No Wi‑Fi scan, test or export has run on the tablet yet.
- **Breadcrumb:** the chronological detail is in [`AndroidApp/docs/PROGRESS_LOG.md`](../../AndroidApp/docs/PROGRESS_LOG.md) (AP-01 … AP-13).

## Expected behavior
Every Windows feature exists one for one on Android, using the tablet's own Wi‑Fi. Reports can be sent or saved to any installed app. Every parity claim has proof, and every device claim is bound to the exact APK hash.

## Initial conditions and boundaries
- Windows 1.5.0 is the source of truth.
- The reference projects are read-only.
- The engine is read-only.
- Toolchain offline-cached.
- The owner sideloads.
- Privacy rule: synthetic addresses only.

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 02:42Z | AP-01/02 | Decision, scaffold, first debug APK | Pass; `local.properties` escaping issue | DR-0015, APK | Write property files with the Write tool |
| 02:4xZ | AP-03 | UX spec (C-11) | Pass; engine UX gap noted | UX_SPEC, mock-up | — |
| local | AP-04 | DR-0016 future feature | Control file rejected twice (WP count, lesson_ids) | validator | Template rules apply to deferred items too |
| — | AP-05 | Core port | Compiles; .NET rounding and format semantics replicated | core | Never use Kotlin defaults for .NET-formatted numbers |
| — | AP-06 | Parity fixtures | 4/6 → 6/6 after fixing the JSON redaction scope and **lazy LINQ in the generator** | fixtures | Materialize fixture snapshots |
| — | AP-07 | Reports, API, parsers, forms | Storage round trip failed (null → "") → fixed | CoreTests | An explicit null must stay null |
| 13:17Z | AP-10 | Device gate | `unauthorized` detected, install blocked | deploy-…131727Z | See LL-2026-10-19-ANDROID-DEVICE-AUTH |
| 13:24Z | AP-11/12 | Authorized install of preview1 | Installed and launched, **black screen** | deploy-…132423Z, screenshot | Screenshot added to the gate |
| 13:27Z | AP-13 | preview2 | Installed, foreground, visible | deploy-…132733Z, launch-…png | — |

## Actual behavior and deviations
- **Planned difference from Windows:** CSV formula neutralization (A-LRN-034).
- **Platform limits stated in the parity matrix:** no TSF and no frame counters on Android. The tablet uses MLD evidence instead.
- The platform layer and the screens are still pending.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| AP-02 | Gradle "filename syntax incorrect" | Backslashes mangled in `local.properties` | Shell quoting | File written with the Write tool | Build passed |
| AP-04 | Control file invalid (T28/F31) | Fewer than 3 WPs; empty `lesson_ids` | Deferred items still follow the schema | WP3 added; lesson linked | `validate` true |
| AP-05 | Rounding/format differences (risk) | .NET banker's `Math.Round` vs Java; `F`/`0.##` rounding | — | `roundTo` via `Math.rint`; HALF_UP formatting | Parity 7/7 |
| AP-06a | Missing `radios[].key` | JSON writer redacted every "key" property | Over-broad secret filter | Redact `ResponderSettings.key` only | Parity pass |
| AP-06b | Selection fixture showed only the final state | C# generator kept lazy LINQ and live collections | Fixture not materialized | `.ToList()`; regenerated | Parity pass |
| AP-07 | Null strings became "" | Reader defaulted every null | No nullability info in Java reflection | Explicit null kept; default only when the key is absent | Round-trip test |
| AP-08/09/10 | Solved ADB problem repeated; size-only copy | Reference deployment lessons not imported | No device gate | `android_deploy.ps1`, DEPLOYMENT.md, DR addendum | LL-…-DEVICE-AUTH |
| AP-12 | Black screen after a "successful" launch | Compose default text colour black, no theme | The gate had no visual check | Explicit colours; foreground and screenshot check in the gate | preview2 screenshot |

## Root-cause narrative
- **Cross-language behaviour (AP-05/06/07):** the defects came from places where two runtimes differ quietly: rounding, lazy evaluation and nullability. Running the C# code to produce answers and requiring exact equality in Kotlin caught every one before any screen exists.
- **Device side (AP-08…12):** the failures came from treating weaker signals as proof:
  - "connected" as authorized;
  - "copied" or "same size" as verified;
  - "launched" as visible.

  Each is now a separate gate with its own evidence.

## Corrections and changed artifacts
- **New:** `AndroidApp/core/**`, `AndroidApp/core/src/test/**`, `WirelessScanner.Tests/AndroidParityFixtures.cs`, `tools/android_deploy.ps1`, `AndroidApp/docs/{UX_SPEC,PARITY_MATRIX,DEPLOYMENT,PROGRESS_LOG}.md`, DR-0015 (with addendum), DR-0016, `governance/bootstrap/add_android_and_categories_scope_2026-10-19.py`.
- **Changed:** README (documentation table, planned features), STATUS backlog, consultation log (C-11), control file (F30–F32, T27–T29), `AndroidApp/app/MainActivity.kt` (visible preview).

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| Core tests | — | 18/18 (7 parity suites, 11 behaviour tests) | JVM |
| C# fixture generator | — | 7/7 | Windows |
| Device install (preview2) | blocked | passed, foreground, screenshot reviewed | SM-X200 API 34 |
| Control and lesson validators | — | valid / no errors | governance |

## Engine capability impact
None (read-only). Gaps recorded: C-10 (no Android Wi‑Fi APIs), C-11 (no tablet, TalkBack or gesture concepts).

## What worked
- C#-generated parity fixtures with an exact JSON-tree comparison.
- Keeping all logic in a JVM module so it is tested without a device.
- Reading the reference records once the owner pointed at them: the answer was already there.

## What did not work
- Design-only reference intake.
- Size-only copy verification.
- Launch-only device checks.
- Over-broad secret redaction.

## Resume and anti-duplication map
- **Start of each Android session:** `tools/android_deploy.ps1 -PreflightOnly`.
- **Each build:** `tools/android_deploy.ps1 -ApkPath <apk> -Install`, then review the screenshot.
- **After any C# analytics change:** regenerate the fixtures (`dotnet test --filter Category=AndroidParity`) and run `./gradlew :core:test`.
- **Next work:** the platform layer, then the screens (PROGRESS_LOG "Next").

## Reusable prevention
1. **Parity by generated fixtures:** the source implementation computes the answers. Snapshot values eagerly, never through live views.
2. **Replicate number semantics explicitly** across runtimes (rounding mode, formatting culture, null handling).
3. **One evidence class per gate:** authorized ≠ connected; verified copy ≠ copied; visible ≠ launched.
4. **Reference intake covers operational sections**, and each known failure becomes a gate (see LL-2026-10-19-ANDROID-DEVICE-AUTH).

## Follow-up tasks
- T27: platform layer, screens, renders, device runs per milestone.
- T28: DR-0016 decisions from the owner.
- T29: confirm or reject H1 (the authorization-timeout setting reads `null`).
- Propose CSV formula neutralization for the Windows app.

## Residual risk
- Android scan throttling (4 scans per 2 minutes) will limit saturation-test cadence unless the owner turns throttling off.
- ICMP depends on the system `ping` binary.
- Exact JSON field parity holds for constructor fields; derived properties are added explicitly.

## Supersession
None.

## Completeness attestation
Covers every issue from AP-01 to AP-13 in the progress log. Nothing on the platform layer or screens is claimed.
