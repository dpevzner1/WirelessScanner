# Decision DR-0012: Report completeness, precision and provenance (SAT-ANALYTICS-2, report schema v2)

Status: approved by the owner's request; implemented in 1.3.0 (2026-10-16)  
Date: 2026-10-16  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Supersedes: the SAT-ANALYTICS-1 definitions in SATURATION_TEST_SPEC §6 for active time, exposure weighting, trend and neighbours (all other definitions unchanged)

## Context
The owner asked to validate that the generated reports "contain all relevant data that we must capture in such a report for network diagnostics and performance tracking, with clarity, granularity, and absolute precision".

The audit used the owner's first live 1.2.0 report (5-minute Mixed test, Intel AC 7265). It found five kinds of problem.

Precision errors:
- **Active duration** was the elapsed time of the last scan (296.7 s), not the real 300 s, so the report read "00:04:56 of planned 00:05:00".
- **Exposure** gave the last scan a full interval, so "longest below −70 dBm" was 301 s, longer than the test.
- A **trend** of +35.6 and −19.1 dB/h was published for a 5-minute test. The plain-language finding was already suppressed below 15 minutes; the number was not.
- **Co-channel counts** included cached (stale) entries: 3 neighbours in cycle 1, then 0. They also matched the primary channel only, so an 80/160 MHz AP's bonded block was ignored. Overlap was computed for 2.4 GHz only.
- The REST API and the PDF footer reported **version 1.1.0** for a 1.2.0 install, because each project had its own version number.

Missing context:
- No app version, OS, time zone or adapter driver.
- Not recorded whether the measuring PC was connected (Windows throttles scans while connected).
- Local times without a UTC offset.

Missing diagnostics that Windows provides but the app discarded:
- the 802.11 standard (PHY);
- the beacon interval;
- the AP's own **BSS Load** (associated stations, channel-busy share);
- link quality was captured but never summarised;
- no interference margin.

Clarity: chart labels printed at about 2 pt, which is unreadable. The 7 px text was drawn into a raster about 4× larger than the printed size.

Robustness:
- Capture CSV values used the current culture: a comma-decimal locale would write `1,500` and break the file.
- Capture CSV timestamps were UTC without saying so.
- A stored test whose configuration lacked fields came back with a random TestId.

## Decision
1. **SAT-ANALYTICS-2:**
   - Active time is **measured** by the runner and stored (`SaturationTests.ActiveSeconds`). For older tests it is reconstructed as the last cycle plus one interval, capped at the planned duration.
   - The last cycle's exposure window ends at the end of the test.
   - **Trend is null** when the observed span is under 15 minutes.
   - Neighbours count **fresh** entries only.
     - **Co-channel** = same primary channel.
     - **Overlapping** = a different primary channel whose occupied block (40/80/160 MHz bonding, every band) shares spectrum.
     - **Interference margin** = target RSSI minus the strongest such neighbour.
2. **Capture**, all null when not reported and never guessed:
   - from Windows: PHY type, beacon interval and primary centre frequency;
   - BSS Load (element 11): stations, channel busy %, admission capacity.
3. **New per-target statistics:**
   - identity: vendor, security, PHY, beacon interval, DFS;
   - link quality mean and minimum;
   - estimated SNR, labelled as estimated;
   - BSS Load share advertised, stations and busy % (mean and max);
   - interference margin median and minimum;
   - observed span.
4. **Provenance** (`SaturationEnvironment`), captured at start and stored with the test:
   - app version, OS, machine, time zone and offset;
   - adapter, MAC, and driver version, date and provider (RSSI is comparable only per adapter and driver);
   - the PC's own association: SSID, BSSID, channel, PHY, rx/tx rate and signal.
5. **Report schema v2:**
   - JSON gains `environment`, `generatedBy`, `precision`, `neighbours`, `startLocal` and `endLocal`.
   - The CSV bundle gains `_info.csv` (key/value provenance, configuration and definitions), a `WallLocal` column, and the new metrics.
   - The PDF gains a "Measurement environment" section, the tier and margin in the summary, local times with offset plus UTC, and precision and neighbour definitions. Trend shows "n/a (span < 15 min)".
   - Charts are drawn in points, so labels print at 7.5 pt, with axis units.
   - Tests recorded before 1.3 say **"not recorded"**. Missing data is never presented as "not advertised" or "none".
6. **One version** for every project, in `Directory.Build.props`. The window header reads it from the assembly.
7. **Capture-session CSV** uses invariant culture, a `Timestamp_UTC` column, labelled start and end times, and a generator line.
8. **Schema 3:** the migrator adds missing **nullable** columns with `ALTER TABLE ... ADD COLUMN`. It never drops or rewrites data, and refuses non-nullable additions.

## Consequences
- **Breaking for data consumers:**
  - `_samples.csv` `Overlap24_Count` becomes `Overlap_Count`, and its definition widens to all bands with bonding and fresh-only.
  - Capture CSV `Timestamp` becomes `Timestamp_UTC`.
  - The JSON schema becomes `...saturation_report.v2`.
  - The CSV bundle has six files.
  - Documented in the CHANGELOG.
- Older tests re-analyse with corrected active time, exposure and trend. Their neighbour numbers stay in the 1.2 definition, which the PDF states.
- The interference margin is a receive-side indicator from one client position. It is not a measurement of SINR or of throughput.

## Evidence
`ReportPrecisionTests` cover:
- parsers, geometry, fresh-only neighbours, active time and exposure (the live case), and trend gating;
- the new statistics, the schema-3 upgrade with round-trip, and the JSON/CSV contract;
- culture invariance and the regeneration harness.

The owner's live report was regenerated from a database copy with the new code and reviewed page by page.
