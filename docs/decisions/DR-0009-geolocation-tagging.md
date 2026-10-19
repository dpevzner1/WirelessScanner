# Decision DR-0009: Geolocation tagging of saturation tests; network identifiers kept in reports, public-repo evidence redacted

Status: approved  
Date: 2026-10-15  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Supersedes: none

## Context
- During T06 the implementer proposed keeping raw hardware-run exports (which name nearby SSIDs and BSSIDs) out of the GitHub mirror, because those identifiers can be geolocated through public Wi-Fi databases.
- The owner responded on 2026-10-15: "we do need the geo location in the report, this is important for proper metric tracking."

## Decision
1. **Reports keep full identity and gain location.** Saturation reports always contain full SSIDs and BSSIDs; nothing in the product is redacted. Each test also carries a structured `GeoTag`:
   - WGS84 latitude and longitude;
   - accuracy in metres;
   - source: `manual`, or `windows-location (<PositionSource>)`;
   - capture time;
   - building, floor and zone.
2. **How it is entered.** By hand, or with **DETECT**, which uses the Windows Location service (`Windows.Devices.Geolocation`). Detection failures are explained and never block a manual entry. Coordinates are validated: both or neither, latitude between −90 and 90, longitude between −180 and 180; a decimal comma is accepted.
3. **Where it appears in exports:**
   - PDF: test details (coordinates, source, capture time, OpenStreetMap link, building/floor/zone);
   - JSON: a top-level `geo` object;
   - CSV: geolocation columns repeated on **every** samples and summary row, so tests from many places can be merged and tracked by location.
4. **Coordinates stay optional.** A free-text location is still required, but coordinates are not, because some sites cannot use location services. The UI recommends them.
5. **Public-repo evidence boundary (unchanged).** Raw RF captures from the owner's own environment stay in `governance/evidence/local-only/`, which the sync excludes. The trail cites redacted summaries carrying the raw files' SHA-256. This governs only what is published to GitHub, not what the app records.
6. **Build target.** The Presentation and Tests projects now target `net10.0-windows10.0.19041.0`, which provides the WinRT location API (Windows 10 2004 or later).

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Owner chat instruction | 2026-10-15 | Geolocation required in reports | — |
| `GeoTagTests` (6 tests) | suite 90/90 | Validation, detection success and failure, every CSV row, JSON, PDF | Detection is tested with a fake provider; the real Windows Location service is not exercised |

## Alternatives

### Redact identifiers in reports
Rejected by the owner.

### GPS dongle / NMEA input
Deferred: it would need hardware support. Windows Location already uses GNSS when a receiver is present.

## Consequences
- **Benefits:** results can be compared and tracked by site, floor and zone, and map links make field review easier.
- **Costs and obligations:**
  - Reports now contain location data plus network identifiers. Treat exported reports as **sensitive** and share them deliberately (documented in the User Guide).
  - Raising the minimum OS to Windows 10 2004 (19041).

## Exit and rollback criteria
- Success gate: geolocation is present in all export formats (tests). The owner verifies DETECT on their machine; until then that check is **Blocked**.
- Rollback: the `GeoTag` is optional; reverting the target framework removes only auto-detection.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Product | demitri.pevzner | approved (chat 2026-10-15) | 2026-10-15 | session transcript |
| Security/privacy | pending |  |  |  |

## Supersession rule
Write a new decision if location sources change (e.g. GNSS hardware), if location becomes mandatory, or if the publication boundary for raw captures changes.
