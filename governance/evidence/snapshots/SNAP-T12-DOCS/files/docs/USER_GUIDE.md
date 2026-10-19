# WirelessScanner 1.1 — User Guide

WirelessScanner is a Windows desktop tool for Wi‑Fi signal analysis. It does four main jobs:
- **live survey and capture**: see every access point, and record walk-through or fixed-spot sessions;
- **signal saturation testing**: leave the laptop in one place and measure stability over time;
- **reporting**: PDF, CSV and JSON exports;
- **a local REST API** for scripts and automation.

> Status of every feature (Validated / Observed / Designed / Blocked) is in [`STATUS.md`](STATUS.md). Engineering details are in [`TECHNICAL_REFERENCE.md`](TECHNICAL_REFERENCE.md).

---

## 1. Requirements and installation

| Item | Requirement |
| --- | --- |
| OS | Windows 10 version 2004 (build 19041) or later, or Windows 11, x64 |
| Hardware | A Wi‑Fi adapter, with the *WLAN AutoConfig* service running |
| Rights | **Administrator**. Raw Wi‑Fi telemetry needs elevation. The app shows the Windows permission (UAC) prompt and relaunches itself elevated; if you decline, it explains and exits (except in demo mode, §9) |
| Optional | Windows Location turned on (Settings → Privacy & security → Location), for automatic geolocation |

**Install from a release** (folder `EXE INSTALLER` on GitHub):
1. Download **every** file into one local folder: `WirelessScanner.Setup.exe`, `.exe.config`, `WirelessScanner.Setup.Core.dll`, `WirelessScanner.payload.json`, and all `WirelessScanner##.bin` parts.
2. Run `WirelessScanner.Setup.exe`, approve the administrator prompt, choose the folder and shortcuts, then click **Install**.
3. The setup reassembles the parts and **verifies size and SHA‑256** before installing. A corrupt or missing part stops it with a clear message.

There are no prerequisites. The installer uses .NET Framework 4.8 (built into Windows), and the application is self-contained.

**Update, repair, downgrade, modify or uninstall.** Run the setup again, or use *Settings → Apps → WirelessScanner → Modify / Uninstall*.

| Setup sees… | It offers |
| --- | --- |
| A newer package than installed | **Update** (files no longer shipped are removed; your data is kept) |
| The same version | **Repair** (all program files reinstalled) |
| An older package | **Downgrade** (with a warning and confirmation) |
| Any install | **Modify**: add or remove Desktop and Start Menu shortcuts |
| Any install | **Uninstall**: your data is **kept** unless you tick *Also permanently delete all WirelessScanner data* (a second confirmation follows) |

The installer keeps a maintenance copy of itself in `<install folder>\Setup`, so Apps & features can always modify or uninstall. Setup logs are written to `C:\ProgramData\WirelessScanner\logs\setup_*.log`.

**Build from source:** see [`TECHNICAL_REFERENCE.md` §9](TECHNICAL_REFERENCE.md#9-build-test-release).

---

## 2. Where your data lives

| Data | Location |
| --- | --- |
| Database (sessions, samples, saturation tests, settings) | `C:\ProgramData\WirelessScanner\wireless_scanner.db` |
| Application and setup logs (kept 28 days) | `C:\ProgramData\WirelessScanner\logs\` |
| Exported API key file | `C:\ProgramData\WirelessScanner\API\api_key.md` |
| Default export folder | `Documents\WirelessScanner\Reports\` |

- Version 1.0 kept its database next to the program or in the working directory. On first start, 1.1 **copies** that old data to the location above. The originals are left in place as a backup and are never overwritten.
- To relocate the data (e.g. for portable use from a USB drive), set the environment variable `WIRELESSSCANNER_DATA_DIR` to a folder.

**Privacy.** Exports and the database contain network names (SSIDs), access-point MAC addresses (BSSIDs) and, if you enter it, geolocation. These identify places, so share reports deliberately.

---

## 3. Main window

The window has four tabs, plus a status bar at the bottom. The status bar shows:
- the current activity;
- the **last scan cycle**, e.g. `Last scan 14:02:11: FRESH in 1093 ms, 32 APs`;
- the database size.

### Scan cycles: fresh, stale, failed
Every scan asks Windows for a **fresh** scan and waits for Windows to confirm it finished.

| Status | Meaning | What is recorded |
| --- | --- | --- |
| **FRESH** | Windows confirmed a new scan | Every network heard recently |
| **STALE** | No confirmation arrived within 6 s (Windows often throttles scans while you are connected) | Only entries whose beacon is demonstrably recent; cached repeats are never stored as new samples |
| **FAILED** | The adapter is unavailable (unplugged, disabled, service stopped) | Nothing. The reason is shown, e.g. *"The adapter was not found (ERROR_NOT_FOUND)… re-select the adapter."* |

The live tab scans every **5 seconds**. Windows needs about 2–4 s per scan, so scanning faster would only repeat cached data.

---

## 4. LIVE SURVEY tab

**Hardware selection.** Choose your Wi‑Fi adapter. The panel shows its class, PnP ID, MAC and IP address. Plugging or unplugging a USB adapter refreshes the list automatically. **FORCE SCAN TICK** runs one scan immediately.

**SSID / AP view switch.** List networks grouped by **SSID** (network name), or by individual **AP** (BSSID). Tick entries in the list to **filter**: the graphs, the live log and the capture all follow the ticked items. With nothing ticked, everything is included.

**Spectrum graph / telemetry grid switch.**
- *Spectrum graph*: 2.4 GHz and 5 GHz channel maps. Each AP is drawn as a curve at its channel, width and signal strength, with translucent fills so overlapping APs stay visible.
- *Telemetry grid*: the columns are SSID, BSSID, band, channel, RSSI (dBm), **SNR est.** (dB), quality %, security and vendor.

**Signal tiers** are used everywhere in the app:

| Tier | RSSI |
| --- | --- |
| Excellent | ≥ −55 dBm |
| Good | −67 to −55 dBm |
| Fair | −70 to −67 dBm |
| Weak | −80 to −70 dBm |
| Unusable | < −80 dBm |

**Field definitions:**
- **RSSI**: received signal strength reported by the driver (dBm).
- **SNR est.**: RSSI minus an *assumed* noise floor of −95 dBm. Windows does not report the noise floor, so this is an estimate.
- **Quality**: the driver's link-quality percentage.
- **Security**: derived from the beacon's RSN/WPA elements: Open, OWE (Enhanced Open), WEP, WPA-Personal/Enterprise, WPA2-Personal/Enterprise, WPA2/WPA3-Personal (transition), WPA3-Personal, WPA3-Enterprise-192.
- **Vendor**: from a built-in table of common access-point MAC prefixes; *Unknown Vendor* otherwise.
- **Channel width**: from the beacon's HT/VHT/HE operation elements. Blank when the AP does not advertise it.

**Capture sessions** (the left panel):
1. Click **● BEGIN SESSION CAPTURE**. Enter a session name, facility, scope, survey point (zone) and notes, and choose a mode:
   - *Roaming survey*: walk the site and change the zone as you go;
   - *Stationary capture*: one fixed spot. For full analysis use the **SATURATION TEST** tab instead (§5).
2. While **● RECORDING**, every fresh scan stores one sample per AP in scope. The **live captured telemetry log** shows each sample with its fluctuation (▲/▼ dB versus the previous scan) and **VAR (dB)**, the scan-to-scan RSSI variation. This is a dB value, not network latency.
3. **⏸ PAUSE** suspends recording; **■ STOP** ends the session and offers to export it straight away.

Session data is written through one ordered queue, so the session record is always saved before its samples. Version 1.0 could lose samples at session start; 1.1 cannot.

---

## 5. SATURATION TEST tab

A **signal saturation test** leaves the laptop in one test area for a set time. It measures how stable the selected networks are: peaks, drops, dropouts, channel changes, scan-to-scan variation, trend, exposure below signal thresholds, and the surrounding channel congestion. Full definitions are in [`SATURATION_TEST_SPEC.md`](SATURATION_TEST_SPEC.md).

![Setup](images/ui_1_setup_ready.png)

### Step 1: Setup
**1 · What to test**
1. Choose the adapter and press **DISCOVER**. This runs two fresh scans and lists every network with its band, channel, signal (with tier), security and vendor. Use the search box to filter by SSID, BSSID or vendor; **Select shown** and **Clear** tick or untick the visible rows.
2. Choose a **scope**:

| Scope | Select | What is analysed |
| --- | --- | --- |
| Single AP | exactly one row | that BSSID |
| Multiple APs | two or more rows | each BSSID |
| Single SSID | any row of one SSID | every BSSID of that SSID (new ones are added as they appear), plus SSID-level "strongest AP" tracking |
| Multiple SSIDs | rows from two or more SSIDs | the same, per SSID |
| All visible | nothing | every BSSID heard (environment survey) |

Hidden networks can only be tested by BSSID.

**2 · How long**
- Duration: 5 min, 15 min, 30 min, 1 h, 4 h, 8 h, 24 h, custom (1–1440 minutes), or *until stopped*.
- Scan interval: 5 (default), 10, 15, 30 or 60 s. Paused time is not counted towards the duration.

**3 · Where**
- **Test area / location**: required, and printed on the report.
- **Geolocation**: latitude and longitude in decimal degrees (WGS84), with optional accuracy in metres. Press **DETECT** to use the Windows Location service; if it is off or denied, the reason is shown and you can type the coordinates instead.
- **Building / floor / zone**: for indoor comparisons.
- **Test name, facility, operator, client, notes.**
- **Advanced thresholds** (defaults in brackets):

| Setting | Default | Valid range |
| --- | --- | --- |
| Drop threshold | 8 dB | 3–30 |
| Peak threshold | 8 dB | 3–30 |
| Dropout (consecutive missed scans) | 2 | 1–20 |
| Baseline window (samples) | 12 | 3–120 |

- **Keep this PC awake** (on by default): prevents sleep while the test runs. You are warned when running on battery.

The bottom of the panel always shows a summary such as *"Test 1 SSID (all BSSIDs) every 5 s for 00:15:00 (≈180 cycles)"*, the first problem that blocks starting (in red), and **▶ START SATURATION TEST**, which is enabled only when everything is valid.

### Step 2: Running
![Running](images/ui_2_running.png)

- **Status bar:** a progress bar, elapsed and remaining time, cycle counts (fresh / stale / failed), and warnings (adapter problem, scan throttling, on battery). The progress bar is indeterminate for *until stopped*.
- **One tile per BSSID:** current RSSI in large type, with a **text label** (EXCELLENT … UNUSABLE, or NOT SEEN, STALE, SCAN FAILED); min, average and max; σ; availability; event counts; grade so far.
- **Live timeline:** every target, with shaded threshold bands, red ticks where a target was not seen, and dashed marker lines.
- **Add marker:** type what is happening (e.g. "microwave on", "door closed", "forklift charging") and press **Add**. Markers are timestamped and appear on charts and in reports.
- **Event feed:** drops, peaks, dropouts, channel changes and strongest-AP changes as they are detected.
- **❚❚ PAUSE / ▶ RESUME** suspends scanning; paused time does not count. **■ STOP** asks for confirmation, then analyses and saves what was collected.

### Step 3: Results
![Results](images/ui_3_results.png)

- **One row per BSSID:** grade, score, SSID, channel(s), availability %, mean, tier, σ, min/max, drops, dropouts, channel changes, % of time below −80 dBm, and the first finding.
- **Selecting a row** shows its timeline; the right-hand table lists every detected event.
- **Exports:** **PDF REPORT**, **CSV BUNDLE** and **JSON** (§7). **NEW TEST** returns to setup.

**Past tests** (bottom of the setup screen) lists every saved test with its date, name, location, geolocation, scope, cycles and status.
- **Open results** re-analyses a test from its stored scans. Reports are always re-derivable.
- **Delete…** removes a test after confirmation.

### Reading the results

| Metric | Meaning |
| --- | --- |
| **Availability %** | Observed ÷ (observed + missing), over fresh scans only |
| **Drop / peak** | RSSI falls (rises) at least *D* dB from the rolling median of recent samples. It ends once back within *D*/2. Recorded with depth, duration and recovery time |
| **Dropout** | Not seen in at least *N* consecutive fresh scans. A single miss is normal, because Windows drops entries between scans |
| **Channel change** | The AP moved channel or band (auto-channel, DFS radar event, etc.) |
| **Strongest-AP change** (SSID scope) | The strongest BSSID of the SSID changed by at least 3 dB, or the previous one vanished: a roaming or steering candidate |
| **RSSI variation** | Average and 95th-percentile scan-to-scan change, in dB |
| **Exposure** | % of observed time (and longest stretch) below −67, −70 and −80 dBm |
| **Trend** | dB per hour. Only stated in the findings for tests of at least 15 minutes |
| **Co-channel** | Other APs on the same channel; on 2.4 GHz, also on overlapping channels (±4) |
| **Stability score / grade** | `100 − 4·σ − 0.5·(100 − availability) − 3·drops/h − 5·dropouts/h − 5·channel changes/h` (A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, F). This is a heuristic for **stability, not strength**. A steady −70 dBm AP can score A while its tier is *Fair*, so read the grade together with the tier and exposure |

**Measurement limits.** RSSI is what this adapter and driver report; it is not calibrated. Compare results from the same adapter. Throughput, latency and packet loss are not measured.

---

## 6. HISTORY / LOGS tab

- **Saved capture sessions:** filter by name (case-insensitive), **from / to date**, or **✕ CLEAR FILTERS**; **REFRESH LIST**.
- Select a session to see its **historical telemetry samples** and a **timeline graph**. Lines are coloured by average signal tier, and you can tick or untick APs in the legend.
- **Display fields:** choose which columns (Time, SSID, BSSID, Channel, Band, RSSI, SNR, Quality, RSSI variation, Zone) appear on screen and in exports.
- **EXPORT CSV / JSON / PDF:** export the selected session, or several sessions at once with Ctrl/Shift-click (CSV and JSON). **DELETE SESSION** permanently removes a session and its samples.

---

## 7. Exports

| Export | Contents |
| --- | --- |
| **Session CSV** | Metadata header plus one row per sample. Columns include `RSSI_Variation_dB` and `SNR_Estimated_dB` (renamed in 1.1 from `Jitter` and `SNR`) |
| **Session JSON** | Session, summary, samples (keys `RssiVariationDb`, `SnrEstimatedDb`) |
| **Session PDF** | Client and surveyor details, executive summary, timeline of the top 5 APs, per-AP statistics, full sample log |
| **Saturation PDF** | Test details (with geolocation and map link) → executive summary with grades and plain-language findings → methodology and measurement limits → scan health → per-target pages (statistics, timeline with threshold bands, missing ticks, drop spans and markers, RSSI histogram, events) → SSID aggregates → markers → appendix: per-cycle observation log (complete up to 20,000 rows; the CSV is always complete) |
| **Saturation CSV bundle** | `_samples.csv` (one row per scan per target, **including missing, stale and unknown rows**, with geolocation columns), `_events.csv`, `_summary.csv`, `_cycles.csv`, `_markers.csv` |
| **Saturation JSON** | `schema: wirelessscanner.saturation_report.v1`: test configuration, `geo`, health, targets, SSIDs, events, markers, every cycle |

All exports are written atomically: the file is written to a temporary name, then moved into place. A file that is open in another program gives a clear "close it and try again" message.

---

## 8. REST API CONFIG tab

A small HTTP server on **`http://localhost:<port>/`** for scripts, dashboards and automation on this PC.

1. Press **GENERATE** to create a key, choose a **port** (1024–65535, default 5005), tick **Enable REST API Server**, then **APPLY & SAVE CHANGES**.
2. **EXPORT** writes the key to `C:\ProgramData\WirelessScanner\API\api_key.md`; **DELETE** clears the key.
3. If the port is taken, the tab shows *"Port N is already in use… choose another port"*.

| Endpoint | Returns |
| --- | --- |
| `GET /api/status` | Health, version, port |
| `GET /api/live` | Access points from the latest scan |
| `GET /api/sessions` | All capture sessions |
| `GET /api/sessions/{id}` | One session with all its samples |
| `GET /api/stats` | Session and sample counts, database size |

**Authentication** is the `X-API-Key` header only. Keys in the URL are rejected, because URLs leak into logs and history. Browsers on other origins are not allowed (no CORS) unless an origin is explicitly configured.

```powershell
Invoke-RestMethod -Uri http://localhost:5005/api/live -Headers @{ "X-API-Key" = "<your key>" }
```

> **Security note (1.0 users):** release 1.0's README published a working example key. If you used 1.0, press **GENERATE** to create a new key and re-apply. The old key stays valid until you replace it.

---

## 9. Demo mode

To run with simulated networks (training, screenshots, no Wi‑Fi adapter or administrator rights needed):
```powershell
$env:WIRELESSSCANNER_DEMO = "1"; & "C:\Program Files\WirelessScanner\WirelessScanner.Presentation.exe"
```
- The title bar shows **[DEMO MODE - simulated data]**.
- Demo data is kept separately in `%LOCALAPPDATA%\WirelessScanner\demo`.
- Never use demo mode for real surveys.

---

## 10. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Windows asks for permission at start | Expected: WirelessScanner relaunches itself elevated. Choose **Yes** |
| "Administrator Privilege Required" message | The permission prompt was declined. Start again and choose **Yes**, or right-click → *Run as administrator* |
| `Scan FAILED: … ERROR_NOT_FOUND (1168)` | The adapter was unplugged, disabled or re-enumerated. Re-select it |
| `… WLAN AutoConfig service is not running (1062)` | Start the *WLAN AutoConfig* service |
| `… adapter is not ready (21)` | Wi‑Fi is switched off or airplane mode is on |
| Many **STALE** cycles | Windows throttles scans while connected. Results stay valid (stale data is excluded); use a longer interval or disconnect |
| A target shows **NOT SEEN** once | Normal. Windows drops entries between scans; a *dropout* needs ≥ 2 consecutive misses |
| DETECT says location access is denied | Settings → Privacy & security → Location → allow desktop apps, or type the coordinates |
| REST API "port already in use" | Choose another port and apply |
| Setup: "Installation files are missing" or "integrity check failed" | Download every file of the release into one folder again |
