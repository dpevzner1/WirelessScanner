# WirelessScanner 1.3 — User Guide

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

Setup finds an existing installation from its own registry key **and** from the Apps & features entry (64- and 32-bit views), and checks them against the installed files. If they disagree, the maintenance page lists the differences. If program files are missing, it offers **Repair**.

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

The window has three tabs (LIVE SURVEY, REST API CONFIG, HISTORY / LOGS), plus a status bar at the bottom. The status bar shows:
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

The live tab scans every **5 seconds**. Windows needs about 2–4 s per scan, so scanning faster would only repeat cached data. One scan scheduler serves the live view, capture and the saturation test: a scan that is already running is shared, never repeated.

---

## 4. LIVE SURVEY tab

![Live Survey](images/ui_ls_monitor_selected.png)

Live Survey is one screen with three parts:
- the **network list** on the left;
- the **view** in the centre: spectrum, waterfall, grid or test;
- a **mode panel** on the right, which opens for capture or saturation testing.

The **mode bar** at the top switches between:

| Mode | What it does |
| --- | --- |
| **◉ MONITOR** | Watch the spectrum. Nothing is recorded |
| **● CAPTURE** | Record the selected networks to a capture session (§4.4) |
| **◎ SATURATION TEST** | Leave the laptop in place and measure stability over time (§5) |

Next to the modes, a status line says what is happening (*Monitoring · nothing recorded*, *● RECORDING*, *● TESTING · selection locked*), and the time of the last scan is shown on the right. Capture and a saturation test can run **at the same time**. Both share each scan, so neither slows the other down.

### 4.1 Adapter
Choose your Wi‑Fi adapter at the top of the network list. **Adapter details** expands to show its class, link status, PnP ID, MAC and IP address. Plugging or unplugging a USB adapter refreshes the list automatically. Scanning starts as soon as an adapter is chosen.

### 4.2 Network list (choose what you see, capture and test)
Networks are grouped by **SSID** (network name). Click **▸** to show the access points (BSSIDs) inside a network.

| Element | Meaning |
| --- | --- |
| Box on a network row | **✓** = the whole network is selected, *including access points that appear later* · **–** = only some of its access points are selected · empty = not selected |
| Box on an access-point row | Select exactly that radio (BSSID). Unticking one access point of a whole network turns the rest into a fixed list |
| Colour bar | The network's colour on the spectrum (§4.3) |
| Second line | Number of APs · security · bands · vendor |
| Bars + dBm + tier | Best signal of the network, with its tier written out (EXCELLENT … UNUSABLE) |
| AP row | BSSID; channel · band · width; a small line of the **last 12 scans**; signal. Hover for quality, SNR est. and security |

- **Search** filters by SSID, BSSID or vendor. **Sort** orders by signal, name or channel.
- **Chips** narrow the list: **2.4 GHz**, **5 GHz**, **6 GHz**, **≥ −70 dBm**, **Secured** (hides open networks) and **Selected** (only what you ticked).
- **Hidden networks** (no SSID) can only be selected per access point; their network box is disabled.
- The **SELECTION** box says in words what is selected, e.g. *"1 SSID (4 APs now, follows new APs) + 1 AP (fixed) · mixed"*. **With nothing selected, everything is shown and captured.**
- **Select shown** selects every network currently listed (hidden ones per access point); **Clear** empties the selection.
- **Capture session…** and **Saturation test…** open the matching mode. **FORCE SCAN** runs one scan now.
- While a saturation test runs, the selection is **LOCKED** so the test scope cannot change.

The selection filters the spectrum, the grid, the live capture log and what a capture session records. It also defines the saturation test's targets.

### 4.3 Views: SPECTRUM, WATERFALL, GRID, TEST

**SPECTRUM.** Each access point is drawn as a flat-topped block that covers the frequencies it actually uses. A 20 MHz AP is narrow. An 80 or 160 MHz AP spans its whole bonded channel block. The top of the block is the signal in dBm.
- **Colour = network identity.** With nothing selected, the **3 strongest networks** are coloured automatically. Otherwise, the **first 3 networks you selected** get the colours blue, orange and green, in the order you ticked them. A network keeps its colour while you add others.
  - A dashed grey outline is another network.
  - A solid grey outline is a selected network beyond the first three.
  - Only 3 colours are used because overlapping shapes stay distinguishable (also for colour-blind users) only up to three.
- **Bands:** **All** stacks 2.4 GHz and 5 GHz (and 6 GHz when anything is heard there). **2.4 GHz**, **5 GHz** or **6 GHz** shows one band at full height.
- **Reference lines** at −67 (good), −70 (fair) and −80 dBm (weak).
- **DFS** channels (5 GHz, 5250–5730 MHz) are shaded. APs there may be forced to change channel by radar detection.
- The **channel load strip** under each band is darker where more and stronger APs overlap a channel. The **least busy** channel is outlined and named in the band header. It is chosen among 1/6/11 on 2.4 GHz, non-DFS channels first on 5 GHz, and preferred scanning channels (PSC) on 6 GHz.
- **Labels:** coloured networks are labelled with name and dBm. **Label all** labels every AP; labels that would overlap are moved up or left out.
- **Click** a shape to add or remove that access point from the selection. Hover for its details.

**WATERFALL.** Each row is one scan, newest at the top, for the last **60 fresh scans**. Each cell is a channel, and brighter means a stronger signal heard there. It covers all networks, whatever is selected. Use it to see channels that come and go: interference, DFS moves, APs that switch off.

**GRID.** The telemetry table of the networks shown: SSID, BSSID, band, channel, width, RSSI (dBm), **SNR est.** (dB), quality %, security and vendor.

**TEST.** The saturation test's live tiles and timeline while a test runs, and its results afterwards (§5).

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
- **Channel width**: from the beacon's HT/VHT/HE operation elements. Blank (or *width ?*) when the AP does not advertise it. On 2.4 GHz, a 40 MHz AP is drawn above its channel below channel 8 and below it from channel 8 up, because Windows does not report the side.

### 4.4 Capture sessions (CAPTURE mode)
![Capture](images/ui_ls_capture_recording.png)

1. Select the networks to record, or nothing for everything. Click **● CAPTURE** (or **Capture session…**), then **● BEGIN SESSION CAPTURE**.
2. Enter a session name, facility, scope, survey point (zone) and notes, and choose a mode:
   - *Roaming survey*: walk the site and change the zone as you go.
   - *Stationary capture*: one fixed spot. For full analysis use **◎ SATURATION TEST** instead (§5).
3. While **● RECORDING**, every fresh scan stores one sample per AP in scope. The **live captured telemetry log** below the view shows:
   - the session details (session, facility, scope, zone, mode, notes);
   - each sample with its fluctuation (▲/▼ dB versus the previous scan);
   - **VAR (dB)**, the scan-to-scan RSSI variation. This is a dB value, not network latency.
4. **⏸ PAUSE** suspends recording (the log header shows **⏸ PAUSED**). **▶ RESUME** continues. **■ STOP** ends the session and offers to export it straight away.

Session data is written through one ordered queue, so the session record is always saved before its samples. Version 1.0 could lose samples at session start; 1.1 and later cannot.

---

## 5. Saturation test (SATURATION TEST mode)

A **signal saturation test** leaves the laptop in one test area for a set time. It measures how stable the selected networks are: peaks, drops, dropouts, channel changes, scan-to-scan variation, trend, exposure below signal thresholds, and the surrounding channel congestion. Full definitions are in [`SATURATION_TEST_SPEC.md`](SATURATION_TEST_SPEC.md).

![Setup](images/ui_ls_saturation_setup_ready.png)

### Step 1: Setup (panel on the right)
**1 · What to test**

Tick the networks in the **network list** (§4.2). The panel repeats the selection in words. The test uses the Live Survey adapter.

| You select | Scope recorded | What is analysed |
| --- | --- | --- |
| one access point | Single AP | that BSSID |
| two or more access points | Multiple APs | each BSSID |
| one whole network (✓) | Single SSID | every BSSID of that SSID (new ones are added as they appear), plus SSID-level "strongest AP" tracking |
| two or more whole networks | Multiple SSIDs | the same, per SSID |
| whole networks **and** single access points | **Mixed** | the single APs from the first scan, plus every BSSID of the whole networks; SSID-level tracking for the whole networks only |
| nothing | All visible | every BSSID heard (environment survey). You are asked to confirm before it starts |

Hidden networks can only be tested by BSSID. The selection is **locked while the test runs** and unlocked when it ends.

**2 · How long**
- Duration: 5 min, 15 min, 30 min, 1 h, 4 h, 8 h, 24 h, custom (1–1440 minutes), or *until stopped*.
- Scan interval: every 5 (default), 10, 15, 30 or 60 s. Paused time is not counted towards the duration.
- The test records one scan per interval, even while the live view scans more often. Both use the same scans, so a test never causes extra scanning.

**3 · Where**
- **Test area / location**: required, and printed on the report.
- **Geolocation**: latitude and longitude in decimal degrees (WGS84), with optional accuracy in metres. Press **DETECT** to use the Windows Location service; if it is off or denied, the reason is shown and you can type the coordinates instead.
- **Building / floor / zone**: for indoor comparisons.
- **Report details** (expand): test name, facility, operator, client, notes.
- **Advanced thresholds** (expand). Defaults:

| Setting | Default | Valid range |
| --- | --- | --- |
| Drop threshold | 8 dB | 3–30 |
| Peak threshold | 8 dB | 3–30 |
| Dropout (consecutive missed scans) | 2 | 1–20 |
| Baseline window (samples) | 12 | 3–120 |

- **Keep this PC awake** (on by default): prevents sleep while the test runs. You are warned when running on battery.

The bottom of the panel always shows:
- a summary, such as *"Test 1 SSID (all BSSIDs) every 5 s for 00:15:00 (≈180 cycles)"*;
- the first problem that blocks starting, in red;
- **▶ START SATURATION TEST**, which is enabled only when everything is valid.

### Step 2: Running
![Running](images/ui_ls_saturation_running.png)

Starting switches the centre to **TEST**.
- **Panel:**
  - status, a progress bar, elapsed and remaining time;
  - cycle counts (fresh / stale / failed);
  - warnings: adapter problem, scan throttling, on battery. The progress bar is indeterminate for *until stopped*.
- **One tile per BSSID:**
  - current RSSI in large type, with a **text label** (EXCELLENT … UNUSABLE, or NOT SEEN, STALE, SCAN FAILED);
  - min, average and max; σ; availability;
  - event counts and the grade so far.
- **Live timeline:**
  - every target, with shaded threshold bands;
  - red ticks where a target was not seen;
  - dashed marker lines.
- **Add marker:** type what is happening (e.g. "microwave on", "door closed", "forklift charging") and press **Add**. Markers are timestamped and appear on charts and in reports.
- **Event feed:** drops, peaks, dropouts, channel changes and strongest-AP changes as they are detected.
- **❚❚ PAUSE / ▶ RESUME** suspends scanning; paused time does not count. **■ STOP** asks for confirmation, then analyses and saves what was collected.

You can switch to SPECTRUM, WATERFALL or GRID while a test runs; the test continues. **TEST** brings the tiles back.

### Step 3: Results
![Results](images/ui_ls_saturation_results.png)

- **One row per BSSID:** grade, score, SSID, channel(s), availability %, mean, tier, σ, min/max, drops, dropouts, channel changes, % of time below −80 dBm, and the first finding.
- **Selecting a row** shows its timeline; the table below lists every detected event.
- **Exports** (panel): **PDF REPORT**, **CSV BUNDLE** and **JSON** (§7). **NEW TEST** returns to setup and to the spectrum.

**Past tests** are under **HISTORY / LOGS → SATURATION TESTS** (§6).

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
| **Trend** | dB per hour. **Reported only for tests whose observed span is at least 15 minutes** (shown as "n/a" otherwise: a per-hour figure from a few minutes is not meaningful) |
| **Neighbours** | Other APs heard *fresh* in the same scan. **Co-channel** = same primary channel. **Overlapping** = a different channel whose occupied block overlaps (including 40/80/160 MHz bonding, in every band) |
| **Interference margin** | This AP's signal minus the strongest co-channel or overlapping neighbour, in dB. Higher is better: aim for 20 dB or more. Below 10 dB is flagged |
| **BSS Load** | What the AP itself advertises: connected clients, and how busy it sees the channel (%). Not every AP advertises it ("not advertised") |
| **Link quality / SNR** | The driver's link-quality %, and SNR **estimated** from an assumed −95 dBm noise floor (Windows does not report the real one) |
| **Active duration** | Measured test time excluding pauses. Durations and exposure never exceed it |
| **Stability score / grade** | `100 − 4·σ − 0.5·(100 − availability) − 3·drops/h − 5·dropouts/h − 5·channel changes/h` (A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, F). This is a heuristic for **stability, not strength**. A steady −70 dBm AP can score A while its tier is *Fair*, so read the grade together with the tier and exposure |

**Measurement limits.** RSSI is what this adapter and driver report; it is not calibrated. Compare results only between tests with the **same adapter and driver**; both are printed on every report. Throughput, latency and packet loss are not measured.

**Older tests.** Reopening a test recorded before 1.3 re-analyses it with the corrected rules (active time, exposure, trend). Data that 1.2 did not record (access point identity, BSS Load, interference margin, environment) is shown as **"not recorded"**, never as zero or "none".

---

## 6. HISTORY / LOGS tab

Two sub-tabs:

**CAPTURE SESSIONS**
- **Saved capture sessions:** filter by name (case-insensitive), **from / to date**, or **✕ CLEAR FILTERS**; **REFRESH LIST**.
- Select a session to see its **historical telemetry samples** and a **timeline graph**. Lines are coloured by average signal tier, and you can tick or untick APs in the legend.
- **Display fields:** choose which columns (Time, SSID, BSSID, Channel, Band, RSSI, SNR, Quality, RSSI variation, Zone) appear on screen and in exports.
- **EXPORT CSV / JSON / PDF:** export the selected session, or several sessions at once with Ctrl/Shift-click (CSV and JSON). **DELETE SESSION** permanently removes a session and its samples.

**SATURATION TESTS**

![Saturation history](images/ui_history_saturation_tests.png)

- Every saved test with its start time (UTC), name, location, geolocation, scope, cycles and status.
- **Open results** re-analyses the selected test from its stored scans and shows its targets, timeline, events and exports right there. Reports are always re-derivable. While a test is running, open past results after it finishes.
- **Refresh** reloads the list. **Delete…** removes a test and all its cycles after confirmation.

---

## 7. Exports

| Export | Contents |
| --- | --- |
| **Session CSV** | Metadata header (start and end in UTC **and** local with offset, generator version, definitions) plus one row per sample. Columns: `Timestamp_UTC` (renamed in 1.3 from `Timestamp`), `RSSI_Variation_dB` and `SNR_Estimated_dB`. Numbers always use a decimal point, whatever the Windows language |
| **Session JSON** | Session, summary, samples (keys `RssiVariationDb`, `SnrEstimatedDb`) |
| **Session PDF** | Client and surveyor details, executive summary, timeline of the top 5 APs, per-AP statistics, full sample log |
| **Saturation PDF** | The sections, in order: <ul><li>**Test details:** geolocation and map link; start and end in local time with UTC offset **and** in UTC; active vs planned duration.</li><li>**Measurement environment:** app version, computer, OS, time zone, adapter, MAC, driver, and this PC's own Wi‑Fi connection at the start.</li><li>**Executive summary:** grade, score, **tier**, availability, mean, σ, min/max, **interference margin**, events, plain-language findings.</li><li>**Methodology:** definitions, trend rule, neighbour definition, precision statement.</li><li>**Scan health.</li><li>**Per target:** access point identity (vendor, security, 802.11 standard, beacon interval), band, channel, width and DFS, statistics, link quality and estimated SNR, exposure, events, neighbours, interference margin, **BSS Load**, timeline and histogram with labelled axes.</li><li>**SSID aggregates**, then **markers**.</li><li>**Appendix:** per-cycle log (complete up to 20,000 rows; the CSV is always complete).</li></ul> |
| **Saturation CSV bundle** | `_info.csv` (every setting, version, the environment and definitions as key/value rows), `_samples.csv` (one row per scan per target, **including missing, stale and unknown rows**, with UTC and local time, neighbours, interference margin, BSS Load and geolocation), `_events.csv`, `_summary.csv` (every metric per target), `_cycles.csv`, `_markers.csv` |
| **Saturation JSON** | `schema: wirelessscanner.saturation_report.v2`: `environment`, `generatedBy`, `precision`, `neighbours`, `startLocal`/`endLocal`, test configuration, `geo`, health, targets, SSIDs, events, markers, every cycle. Values are unrounded |

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
