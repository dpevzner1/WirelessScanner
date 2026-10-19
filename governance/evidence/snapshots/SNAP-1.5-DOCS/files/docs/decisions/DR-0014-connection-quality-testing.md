# Decision DR-0014: Connection and internet quality over the current Wi‑Fi connection

Status: **approved 2026-10-18; stages A, B and C implemented in 1.5.0** (owner: "build it all")  
Date: 2026-10-18  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Builds on: DR-0004 (measurement boundary), DR-0012 (provenance), DR-0013 (physical units)  
Engine consultation: C-09

## Context
The owner asked for a strong, solid way to test **connection quality and internet quality** over a wireless connection the device is **already connected and authenticated to**.

Today WirelessScanner is passive. It measures what the radio hears: RSSI, channels, neighbours. DR-0004 states that throughput, latency and packet loss are **not measured**. Every option below sends traffic, so each one **changes the measurement boundary** and needs this decision.

**Rules common to every option:**
- **Never touch the connection.** No connect, disconnect or roam; no profile or credential use. Measure whatever the PC is associated with, and record it as provenance: SSID, BSSID, channel, band, PHY, rx/tx rate, RSSI, IP, gateway, DNS servers.
- **Bind every probe to the Wi‑Fi interface** (source address or interface index). On a PC that also has Ethernet, unbound traffic silently goes over the cable and the "Wi‑Fi" result is false. This is the most common error in DIY tests.
- **Segment the path** so a problem can be *attributed*, not just observed:

  | Segment | Measured against | What a fault there means |
  | --- | --- | --- |
  | Wi‑Fi link | the radio itself: rate, retries, roams | RF / airtime problem |
  | LAN | default gateway | router / AP / backhaul problem |
  | Access | first ISP hop (found by a TTL-limited probe at start) | ISP access problem |
  | Internet | 2–3 anycast public targets | upstream / peering problem |
  | DNS | configured resolver(s) | name-resolution problem |
  | Application | small HTTPS fetch (time to first byte) | service / TLS problem |

  A drop seen at the gateway is a local problem. A drop seen only beyond the ISP hop is not the Wi‑Fi.
- **Correlate on one timeline** with the existing RSSI, roaming and saturation data. For example: "every latency spike follows a roam to BSSID X". No other common tool joins RF and path quality this way, and it is WirelessScanner's natural strength.
- **Use defined metrics, not ad-hoc ones:**

  | Metric | Standard |
  | --- | --- |
  | Round-trip delay | RFC 2681 |
  | Loss | RFC 7680 (round-trip application) |
  | Delay variation | RFC 3393 |
  | Interarrival jitter | RFC 3550 |
  | TCP throughput methodology | RFC 6349 |
  | Two-way active measurement | STAMP (RFC 8762), TWAMP (RFC 5357) |
  | Voice-quality estimate | ITU-T G.107 E-model (R-factor/MOS; always labelled an **estimate**) |
  | One-way delay guidance | ITU-T G.114 |
  | Responsiveness under load | IETF IPPM responsiveness work (RPM) |

  Report percentiles (p50/p95/p99), not only means.
- **Timing precision:**
  - Use `Stopwatch` (QPC) around TCP-connect and UDP probes for sub-millisecond resolution.
  - .NET `Ping` reports whole milliseconds only, so ICMP is a secondary signal.
  - ICMP is often deprioritised by routers, so TCP-connect to port 443 is the primary internet probe.
- **Context.** Record the interface's own byte counters during each test (`GetIfEntry2`). A spike while the PC itself was downloading is a different finding from a spike on an idle link.
- **Outbound traffic needs the owner's approval.** Public targets and providers are owner-chosen, configurable and listed in every report.

## Options

| | **A. Connection Quality Monitor** (continuous, lightweight, segmented) | **B. A + capacity and responsiveness under load** (on demand / scheduled) | **C. Two-ended measurement with a WirelessScanner responder** on a wired LAN host |
| --- | --- | --- | --- |
| Measures | Per segment: availability, loss, RTT p50/p95/p99, jitter, delay spikes, outage events with start/end; DNS time and failures; HTTPS TTFB; Wi‑Fi link counters (rx/tx rate, retry/failed-frame counters where the driver reports them, roams, disconnects, IP changes); E-model voice estimate; **fault-domain attribution** per event | Everything in A, plus download/upload throughput (multi-stream TCP, fixed duration) **and latency under load** (loaded − idle, at the gateway and on the internet). This is the bufferbloat grade, the strongest single predictor of how a connection "feels" | Everything in A, plus **per-direction** loss, reordering, duplicates and delay variation **of the Wi‑Fi/LAN segment alone** (STAMP-style reflector), a synthetic VoIP stream with an upstream and downstream MOS estimate, and **pure Wi‑Fi capacity** (LAN throughput both ways, no ISP involved) |
| Traffic | ~5–20 kbit/s; safe for hours, beside a saturation test | Saturates the link for ~10–30 s per test; disturbs other users meanwhile | Low (reflector) to saturating (capacity runs) |
| Needs | Nothing new. Owner picks the public targets | A test server: a LAN endpoint (isolates Wi‑Fi) and/or a public provider. Candidates: M-Lab ndt7, which is open source but **publishes every result with the client IP as open data**; Cloudflare's speed endpoints (terms to be checked); a self-hosted server. Avoid tools whose licence forbids embedding or automation | A second, **wired** device on the LAN (PC, Raspberry Pi, NAS/Docker) running a small authenticated responder; one open UDP/TCP port on the LAN |
| Answers | "Is it the Wi‑Fi, the router, the ISP, DNS or the service, and when?" | That, plus "Am I getting my plan's speed, and does the connection stay responsive when busy?" | That, plus "Exactly how good is this Wi‑Fi link up and down, independent of the internet?" |
| Accuracy | High for availability, loss and latency; round-trip only | As A, plus throughput that depends on the server's capacity and path | Highest; directional; repeatable |
| Effort / risk | Medium / low | Medium-high / medium (data use, user disruption, third-party terms) | High / medium (second device, a security surface on the LAN) |
| Evidence gap | ICMP deprioritisation (mitigated by TCP probes); driver support for link counters varies | Server variability; consent; provider terms | Deployment of the agent; secrets; firewall |

## Recommendation
Do it in stages. Each stage stands alone.

1. **A now:** a fourth Live Survey mode, **◉ CONNECTION**.
   - It runs alone or **at the same time as** a saturation test and a capture, on one timeline.
   - It has its own report section, CSV (`_connection.csv`), JSON and REST endpoint (`/api/connection`).
   - Defaults:
     - probe the gateway every 1 s;
     - probe the internet targets every 2 s;
     - check DNS every 10 s;
     - fetch HTTPS every 30 s;
     - summarise into 1-minute rows;
     - raise an outage event after 3 consecutive failures.
2. **B next, on demand and opt-in:**
   - start with a **LAN endpoint**, then one owner-chosen public provider;
   - offer optional scheduling (e.g. every 30 min) for tracking against the ISP plan;
   - always show the data use, and confirm before running.
3. **C later**, if the owner has (or wants) a wired host. This is professional site-survey grade.

**Not recommended:**
- **Scraping consumer speed-test websites:** unstable, against their terms, no latency-under-load.
- **ICMP-only "ping" monitoring:** ms resolution, deprioritised, no attribution.

**Owner, 2026-10-18:** *"agree on A, B, then C"*. The order is agreed.

UX decisions confirmed by the owner the same day:
- the mode is named **Connection test**;
- it runs **independently**, with an optional checkbox that also starts a saturation test of the connected access point on one timeline;
- history gets a new **CONNECTION TESTS** sub-tab;
- default internet targets are **Cloudflare (1.1.1.1), Google (8.8.8.8) and Quad9 (9.9.9.9)**, probed by TCP 443 plus ICMP, and editable. The configured DNS servers are always tested.

Status: the stage order and UX are agreed. Implementation starts when the owner gives the go-ahead (after the 1.4.0 install and live checks, unless told otherwise).

## Proposed UX (A, then B and C): treated like the saturation test

**Placement: a fourth Live Survey mode, ◉ CONNECTION TEST.**
- It sits next to MONITOR, CAPTURE and SATURATION TEST, with the same pattern:
  - a mode button;
  - a setup drawer on the right;
  - a live view in the centre (TEST);
  - results;
  - a list under HISTORY / LOGS.
- Like the other modes, tests keep running when you switch modes.
- Capture, a saturation test and a connection test can all run **at the same time**.
- The status line shows each running test, e.g. *● TESTING SIGNAL · ● TESTING CONNECTION*.

**Clarity: one line under the mode bar says what each test is:**

| Mode | One-line description (shown in the UI) | Sends traffic? | Target |
| --- | --- | --- | --- |
| Saturation test | Listens to the radio of the networks you select and measures how stable their signal is | No | Networks or access points you tick in the list |
| Connection test | Checks your current connection end to end, from Wi‑Fi to router to internet, with tiny probes, and shows where problems happen | Yes, about 12 kbit/s; never changes the connection | The network this PC is connected to (fixed; not chosen from the list) |

The drawer header repeats this, plus a "What this test does" expander covering:
- the segments;
- the targets;
- the data rate;
- the privacy notes.

**Setup drawer, same three steps as the saturation test:**
1. **What is tested.** A read-only card for *your connection*: SSID, the access-point unit name (1.4), band and channel, PHY rate, signal, IP, gateway and DNS.
   - Not connected over Wi‑Fi → the test is blocked with a clear message.
   - Ethernet also connected → the drawer says *"probes are forced through Wi‑Fi"*.
   - The network list marks the connected network with a CONNECTED badge.
   - The path segments are listed and can be switched off individually.
   - Internet targets can be edited under *Targets*.
2. **How long.** The saturation presets (15 min … 24 h, custom, until stopped), plus intensity:
   - *Light* (default, about 12 kbit/s);
   - *Detailed* (faster probing, about 40 kbit/s).
3. **Where.** Location, geolocation, building, floor and zone. These fields are shared with, and prefilled from, the saturation test.

Options:
- *Also run a saturation test of this access point (one timeline)*. It pre-selects the connected unit.
- *Stop if the connection changes* (roam to another network or SSID). This is off by default: roams within the same network are recorded as events.

**Running (TEST view):**
- the **connection path** strip (Wi‑Fi link → Router → ISP → Internet; DNS; Web), each segment with its current latency, loss and a status word (GOOD / DEGRADED / DOWN, never colour alone);
- an event feed, with plain-language attribution;
- stacked charts on one time axis: latency per segment, loss, and Wi‑Fi signal (no dual axis);
- markers, pause, resume and stop, as in the saturation test.

**Results:**
- **Verdict line:** suitability by use, e.g. browsing / streaming / video calls / gaming, each rated GOOD / FAIR / POOR from the latency, jitter and loss thresholds printed in the report.
- **Where problems happened:** event counts per segment.
- A per-segment statistics table.
- Exports: PDF, CSV bundle, JSON.

**History:** HISTORY / LOGS gains a **CONNECTION TESTS** sub-tab next to CAPTURE SESSIONS and SATURATION TESTS. A combined run appears in both lists, linked by its run ID.

**Stage B placement:**
- A *Speed and responsiveness* section in the same drawer offers *Run now* and an optional *schedule*.
- Setup lives in a new **SETTINGS → Test servers** page: the LAN server and the public provider, with disclosures. That page is also the natural home for REST API settings.
- Stage B results appear as markers on the connection timeline.

**Stage C placement:** SETTINGS → Test servers → *LAN responder*, with pairing (address plus key). When paired, the path strip gains a *Wi‑Fi (two-way)* segment with per-direction results.

## Implementation 1.5.0 (control F29 / T24–T26)
| Stage | Built | Proof |
| --- | --- | --- |
| A | Fourth Live Survey mode **⇄ CONNECTION TEST** (drawer, CONNECTION centre view, history sub-tab). The runner probes each segment bound to the Wi‑Fi address; ISP hop by TTL discovery (retried at tick 30, then every 300 ticks); link sampling with driver counters and an RSSI fallback. `CONN-ANALYTICS-1` (outage and spike attribution, verdicts, E-model estimate). Reports PDF / CSV / JSON v1. `GET /api/connection`. CONNECTED badge in the explorer. Linked saturation test | `ConnectionAnalyzerTests`, `ConnectionRunnerTests`, `ConnectionProberTests`, `ConnectionUiTests`, renders; real 45 s run (Observed) |
| B | Speed and responsiveness: idle / loaded latency, multi-stream TCP with ramp exclusion, bufferbloat grade, RPM approximation. On demand (confirmed), at start, or scheduled. LAN responder or custom HTTP server; **no public provider preset** | `SpeedTest_PausesRegularProbes_…`, loopback throughput |
| C | `WirelessScanner.Responder` (+ `.Core`): WSR1 protocol with HMAC authentication, reflector safety limits, per-direction loss / reordering / delay variation / MOS, LAN capacity. Pairing in SETTINGS → TEST SERVERS with the key under DPAPI. Portable zip in the release | `ResponderTests` (loopback), `TestServers_KeyIsStoredProtected_…` |

**Deviations from the proposal:**
- **Table names.** Speed, two-way and marker results share one `ConnectionResults` table (stored as JSON) instead of separate tables.
- **Raw probes.** All raw probes are kept; there are no 1-minute aggregates. Reports are derived, as for saturation tests.
- **TCP resets.** A TCP reset is recorded as `Refused` without a round trip. Windows retries the SYN before reporting it, so the elapsed time is not an RTT; this was found by a test.
- **DNS.** DNS probes cover IPv4 resolvers only.

**Found on real hardware:**
- ISP-hop discovery at start intermittently got no replies (about 1 run in 4; other runs found the hop at TTL 3 every time). Fixed with a per-TTL retry and repeated discovery (tick 30, then every 300 ticks); the 60 s real run confirmed the recovery.
- The adapter does not support the RSSI opcode. Fixed with a fallback to the associated BSS entry.

## Engineering requirements (engine operating rules)
- **Error handling:** probe failures are data (timeout, refused, unreachable, DNS error), never exceptions.
- **No silent gaps:** missed intervals are recorded, as in the scan-cycle model.
- **Clock checks:** clock jumps and sleep/resume are detected and marked.
- **Telemetry:** every probe outcome has a reason code. Totals reconcile, e.g. sent = received + lost + unknown.
- **Validation:**
  - simulated networks (fault injection: loss, delay, outage per segment) prove the attribution rules;
  - mutation checks prove the tests catch broken detectors;
  - an owner hardware run on the real connection (Observed).
- **Deployment and rollback:** the feature is off until enabled; targets are configurable. Disabling it stops all outbound probes.

## Data and compatibility
- New tables: `ConnectionTests`, `ConnectionProbes` (or 1-minute aggregates plus raw rows for N days) and `ConnectionEvents`. They are added by the schema migrator and are additive only.
- Analytics are versioned separately (`CONN-ANALYTICS-1`).
- The report schema gains a `connection` section. Existing reports are unchanged.
- Privacy:
  - public IP addresses are not stored unless the owner enables it;
  - target hostnames and IPs are listed in reports;
  - B with M-Lab requires a visible disclosure, because results become public.

## Acceptance (A)
- On a PC with Wi‑Fi and Ethernet both connected, every probe leaves through the Wi‑Fi interface (proven by a test).
- Injected faults are attributed to the right segment:
  - gateway loss → LAN;
  - loss beyond the first ISP hop only → Access/Internet;
  - DNS timeouts with healthy RTT → DNS.
- Totals reconcile; missed intervals and sleep/resume are visible.
- Saturation and connection results share one timeline in the report.
- No connection change is ever made (no WLAN connect/disconnect API calls are present in the feature code).
