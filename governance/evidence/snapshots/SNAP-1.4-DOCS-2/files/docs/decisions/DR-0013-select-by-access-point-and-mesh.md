# Decision DR-0013: Select by physical access point; single-AP and mesh quality tests

Status: **approved (2026-10-18); stage-1 MVP implemented in 1.4.0** (see *Implementation 1.4.0*); later stages open  
Date: 2026-10-18  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Builds on: DR-0010 (explorer), DR-0011 (shared selection, scopes), DR-0012 (provenance, neighbours)  
Engine consultation: C-08

## Context
The owner wants three things:
- to select by **hardware**, meaning the physical AP or router that broadcasts the signal, not only by SSID or single BSSID;
- to test **one AP across all the SSIDs it broadcasts**;
- to test **mesh quality**.

The app knows SSIDs and BSSIDs. Windows does not report which BSSIDs belong to one device, so device identity has to be **inferred** and stated with a confidence.

### Evidence from the owner's environment
Read-only IE inventory probe, 2026-10-18, 20 BSSIDs (`HardwareIeInventoryProbe`).

> **Privacy:** the addresses below and in the tests are **synthetic stand-ins**. They have the same structure as the real ones: the same locally-administered bits, octet-4 radio index, last-nibble SSID index, and which addresses share a prefix. The real BSSIDs and SSIDs stay in `governance/evidence/local-only/`.

| Observation | Meaning for the design |
| --- | --- |
| `9A:3C:5E:1F:42:70/73` (2.4 GHz) and `9A:3C:5E:26:42:70/74/76` (5 GHz): one router, 2 radios, 4 SSIDs including a hidden one; −25 to −51 dBm (next to the PC) | The single-AP, all-SSIDs case is real here. This vendor encodes the **radio in octet 4** and the **SSID index in the last nibble** |
| `80:AA:12:4C:7D:F0/F3` and `82:AA:12:4C:7D:F3`; `C4:55:66:31:5B:F0/F3` and `C6:55:66:31:5B:F3` | Another pattern: the **locally-administered bit** is set in octet 1 for virtual BSSIDs, and the 5 GHz radio also carries a **hidden SSID** (typical of a mesh/backhaul or IoT BSS) |
| No Multiple-BSSID (71), no EasyMesh Multi-AP element, no 802.11s Mesh ID; one RNR (201); BSS Load on 12 of 20 | Standards-based grouping is **not** available from these APs, so heuristics and manual confirmation are required |
| WPS element (vendor `00:50:F2` type 4) on several APs | WPS carries the device name, model and UUID-E. That is a **strong, passive device key** when present |

Different vendors use different MAC schemes, so no single rule groups correctly. The registry must combine evidence, show its confidence, and let the user correct it.

## Addendum 2026-10-18: generic identification, public vendor data, feasibility of C

**Vendor-independent radio identity (measured).**
- Every BSSID served by one radio shares that radio's TSF timer, and Windows reports it with each scan (`ullTimestamp`).
- The offset *TSF − host receive time* matched **to within 1 ms** for BSSIDs on the same radio in two consecutive scans: 9A:3C:5E:26:42:70/74/76; 80:AA:12:4C:7D:F3 and 82:AA:12:4C:7D:F3.
- Different radios of one box can differ by seconds or more: in the Arris BGW210, Broadcom 2.4 GHz and Quantenna 5 GHz differ by about 45 s.
- Different boxes that booted after the same power event also differ by only seconds: about 12 neighbour gateways show about 22.9 h uptime.

Rules:
- A TSF match within about 5 ms, stable across scans, means **same radio** (High).
- A device needs radio groups joined by a MAC family, a WPS UUID-E/model, or standard elements.

**WPS decoding (measured):**
- Arris BGW210-700 (×3 units). Each 5 GHz radio reports "Quantenna Topaz" with model number BGW210, so the manufacturer differs per radio inside one box.
- D-Link DIR-820L, Netgear PGZNG1.
- The closest router (9A:3C:5E, locally-administered BSSIDs) broadcasts **no WPS**: it is identified by TSF + MAC family only.

**Public, updateable data:**
- **IEEE Registration Authority registries** (MA-L, MA-M, MA-S, CID; public CSV, updated continuously) give the vendor name for every globally-unique OUI.
  - Plan: ship a snapshot; add a **Check for updates** action that downloads from the IEEE site over HTTPS, validates it, and keeps the previous copy on failure. It is off until the owner approves, because it is an outbound call.
  - Print the registry date in reports (provenance).
  - Locally-administered BSSIDs have no OUI: they resolve through their MAC family (LA bit cleared / sibling BSSIDs).
- **No authoritative public database** maps beacon fingerprints to AP models. Fingerbank is commercial, keyed, and DHCP-oriented.
  - Hence a project-owned, versioned **AP-family rule pack** (JSON with a hash, published with releases), seeded from observed patterns: LA-bit families; octet-4 radio / last-nibble SSID families; Arris BGW210 = Broadcom 2.4 + Quantenna 5.
  - User merges can be exported as candidate rules.

**Feasibility of C (management plane) for the equipment in range:**
- Arris BGW210 (AT&T gateway): no local API.
- D-Link DIR-820L: legacy HNAP/SOAP, end-of-life, insecure.
- Netgear PGZNG1: limited SOAP.
- The 9A:3C:5E router: unknown model, no WPS; if it is a mesh product its management is usually a **cloud** app.

Generic standards (SNMP 802.11 MIB, TR-069/USP) are rarely open on consumer gear. C is therefore feasible only as a **plug-in framework with per-model adapters, for equipment the owner controls and that exposes an API** (e.g. UniFi, Omada, OpenWrt, Meraki/Aruba cloud). It never applies to neighbours' equipment.

**Revised recommendation:** Stage 1 = A + B **plus** TSF radio grouping, IEEE OUI registry with update, and the AP-family rule pack. C as an adapter framework once the owner's own equipment is known.

## Options (engine C-08 axes)

| | A. Passive, OS-only heuristics | B. Passive + vendor/standard elements | C. Management plane (router or controller APIs, SNMP/LLDP) | D. Active client tests |
| --- | --- | --- | --- | --- |
| How a device is identified | MAC patterns (octet/nibble/LA-bit families) + co-movement of RSSI + same location | A, plus WPS UUID-E/model/name, Multiple-BSSID, RNR co-location, EasyMesh roles, AP-name vendor IEs | Authoritative inventory, mesh topology, backhaul links | n/a (measures quality, not identity) |
| Single AP, all SSIDs | Yes | Yes (higher confidence) | Yes | Per-SSID connect tests |
| Mesh quality | Client-side view: node coverage, best-node margin, overlap, channel plan, hidden-backhaul visibility | Same, plus fronthaul/backhaul roles where advertised | **True** backhaul RSSI/rate, hops, client steering | Latency, loss, jitter, throughput per node |
| Accuracy | Medium; false merge/split possible | High when the elements exist (WPS here), else as A | High | High, but disruptive |
| Privileges / effort | None new / medium | None new / medium | Credentials per vendor, network access / high | Changes the PC's connection, needs Wi-Fi profiles / high |
| Vendor dependence | None | Low (standards + common vendor IEs) | High (one adapter per vendor) | None |
| Evidence gap | Heuristic validity per vendor | Coverage per vendor | Vendor APIs, credentials | Measurement-boundary change (DR-0004) |

## Recommendation
Do it in stages. Each stage is useful on its own and none blocks the next.

### Stage 1: A + B, a **device registry**
1. **Device registry** (new domain concept, persisted):
   - **Device → Radio(s) → BSSID(s) → SSID(s)**;
   - each link carries its evidence (MAC pattern, WPS UUID, Multiple-BSSID, RNR, RSSI co-movement) and a **confidence** (High / Medium / Low);
   - user actions: **merge**, **split**, **rename** (e.g. "Living-room node"), **mark as mesh system**. Manual decisions always win and are stored.
2. **Explorer: "Group by: Network (SSID) | Access point (hardware)".**
   - Hardware view: Device → radios (2.4 / 5 / 6 GHz) → SSIDs, with the confidence badge and the evidence in a tooltip.
   - Ticking a **device** selects every BSSID it broadcasts, **including ones that appear later** (like a whole SSID today).
   - Ticking a **radio** selects that radio's SSIDs.
   - Both groupings drive the same `NetworkSelection`, so the live view, capture and tests stay consistent (DR-0011).
3. **New test scopes:**
   - **Single device**: every SSID and radio of one AP.
   - **Multiple devices / mesh system**: the nodes of one mesh.
   - Mixed stays available.
4. **New analytics (SAT-ANALYTICS-3), single device:**
   - **Per-SSID consistency on one radio.** SSIDs on the same radio should track within about 1–2 dB; divergence flags misgrouping or SSID-specific power or rate settings.
   - **Per-SSID availability.** One SSID disappearing while its radio stays up indicates an SSID or service problem, not RF.
   - Per-radio comparison (2.4 vs 5 vs 6), BSS Load per radio, channel and width per radio.
5. **New analytics, mesh system (client-side, passive):**
   - **Best node** per cycle, and the **best-node margin** (best minus second-best). Under about 6 dB means roaming ambiguity and a ping-pong risk.
   - **Node changes** (the strongest-BSSID detector at device level).
   - **Node overlap and co-channel** between nodes.
   - **Fronthaul channel plan**: same-channel nodes contend.
   - **Hidden backhaul BSS visibility** (a proxy).
6. **Guided "node position" survey** (passive approximation of backhaul quality):
   - run a short test with the PC **next to each node** in turn;
   - the report then builds a **node-to-node matrix** of how strongly each node hears the others' radios, including hidden backhaul BSSs;
   - this estimates backhaul link quality without credentials;
   - it reuses tests, zones and geolocation (DR-0009).

### Stage 2 (optional, owner decision): D, active per-node tests
Connect to a chosen BSSID and measure latency, loss and jitter to the gateway (and optionally throughput).
- This changes DR-0004's measurement boundary.
- It temporarily disconnects the PC from its current network.
- It needs stored Wi-Fi profiles.

### Stage 3 (optional, per vendor): C, management-plane adapters
Only for owned equipment with an API (e.g. UniFi, Omada, OpenWrt, ASUS). These provide true backhaul metrics. They need credentials and secure secret storage.

## Data and compatibility
- New tables `Devices`, `DeviceMembers` (BSSID ↔ device with evidence and confidence) and `DeviceAliases`, all added by the schema migrator.
- Observations keep their BSSID key. Device grouping is resolved at analysis time and stored with the test, so later edits do not rewrite history.
- Reports gain a "Device" column and, for device/mesh scopes, the sections above. Report schema v3; additions only.

## Owner decision (2026-10-18)
- The owner asked whether A, B and C were feasible and whether a generic or public, updateable source exists (answered in the addendum).
- The owner's direction: *"If there is a unit approximation that can serve us well, or at the bare minimum identify that several SSIDs are coming from the same physical unit, this would be a strong MVP"*.
- Approval: *"lets go with the proposal, and ensure this is documented throoughly in the project artifacts and read me"*.

**Scope chosen for 1.4.0: stage-1 MVP.**
- **Delivered:**
  - unit inference (TSF + WPS + MAC family) with confidence, evidence and corrections;
  - unit selection and scopes;
  - the explorer hardware view;
  - single-unit analytics;
  - reports.
- **Deferred (still in stage 1, not in the MVP):**
  - the IEEE OUI registry with update;
  - the AP-family rule pack;
  - mesh-system analytics (best node, best-node margin, node changes, channel plan);
  - the guided node-position survey.
- **Stages 2 and 3:** unchanged.

## Implementation 1.4.0 (control F28 / T23)
| Area | What was built | Proof |
| --- | --- | --- |
| Capture | `AccessPoint.TsfOffsetMs`, WPS fields (`WlanAdapter.ParseWps`) | `PhysicalUnitTests` parser tests |
| Inference | `PhysicalUnitGrouper`: same-band TSF agreement ≤ 5 ms with an agree ≥ disagree vote over scans; WPS UUID links, and **conflicting UUIDs block any link**; MAC family (octet 4 may differ only for locally-administered addresses); 10-minute evidence retention; confidence High / Medium / Single / Manual; mesh candidates by shared visible SSID | `PhysicalUnitTests` (owner-environment fixtures) |
| Corrections | `UnitOverrides` (assignments, names) in Settings `PhysicalUnitOverrides`; rename, split, move, reset; refused while a test runs | `SplitMergeRename_ArePersistedAndWin`, `Overrides_PersistInSettings_AndReloadOnStart` |
| Selection | Unit entries in `NetworkSelection`; `SingleUnit` / `MultipleUnits` / Mixed; lock freezes membership | `PhysicalUnitIntegrationTests` |
| Test config | Units resolved at start into `TargetUnits`; members added as fixed BSSIDs | `Explorer_AccessPointView_…`, `Tracker_UnitScope_…` |
| Analytics | SAT-ANALYTICS-3 `AnalyzeUnit`: on-air %, best mean, per-radio SSID consistency (finding > 3 dB), SSID-only outage (finding ≥ 5 %), confidence finding | `AnalyzeUnit_*` |
| Reports | Schema v3; PDF 6b; `_units.csv`; `_info.csv` `TargetUnits` and `UnitInference`; JSON `units` | `Reports_CarryUnitSection_…` |
| UI | Explorer *GROUP BY: Access point*; badges, evidence tooltip, mesh hint, context menus | render `ui_ls_explorer_access_points.png` |

**Deviations from the stage-1 design above:**
- **No new tables.** `Devices`, `DeviceMembers` and `DeviceAliases` are not used. Corrections live in Settings, and the grouping for each test is stored inside its `ConfigJson`, so history is never rewritten.
- **RSSI co-movement is not used as evidence.** It is too weak on its own and is slow to accumulate.
- **Badge wording.** The third confidence level is *Single* (one BSSID), not *Low*.

## Acceptance (Stage 1)
MVP status in 1.4.0:
- **Grouping:** Validated on fixtures from the owner's live inventory.
- **Evidence, confidence and corrections:** Validated.
- **Single-device test with per-SSID divergence:** Validated.
- **Mesh test metrics:** deferred. Only mesh candidacy is shown.
- **Parity:** Validated; the network view is unchanged, see `PARITY_CHECKLIST.md`.
- **Confirmation on the installed build:** Blocked pending the owner.

- In the owner's environment, `9A:3C:5E:1F/26` group into **one device with 2 radios and 4 SSIDs**. `84/82:AA:12:4C:7D:*` and `A0/C6:55:66:31:5B:*` group by their LA-bit family. Unrelated OUIs never merge.
- Every grouping shows its evidence and confidence. Merge, split and rename persist and override the heuristics.
- A single-device test lists every SSID of the device and flags per-SSID divergence. A mesh test reports the best node, best-node margin, node changes and the channel plan.
- No measurement-boundary change in Stage 1. The parity checklist is extended, so no existing selection behaviour is lost.
