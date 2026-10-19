# Decision DR-0016: Group SSIDs by category (future feature)

Status: **proposed — deferred to a future release (owner, 2026-10-19: "Add this as a future feature, document it")**  
Date: 2026-10-19  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Builds on: DR-0010 (explorer), DR-0011 (shared selection, scopes), DR-0013 (GROUP BY Access point), DR-0015 (Android port)

## Context
The owner asked whether the network list (filter window) can group SSIDs **by category**. Neither app can do this today; both offer only:
- **GROUP BY:** *Network* (one group per SSID) or *Access point* (one group per physical unit);
- **chips:** 2.4 / 5 / 6 GHz, ≥ −70 dBm, Secured, Selected;
- **search and sort.**

Typical categories are *Corporate*, *Guest*, *IoT*, *Neighbours* and *Hidden*. They let a surveyor select or ignore whole classes of networks at once.

## Proposed design (to be confirmed before implementation)
1. **A third GROUP BY option: *Category*.** Groups are categories and their children are SSIDs, with the same tri-state selection. Ticking a category selects every network in it for a capture or test. Uncategorised SSIDs go in **Uncategorised**.
2. **Assigning categories:**
   - right-click on Windows, or long-press / ⋮ on Android, then **Set category…**;
   - several SSIDs can be assigned at once.

   Assignments are stored (keyed by SSID; hidden networks by BSSID), so they stay assigned on every scan and in history.
3. **Optional suggestion rules** (never applied silently; the user confirms each suggestion):
   - open → *Guest / open*;
   - hidden → *Hidden*;
   - an SSID pattern such as `*-IoT` → *IoT*;
   - an SSID heard only below a signal threshold and never selected → *Neighbours*.
4. **Filter chip.** A *Category* chip (multi-select) filters the list.
5. **Reports (optional).** A `category` field in CSV / JSON and a category column in the PDF target table. This would be an additive schema change.
6. **Parity.** Built once in shared logic: a `NetworkCategories` class in Application, ported to Android `core`. Both apps ship it in the same release.

## Open questions for the owner
1. Fixed list, user-defined names, or an editable starter list?
2. Suggestion rules or manual assignment only?
3. Categories in reports, or only in the network list?
4. Should categories sync between the Windows and Android apps (for example by export and import of a small JSON file)?

## Consequences
- **No change to current behaviour until it is approved and built.** *Network* stays the default grouping.
- **Selection rules are unchanged.** A category maps to a set of SSIDs, and existing scope derivation (`NetworkSelection`) applies.
- **Tests at implementation:**
  - assignment persistence;
  - tri-state category selection;
  - a rule suggestion never auto-applied;
  - export fields;
  - Windows/Android fixture parity.

## Tracking
- Control file: finding **F31**, task **T28** (status `planned`, deferred).
- `docs/STATUS.md` backlog.
- Android parity matrix row **F1** (future).
