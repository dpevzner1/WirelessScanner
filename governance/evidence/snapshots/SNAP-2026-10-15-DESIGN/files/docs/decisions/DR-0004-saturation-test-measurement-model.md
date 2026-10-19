# Decision DR-0004: Saturation test measurement model and analytics `SAT-ANALYTICS-1`

Status: approved (implementation tracked as T06)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
The owner requested a signal saturation test (finding F10). The test leaves the device in the test area for a chosen period and measures peaks, drops, channel hops and other variation for selected SSIDs or APs, with fully granular reports. The baseline app also mislabels scan-to-scan RSSI change as "jitter (ms)" (F14), and records missing or assumed values as numbers (F09).

## Decision
Adopt the measurement model and analytics definitions in `docs/SATURATION_TEST_SPEC.md` §3–§6 exactly:
- cycle statuses `fresh/stale/failed/skipped`, and observation statuses `observed/stale/missing/unknown`;
- quality labels `measured/derived/estimated/unavailable`: the noise floor is **unavailable**, SNR is **estimated**;
- wall-clock UTC plus monotonic elapsed time;
- event detectors against a rolling-median baseline with hysteresis, for drops, peaks, dropouts, channel changes and strongest-BSSID changes;
- statistics: percentiles, σ, availability, slope, threshold exposure, co-channel load and the derived stability score;
- the analytics are pure functions (`SaturationAnalyzer`), versioned `SAT-ANALYTICS-1`. Every report prints the version, thresholds and formula.

"Jitter" is renamed in the UI and exports to **RSSI variation (dB)**. The stored column name stays `Jitter` for compatibility.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Engine C-03 (telemetry capture source + case study) | 2026-10-15 | Missing ≠ zero; quality and status separate; no false resolution; keep timestamps | Not RF-specific |
| Engine C-01 | 2026-10-15 | No RF/Wi‑Fi concepts in the engine | The RF thresholds (−67/−70/−80 dBm) are common industry design targets used by the implementer, not engine-sourced |
| Baseline F09, F14 | BL-2026-10-15 | Constant noise floor shown as measured; "ms" unit on a dB value | — |

## Alternatives

### Fixed absolute thresholds only (no baseline)
Rejected: they miss relative drops at strong locations (e.g. −45 → −58 dBm).

### Statistical outlier tests (z-score)
Rejected for v1: they need a stationary distribution and are harder to explain. The rolling median with hysteresis is robust and easy to explain.

## Consequences
- Benefits: reproducible, explainable and testable analytics; honest labelling.
- Costs:
  - thresholds are heuristics and must be visible and adjustable;
  - the stability score must never be presented as a standard.

## Exit and rollback criteria
- Success gate: synthetic-series tests with known answers pass, including negative cases (SAT-AC-03, SAT-AC-04).
- Stop condition: a hardware run shows the detectors trigger on ordinary driver quantisation noise at default thresholds. Then tune the defaults, bump to `SAT-ANALYTICS-2` and write a new decision.
- Rollback: analytics are versioned, so old reports keep their version label.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Architecture | demitri.pevzner | approved via "begin" (chat 2026-10-15) | 2026-10-15 | session transcript |

## Supersession rule
Any change to a definition, default threshold or the score formula needs a new analytics version and a new decision.
