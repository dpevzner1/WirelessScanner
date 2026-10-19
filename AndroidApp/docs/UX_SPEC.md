# WirelessScanner for Android — UI/UX specification

| Field | Value |
| --- | --- |
| Status | Draft for owner review (2026-10-19) |
| Decision | [DR-0015](../../docs/decisions/DR-0015-android-port.md) |
| Engine consultation | C-11 (UX operating rules: define UX before code; Android Compose / Material 3 sources; *Principles of UI/UX Design*; *Design Thinking for UX*) |
| Reference lessons | Breathwork port A-LRN-001, 007, 029, 030, 039, 041, 044, 046, 053 (read-only reference) |
| Goal | **Touch-optimised, but the same functionality and the same layout logic as the Windows app** |

## 1. Users and jobs to be done
| Role | Job | Where the tablet is |
| --- | --- | --- |
| Field surveyor | Walk a site, see every network, pick some, record a roaming capture per zone | In hand, often one-handed, standing |
| Technician | Leave the tablet in a spot for a saturation and/or connection test, check progress, add markers | On a desk or shelf, plugged in |
| IT / client reviewer | Open past tests, read results, **send or save the report** (email, Drive, OneDrive …) | Seated, two hands |
| Integrator | Read live data over the REST API; pair a responder | Settings, occasionally |

## 2. Information architecture: the same as Windows
```
LIVE SURVEY ─ mode bar: MONITOR · CAPTURE · SATURATION TEST · CONNECTION TEST  (+ one-line "what this mode does")
            ├─ Network explorer   (adapter card, search, sort, chips, GROUP BY Network | Access point, tree, selection footer)
            ├─ Centre view        SPECTRUM · WATERFALL · GRID · SATURATION · CONNECTION
            └─ Mode panel         (capture / saturation / connection setup → running → results)
HISTORY     ─ CAPTURE SESSIONS · SATURATION TESTS · CONNECTION TESTS   (list → results → Share / Save)
SETTINGS    ─ REST API · TEST SERVERS · WI-FI SCANNING (throttling guide) · ABOUT & DIAGNOSTICS
```
**Names, order, status words and colours are identical to Windows.** A user who knows one app knows the other.

## 3. Layout by window size (one policy, A-LRN-030)
| Window (width) | Typical device | Layout |
| --- | --- | --- |
| **Expanded ≥ 840 dp** | Tablet landscape | **Same three panes as Windows:** navigation rail · explorer (340 dp) · centre (flexible) · mode panel (360 dp). The panel can collapse to a 48 dp edge handle to give the centre more room |
| **Medium 600–839 dp** | Tablet portrait, split screen | Navigation rail · centre. Explorer = side sheet opened by a **NETWORKS** button (stays open if pinned). Mode panel = bottom sheet: peek, half and full |
| **Compact < 600 dp** | Phone, narrow split | Bottom navigation (Live Survey / History / Settings). Live Survey shows **Networks · View · Panel** as three tabs. A running test shows a persistent status bar |

**Rules:**
- **The layout follows the live window size,** not the device type: rotation, split screen and font scale (A-LRN-007).
- **State is hoisted above the layout branches,** so rotating keeps selection, scroll position, expansion and running tests (A-LRN-039 / 044).
- **A configuration change is not leaving the app:** it never pauses a test.

## 4. Touch adaptation of Windows interactions
| Windows | Android |
| --- | --- |
| Mouse click on small check boxes | **Whole row is the target** (≥ 56 dp rows). The check box is ≥ 48 dp. The caret (expand) is a separate 48 dp target |
| Hover tooltips (evidence, AP details) | ⓘ button and **long-press** open a details sheet. Nothing important is hover-only |
| Right-click menus (rename / split / move AP to unit) | **Long-press** menu, plus a visible **⋮ overflow** button on unit rows |
| Combo boxes | Exposed dropdown menus (48 dp) |
| Chips (2.4 / 5 / 6 GHz, ≥ −70 dBm, Secured, Selected) | Material filter chips, 48 dp hit area, horizontally scrollable on narrow widths |
| Spectrum click toggles an AP | **Tap** selects the nearest block (≥ 48 dp hit slop). Band buttons switch bands. **Drag** scrolls the 5 / 6 GHz axis when zoomed |
| Keyboard text entry (location, markers, API key) | Large single-line fields. Numeric keyboards for coordinates and ports. Markers also offer **quick chips** (door closed, microwave on, people entered, moved) |
| Save dialogs | **Share or send** (Sharesheet: email, chat, Drive, OneDrive …) and **Save a copy** (system file picker, any cloud provider) |
| Status bar text | Top status line + a persistent notification while capture or a test runs |
| Minimum sizes | Touch targets ≥ 48 × 48 dp; body text 14 sp; labels ≥ 12 sp; 8 dp grid spacing |

## 5. Visual design
- **Theme.** Keeps the Windows **HUD theme**: black background, amber/orange accents, the same identity colours (#3987E5 / #D95926 / #199E70) and status colours (#0CA30C good, #FAB219 warning, #EC835A serious, #D03B3B critical).
- **Contrast.** Text contrast ≥ 4.5:1; the orange-on-black pairs are checked.
- **Status is never colour alone:** always a word (EXCELLENT … UNUSABLE, GOOD / DEGRADED / DOWN, CONNECTED), as on Windows.
- **Charts.**
  - The spectrum, waterfall, saturation timelines and connection timelines use the same geometry (`SpectrumLayout` ported) and the same rules: one axis per panel, maximum 3 identity colours.
  - Tapping a chart shows the value under the finger.
- **Font scale.** Up to 200 % reflows text; it is never clipped (tested at 1.0, 1.3 and 2.0).

## 6. Screen and state inventory
| Screen | States that must exist (each designed and tested) |
| --- | --- |
| First launch | Explanation of why location / nearby Wi‑Fi is needed → system prompt → granted / denied (with a "Open settings" path) |
| Live Survey | Scanning · last scan time + FRESH / STALE / THROTTLED · **Wi‑Fi off** (button to Wi‑Fi settings) · **location off** · **no permission** · **throttled** (guide) · no networks · filter shows nothing |
| Explorer | Network / Access point grouping · partial / whole / none selection · locked (test running) · CONNECTED badge · mesh hint · confidence badges |
| Capture panel | Setup (validation) · recording · paused · stopped → export |
| Saturation panel / stage | Setup (validation, effective-rate notice when throttled) · running (tiles, timeline, warnings) · paused · results |
| Connection panel / stage | Setup (connection card; not connected) · running (path tiles, timelines, event feed) · speed / two-way running · results |
| History | Empty · list · loading results · results · delete confirmation |
| Export | Choose format (PDF / CSV bundle / JSON) → Share or Save → cancelled · no app can handle · done (path / app named) |
| Settings | REST API (off / running / error), Test servers (none / saved / check OK / failed), Wi‑Fi scanning guide (throttling on / off detected), Diagnostics export |
| Long test | Foreground notification with progress + Stop · battery warning · resumes view after the app is reopened |

## 7. Feedback, errors, loading
- **Every action gets visible feedback within 100 ms:**
  - pressed state;
  - snackbars for completed exports;
  - inline errors next to the field that caused them. These reuse the Windows validation texts.
- **Long operations show progress and can be cancelled:** scans, PDF generation, speed tests.
- **No silent failures.** A refused scan shows **THROTTLED** with the reason and the next allowed time.

## 8. Accessibility
- **TalkBack.** Every control has a content description equal to the Windows `AutomationProperties.Name`. Tiles, badges and chart summaries have semantic text.
- **Focus order** follows the visual order. Grouped rows read as one item with state ("PlantOps, 4 access points, whole network selected").
- **Motion.** Respects *Remove animations*. Nothing depends on animation.

## 9. Usability validation
1. **Emulator renders** at compact, medium and expanded sizes, in portrait and landscape, at font scales 1.0, 1.3 and 2.0, for every screen state above. These are reviewed like the Windows renders.
2. **Automated checks:**
   - Compose UI tests for the main task flows;
   - touch-target size checks (≥ 48 dp);
   - content-description presence.
3. **Owner sideload checklist** (field use): one-handed explorer selection while walking; leaving a test running with the screen off; sharing a PDF to email and saving to the owner's cloud app.

## 10. UX acceptance criteria
- **Parity.** Every Windows control and state in `PARITY_MATRIX.md` exists on Android with the same name and meaning.
- **Touch.** No touch target under 48 dp. No information available only on hover or long-press: each also has a visible ⓘ or ⋮.
- **Rotation and resizing.** Rotation and split screen never lose selection, scroll position, form input or a running test.
- **Export.** PDF / CSV / JSON can be **sent** (email or any share target) and **saved** (any installed document provider). Cancel paths are handled.
- **Status.** Every status has a word, not only a colour. All screens are usable with TalkBack and at 200 % font scale.
