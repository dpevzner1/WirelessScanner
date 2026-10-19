# Android Wi‑Fi scan throttling: reference for this and future projects

| Field | Value |
| --- | --- |
| Status | Reference (2026-10-19). Measured on the owner's tablet; platform rules from Android documentation and AOSP behaviour |
| Applies to | Any Android app that calls `WifiManager.startScan()`: site surveys, signal meters, saturation or roaming tests, indoor positioning |
| Lesson | [LL-2026-10-19-ANDROID-SCAN-THROTTLING](../../LessonsLearned/records/2026-10-19-android-wifi-scan-throttling.md) |
| Decision | [DR-0015](../../docs/decisions/DR-0015-android-port.md) ("detect, guide and fall back"), addendum 2026-10-19 |
| Evidence | `governance/evidence/android/scan-throttle-probe-20261019T181519Z.txt` (read-only probe; nothing changed on the device) |

## 1. The limit in one paragraph
Since Android 9, the system limits how often an **app** may start a Wi‑Fi scan. A **foreground** app gets **4 scans in any 2‑minute window**. **All background apps together** get **1 scan every 30 minutes**. When the limit is reached, `startScan()` returns `false` and **no scan happens**. `getScanResults()` then returns the **last cached results**, which can be up to two minutes old. Scans started by the system itself (connectivity, Wi‑Fi settings screen, location services) are **not** limited, and their results land in the same cache. On Android 10 and later, the owner can turn the limit off on the device in **Developer options → Networking → Wi‑Fi scan throttling**. Nothing an app can declare or be granted at install time lifts it.

## 2. What we measured (Samsung Galaxy Tab A8 SM-X200, Android 14 / API 34, build X200XXS6DYG6)
| Probe (read-only) | Result | Meaning |
| --- | --- | --- |
| `dumpsys wifi` | `wifi_scan_throttle_enabled=true` | Throttling is on (factory default) |
| `dumpsys wifi` | `numExternalForegroundAppOneshotScanRequestsThrottled=114` (rising) | The system refused 114 of our foreground scan requests in one afternoon |
| `logcat` (`WifiScanRequestProxy`) | `Scan request from com.wirelessscanner.android throttled` | The system's own message for each refused request |
| `settings get global wifi_scan_throttle_enabled` | `null` | The setting is **not** kept in `Settings.Global` on Android 14. It is kept in the Wi‑Fi service's own settings store, so writing `Settings.Global` would do nothing |
| `cmd wifi help` | no throttle command | The Wi‑Fi shell command has no switch for it on this build |
| App behaviour | live view shows `STALE (THROTTLED)` between allowed scans, roughly 3 of every 4 cycles at a 30 s cadence | Matches the 4-per-2-minutes rule |

## 3. What does and does not lift the limit
| Route | Lifts it? | Why |
| --- | --- | --- |
| Normal permissions: `ACCESS_FINE_LOCATION`, `CHANGE_WIFI_STATE`, `ACCESS_WIFI_STATE`, `NEARBY_WIFI_DEVICES` (API 33+) | **No** | They are needed to scan **at all**; the app is still limited |
| Being in the foreground, a foreground service, a wake lock | **No** | They only move the app from the background budget (1 per 30 min) to the foreground budget (4 per 2 min). **Still worth doing** |
| Granting a permission at install or by `adb shell pm grant` | **No** | The exempt permissions (`NETWORK_SETTINGS`, `NETWORK_SETUP_WIZARD`, `NETWORK_STACK`, `NETWORK_MANAGED_PROVISIONING`) are signature or privileged permissions. They are only granted to apps signed with the platform key or pre-installed as privileged system apps. `pm grant` refuses them for a sideloaded APK |
| `WRITE_SECURE_SETTINGS` (grantable by adb) + writing `Settings.Global.wifi_scan_throttle_enabled` | **No** on Android 11+ | The value moved into the Wi‑Fi service's own store (measured: the global key reads `null`). Also a security-sensitive grant; not used |
| `WifiManager.setScanThrottleEnabled()` | **No** | `@SystemApi`, needs `NETWORK_SETTINGS` |
| Device owner / Android Enterprise | **No** | Not one of the exempt roles |
| Root, a custom ROM, or platform-signed builds | Yes | Out of scope: it voids the device setup and is not a product route |
| **Developer options → Wi‑Fi scan throttling OFF** | **Yes** | The supported switch (Android 10+). One change by the owner, kept across restarts. Developer options must be enabled first (tap *Build number* 7 times) |

**Rule for future projects:** do not search for a permission or manifest flag to fix this; there is none. Plan the product for the throttled case and guide the user to the switch.

## 4. What an app can do (design pattern used here: detect, guide, fall back)
1. **Detect.**
   - On API 30+, read `WifiManager.isScanThrottleEnabled()`; it is readable without special permission.
   - Treat `startScan() == false` as a refused scan.
   - Use `EXTRA_RESULTS_UPDATED` on `SCAN_RESULTS_AVAILABLE_ACTION`: true means fresh results, false means cached.
2. **Never show cached results as fresh.** Label the cycle STALE with the reason, and show the age of each access point.
   - Each access point's age is `SystemClock.elapsedRealtime()*1000 - ScanResult.timestamp`, in µs since boot.
   - A per-AP age is more honest than one label for the whole cycle.
3. **Pace requests to the budget.** While throttling is on, request one scan every 30 s; that is 4 per 2 minutes, spread evenly. Refused requests don't use up the budget, but they waste work, clutter the log and make the status misleading.
4. **Use free results.** Results from scans the system starts itself arrive through the same broadcast. Process **every** `SCAN_RESULTS_AVAILABLE_ACTION`, not only the ones our app requested.
5. **Use the connected link.** `WifiManager.getConnectionInfo()` (or `NetworkCapabilities.transportInfo` on API 31+) gives RSSI, link speed and frequency for the **connected** network every few seconds without a scan. Tests aimed at that network (saturation, connection quality) can sample it at full rate even while throttled.
6. **Guide.** Show a THROTTLED badge and a guide screen with the exact path to the developer option. Detect the change automatically when the user returns to the app.
7. **State the effective rate.** Every report produced while throttled must print the effective scan interval and how many scans were refused, so a reader never takes a 30 s cadence for the 5 s Windows cadence.
8. **Don't change system settings for the user.** The app (and the assistant) may only *read* the setting. Turning it off is the device owner's decision.

## 5. Status in WirelessScanner Android (1.5.0 b6)
| Item | State |
| --- | --- |
| Detect (`isScanThrottleEnabled`, refused `startScan`, `EXTRA_RESULTS_UPDATED`) | Done |
| STALE (THROTTLED) label, THROTTLED badge, banner, SETTINGS → WI‑FI SCANNING guide | Done |
| Saturation warnings and diagnostics include the throttle state and the refused-scan count | Done |
| Pace requests to 30 s while throttled | **Defect.** Code chooses 30 s, but the tablet log shows a request every 5 s (`logcat`, 13:13:05 to 13:14:10 local). Follow-up in the lesson record |
| Per‑AP age from `ScanResult.timestamp` | Planned |
| Process system-initiated scan results | Planned (verify the receiver does not discard broadcasts the app did not request) |
| Connected-link sampling for tests on the connected network | Planned |
| Throttle banner on one line with ⓘ (it currently takes two lines and costs list rows) | Planned |

## 6. Checklist for the next Android Wi‑Fi project
- [ ] Read this file and the lesson record before designing scan cadence or test timing.
- [ ] Decide the throttled-mode cadence and the report wording at design time, not after the first device run.
- [ ] Put a read-only throttle probe in the device preflight: `dumpsys wifi | grep -i throttl` and the app's own `isScanThrottleEnabled()`.
- [ ] Device acceptance tests run twice: throttling **on** (default) and **off** (owner sets the developer option).
- [ ] Check `logcat` for `WifiScanRequestProxy … throttled` to confirm the request cadence matches the design.
- [ ] Never promise "real-time" scanning on Android without stating the developer-option requirement.
