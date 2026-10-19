# Decision DR-0010: Grouped network explorer and focused, width-true RF spectrum

Status: approved · implemented in 1.2.0 (2026-10-16; evidence: docs/PARITY_CHECKLIST.md, RV-2026-10-16-REDESIGN)  
Date: 2026-10-16  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Supersedes: none

## Context
- The owner found the SSID/AP list "somewhat outdated" and the spectrum "a little monotone", and asked for proposals with visuals.
- The proposal (`governance/evidence/design/PROPOSAL-live-survey-redesign-v1.html`) was approved on 2026-10-16: "love it, implement these and ensure no current features, capabilities, or capacity is lost".
- Findings: F19 (selection UX: flat list, mode switch, no search/filter/sort) and F20 (spectrum: tier-only colour, parabolic shapes, label collisions).

## Decision
1. **Network explorer** replaces the card list and the SSID/AP switch:
   - SSID groups with expandable BSSID rows; a tri-state group checkbox (selecting a whole SSID also follows APs that appear later; hidden SSIDs can only be selected per AP);
   - per-row signal bars, dBm, tier label and a 12-scan sparkline;
   - search, sort (signal / name / channel), and chips for band (2.4 / 5 / 6), fair-or-better, secured and selected-only;
   - a footer that states the resulting scope in words.
2. **Selection model.** `NetworkSelection` holds whole-SSID selections and fixed-BSSID selections. An empty selection means "everything", the same as the 1.0 capture semantics. The legacy `IsSsidView` / `AccessPointDisplayItem` API stays as a bridge, so existing behaviour and tests keep working.
3. **Identity colour:**
   - each network (SSID) gets a colour from the validated categorical palette (dark steps, surface #0b0b0d);
   - **at most 3 networks are coloured at once**, because overlapping shapes need all-pairs separation, which only slots 1–3 pass;
   - colour follows the network in selection order and never jumps between networks;
   - with nothing selected, the 3 strongest networks are auto-focused and labelled "(auto)";
   - every other network is drawn as a dashed outline.
4. **Spectrum:**
   - flat-topped masks matching the channel width;
   - signal bands at −67, −70 and −80 dBm;
   - DFS shading on 5 GHz;
   - a channel-load strip with a least-busy marker (2.4 GHz picks from channels 1/6/11);
   - band selector 2.4 / 5 / 6 / **All** (All = stacked bands, the previous default);
   - views **Spectrum / Waterfall / Grid** (Grid is the previous telemetry grid);
   - hover tooltips; clicking a shape toggles selection;
   - a "Label all" option keeps the previous label-every-AP behaviour.
5. **Waterfall:** channel × time over the last 60 fresh scans, using a single-hue sequential ramp (strongest RSSI per channel).

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Proposal page (approved) | v1, 2026-10-15 | Mockups and rules | Simulated data |
| Palette validator (dataviz skill) | 2026-10-15 | 8 slots pass adjacent pairs on #0b0b0d; only slots 1–3 pass all pairs (slot 3 vs 5 has CVD ΔE 1.6) | Synthetic validation, not user testing |
| Engine C-04/C-05 (consultation C-07) | 2026-10-16 | Gestalt grouping, Hick's law, visible status, never colour alone | Engine has no RF-visualisation sources |

## Alternatives

### Colour by tier (1.0 behaviour)
Rejected: many networks become indistinguishable.

### All 8 identity colours on overlapping shapes
Rejected: fails colour-vision separation for overlapping marks.

### Keep the parabolas
Rejected: they misrepresent OFDM occupancy and channel width.

## Consequences
- **Benefits:** faster scanning of the list, accurate occupancy, consistent identity across views.
- **Costs:**
  - more view code;
  - a waterfall history buffer of 60 cycles (small);
  - render tests and a feature-parity checklist must prove nothing was lost.

## Exit and rollback criteria
- Success gate: the parity checklist is fully Validated, all tests pass, and the render review shows no clipping or overlap.
- Rollback: the previous views remain in git history (mirror).

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Product/UX | demitri.pevzner | approved (chat 2026-10-16) | 2026-10-16T14:30Z | session transcript |

## Supersession rule
Write a new decision for a change to the colour limit, the palette, or the scope semantics of a selection.
