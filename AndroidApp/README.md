# WirelessScanner for Android

This is the Android tablet port of WirelessScanner 1.5.0 (decision [DR-0015](../docs/decisions/DR-0015-android-port.md)). It is a native Kotlin + Jetpack Compose app that uses the tablet's own Wi‑Fi radio.

**Status (2026-10-19): 1.5.0 release APK.**
- Every Windows 1.5.0 feature is ported. The shared logic is checked against the Windows app (18 core tests, 7 parity suites).
- The APK is installed and smoke-tested on the owner's tablet (Samsung Galaxy Tab A8, Android 14).
- **Get it:** [`ANDROID APK`](../ANDROID%20APK/) in the repository. **Install and setup:** [docs/INSTALL_GUIDE.md](docs/INSTALL_GUIDE.md).
- **Known platform limit:** Android Wi‑Fi scan throttling; see [docs/WIFI_SCAN_THROTTLING.md](docs/WIFI_SCAN_THROTTLING.md).
- **Two-way parity:** every change is carried between the Windows and Android apps ([DR-0017](../docs/decisions/DR-0017-two-way-platform-parity.md)).

| Document | For |
| --- | --- |
| [docs/INSTALL_GUIDE.md](docs/INSTALL_GUIDE.md) | **For users:** install, permissions, recommended settings, updating, troubleshooting |
| [docs/WIFI_SCAN_THROTTLING.md](docs/WIFI_SCAN_THROTTLING.md) | Android scan limit: what lifts it and what does not; checklist for future projects |
| [docs/PROGRESS_LOG.md](docs/PROGRESS_LOG.md) | **Breadcrumb trail:** every step, issue and solution, oldest first |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Device gate (authorize first), deploy to `SD card\DEPLOY`, install, troubleshooting |
| [docs/UX_SPEC.md](docs/UX_SPEC.md) | Tablet UX: layouts by window size, touch mapping, states, accessibility |
| [docs/PARITY_MATRIX.md](docs/PARITY_MATRIX.md) | Every Windows feature, its Android form, and its proof |
| [Lesson: Android port](../LessonsLearned/records/2026-10-19-android-port.md) | Issues and prevention rules for this port |
| [Lesson: device authorization](../LessonsLearned/records/2026-10-19-android-device-authorization.md) | The repeated ADB `unauthorized` failure and its gate |
| [Lesson: scan throttling](../LessonsLearned/records/2026-10-19-android-wifi-scan-throttling.md) | Measured limit, rejected routes, follow-ups |

## Build and test
Requirements: JDK 21 and the Android SDK (platform 36). The build runs offline from the Gradle cache.

**Core tests:** 18 tests, including the parity suites.

```bash
./gradlew :core:test --offline
```

**Debug APK.** It is signed with the sideload key when `local.properties` provides it; otherwise Android's debug key is used.

```bash
./gradlew :app:assembleDebug --offline
```

**Release APK and the repository's `ANDROID APK` folder.** This builds, runs the core tests, verifies the signature and writes the README with SHA-256 (run from the repository root):

```bash
python tools/build_android_release.py
```

**Regenerate the parity fixtures** after any change to the Windows analytics (run from the repository root):

```bash
dotnet test WirelessScanner.Tests --filter "Category=AndroidParity"
```

## Device
The app targets the Samsung Galaxy Tab A8 (SM-X200, Android 14). Run the preflight at the start of every session, before coding:

```bash
powershell -ExecutionPolicy Bypass -File ../tools/android_deploy.ps1 -PreflightOnly
```
