# Decision DR-0017: Two-way Windows ↔ Android parity (hard project control)

Status: **accepted — hard control (owner, 2026-10-19)**  
Date: 2026-10-19  
Owners: demitri.pevzner (project owner); Claude Code (implementer)  
Builds on: DR-0015 (Android port), `AndroidApp/docs/PARITY_MATRIX.md`, `docs/PARITY_CHECKLIST.md`

## Owner instruction (verbatim)
> "also, moving forward, any change to the windows app trickles down to the Android app with all appropriate considerations for menu placement and Android layout, and similarly with any changes to Android app trickling down to Windows app, this is a hard control/harness for this project"

## Rule
Every change to **either** app is a change to **both** apps. A change is only finished when it is either:
- carried to the other app, adapted to that platform's layout and conventions; or
- recorded as a **platform limit** or an **owner-approved exception**.

**Direction 1, Windows → Android:**
- carried over with Android layout considerations: menus become ⋮ / long-press; side panes become slide-overs on screens smaller than 1360 × 760 dp; tooltips become ⓘ; window sizes become portrait and landscape;
- touch targets at least 48 dp; Back gesture; the permission and throttling states.

**Direction 2, Android → Windows:**
- carried over with Windows layout considerations: slide-overs become side panes or dialogs; long-press becomes right-click or a context menu; ⓘ becomes tooltips plus ⓘ;
- keyboard and mouse; DPI scaling; the WPF HUD theme.

**What counts as a change:** a feature; a behaviour change; a bug fix in shared logic; a report, export or API schema change; a UI change visible to the user, such as labels, flows or defaults; a new setting; or a fix found on one platform that could exist on the other.

**Shared logic stays identical.** Analytics, selection, report and protocol logic live in the C# Application layer and the Kotlin `core` module. A change on one side requires the matching port and **regenerated parity fixtures** (`WirelessScanner.Tests/AndroidParityFixtures.cs`, `Category=AndroidParity`), and the Kotlin `ParityTests` must pass.

**Platform-only work is allowed only as:**
- a **platform limit**: the other OS cannot do it (for example Android scan throttling, or Windows having no slide-over need on a large screen); or
- an **adaptation**: the same capability presented differently (for example the network slide-over on Android and the fixed side pane on Windows).

Either case is recorded in the parity matrix with its reason. *"Not done yet"* is not an exception; it is an open parity row.

## Gate: run for every change, before calling it done
1. **Parity impact line** in the progress log or changelog entry: `Parity: Windows <done | carried as … | limit: … | exception DR/owner …> · Android <…>`.
2. **Matrix row** in `AndroidApp/docs/PARITY_MATRIX.md` added or updated (ID, both implementations, proof, status).
3. **Shared logic** changed → both ports, fixtures regenerated, `dotnet test` and `gradlew :core:test` pass.
4. **UI** changed → a layout note for the other platform (where the control goes and how it scales). Renders are checked on both: a Windows screenshot, and a tablet screenshot in landscape and portrait (portrait needs the owner to rotate the tablet).
5. **Versions** move together (`1.5.x` Windows ↔ `versionName 1.5.x` Android). Both changelogs carry the entry.
6. **Control file:** the iteration lists the tasks for both platforms. Closing a task whose change touched only one app needs a parity row or an exception.

## Applying it now (back-fill, 2026-10-19)
These Android changes since the port need a Windows decision under this rule:

| Android change | Windows action |
| --- | --- |
| Adaptive layout: three panes only at ≥ 1360 × 760 dp; otherwise a selection bar plus a NETWORKS slide-over | **Carry over:** a narrow-window mode for small laptops or snapped windows (selection bar + slide-over explorer) when the window is narrower than the three-pane minimum. Large windows keep the current layout |
| Network slide-over stays open for multi-select; closes on CLOSE ▸ or a tap outside | Applies to the Windows narrow-window mode above (click outside or Esc) |
| Row order pinned while the slide-over is open (no re-sort under the pointer between ticks) | **Evaluate:** Windows re-sorts the explorer on each scan. Pin the order while the pointer is over the list, or show a "resume live sort" control |
| FILTERS dialog with an active count; one-row header with ⓘ | Adaptation (Windows has room for chips); keep the chips. Add the active-filter count to the Windows header |
| Throttling detect / guide / fall back | **Platform limit** (Windows has no app scan budget). Windows reports already state the scan interval; no change |
| Selection bar text ("Selected: n SSIDs (m APs now, follows new APs)") | The text comes from shared logic (`NetworkSelection` describe, the same on both). **Check** that the Windows main window shows it outside the explorer too, as the Android selection bar does |

These Windows changes are already reflected on Android: 1.5.0 (connection test, physical units) per the PARITY_MATRIX.

## Consequences
- More work for each change, but the two apps stop drifting apart: the owner's surveys, reports and API clients behave the same on both.
- Lesson LL-2026-10-19-ANDROID-PORT records this rule. The persistent assistant memory stores it so every future session applies it.
