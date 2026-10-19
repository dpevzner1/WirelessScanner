# Android port — progress log (breadcrumb trail)

This is a chronological record of the WirelessScanner Android port (DR-0015, control finding F30 / task T27). Each step lists what was attempted, what went wrong, how it was solved, and where the evidence is.

- **Lessons:**
  - [LL-2026-10-19-ANDROID-PORT](../../LessonsLearned/records/2026-10-19-android-port.md): the whole port;
  - [LL-2026-10-19-ANDROID-DEVICE-AUTH](../../LessonsLearned/records/2026-10-19-android-device-authorization.md): device authorization.
- **Order:** oldest first. Times are UTC unless marked *local* (America/Chicago).
- **Rule:** append new entries; never rewrite old ones. Superseded statements are marked, not deleted.

## Quick status
| Item | State | Evidence |
| --- | --- | --- |
| Design (UX spec, parity matrix, decision) | Done; owner reviewed the layout mock-up | DR-0015, [UX_SPEC.md](UX_SPEC.md), [PARITY_MATRIX.md](PARITY_MATRIX.md) |
| Core module (pure Kotlin port of the Windows logic) | Built; **18/18 tests**, including **7 parity suites checked against C#** | `./gradlew :core:test`; fixtures from `WirelessScanner.Tests/AndroidParityFixtures.cs` |
| Android platform layer (Wi‑Fi scanning, probes, storage, PDF, Share/Save, service, REST, responder) | **Not built yet** | — |
| Screens (Compose, tablet layout) | **Not built yet**; the preview shows one status screen | — |
| Tablet | SM-X200, Android 14 / API 34, arm64-v8a; **authorized** | `deploy-20261019T132733Z.json` |
| Latest build on the tablet | `1.5.0-preview2`, SHA-256 `6230F066…51C0DBDAC`: installed, launched, visible | `launch-20261019T132733Z.png` |

## Entries

### AP-01 — 2026-10-19T02:42Z — Owner request and decisions
- **Request:** an Android APK in `/AndroidApp`, every feature ported one for one, the tablet's own Wi‑Fi, reports sendable by email or to any installed cloud app. The two reference projects are read-only.
- **Owner decisions:**
  - native Kotlin + Compose;
  - scan throttling: guide + detect + fall back;
  - REST API and responder role on the tablet;
  - the owner sideloads.
- **Engine consultation C-10:** strong on Android architecture; **no Android Wi‑Fi API sources** (gap recorded).
- **Output:** DR-0015.

### AP-02 — 02:4xZ — Scaffold and toolchain
- **Done:** Gradle 8.14.5, AGP 8.13.2, Kotlin 2.3.21, compose-bom 2026.04.01 (all offline-cached), JDK 21; modules `core` (JVM) and `app`.
- **Issue:** `local.properties` backslashes were mangled when written from bash/python, causing a Gradle "filename syntax incorrect" error.
- **Solution:** write the file with the Write tool.
- **Result:** first debug APK built.

### AP-03 — 02:4xZ — Owner: "touch-optimised but mimic the Windows layout"
- **Engine consultation C-11:** UX operating rules found; no tablet / TalkBack / gesture concepts (partial gap).
- **Output:**
  - [UX_SPEC.md](UX_SPEC.md): window-size policy, touch mapping, state inventory, accessibility, acceptance criteria;
  - an expanded-layout mock-up.

### AP-04 — 2026-10-19 *local* — Future feature: SSID categories
- **Owner:** document GROUP BY *Category* as a future feature.
- **Output:** DR-0016 (proposed, deferred); control F31 / T28; STATUS backlog; README "Planned features"; parity matrix row F1.
- **Governance issue:** the control validation rejected T28 (fewer than 3 work packages) and F31 (empty `lesson_ids`).
- **Solution:** added WP3 and a lesson link; the control file is valid.

### AP-05 — Core port, part 1 (domain, analytics, selection, runtime)
- **Ported:** `AccessPoint`/`ChannelMath`, saturation and connection domains, `SaturationAnalyzer` (SAT-ANALYTICS-3), `ConnectionAnalyzer` (CONN-ANALYTICS-1), `NetworkSelection`, `PhysicalUnitGrouper` (plus Android Wi‑Fi 7 MLD evidence), observation builder, scan scheduler, ordered writer, session manager, saturation and connection runners, spectrum layout, responder protocol / server / client (WSR1, byte-compatible).
- **Issue:** .NET numeric semantics differ from Kotlin/Java:
  - `Math.Round(x, n)` rounds half to even on the scaled value;
  - `ToString("F1")` and `"0.##"` round half away from zero.
- **Solution:** `roundTo` uses `Math.rint(x·10ⁿ)/10ⁿ`; formatting uses `Locale.ROOT` with `HALF_UP` (`Fmt`, `ReportText.fmt`).
- **Issue:** Kotlin smart casts on nullable RSSI in the SSID aggregation did not compile.
- **Solution:** take the value once into a local variable.

### AP-06 — Parity fixtures: C# computes the reference answers
- **Done:** `AndroidParityFixtures.cs` (explicit, `Category=AndroidParity`) writes deterministic inputs and C# outputs to `core/src/test/resources/parity/`, using synthetic addresses only.
- **First Kotlin run: 4 of 6 suites passed.** Two failures:
  - **AP-06a:** the physical-unit radio `key` field was missing from the Android JSON. Cause: the JSON writer redacted *every* property named `key` (meant only for the responder secret). **Solution:** redact by class and property (`ResponderSettings.key` only).
  - **AP-06b:** in the selection fixture every step showed the final state. Cause: the C# generator stored **lazy LINQ views and live collections**, which were serialized at the end. **Solution:** materialize with `.ToList()` at each step and regenerate. *This was a fixture bug, not a port bug.*
- **Result:** 6/6 parity suites pass.
- **Issue:** a Kotlin type-inference error on `Pair<Class<*>, String>`. **Solution:** explicit type arguments.

### AP-07 — Core port, part 2 (reports, API, parsers, forms)
- **Ported:**
  - JSON writer equal to System.Text.Json (camelCase, string enums, TimeSpan text, Default encoder escaping) and a reader for storage;
  - CSV writers (UTF-8 BOM, CRLF);
  - the PDF document model;
  - REST API (X-API-Key header, same endpoints and PascalCase fields);
  - 802.11 element parsers;
  - explorer model;
  - saturation and connection forms (same validation texts);
  - DNS packet and ping parsers;
  - demo provider.
- **Deliberate difference from Windows:** CSV cells that start with `= + - @` and are not numbers get a leading apostrophe (A-LRN-034; SSIDs are chosen by strangers). Proposed for Windows as well.
- **Issue:** the storage round-trip test failed. **Null strings came back as `""`**, because the reader replaced every null with a type default.
- **Solution:** an explicit null stays null; only an absent key gets a default.
- **Result:** 18/18 core tests pass, including the IE parser parity suite (7 parity suites in total).

### AP-08 — Owner: push to the tablet; will the icon carry over?
- The icon carries over (adaptive and round launcher icons in all densities).
- `adb devices` was empty, then **`unauthorized`**.
- **Mistake:** generic advice was given. The reference projects had already solved this exact case. See AP-10.

### AP-09 — Owner: "all install files go here: SD card\DEPLOY"
- **Done:** `WirelessScanner-Android-1.5.0-preview1.apk` (SHA-256 `B14C63E2…A140820D`) copied over MTP.
- **Issue:** the copy was verified by **size only**.
- **Solution (AP-10):** read the copy back and compare SHA-256.

### AP-10 — 13:0x–13:17Z — Owner: "document this error … other projects circumvented it … fails the breadcrumb harness"
- **Findings:**
  - same tablet serial, same host ADB key (`EA8421B2…`) and the same `unauthorized` history in both reference projects;
  - their fix: on the tablet, unlock → revoke or toggle USB debugging → replug → "Always allow"; `adb kill-server` alone does not fix it;
  - their rule: authorize the tablet first thing every session, before coding.
- **Root cause:** the reference intake read design lessons only, not deployment sections; A-LRN-043 was mis-cited.
- **Solution:**
  - `tools/android_deploy.ps1`: preflight gate; hash-verified DEPLOY copy; install only on an authorized target; JSON evidence;
  - [DEPLOYMENT.md](DEPLOYMENT.md);
  - DR-0015 addendum;
  - finding F32 / task T29;
  - a memory rule.
- **Evidence:** GATE-1 (`deploy-20261019T131727Z.json`): `unauthorized` detected; copy hash matched; install **blocked, not claimed**.

### AP-11 — 13:24Z — Owner authorized the tablet
- **Preflight:** `device`, SM-X200, API 34, arm64-v8a.
- **preview1:** `install -r` succeeded; cold launch; process alive; no `FATAL EXCEPTION` (`deploy-20261019T132423Z.json`).
- **Open item:** `settings get global adb_allowed_connection_time` returns **`null`**, so the tablet does not report the "Disable adb authorization timeout" setting through this key. Hypothesis H1 (7-day revocation) is still unconfirmed; the preflight keeps reading it.

### AP-12 — 13:24Z — Installed and launched, but the screen was black
- **Issue:** the screenshot showed an empty black screen. The app was in the foreground with no crash, but the placeholder `Text` used Compose's default **black** text (no MaterialTheme) on the black window.
- **Lesson:** "installed + launched + no crash" is not "visible". Visual evidence needs a screenshot (reference A-LRN-017 device evidence; playbook L4).
- **Solution:**
  - explicit HUD colours in the preview;
  - the gate now checks that the app is in the foreground and captures a screenshot (`launch-<utc>.png`, hashed) for visual review.

### AP-13 — 13:27Z — preview2 on the tablet
- **Build:** `WirelessScanner-Android-1.5.0-preview2.apk`, SHA-256 `6230F066DAF83B49ACC8C1F240D5A4ABAD4A54FBD656771E8F54FDC51C0DBDAC`.
- **Results:**
  - DEPLOY copy: read-back hash match;
  - install: passed; foreground: yes; no fatal;
  - screenshot `launch-20261019T132733Z.png`: title and version visible on the HUD background.
- **Note:** battery at **5 %** on USB power during the run. Charge the tablet before long tests.

## Next
1. Platform layer: Wi‑Fi provider (throttle detection), connection prober (bound sockets, system ping), SQLite repositories, Keystore secrets, foreground service, PDF renderer, Share/Save, REST server, responder role, diagnostics.
2. Compose screens per UX_SPEC.
3. Emulator renders at three window sizes, then a device run through `tools/android_deploy.ps1 -Install` for each build.

### AP-14 — 2026-10-19 — Owner: "the app only shows placeholder text; implement the actual app" (paused mid-build)
- **Owner direction:** remove all placeholder text and implement the real app. Demo mode stays only as an off-by-default Settings option (Windows parity D1).
- **Session-start preflight:** `device` (`preflight-20261019T133740Z.json`).
- **Written but not yet compiled:**
  - `app/.../platform/`: `Infrastructure.kt` (log, Keystore secrets, settings, SQLite repositories), `Wifi.kt` (WifiProvider with throttle detection, WifiNetwork binder, AndroidProber), `Output.kt` (location, PDF renderer, charts, Exporter Share/Save, TestService);
  - `AppGraph.kt` and `WirelessScannerApp.kt`;
  - `ui/`: `Theme.kt`, `Explorer.kt`, `CenterViews.kt`, `Panels.kt`;
  - `res/drawable/ic_stat_wifi.xml`.
- **Removed:** the placeholder `platform/TestService.kt`; the `-preview2` version suffix.
- **Last compile:** `:app:compileDebugKotlin` passed with the platform layer and AppGraph, before any `ui/` file was added. `MainActivity.kt` still shows the placeholder text.
- **Resume from here:**
  1. `ui/History.kt` (capture / saturation / connection lists; open, export, delete).
  2. `ui/SettingsScreen.kt` (REST API, test servers, responder role, Wi-Fi scanning guide, demo mode, diagnostics).
  3. Replace `MainActivity.kt` with the permission gate, the window-size layouts (expanded / medium / compact per UX_SPEC) and the navigation rail or bar, calling `graph.setVisible` in onStart/onStop.
  4. Compile and fix.
  5. `tools/android_deploy.ps1 -ApkPath … -Install`, then review the screenshots.
  6. Update this log and LL-2026-10-19-ANDROID-PORT.

### AP-15 — 2026-10-19 — Adaptive layout (b3) and right-side network slide-over (b4)
- b3: the owner approved the merged layout. Three panes are shown only when the screen is at least 1360 × 760 dp; smaller screens get one main view, a selection bar and a NETWORKS slide-over (C-12 proposal, option B). Installed through the gate.
- Owner (during the smoke test): "if networks is a right aligned button, the SSID/AP interface should be right aligned as well, slide open dynamically, and slide closed dynamically when a selection is made or it is de-selected by tapping outside of it".
- b4 (SHA-256 `5F37EF76…D954B`, deploy-20261019T175335Z.json, read-back hash verified, install passed):
  - the list is anchored on the right, under the `◂ NETWORKS` button: 58 % of the width in landscape, 90 % in portrait;
  - it slides in from the right edge (260 ms) and slides out (220 ms), over a dimmed scrim;
  - it closes 350 ms after a selection (ticking a network, unit or AP; SELECT SHOWN; CLEAR), so the tick is seen first; expanding rows, search, filters and GROUP BY keep it open;
  - it also closes on a tap outside (the tap is consumed, so it does not also select a spectrum block), on CLOSE ▸, or with Back.
- Verified on the tablet (screenshots in local-only `android-screens/smoke-04…09`): open, tick, auto-close with the spectrum filtered to the selection, tap-outside, Back. No FATAL in logcat.
- Compile note: `AnimatedVisibility` inside a Column picks the `ColumnScope` overload; call it fully qualified (`androidx.compose.animation.AnimatedVisibility`) inside a Box.

### AP-16 — 2026-10-19 — Multi-select restored; stable row order (b5, b6)
- Owner: "now when I select any one thing, it disappears, make sure that I can still select multiple selections, the closing of the networks tab should only occur when the user clicks CLOSE> or outside of the boundary of the Network Explorer window itself".
- b5: removed the close-on-selection added in b4. Ticking, SELECT SHOWN and CLEAR now keep the list open. It closes only on CLOSE ▸, a tap outside the panel, or Back (Back kept as the Android system gesture).
- Found on the tablet while testing: every scan (about 30 s when throttled) re-sorted the rows by signal, so a row could move under the finger between two ticks.
- b6 (SHA-256 `45C75C53…B68AC7`, deploy-20261019T180550Z.json, read-back hash verified, install passed): while the slide-over is open the row order is pinned; new rows join at the end; changing the search, filters, sort or grouping re-sorts; reopening re-sorts. The three-pane layout (Windows parity) still re-sorts live.
- Verified on the tablet (local-only `smoke-13…17`): ticked two networks either side of a rescan and the list stayed open ("Selected: 2 SSIDs (3 APs now…)"). Row order held across two rescans. A tap outside closed the list. No FATAL in logcat.
- Note: the `<Hidden SSID>` group checkbox is disabled by design (Windows parity); hidden networks are picked by individual access point.

### AP-17 — 2026-10-19 — Scan throttling documented; two-way parity made a hard control; GitHub mirror includes AndroidApp
- **Scan throttling.** The owner asked whether code, permissions or install can lift Android scan throttling. A read-only probe (`governance/evidence/android/scan-throttle-probe-20261019T181519Z.txt`) answered it:
  - `wifi_scan_throttle_enabled=true`; 114 foreground requests refused;
  - the `Settings.Global` key reads null;
  - there is no shell switch.

  Conclusion: no permission or install-time route exists; the only fix is the owner's developer option.
- **Documentation:** `docs/WIFI_SCAN_THROTTLING.md` (the reference for future projects), lesson LL-2026-10-19-ANDROID-SCAN-THROTTLING, a DR-0015 addendum, finding F33 and task T30.
- **Defect found:** the app requests a scan every 5 s while throttled; it should wait 30 s (T30-WP2).
- **Two-way parity.** Owner: "any change to the windows app trickles down to the Android app ... and similarly ... this is a hard control/harness". Recorded in DR-0017, `docs/PROJECT_CONVENTIONS.md` §10, finding F34 and task T31. The DR-0017 back-fill table lists the Android UI changes from AP-15 and AP-16 that Windows still needs.
- **GitHub mirror.** `tools/sync_github_export.py` now mirrors `AndroidApp/` as source only. It excludes `build`, `.gradle`, `local.properties`, the sideload keystore, APKs and `artifacts/` (device run records). APKs ship as Release assets. The scan for real identifiers is clean: only synthetic `02:…` addresses appear.
- Parity: Windows — limit (no scan budget on Windows) for throttling; T31 back-fill pending for the AP-15/16 UI · Android — done (documentation).
