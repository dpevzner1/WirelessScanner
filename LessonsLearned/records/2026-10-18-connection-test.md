# Lesson LL-2026-10-18-CONNECTION-TEST: Measuring connection quality over the current Wi‑Fi connection (1.5.0)

| Field | Required value |
| --- | --- |
| Lesson ID | `LL-2026-10-18-CONNECTION-TEST` |
| Task/change reference | Control tasks `T24`–`T26` (finding F29); decision DR-0014; engine consultation C-09 |
| Project | WirelessScanner |
| Components | ConnectionTestRunner, PathDiscovery, ConnectionAnalyzer, WindowsConnectionProber, WlanAdapter.ReadLinkAsync, ResponderProtocol/Server/Client, HttpSpeedClient, SecretProtector, ConnectionRepository, ConnectionExportService, ConnectionTestViewModel and views, MainWindow (modes, SETTINGS, history), RestApiServer, release script, documentation |
| Observed period (UTC) | `2026-10-18T21:33:47Z` (1.4.0 ledger sealed; owner question followed) to `2026-10-19T00:38:31Z` (release built; seal follows in the same ledger) |
| Local date/time zone | 2026-10-18, America/Chicago |
| Recorded/updated (UTC) | 2026-10-19T00:38:31Z |
| Status | validated |
| Owners/reviewers | Implementer: Claude Code (claude-opus-5-5) for demitri.pevzner. Reviewer: pending (not independent) |
| Related run/profile/snapshot IDs | `RV-2026-10-18-RELEASE-1.5.0`: TEST-1.5-FULL-1/2/3 249/249, HW-CONN-2, HW-CONN-3, DOC-AUDIT-1.5-1, RELEASE-1.5.0-1/2 |
| Source/evidence scope | Real-run exports (SSID, BSSID, IP addresses) only in `governance/evidence/local-only/conn-hw-*`; ledger stdout is redacted |

## Executive outcome
- **What is now true:**
  - A connection test measures the current connection end to end. Probes are bound to Wi‑Fi and the connection is never changed.
  - Problems are attributed to a part of the path.
  - Speed and responsiveness tests exist, plus an authenticated LAN responder for two-way tests.
  - Reports, history, a REST endpoint and documentation are in place.
- **Real connection, 60 s, final code:** 277/278 probes answered, every segment measured from the start, association unchanged.
- **Not proven:**
  - stage B against a real server;
  - stage C over real Wi‑Fi (needs a wired responder);
  - the installed build on the owner's machine;
  - independent review.
- **Authority:** no GitHub push.

## Expected behavior
The owner asked for a strong way to test internet and connection quality over an existing authenticated wireless connection. The owner chose A, B, then C, made the UX choices, and asked to *"build it all"*.

## Initial conditions and boundaries
- 1.4.0 sealed, not installed.
- The DR-0004 boundary was passive only, until DR-0014.
- Targets were limited to those the owner approved.
- No connection changes.
- The engine was used read-only (C-09: no IP-measurement sources).

## Iteration log
| Date/time (UTC) | Attempt/run | Objective and action | Result | Evidence | Reuse/next decision |
| --- | --- | --- | --- | --- | --- |
| 2026-10-18 | C-09 | Engine consultation | Gap: no jitter / SLO / bufferbloat sources; monitoring practice only | consultation log | Definitions from IETF IPPM / ITU-T, stated as such |
| 2026-10-18 | DESIGN | Options A/B/C, UX mock-up, owner choices | Approved | DR-0014 | — |
| 2026-10-18 | CORE-TEST-1 | 32 new tests (analyzer, runner, responder, prober) | **3 failures**: (1) SSID change missed when the BSSID was unchanged (analyzer bug); (2) wrong JSON casing in the test; (3) **TCP reset to a closed port took ~2 s on Windows** (SYN retries), so the "refused = valid RTT" assumption was wrong | console | Refused recorded without an RTT; SSID check moved first |
| 2026-10-18 | UI-RENDER-1 | Renders of setup / running / results / settings / history | Missing field labels; blank connection fields; DNS count included IPv6 | renders | Fixed |
| 2026-10-18 | HW-CONN-1 | First real run (45 s) | Router and internet fine, but **ISP hop not found** and **RSSI empty** (driver lacks the RSSI opcode) | local-only | Diagnostic test written |
| 2026-10-18 | DIAG | Repeated discovery, cold and warm | Always found at TTL 3 in the diagnostic | console | Runner start failure is intermittent |
| 2026-10-19T00:2xZ | HW-CONN-2 | 60 s real run after the per-TTL retry and the tick-30 retry | Start-up discovery failed again; **the tick-30 retry found the hop** (ISP probes from round 32) | ledger | Retry repeated every 300 ticks; discovery result logged |
| 2026-10-19T00:3xZ | HW-CONN-3 | Final 60 s real run | Discovery at start found TTL 3; all segments from round 0; association unchanged | ledger | — |
| 2026-10-19 | RELEASE-1.5.0-1 | Release with the responder zip | Pass; smoke test showed **"→" not rendered in the Windows console** | console | ASCII arrows; RELEASE-1.5.0-2 |
| 2026-10-19 | DOC | Docs claimed "250" tests | Actual 249 (explicit tests excluded) | suite | Corrected before the audit |

## Actual behavior and deviations
- **Deviations from DR-0014:**
  - one `ConnectionResults` table for speed, two-way and marker results;
  - raw probes kept (no 1-minute aggregates);
  - `Refused` without a round trip;
  - IPv4 DNS only.
- **Real-world behaviour:** start-up ISP discovery is intermittent on this network, recovered by repeated discovery.

## Failure, root-cause, and remediation register
| ID | Symptom/evidence | Direct cause | Contributing conditions/control gap | Remediation | Validation/state |
| --- | --- | --- | --- | --- | --- |
| CT-1 | SSID change not detected | Network change checked only inside the BSSID-change branch | Test fixture changed the SSID with the same BSSID | SSID check first | `LinkEvents_…` |
| CT-2 | Closed-port connect took ~2 s | Windows SYN retransmission before reporting the reset | Assumed Linux-like immediate RST | `Refused`, no RTT | `Tcp_OpenPort_IsOk_ClosedPort_IsRefusedWithoutRtt` |
| CT-3 | ISP hop missing at start (intermittent) | No replies to TTL-limited probes at start-up on the owner's path | One-shot discovery | Per-TTL retry; rediscovery at tick 30 and every 300 ticks; logged | HW-CONN-2 (recovered), HW-CONN-3 (found at start) |
| CT-4 | RSSI empty on real hardware | Driver does not implement the msm RSSI opcode | Tested only through mocks | Fallback to the associated BSS entry | HW-CONN-2/3 RSSI -72 / -71 dBm |
| CT-5 | Arrow unreadable in the console | Non-ASCII in console output; Windows console code page | Not smoke-tested | ASCII arrows | RELEASE-1.5.0-2 |

## Root-cause narrative
Most defects came from assumptions that hold in tests or on other platforms but not on this Windows machine and driver:
- the reset timing;
- RSSI opcode support;
- console encoding;
- ISP routers' reply behaviour.

The real-connection run and the packaged-artifact smoke test found them; unit tests could not.

## Corrections and changed artifacts
- **Domain:** `ConnectionTest.cs`; `INetworkAdapterProvider.ReadLinkAsync`.
- **Application:** `ConnectionAnalyzer`, `ConnectionTestRunner` (+ `PathDiscovery`).
- **Infrastructure:**
  - probes and link: `ConnectionProber`, `WlanAdapter` (`ReadLinkAsync`, `ParseWlanStatistics`);
  - stage B/C clients: `CapacityClients` (`ResponderClient`, `HttpSpeedClient`, `SecretProtector`);
  - storage: `ConnectionRepository` (+ `MockConnectionProber`), `WlanDbContext` / `SchemaMigrator` (schema 4);
  - reports and API: `ConnectionExportService`, `RestApiServer`.
- **Responder:** `WirelessScanner.Responder.Core`, `WirelessScanner.Responder`.
- **Presentation:** `ConnectionTestViewModel`, `ConnectionDrawerView`, `ConnectionStageView`, `MainWindow(.xaml/.cs)`, `MainWindowViewModel`, `NetworkExplorerView(Model)`, `SaturationTestViewModel.ActiveTestId`, `App.xaml.cs`.
- **Tools:** `tools/build_release.py` (responder zip), `tools/api_smoke.py` (connection checks; default 1.5.0).
- **Version:** `Directory.Build.props` 1.5.0.
- **Docs:** `CONNECTION_TEST_SPEC.md` (new), README, USER_GUIDE, TECHNICAL_REFERENCE, STATUS, CHANGELOG, PARITY_CHECKLIST, DR-0014, consultation log; screenshots `ui_conn_*`, `ui_settings_test_servers`, `ui_history_connection_tests`.

## Validation
| Test/evidence | Before | After | Scope |
| --- | --- | --- | --- |
| Full suite | 209 | 249/249 (Debug and Release) | automated |
| HW-CONN-3 | n/a | 278 probes, 1 failed; all segments; unchanged association | owner's real connection, 60 s |
| Responder smoke test | n/a | Packaged zip runs (`--help`, key creation) | local |
| DOC-AUDIT-1.5-1 | n/a | 61 identifiers, none missing; 12 topics present | docs |

## Engine capability impact
Not applicable: read-only consultation. The gap (no IP performance-measurement sources) is recorded for the engine owner in C-09.

## What worked
- The fake prober and the in-process responder made the whole pipeline testable without the network.
- The explicit real-run test with redacted output and local-only exports.
- The diagnostic test that separated "discovery broken" from "intermittent at start-up".

## What did not work
- Assuming Windows TCP reset timing.
- Trusting the RSSI opcode.
- Non-ASCII console text.
- One-shot discovery.

## Resume and anti-duplication map
- **Owner actions:** install 1.5.0; a 15-minute connection test; optionally run the responder on a wired machine and pair it; choose a stage-B server.
- **Do not add a public speed provider** without the owner's explicit choice (privacy).
- **Reuse:** `ConnectionHardwareRun` and `ConnectionDiagnostics` for new networks.

## Reusable prevention
- Run every network feature once on the real target machine before release, and smoke-test packaged artifacts.
- Never treat an elapsed time as a round trip unless the protocol guarantees one exchange.
- Discovery steps against third-party routers must retry and recover during the run.
- Keep console output ASCII.

## Follow-up tasks
- T24-WP4: owner live review on the installed build.
- T25-WP3: owner speed-test server.
- T26-WP4: owner wired responder run.
- Independent review.

## Residual risk
- Stage B/C real-world behaviour is unverified.
- ICMP-based ISP probing depends on the provider's routers.
- Bufferbloat grade thresholds follow common practice and are not standardised.

## Supersession
None.

## Completeness attestation
All material attempts are listed. Console-only step times were not captured and are not estimated; ledger steps carry exact times.
