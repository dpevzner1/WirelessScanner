# WirelessScanner 1.5.0 — Android APK

| File | `WirelessScanner-Android-1.5.0.apk` |
| --- | --- |
| Version | 1.5.0 (versionCode 10500) — same feature version as the Windows installer in [`EXE INSTALLER`](../EXE%20INSTALLER/) |
| Size | 9,088,809 bytes |
| SHA-256 | `45c75c53d55ac20832909f80160cb6d292f0a0428ba52c7060b06ed406b68ac7` |
| Signer certificate SHA-256 | `e56209fcaf2efed750571eaebb02edfb3e38e5b8f9a72d014abd366287cfe710` (sideload key; updates install over earlier builds signed with it) |
| Android | 8.0 or newer (minSdk 26); tested on a Samsung Galaxy Tab A8, Android 14 |

Check the download before installing (Windows PowerShell): `Get-FileHash .\WirelessScanner-Android-1.5.0.apk` — it must match the SHA-256 above.

Source, design and parity documents: [`AndroidApp/`](../AndroidApp/README.md).

WirelessScanner is a Wi‑Fi signal analysis, site-survey, **saturation test** and **connection test** tool. This is the Android version of WirelessScanner 1.5.0 for Windows. It uses the device's own Wi‑Fi and is installed from an APK file; it is not on the Play Store.

You need **no password, account or key** to install or use it.

## 1. Requirements
- Android **8.0 or newer**. The app is designed for tablets and was tested on a Samsung Galaxy Tab A8 (Android 14) in landscape. Portrait and phone layouts are built in but not yet tested on a device.
- **Wi‑Fi turned on.** The device does not need to be connected to a network to scan.
- **Location turned on** for the whole device (quick settings → *Location*). Android hides Wi‑Fi scan results from every app while it is off.

## 2. Install
1. Get **`WirelessScanner-Android-1.5.0.apk`** from the repository folder [`ANDROID APK`](https://github.com/dpevzner1/WirelessScanner/tree/main/ANDROID%20APK).
   - Download it on the device, or copy it over a USB cable.
   - Optionally, compare its SHA-256 with the value in that folder's README.
2. Open the APK from **My Files** (or **Files**) or from the browser's downloads.
3. If Android says installing unknown apps is not allowed:
   - choose **Settings** in the prompt and allow the app you opened the APK with (for example My Files or Chrome);
   - this is also under **Settings → Apps → Special access → Install unknown apps**.
4. **Samsung (One UI 6 and later).** If the install is refused with no option to allow it, turn off **Settings → Security and privacy → Auto Blocker**, install, then turn it back on.
5. If **Google Play Protect** warns about an unknown app, choose **More details → Install anyway**.
6. Open **WirelessScanner** from the app drawer.

## 3. First launch: permissions
| Prompt | Choose | Why |
| --- | --- | --- |
| Location | **While using the app**, with **Precise** on | Android returns no Wi‑Fi networks to apps without precise location. The app reads your GPS position only when you press **DETECT LOCATION** on a test's place fields |
| Nearby Wi‑Fi devices (Android 13+) | **Allow** | Required to scan |
| Notifications | **Allow** | Captures and tests show a notification while running, so Android does not stop them |

If you refused a permission, the app shows what is missing and a button to the right settings page.

## 4. Recommended settings
### Full-speed scanning (turn off Wi‑Fi scan throttling)
Android limits every app to **4 Wi‑Fi scans per 2 minutes**. While this limit is on:
- the app scans every 30 s instead of every 5 s;
- readings between scans are marked **STALE**;
- the header shows **THROTTLED**.

No app permission can lift the limit; only you can, on the device:
1. **Settings → About tablet** (or *About phone*) **→ Software information**, then tap **Build number** 7 times. Enter your screen lock if asked. *Developer options* is now on.
2. **Settings → Developer options**, find **Wi‑Fi scan throttling** (in the *Networking* section) and turn it **off**.
3. Return to WirelessScanner. The THROTTLED badge disappears at the next scan.

To undo: turn the switch back on, or turn Developer options off.

### Long tests
For captures or tests longer than a few minutes, go to **Settings → Apps → WirelessScanner → Battery** and choose **Unrestricted**. Otherwise Samsung and other battery savers may pause the app in the background. Keep the device on a charger for tests longer than an hour.

## 5. Using it
- **Choosing networks:**
  - **Live Survey** shows nearby networks.
  - Tap **◂ NETWORKS** to open the network list from the right; tick as many networks or access points as you need.
  - Tap **CLOSE ▸** or anywhere outside the list to return. Everything you ticked is shown and recorded.
- **Modes:**
  - **Monitor**: watch only, nothing recorded.
  - **Capture**: record a survey.
  - **Saturation test**: how busy the selected networks' channels are.
  - **Connection test**: quality of the network this device is connected to. Connect to it first in Android's Wi‑Fi settings.
- **Reports** (PDF, CSV, JSON):
  - **Share or send** sends to email or any installed app.
  - **Save a copy** saves to Google Drive, OneDrive, Dropbox, the device or any other storage provider.
- **History** keeps past captures and tests. You can reopen, export or delete them.
- **REST API** (*Settings → REST API*): off by default. When on, it answers only on this device unless you also allow access from the local network. Clients must send the API key shown there.
- **Responder role:** the tablet can act as the far end of another WirelessScanner's two-way test (*Settings → Responder role*).

## 6. Updating
- Install the new APK over the installed one; your settings and history are kept.
- If Android says **"App not installed"** or **"package conflicts with an existing package"**, the new APK was signed with a different key:
  1. export any reports you need;
  2. uninstall WirelessScanner;
  3. install the new APK.

## 7. Troubleshooting
| Problem | Fix |
| --- | --- |
| No networks listed | Turn on device Location; allow Location with **Precise** on; turn on Wi‑Fi |
| Most readings are STALE / THROTTLED | Turn off Wi‑Fi scan throttling (section 4) |
| A long test stopped | Set Battery to **Unrestricted**; keep the app's notification allowed |
| Connection test will not start | Connect the device to a Wi‑Fi network first |
| Install blocked on Samsung | Turn off **Auto Blocker** while installing (section 2) |
| Need help | *Settings → ABOUT & DIAGNOSTICS → SHARE DIAGNOSTICS* creates a text file to send. It contains the device model, app version and recent app log; read it before sharing |

## 8. Privacy
- Scan results, tests and reports stay on the device until you share or save them.
- The app sends nothing anywhere by itself. The connection test only contacts the test targets listed in its setup.
