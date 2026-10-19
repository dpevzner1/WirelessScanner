# Connection Test — specification

| Field | Value |
| --- | --- |
| Spec ID | `SPEC-CONN-001` |
| Version | 1.0 (2026-10-18) — implemented in WirelessScanner 1.5.0 |
| Decision record | [`DR-0014`](decisions/DR-0014-connection-quality-testing.md) (stages A, B, C; UX; defaults) |
| Engine consultation | C-09 (`docs/consultation/ENGINE_CONSULTATION_LOG.md`) |
| Analytics version | `CONN-ANALYTICS-1` (`ConnectionAnalyzer`) |
| Report schema | `wirelessscanner.connection_report.v1` |
| Control | finding F29, tasks T24 (A), T25 (B), T26 (C) |

## 1. Purpose
Measure the **quality of the connection this PC is already associated and authenticated to**, and show **where along the path** a problem happens:
- the Wi‑Fi link;
- the router;
- the ISP;
- the internet;
- DNS;
- the web.

It is the counterpart of the saturation test:

| | Saturation test | Connection test |
| --- | --- | --- |
| Question | How stable is the radio signal of the networks around me? | How good is my connection, and where does it break? |
| Method | Listens (scans); sends nothing | Small probes over the current connection |
| Target | Networks / access points ticked in the list | The connection this PC is on (fixed) |

## 2. Rules (never broken)
1. **No connection changes.** The feature never calls WLAN connect, disconnect, roam or profile APIs, and never uses credentials.
2. **Bound to Wi‑Fi.** Every probe's socket or ICMP request uses the Wi‑Fi interface's IPv4 address as its source. A PC with Ethernet also connected still measures the Wi‑Fi path (the setup card says so).
3. **Failures are data.** Every probe ends in exactly one outcome:
   - `Ok`, `Timeout`, `Refused`, `Unreachable`, `TtlExpired`, `DnsError`, `HttpError`, `Error` or `Skipped`.
   - Each outcome carries a reason.
   - Totals reconcile: sent = ok + failed. Skipped probes are not counted as sent.
4. **No invented data.** A late loop wake-up (sleep, stall) of ≥ 3 ticks is recorded as a gap with its length. Gaps are never counted as outages.
5. **Outbound traffic is owner-approved.**
   - Default internet targets: Cloudflare `1.1.1.1`, Google `8.8.8.8`, Quad9 `9.9.9.9` (owner, 2026-10-18). They are editable.
   - The configured DNS resolvers are always used for the DNS segment.
   - No public speed-test provider is preset.

## 3. Segments and probes (stage A)
| Segment | Probe | Light / Detailed interval | Timeout |
| --- | --- | --- | --- |
| Wi‑Fi link | WLAN link read: association, SSID/BSSID, RSSI, PHY rx/tx rate, channel, driver frame counters; interface byte counters | every tick (1 s / 0.5 s) | — |
| Router | ICMP echo to the default gateway | every tick | 1 000 ms |
| ISP hop | ICMP echo to the first public hop after the gateway; or, if it ignores echo, a TTL-limited probe towards the first internet target that must expire at that hop | every 2 ticks | 1 500 ms |
| Internet | TCP connect to port 443 (**primary**) and ICMP echo, to each target | every 2 ticks | 2 000 ms |
| DNS | One A query (`example.com`) over UDP to each configured IPv4 resolver | every 10 ticks | 2 000 ms |
| Web | Cold HTTPS GET of `https://one.one.one.one/cdn-cgi/trace` (resolution + TCP + TLS + first byte) | every 30 ticks | 5 000 ms |

**Path discovery.** TTL-limited ICMP probes run towards the first internet target, TTL 1–10, with one retry per TTL on timeout.
- **ISP hop:** the first answering hop that is neither RFC 1918, link-local, loopback nor the gateway. Carrier-grade NAT `100.64/10` counts as ISP.
- **If no hop is found at start,** discovery is repeated after 30 ticks and then every 300 ticks until a hop is found. A marker is added when it succeeds. Observed on the owner's connection: the start-up discovery occasionally gets no replies (about 1 run in 4); the later attempt then finds the hop.
- **Echo support** is tested twice. It decides between echo and TTL-limited probing.

**Timing.**
- `Stopwatch` (QPC) is read around each probe, giving sub-millisecond resolution.
- ICMP uses `IcmpSendEcho2Ex` with an explicit source address.
- TCP sockets close with RST (linger 0), so thousands of probes leave no TIME_WAIT state behind.

**A TCP reset is not a round trip.** It is recorded as `Refused` with no RTT, because Windows retries the SYN before reporting a reset (about 2 s on loopback).

**Probe traffic.** About 9–12 kbit/s at Light with three targets and one resolver. The exact estimate is printed in setup and on the report (`ConnectionTestConfig.EstimatedKbps`).

## 4. Analytics `CONN-ANALYTICS-1` (definitions)
| Metric | Definition |
| --- | --- |
| Loss % | failed ÷ sent × 100 |
| Availability % (segment) | rounds with ≥ 1 successful probe ÷ rounds × 100. A round is one tick's probes of that segment (all internet targets together) |
| RTT min / p50 / p95 / p99 / max / mean | Successful probes only. Nearest-rank percentiles (same method as the saturation analytics) |
| Jitter | Mean \|RTT(n) − RTT(n−1)\| over consecutive successful probes of one target (ms) |
| Delay variation P95 | 95th percentile of the same consecutive differences (round-trip delay variation) |
| Primary method | Internet: TCP connect when present (ICMP is often deprioritised). Otherwise the most used method of the segment |
| Outage | ≥ 3 consecutive failed rounds of a segment. Start = first failed round; end = first successful round (open if still failing at the end) |
| Attribution (outages) | First failing segment upstream during the window, in this order: Wi‑Fi link (association down), router (≥ half its rounds failed), ISP hop, then the segment itself. DNS outage with a healthy router and internet → DNS. Web → router / internet / DNS / web, in that order. **ISP-hop silence while the internet stays reachable is not a fault** (ICMP rate limiting): attributed to nothing |
| Latency spike | RTT > max(3 × baseline, baseline + 100 ms), where baseline = median of the previous 30 successful RTTs of that target (≥ 10 needed; spikes never raise the baseline). Consecutive spike samples form one event with its peak |
| Attribution (spikes) | Router spike → Wi‑Fi link if a roam or reconnect happened in the previous 10 s, otherwise router. Internet, DNS or web spike while the router was slow (± 2 s) → router (or Wi‑Fi link after a roam); while the ISP hop was slow → ISP hop; otherwise the segment itself |
| Link events | Roam (BSSID change, same SSID), network change (SSID change), disconnect / reconnect, IP or gateway change, sleep gap |
| Wi‑Fi counters | Sums of positive deltas of driver counters (a counter reset is skipped). Retry % = Δretries ÷ Δtx frames; failed % = Δfailed ÷ Δtx; FCS % = ΔFCS errors ÷ (Δrx + ΔFCS). "Not reported" when the driver gives none |
| PC traffic | This PC's own interface throughput (kbit/s, both directions) from consecutive byte counters: context for spikes |
| Verdicts | From the internet segment (pooled TCP): GOOD when p95, jitter and loss are all within the GOOD limits; FAIR within the FAIR limits; else POOR. Browsing is capped at FAIR when DNS p95 > 300 ms. "NOT TESTED" without internet probes |
| Voice estimate | Simplified ITU‑T G.107 E-model on the internet segment: one-way ≈ mean RTT ÷ 2; Eff = one-way + 2 × jitter + 10; R = 93.2 − Eff/40 (Eff < 160) else 93.2 − (Eff − 120)/10; R −= 2.5 × loss %; MOS = 1 + 0.035R + 7·10⁻⁶ R(R − 60)(100 − R). Always labelled an **estimate** |

**Verdict thresholds:**

| Use | GOOD: p95 / jitter / loss | FAIR: p95 / jitter / loss |
| --- | --- | --- |
| Browsing | ≤ 200 ms / ≤ 100 ms / ≤ 2 % | ≤ 500 / ≤ 200 / ≤ 5 |
| Streaming video | ≤ 300 / ≤ 100 / ≤ 1 | ≤ 800 / ≤ 200 / ≤ 3 |
| Video calls | ≤ 150 / ≤ 30 / ≤ 1 | ≤ 300 / ≤ 50 / ≤ 3 |
| Online gaming | ≤ 60 / ≤ 15 / ≤ 0.5 | ≤ 100 / ≤ 30 / ≤ 1.5 |

## 5. Speed and responsiveness (stage B)
**Servers:**
- **LAN responder:** measures the Wi‑Fi and LAN capacity.
- **Custom HTTP server you control:** a large-file GET and a POST that accepts uploads. It measures the internet path.
- **No public provider is preset.** Some publish every result with the client IP; choosing one is the owner's decision.

**Run:**
1. **Pause.** Regular probes pause; in-flight probes finish.
2. **Idle latency.** 12 samples at 250 ms. The target is the router (ICMP) for a LAN server, or the first internet target (TCP) for an HTTP server.
3. **Download.** N parallel TCP streams (default 4) for a fixed time (default 10 s), while latency is sampled every 250 ms.
4. **Upload.** The same.
5. **Throughput.** Bytes counted after a 1 s ramp-up (TCP slow start excluded), divided by the measured counting time.

**Results:**
- **Latency increase.** The higher of the two loaded medians (download, upload) minus the idle median, floored at 0.
- **Bufferbloat grade.** A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F ≥ 400.
- **Responsiveness ≈ RPM.** 60 000 ÷ the worst loaded median. This is an approximation, not the IETF algorithm.
- **Running it.**
  - On demand ("SPEED TEST NOW", with a confirmation showing the time and impact), at the start, or every 15, 30 or 60 minutes.
  - Only one capacity test runs at a time.
  - It appears as an event, and its time is excluded from the monitoring statistics.

## 6. Two-way Wi‑Fi tests with a LAN responder (stage C)
**Responder:**
- `WirelessScanner.Responder` (cross-platform console; portable zip in the release) runs on a **wired** machine.
- Protocol `WSR1` on one TCP and one UDP port (default 5410).
- TCP handshake:
  - the server sends a 16-byte nonce;
  - the client proves the key with HMAC-SHA256(key, nonce ‖ "client");
  - a wrong key closes the connection without a reply.
- Commands: Ping, Download, Upload, OpenUdp (returns a session), UdpStats.
- UDP packets carry a 52-byte header and a 16-byte truncated HMAC. Only packets with a valid MAC for an open session are answered.
- **Safety limits:** replies are never larger than requests; at most 8 sessions and 16 TCP connections; durations capped at 60 s; 200 packets/s per session.
- **The key is stored protected:**
  - On the responder: in a user-only key file, printed on first run or with `--show-key`.
  - In the app: protected with Windows DPAPI for the current user, and never shown again or written to reports.

**Two-way stream:**
- **Traffic:** 10 s at 50 packets/s, 172-byte packets (G.711-like).
- **Per-direction loss:**
  - up = 1 − unique received by the responder ÷ sent;
  - down = 1 − replies received ÷ replies sent.
- **Round trip:** (t4 − t1) − (t3 − t2).
- **Per-direction delay variation:**
  - up = mean |Δ(t2 − t1)| between consecutive packets;
  - down = mean |Δ(t4 − t3)|.
  - The unknown clock offset cancels.
- **Reordering:** counted on each side.
- **MOS:** per direction, using the same E-model with one-way delay ≈ median RTT ÷ 2.

**LAN capacity:** 4 streams for 5 s, each direction.

## 7. Report output
| Output | Content |
| --- | --- |
| **PDF** | 1. Test details. 2. What was tested: the connection, path, ISP hop, targets, other interfaces. 3. Summary: verdicts with reasons, voice estimate, *where problems happened*, totals. 4. Segments. 5. Targets with failure reasons. 6. Timelines per segment and Wi‑Fi signal, one time axis, one scale per panel. 7. Wi‑Fi link. 8. Speed. 9. Two-way. 10. Events with attribution. 11. Method, definitions, limits, thresholds. 12. Markers. Appendix: probe log (complete up to 20 000 rows) |
| **CSV bundle** | `_info.csv` (every setting, provenance and definition as key/value rows), `_probes.csv` (one row per probe, failures included, with geolocation), `_link.csv`, `_segments.csv` (segment and target levels), `_events.csv`, `_markers.csv`; `_speed.csv` and `_twoway.csv` when present |
| **JSON** | `schema: wirelessscanner.connection_report.v1`: method, definitions, limits, thresholds, test, environment, connection, totals, verdicts, voice, problems by segment, segments, targets, link, speed, two-way, events, markers, probes, link samples. Unrounded |
| **REST API** | `GET /api/connection` (running test: segments, problems, verdicts, counts), `GET /api/connection/tests` |

**Provenance**, the same as the saturation test:
- app version, OS, machine and time zone;
- adapter, MAC and driver;
- the connection at the start;
- geolocation, building, floor and zone.

## 8. Storage
Tables, added by `SchemaMigrator` (schema 4; additive):
- `ConnectionTests`: config JSON, times, status, active seconds, linked saturation test;
- `ConnectionProbes`;
- `ConnectionLinkSamples`, including driver counters and the gap;
- `ConnectionResults`: speed, two-way and markers as JSON.

Writes go in order, batched once per tick, through the shared ordered writer. Reports are always re-derived.

## 9. Acceptance criteria (`CONN-AC`)
| ID | Criterion | Verification |
| --- | --- | --- |
| CONN-AC-01 | Every probe is bound to the Wi‑Fi address | `Runs_Ticks_WithDueProbes_BoundToWifiAddress_AndPersistsEverything`; real run |
| CONN-AC-02 | Injected faults are attributed correctly: router, internet, Wi‑Fi down, ISP-hop rate limiting | `ConnectionAnalyzerTests` |
| CONN-AC-03 | Statistics match hand-computed values; totals reconcile | `Statistics_MatchHandComputedValues`, `Totals_Reconcile_…` |
| CONN-AC-04 | Gaps are recorded, not counted as outages; roams and network changes detected | `LinkEvents_RoamNetworkChangeAndSleepGap` |
| CONN-AC-05 | The test refuses to start without a Wi‑Fi connection and never changes it | `RefusesToStart_WithoutWifiConnection`; the real run reports the association unchanged |
| CONN-AC-06 | Speed test pauses probes and measures latency under load | `SpeedTest_PausesRegularProbes_…` |
| CONN-AC-07 | Responder: two-way stream, throughput, wrong key rejected, forged UDP unanswered | `ResponderTests` (loopback) |
| CONN-AC-08 | Reports carry every section; exports round-trip | `Repository_RoundTrip_AndExports` |
| CONN-AC-09 | UI: mode, drawer, stage, history, settings; linked saturation | `ConnectionUiTests` + renders |
| CONN-AC-10 | Real connection: every segment answered and nothing changed | `ConnectionHardwareRun` (Observed); two-way on real Wi‑Fi: Blocked until a wired responder is available |

## 10. Known limits
- **Round trip only.** One-way delay needs synchronised clocks; stage C measures one-way *variation* per direction.
- **ICMP deprioritisation.** Routers may deprioritise ICMP, so the internet verdicts use TCP connect.
- **Voice score.** The voice score is an estimate, not a measured call.
- **Speed results** depend on the server and its path, and saturate the link while they run.
- **Scope.** Results describe this PC at this place on this connection.
- **DNS.**
  - DNS probes use IPv4 resolvers only.
  - The web probe resolves its name with the system resolver (not bound to Wi‑Fi). The name lookup is also covered by the DNS segment.
