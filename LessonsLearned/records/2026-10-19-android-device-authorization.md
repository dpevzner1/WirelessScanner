# Lesson LL-2026-10-19-ANDROID-DEVICE-AUTH: A solved Android device-authorization failure was repeated because reference deployment lessons were not carried into the port

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-19-ANDROID-DEVICE-AUTH` |
| Task/change reference | Control task `T27` (finding F30, Android port); decision DR-0015 (addendum 2026-10-19); finding **F32** (process) |
| Project | WirelessScanner (Android port) |
| Components | Android deployment, reference-lesson intake (DR-0015), breadcrumb/lessons harness, `tools/android_deploy.ps1`, `AndroidApp/docs/DEPLOYMENT.md` |
| Observed period (UTC) | `2026-10-19T02:42:03Z` (DR-0015 written; "no device on adb" noted, not acted on) to `2026-10-19T13:27:33Z` (authorized install of preview2 passed with a visible screen) |
| Local date/time zone | 2026-10-19, America/Chicago |
| Recorded/updated (UTC) | 2026-10-19T13:40:00Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Raised by the owner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `deploy-20261019T131727Z.json`; reference records: breathwork `LESSONS_LEARNED.md` A-LRN-016/017/043 and `PROGRESS.md` A-013…A-025; MobileApp `BUILD_LESSONS_PLAYBOOK.md` §1, §6, §10 and `REFERENCE_SCRIPTURE_APP.md` §5 |
| Source/evidence scope | The device serial is masked in this repository. The host key fingerprint is public-key material, not a secret |

## Executive outcome
- **What happened.**
  - The first Android preview APK was built and copied to the owner's tablet (`SD card\DEPLOY`) while `adb devices` reported the tablet as **`unauthorized`**.
  - The assistant asked the owner to fix the connection as if this were a new problem.
  - The same tablet (same serial), with the **same host ADB key** (SHA-256 `EA8421B2…49D2CAE`), had already hit this exact failure in both reference projects. Both recorded the fix and the rule to authorize the tablet *before coding* every session.
- **Why it matters.** The breadcrumb / lessons-learned harness exists so a later project does not repeat a solved mistake. This one was repeated because the reference intake carried over architecture and export lessons but **not the device and deployment lessons**, and nothing in the harness turned them into a gate.
- **What is now true.**
  - A device gate tool exists: `tools/android_deploy.ps1`. It runs a preflight, a deploy copy with a read-back SHA-256 check, and an install only on an authorized target, and it writes JSON evidence bound to the APK hash.
  - A deployment procedure exists (`AndroidApp/docs/DEPLOYMENT.md`).
  - DR-0015 has an addendum that maps the reference device lessons.
  - A mis-cited lesson ID is corrected.
  - A finding (F32) and a memory rule now require the preflight at the start of every Android session.
- **Resolution (2026-10-19T13:24Z).**
  - The owner performed the reference fix and enabled *Disable adb authorization timeout*.
  - Preflight now reports `device`.
  - preview2 (SHA-256 `6230F066…51C0DBDAC`) is installed, in the foreground and visibly rendered (GATE-3).
- **Not proven.** H1 (7-day revocation) is unconfirmed: `settings get global adb_allowed_connection_time` returns `null` on this tablet. The preflight in later sessions will show whether the authorization persists.

## Expected behavior
Before the first Android build was presented as deployable, the session should have:
1. Read the reference projects' deployment and device lessons, not only their architecture lessons.
2. Run `adb devices -l` and, on `unauthorized`, given the owner the reference fix at the start, before coding.
3. Recorded the device state as a gate.
4. Treated a file copy as "copied (hash-verified)", never as "installed".

## Initial conditions and boundaries
- **Reference projects** (read-only): `breathwork-app/2026AndroidPort` and `Daozhang/.../Publication/MobileApp`. Both target the Samsung Galaxy Tab A8 (SM-X200, Android 14 / API 34). The MobileApp playbook records the serial.
- **Owner instructions:** "I'll sideload the APK myself"; later, "all install files go here: This PC\Galaxy8NOMAD\SD card\DEPLOY".
- **Host:** the same Windows user and the same `%USERPROFILE%\.android\adbkey`, created 2026-08-03 and used by the reference projects.

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-19T02:4xZ | DR-0015 intake | Read the reference lessons for the port design | Imported A-LRN-001/004/005/010/011/012/030/034/035/036/041/043/044; **A-LRN-043 mis-summarised as "device evidence bound to APK hash"** (that is A-LRN-017). Deployment sections (playbook §1/§6/§10, R3 §5) **not read** | DR-0015 (original), session summary | Intake must cover the deployment and device sections |
| 2026-10-19T02:4xZ | Toolchain probe | `adb devices` | "No device on adb" noted as a fact and moved on; no gate | session summary | A device probe result must become a gate state |
| 2026-10-19 ~08:1x local | DEVICE-1 | Owner asked to push the app | `adb devices` empty; assistant gave generic USB-debugging advice | conversation | — |
| 2026-10-19 | DEVICE-2 | Owner showed the tablet in File Explorer | `*******SHKH unauthorized`; generic "accept the prompt" advice | conversation | Should have recognised the serial from the reference playbook |
| 2026-10-19 | DEPLOY-1 | Owner set the install folder; APK copied over MTP | Copy confirmed **by size only** (12,177,994 bytes); install not attempted | conversation | MTP success is not proof (playbook §6.4) |
| 2026-10-19T13:0xZ | REVIEW | Owner: *"this is a critical lessons learned moment … we are replicating an error that was solved for"* | Reference records searched: the same serial, the same host key fingerprint, the same `unauthorized` state across breathwork A-013…A-025, and the documented fix | reference files (read-only) | Root cause below |
| 2026-10-19T13:17:27Z | GATE-1 | `tools/android_deploy.ps1 -Install` with the preview APK | Preflight `unauthorized` (fix steps printed); deploy copy **read-back SHA-256 match**; install **BLOCKED BY DEVICE AUTHORIZATION** | `deploy-20261019T131727Z.json` | Gate works; owner action pending |
| 2026-10-19 ~08:23 local | OWNER-FIX | Owner performed the reference fix on the tablet (unlock, revoke/toggle, replug, "Always allow") and switched on *Disable adb authorization timeout* | Owner: "these are completed" | conversation | — |
| 2026-10-19T13:24:23Z | GATE-2 | `-Install -SkipCopy` with preview1 | Preflight **`device`** (SM-X200, API 34, arm64-v8a); install passed; launch PID; no fatal. **Screenshot: black screen** (finding AP-12 in LL-2026-10-19-ANDROID-PORT). `adb_allowed_connection_time` reads `null` | `deploy-20261019T132423Z.json`, `preview1-launch.png` | Screenshot and foreground check added to the gate |
| 2026-10-19T13:27:33Z | GATE-3 | preview2 (visible text) deploy + install | Copy hash match; install passed; foreground yes; screenshot shows the title and version | `deploy-20261019T132733Z.json`, `launch-20261019T132733Z.png` | Gate complete |

## Actual behavior and deviations
- **Deviation 1.** The device state was observed twice and never gated. Generic troubleshooting was offered instead of the reference's proven procedure.
- **Deviation 2.** The DR-0015 reference crosswalk omitted the device and deployment lessons, and mis-cited A-LRN-043.
- **Deviation 3.** The first deploy copy was verified only by size, not by hash.
- **Deviation 4.** Recognition of the tablet depended on the owner raising it.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| DA-1 | Tablet `unauthorized`; install impossible | The tablet has not (re)accepted this computer's ADB key | **H1 (hypothesis, unverified):** Android 11+ revokes authorizations unused for 7 days unless *Disable adb authorization timeout* is on. This fits "reset between sessions" in the references. Other possibilities: prompt dismissed, screen locked, authorization revoked | Owner performs the reference fix on the tablet (unlock → revoke/toggle → replug → "Always allow"), then enables *Disable adb authorization timeout* | **Open, owner action**; preflight reports the setting once authorized |
| DA-2 | Solved problem repeated | Reference deployment lessons not imported | Intake scoped to "architecture/export/UX"; no checklist item for "device/deploy lessons"; reference playbook §10 says *authorize first thing every session* | DR-0015 addendum mapping A-LRN-016/017/043 and playbook §1/§6/§10 and R3 §5; finding F32 | Corrected (documents) |
| DA-3 | A-LRN-043 mis-cited | Summary written from memory of the lesson table | No re-read of the source row when citing | Citations corrected (DR-0015 addendum, PARITY_MATRIX D2) | Corrected |
| DA-4 | Device state noticed but not acted on | No gate: the probe result was prose | Harness has no device-state step for mobile projects | `tools/android_deploy.ps1 -PreflightOnly` (exit 2 on not authorized); JSON evidence | GATE-1 ran |
| DA-5 | Copy accepted by size | MTP "copied" treated as verified | Playbook §6.4 not applied | Read-back SHA-256 comparison in the deploy step | GATE-1: match |

## Root-cause narrative
The engineering problem (an unauthorized ADB key) is ordinary, and the reference projects had solved it. The failure was in **knowledge transfer**:
- The reference intake for DR-0015 read the lesson tables for design topics and stopped there. It did not read the deployment playbook sections, which state the tablet serial, the `unauthorized` history, the fix sequence, and the rule to authorize before coding.
- Because the harness had no "mobile device gate" step, the first `adb` probe ("no device") was recorded as a fact rather than as a blocking state.
- Later probes (`unauthorized`) were answered with generic advice.

A breadcrumb trail only prevents repetition if:
1. intake explicitly covers *operational* sections (deployment, devices, release), not only design;
2. every known operational failure becomes an executable gate, not just a sentence.

## Corrections and changed artifacts
- **New:** `tools/android_deploy.ps1`
  - preflight: masked serial, host key SHA-256, model, API, ABI, authorization-timeout setting;
  - deploy copy over MTP with read-back SHA-256;
  - install smoke only on an authorized target: `adb -s … install -r`, cold launch, `FATAL EXCEPTION` check;
  - JSON evidence.
- **New:** `AndroidApp/docs/DEPLOYMENT.md` (gate, fix steps, evidence states, failure-diagnosis order).
- **Changed:** `docs/decisions/DR-0015-android-port.md` (device and deployment addendum, citation correction); `AndroidApp/docs/PARITY_MATRIX.md` D2 (A-LRN-017).
- **Governance:** finding F32 with task T29, an event, and evidence in the control file.
- **Memory:** the Android deploy memory now requires the preflight at the start of every Android session and reading the reference deployment sections.

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| Device preflight | prose ("no device") | `unauthorized` detected; exit code 2; fix printed | GATE-1 |
| Deploy copy | size match only | SHA-256 read-back match (`B14C63E2…A140820D`) | GATE-1 |
| Install | not attempted, not claimed | `BLOCKED BY DEVICE AUTHORIZATION`, recorded | GATE-1 |
| Authorized install and launch | — | GATE-2 preview1 passed (black screen); GATE-3 preview2 passed, foreground, visible | SM-X200 API 34 |

## Engine capability impact
None to the engine (read-only). This is a harness and process gap in this project's intake, not an engine fault. The engine's mobile guidance (C-10) asks for deployment and device validation; this project did not turn that into a gate.

## What worked
- The reference projects' records were complete enough to identify the problem in minutes: serial, host key, symptom, fix and the "authorize first" rule.
- The owner's insistence on the breadcrumb principle.
- An executable gate with JSON evidence, instead of another paragraph.

## What did not work
- Topic-scoped reference intake (design only).
- Citing lesson IDs from memory.
- Generic troubleshooting when a project-specific, proven procedure existed.
- Size-only verification of an MTP copy.

## Resume and anti-duplication map
- **Every Android session, first command:** `powershell -ExecutionPolicy Bypass -File tools/android_deploy.ps1 -PreflightOnly`. If the result is not `device`, give the owner the printed fix steps **before coding**.
- **Deploy:** `tools/android_deploy.ps1 -ApkPath <apk>` (hash-verified copy to `SD card\DEPLOY`).
- **Install:** add `-Install`. Only `passed` with the exact APK hash counts as device evidence.
- **Do not:**
  - use `adb kill-server` as the fix;
  - claim installs from MTP copies;
  - cite reference lessons without re-reading the row.

## Reusable prevention
1. **Reference intake checklist for any port:** read the reference's lessons *and* its operational sections (build, deployment, device, release, rollback). Map each lesson to *adopt / gate / not applicable* in the decision record.
2. **Known operational failures become executable gates.** Each gate has an exit code, an evidence file and printed remediation.
3. **The device state is a first-class status:** `device` / `unauthorized` / `offline` / `no-target`. It is checked at session start, before coding.
4. **Verify copies by content hash**, never by "copy succeeded" or size alone.
5. **Cite lessons by re-reading the source row.**

## Follow-up tasks
- **Done:** the owner authorized the tablet and enabled the timeout option; GATE-2 and GATE-3 recorded.
- **Next session:** if the preflight shows `device` without a new prompt after more than 7 days, H1 is supported and the setting is effective. If authorization still resets, reject H1 and record the new cause.
- **T29:** add the "operational sections" item to the intake checklist used for future ports.

## Residual risk
- H1 unverified (the setting reads `null` through `settings get global`; Samsung may store it differently).
- Samsung may still reset authorizations on firmware updates.
- MTP read-back depends on Windows Shell automation.

## Supersession
None. It extends the reference lessons A-LRN-016/017/043 and playbook §1/§6/§10 to this project.

## Completeness attestation
The record covers the trigger (the owner's review), every observed occurrence in this session, the reference evidence, the root cause, the corrections, the validation state and open items. Nothing is reported as installed or device-validated.
