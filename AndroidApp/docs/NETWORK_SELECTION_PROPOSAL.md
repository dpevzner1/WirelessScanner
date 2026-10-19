# Proposal: network selection on the Android tablet

| Field | Value |
| --- | --- |
| Status | **Proposal — owner to choose** (2026-10-19) |
| Trigger | Owner: "the wireless network selection is extremely compressed beyond reasonable use" |
| Consultation | C-12 (engine: generic hits only; no list-detail, progressive-disclosure, Fitts or Hick concepts; C-05 page pointers and UX operating rules reused) |

## 1. Assessment (measured on the tablet, build b2)
- **Screen size:** 1920 × 1200 px at density 240, which is **1280 × 800 dp in landscape** and **800 × 1280 dp in portrait**.
- **Landscape space today:** the navigation rail takes 80 dp, which leaves 1200 dp for three columns: the network list (330 dp), the view (≈530 dp) and the mode panel (340 dp).
- **Header:** about 170 dp of height (status line, mode bar, description, last scan).
- **Network list controls:** search, chips, GROUP BY and sort take another ≈ 300 dp.
- **Result: about 3 network rows are visible**, and 2 when a banner is shown. The view's tabs are cut off ("SATURATIC"), and hints wrap.
- **Root cause:** UX_SPEC treats any window ≥ 840 dp as "expanded, three panes". At 1280 × 800 dp the *height* and the *third column* are what break it. One width breakpoint is not enough for this tablet.

## 2. Options
| | Option | How it works | Back and forth |
| --- | --- | --- | --- |
| **A** | **Dedicated NETWORKS screen** | A full screen for selection. Live Survey shows a one-line *selection bar* ("2 networks · 5 APs selected — CHANGE"). | One tap on CHANGE, then Back or DONE. Selection applies live |
| **B** | **Full-height slide-over panel** | A NETWORKS button (or edge swipe) slides the list over the view: 60 % of the width in landscape, full width in portrait. The spectrum stays visible beside it in landscape | One tap, or a swipe. Selected blocks light up in the visible spectrum as you tick |
| **C** | **Two panes plus a pane switcher** | Never three columns on this tablet. The left pane switches between NETWORKS and the TEST PANEL; the right pane is the view. A draggable divider lets you widen the list | One tap on the left-pane switcher; both stay live |
| **D** | **Bottom sheet** | The list lives in a sheet with peek, half and full heights | Drag up or down |
| **E** | **Declutter only** | Keep three panes but move the filters into a FILTERS dialog and hide the mode panel by default | No change |

**Changes common to every option** (recommended regardless of choice):
- **One-row header:** mode bar, status and the FRESH badge on one line. The mode description moves into an ⓘ. Saves about 90 dp.
- **Filters collapse into one row:** search, then a FILTERS button with a count badge (bands, signal, secured, selected, sort, group by) that opens a sheet. Saves about 200 dp.
- **Orientation-aware breakpoints:** use width *and* height. Below 900 dp of height, never show three columns.
- **Rows:** 64 dp with two lines. The whole row toggles; there is a separate caret.
- **View tabs** scroll horizontally, so labels are never cut. Hints sit under the tabs, not beside them.
- **Visual review:** every screen is checked at 1280 × 800 and 800 × 1280 dp with font scale 1.0 and 1.3 (screenshots go to the local-only folder).

## 3. Grading (weighted; scores 1–5 are design estimates, to be checked with renders)
| Criterion (weight) | A | B | C | D | E |
| --- | --- | --- | --- | --- | --- |
| Network rows visible, landscape / portrait (25) | 5 (≈9 / ≈17) | 5 (≈9 / ≈17) | 4 (≈8 / ≈15) | 3 (≈4 half, 9 full) | 2 (≈5 / ≈10) |
| Live context while selecting: see the spectrum react (20) | 2 | 5 | 5 | 3 | 4 |
| Back-and-forth cost (taps) (15) | 4 | 5 | 5 | 4 | 5 |
| Parity with the Windows layout (15) | 2 | 4 | 4 | 2 | 5 |
| Portrait and landscape scaling (15) | 5 | 5 | 4 | 4 | 2 |
| Implementation risk (10) | 5 | 4 | 3 | 3 | 5 |
| **Weighted score (out of 100)** | **77** | **97** | **87** | **64** | **70** |

## 4. Recommendation
**B (slide-over network panel) plus the common changes.** C's pane switcher is used only for the test panel.

**Landscape:**
- The view gets the full width by default.
- NETWORKS slides over 60 % of the screen, and the spectrum stays live beside it while you tick.
- The test panel opens as its own right-hand panel. The network list and the test panel are never open together.

**Portrait:**
- NETWORKS slides over the full width, with a pinned "LIVE VIEW ▸" handle that shows a mini spectrum strip so you still see the effect of your choices.
- The test panel is a bottom sheet (half or full height).

**Every layout:** a selection bar is always visible ("Nothing selected — everything is shown · NETWORKS"). One tap opens the list, and one tap or a swipe closes it. State is kept across rotation.

## 5. Decisions for the owner
1. Option **B**, **A**, **C**, or a mix (for example, B in landscape and A in portrait)?
2. Should the network list stay **pinned open** as a setting for users who want the Windows-style side list in landscape?
3. Filters in a **sheet**, or a **second chip row** that scrolls?
