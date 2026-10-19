# Android deployment and device gate

This document covers the Android port (DR-0015). The lesson behind it is [LL-2026-10-19-ANDROID-DEVICE-AUTH](../../LessonsLearned/records/2026-10-19-android-device-authorization.md).

**Rule:**
- A build is **deployed** only when its copy in the install folder matches the APK's SHA-256.
- A build is **installed / device-validated** only when `adb` reports the tablet as `device` and the exact APK hash has been installed and launched.
- A USB connection, a File Explorer copy or an `unauthorized` target is never evidence of either.

## Target
| Item | Value |
| --- | --- |
| Tablet | Samsung Galaxy Tab A8, SM-X200, Android 14 / API 34; shows as **Galaxy8NOMAD** in File Explorer |
| ADB serial | known from the reference projects; masked in this repository's outputs |
| Install folder (owner) | `This PC\Galaxy8NOMAD\SD card\DEPLOY` |
| Host ADB key | `%USERPROFILE%\.android\adbkey.pub`, SHA-256 `EA8421B2…49D2CAE` (the same key the reference projects used) |
| Signing | project sideload key `AndroidApp/artifacts/signing/sideload.keystore`; every build installs over the previous one |

## Session-start gate (do this before writing code)
The reference projects lost whole iterations because the tablet showed `unauthorized`. Their instruction: *authorize the tablet first thing every session* (reference playbook §10).

```bash
powershell -ExecutionPolicy Bypass -File tools/android_deploy.ps1 -PreflightOnly
```

The script returns one of these results:

| Result | Meaning | Action |
| --- | --- | --- |
| `device` (exit 0) | Authorized | Install and validate allowed |
| `unauthorized` (exit 2) | This computer's key is not accepted by the tablet | See the fix below; ask the owner, who must act on the tablet |
| `offline` / `no-target` | Cable, USB mode or driver | Reconnect: data cable, USB mode *File transfer*, tablet unlocked |

### Fix for `unauthorized`
This sequence is the one that worked in the reference projects (reference R3 §5). The steps are done on the **tablet**, not the PC:
1. Unlock the screen and keep it unlocked.
2. Developer options → **Revoke USB debugging authorizations**, or switch USB debugging off and on again.
3. Unplug and replug the cable.
4. Accept **"Allow USB debugging?"** on the tablet, with **Always allow from this computer** ticked.
5. Once it shows `device`, switch on Developer options → **Disable adb authorization timeout**. This prevents hypothesis H1: Android 11+ drops an authorization that has not been used for 7 days.

`adb kill-server` alone does not fix this. The authorization also reset between sessions in the reference projects, so run the preflight every session.

## Deploy and install
```bash
powershell -ExecutionPolicy Bypass -File tools/android_deploy.ps1 -ApkPath AndroidApp/artifacts/deploy/<apk>
```

```bash
powershell -ExecutionPolicy Bypass -File tools/android_deploy.ps1 -ApkPath AndroidApp/artifacts/deploy/<apk> -Install
```

**What the script does:**
1. Copies the APK to `SD card\DEPLOY` over MTP.
2. **Reads the copy back and compares SHA-256.** An MTP "copy succeeded" message is not proof (reference playbook §6.4).
3. With `-Install`, and only on an authorized target, it runs `adb -s <serial> install -r`, a cold launch, and a check for `FATAL EXCEPTION`.

Each run writes `AndroidApp/artifacts/deploy/deploy-<utc>.json`, which binds the result to the APK's SHA-256 (A-LRN-017).

**Evidence states:**
- `deployed (hash verified)`;
- `installed + launched (exact hash)`;
- `BLOCKED BY DEVICE AUTHORIZATION`.

A blocked result is never reported as passed.

## Signing
- **Why a fixed key.** Release and debug builds are signed with a project **sideload key**, so each new APK installs over the previous one: Android only accepts an update signed with the same key. It is not a Play Store key.
- **Signer certificate SHA-256:** `e56209fc…5cfe710`.
- **Secrets stay out of the repository** (owner decision, 2026-10-19):
  - the keystore is `AndroidApp/artifacts/signing/sideload.keystore`;
  - its passwords are kept only in `AndroidApp/local.properties`;
  - both are excluded from the GitHub mirror.

  `local.properties` keys: `sideload.storeFile`, `sideload.keyAlias`, `sideload.storePassword`, `sideload.keyPassword`. CI can set the environment variables `WS_SIDELOAD_STORE_FILE`, `WS_SIDELOAD_KEY_ALIAS`, `WS_SIDELOAD_STORE_PASSWORD` and `WS_SIDELOAD_KEY_PASSWORD` instead.
- **Without the key:** a fresh clone still builds. Debug builds use Android's debug key and release builds are unsigned; `tools/build_android_release.py` refuses to publish an unsigned APK.
- **Users never need these values.** Installing and updating need only the APK ([INSTALL_GUIDE.md](INSTALL_GUIDE.md)).
- **If the key is lost or replaced,** existing installs cannot be updated in place. Users must export reports, uninstall, then install the new APK. Say so in the release notes.

## When an install fails on the tablet
This order comes from the reference playbook §6:
1. **"Problem parsing the package"**: run `adb install -r` with the PC's APK. If that works, the problem is the copy or the storage: compare size and SHA-256, check the `PK` header, check the mount state, check `minSdk` (26).
2. **`INSTALL_FAILED_*`**: record the exact code before changing anything.
3. **App closes immediately**: run `adb logcat -d` and search for `FATAL EXCEPTION` first.
