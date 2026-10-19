# WirelessScanner

A Windows desktop tool for **Wi‑Fi signal analysis, site surveys and signal saturation testing**, built with C# / .NET 10 and WPF on the Windows Native Wi‑Fi API (`wlanapi.dll`). It is for network engineers, IT and facilities teams, and field surveyors.

**Version 1.5** · Windows 10 (2004+) / 11 x64 · administrator rights required · MIT licence  
Repository: https://github.com/dpevzner1/WirelessScanner

![Live Survey: grouped network explorer, identity-coloured spectrum, load strip and DFS shading](docs/images/ui_ls_monitor_selected.png)

## What it does

| Feature | Summary | Status* |
| --- | --- | --- |
| **Live survey** | Every AP with RSSI, estimated SNR, link quality, band/channel/width, security (incl. WPA3, OWE, Enterprise), vendor. Grouped network explorer (search, filters, sparklines, whole-SSID or per-AP selection); width-true 2.4/5/6 GHz spectrum with identity colours, DFS shading, channel load and least-busy channel; 60-scan waterfall; telemetry grid | Validated (tests, UI renders) · Observed (hardware) |
| **Access-point (hardware) view** *(new in 1.4)* | Group the explorer by **physical access point** instead of by SSID: the app infers which BSSIDs, SSIDs and radios one router or AP broadcasts, shows a confidence badge (High / Medium / Single / Manual) with its evidence, flags **mesh node / extender candidates** (the same SSID on different units), and lets you rename, split or merge units. Select a whole unit to view, capture or test **every SSID it broadcasts**. See [Access points (hardware view)](#access-points-hardware-view) | Validated (tests, UI render) · grouping rules derived from a live inventory of 20 real BSSIDs |
| **Connection test** *(new in 1.5)* | Checks the connection the PC is **already on**, end to end: Wi‑Fi link, router, ISP, internet, DNS and web. Small probes bound to Wi‑Fi; the connection is never changed. It shows **where** problems happen (each outage and latency spike is attributed to a part of the path), with use verdicts (browsing / streaming / calls / gaming), a voice estimate, Wi‑Fi retries and roams. Optional: **speed and bufferbloat** (stage B) and **two-way per-direction Wi‑Fi tests** against a LAN responder (stage C). See [Connection test](#connection-test) | Validated (tests, UI renders) · Observed (45 s on the owner's real connection) · two-way on real Wi‑Fi: Blocked (needs a wired responder) |
| **Fresh-scan acquisition** | Each cycle requests a new Windows scan and waits for completion; cached repeats are never recorded as new data. One shared scheduler: live view, capture and saturation test never start competing scans | Validated · Observed on Intel AC 7265 |
| **Capture sessions** | Roaming (walk-through, per-zone) and stationary sessions; ordered, loss-free persistence | Validated |
| **Signal saturation test** | A Live Survey mode: leave the laptop in place for minutes to 24 h, for the networks selected in the explorer (APs, SSIDs, whole physical access points, or a mix). Measures drops, peaks, dropouts, channel changes, strongest-AP changes, RSSI variation, trend, threshold exposure and co-channel load per AP or SSID, with markers, pause/resume and a stability grade | Validated · 15-min hardware run Observed |
| **Geolocation** | Latitude/longitude (manual or Windows Location), building, floor and zone on every saturation report and CSV row | Validated (detection with the real Windows Location service: Blocked pending owner check) |
| **Reports** | PDF (charts, findings, methodology, provenance, full appendix), CSV bundle (incl. missing/stale rows; `_units.csv` for access-point tests), JSON (schema v3). Access-point tests add per-unit availability, per-radio SSID consistency and SSID-only outages | Validated |
| **History** | Search sessions and tests by name and date, re-open and re-export, delete | Validated |
| **REST API** | Localhost, `X-API-Key` header, `/api/status`, `/live`, `/sessions`, `/sessions/{id}`, `/stats`, `/connection`, `/connection/tests` | Validated |
| **Installer** | Guided install, update, repair, downgrade warning, modify shortcuts, uninstall with data retention; integrity-checked payload; no prerequisites | Logic validated; running the installer end to end: Blocked pending owner approval |

\* Evidence labels: see [docs/STATUS.md](docs/STATUS.md) and [docs/PROJECT_CONVENTIONS.md](docs/PROJECT_CONVENTIONS.md).

## Quick start

**Install a release:** download every file from the `EXE INSTALLER` folder of the latest release into one folder and run `WirelessScanner.Setup.exe`. See [`EXE INSTALLER/README.md`](EXE%20INSTALLER/README.md).

**Build from source** (.NET 10 SDK):
```powershell
git clone https://github.com/dpevzner1/WirelessScanner.git
cd WirelessScanner
dotnet build WirelessScanner.slnx
dotnet test WirelessScanner.Tests/WirelessScanner.Tests.csproj
dotnet run --project WirelessScanner.Presentation/WirelessScanner.Presentation.csproj   # prompts for elevation
```
To try it without Wi‑Fi hardware or elevation: `$env:WIRELESSSCANNER_DEMO="1"` (simulated data).

**REST API example** (enable it on *SETTINGS → REST API* and generate your own key):
```powershell
Invoke-RestMethod -Uri http://localhost:5005/api/live -Headers @{ "X-API-Key" = "<your key>" }
```
> If you used release 1.0, regenerate your API key: the 1.0 README published a working example key.

## Documentation

| Document | For |
| --- | --- |
| [User Guide](docs/USER_GUIDE.md) | Every screen, setting, export, the API, troubleshooting |
| [Saturation Test Specification](docs/SATURATION_TEST_SPEC.md) | Measurement model, analytics definitions, UX design, acceptance criteria |
| [Connection Test Specification](docs/CONNECTION_TEST_SPEC.md) | Probes, attribution rules, analytics definitions, speed and two-way tests, responder protocol, acceptance criteria |
| [Technical Reference](docs/TECHNICAL_REFERENCE.md) | Architecture, threading, data model, file locations, installer, build and release |
| [Status](docs/STATUS.md) | Evidence state of every requirement and feature |
| [Changelog](CHANGELOG.md) | What changed, including breaking changes |
| [Decisions](docs/decisions/) | DR-0001 … DR-0014 (DR-0013: access-point grouping; DR-0014: connection test) |
| [Parity checklist](docs/PARITY_CHECKLIST.md) | Proof that the 1.2 redesign kept every 1.1 feature |
| [Project conventions](docs/PROJECT_CONVENTIONS.md) | Documentation, evidence trail, engine consultation, repository sync |
| [Lessons learned](LessonsLearned/) | What was tried, what failed, and why |
| [PLAN/](PLAN/) | Original May 2026 design baseline (historical) |

## Access points (hardware view)

![Explorer grouped by access point: one unit selected and expanded, confidence badges](docs/images/ui_ls_explorer_access_points.png)

Windows reports every BSSID (one network name on one radio) separately and never says which ones come from the same box. Since 1.4 the explorer can **group by physical access point** (`GROUP BY: Access point`). One row is one router or AP, with every radio and SSID it broadcasts, including hidden ones.

**How a unit is recognised** (passive; nothing is sent to the access points):

| Evidence | What it shows | Confidence |
| --- | --- | --- |
| **Radio clock (TSF).** Every beacon carries the radio's timer. BSSIDs whose timer offset agrees within **5 ms** in the same band, and keeps agreeing over scans, share one radio. Separate radios and separate boxes differ by seconds or more | Several SSIDs on **one radio** | High |
| **WPS device identity.** The same WPS UUID-E; manufacturer and model are shown when advertised | One device | High |
| **MAC-address family.** Addresses that differ only in the bits vendors use for "virtual" BSSIDs (locally-administered bit, SSID index in the last digit, radio index in the 4th byte for locally-administered schemes) | Radios / SSIDs of one device | Medium |
| **Your correction** (rename, *This is a separate device*, *Belongs to access point…*, *Reset to automatic grouping*) | Always wins; saved with your settings | Manual |

A unit that shares a visible SSID with another unit is labelled a **mesh node or extender candidate**.

**What you can do with it:**
- Tick a unit to show, capture or **saturation-test every SSID and radio of that one device** (scope *Single access point*), or tick several units, e.g. the nodes of a mesh (*Multiple access points*). Units can be mixed with SSIDs and single APs.
- The unit's members are **fixed when a test starts**, so regrouping later never changes what a test measured.
- Reports add an **Access points** section (PDF 6b, `_units.csv`, JSON `units`), per unit:
  - **on-air %**: how often at least one member was heard;
  - **best mean signal**;
  - per radio, **SSID consistency**: SSIDs on one radio share one transmitter, so a gap of more than 3 dB means wrong grouping or SSID-specific power settings;
  - per radio, **SSID-only outage**: an SSID missing while its radio is on air is a service problem, not coverage.

**Limits:**
- Grouping is an inference and is always labelled with its confidence and evidence.
- Hidden backhaul links between mesh nodes cannot be measured from a client; only their visibility is.
- Management-plane data (real backhaul rates, controller inventories) is out of scope for this release (DR-0013, stage 3).

## Connection test

![Connection test: connection path, timelines and event feed](docs/images/ui_conn_running.png)

**What it is.** Live Survey's fourth mode, **⇄ CONNECTION TEST**. It checks the connection this PC is already associated and authenticated to, **end to end**, and shows **where** a problem happens. The saturation test listens to the radio; the connection test sends small probes over your connection (about 10 kbit/s).

**Rules:**
- **No connection changes.** The test never connects, disconnects or roams.
- **Wi‑Fi only.** Every probe is bound to the Wi‑Fi adapter, even when Ethernet is also connected.
- **Approved targets only.** Outbound targets are the router, the ISP hop, **Cloudflare / Google / Quad9** (editable) and your DNS servers.

| Part of the path | Measured with | Every (Light) |
| --- | --- | --- |
| Wi‑Fi link | Signal, PHY rate, driver frame retries, roams, disconnects | 1 s |
| Router | ICMP echo to the gateway | 1 s |
| ISP | First public hop after the router (found automatically) | 2 s |
| Internet | TCP connect to port 443 (primary) + ICMP, each target | 2 s |
| DNS | One query to each of your resolvers | 10 s |
| Web | Small HTTPS request (time to first byte) | 30 s |

**Results:**
- **Verdicts** for browsing, streaming, video calls and gaming.
- **Where problems happened.** Each outage (≥ 3 failed rounds) and latency spike is attributed to the first failing part upstream. *Router 2 · Internet 1* means two problems at home, one beyond the ISP.
- **Per-segment statistics:** loss, availability, p50/p95/p99, jitter.
- **A voice-quality estimate.**
- **Wi‑Fi retries and roams.**
- **Timelines** on one time axis.
- **Exports:** PDF, CSV (`_probes.csv`, `_segments.csv`, …), JSON (`connection_report.v1`) and `GET /api/connection`.

**Run it together with a saturation test.** One tick starts a saturation test of the connected access point on the same timeline.

**Optional stages** (SETTINGS → TEST SERVERS):
- **B, speed and responsiveness.** Download and upload capacity plus the **bufferbloat grade** (how much delay grows under load), against your LAN responder or an HTTP server you control. No public provider is preset.
- **C, two-way Wi‑Fi tests.** Run the included `WirelessScanner.Responder` on a wired machine (PC, NAS, Raspberry Pi). It measures loss, reordering and delay variation **upstream and downstream separately**, a call-quality estimate each way, and the pure Wi‑Fi capacity. Traffic is authenticated with a pairing key.

Definitions and thresholds: [CONNECTION_TEST_SPEC.md](docs/CONNECTION_TEST_SPEC.md). Decision: [DR-0014](docs/decisions/DR-0014-connection-quality-testing.md).

## Signal tiers used throughout

| Tier | RSSI | Colour |
| --- | --- | --- |
| Excellent | ≥ −55 dBm | green |
| Good | −67 to −55 dBm | green |
| Fair | −70 to −67 dBm | amber |
| Weak | −80 to −70 dBm | orange |
| Unusable | < −80 dBm | red |

Colour is never the only signal: every tier is also labelled in text.

## Measurement limits
- RSSI and link quality are reported by the adapter driver and are not calibrated against an RF instrument; compare results from the same adapter.
- The Windows API exposes no noise floor, so SNR is an **estimate** (RSSI − assumed −95 dBm).
- "RSSI variation" is the scan-to-scan change in dB, not network latency jitter.
- The saturation test does not measure throughput, latency or packet loss; the **connection test** does, as round trips over the current connection (one-way delay needs synchronised clocks; stage C gives per-direction loss and delay variation).
- Which BSSIDs belong to one physical access point is **inferred** (radio clock, WPS identity, MAC family), never reported by Windows; every grouping shows its confidence and evidence and can be corrected.
