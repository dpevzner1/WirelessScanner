# Decision DR-0005: Saturation test UX — dedicated tab with a three-step flow (Setup → Running → Results)

Status: approved (implementation tracked as T06)  
Date: 2026-10-15  
Owners: demitri.pevzner; Claude Code (implementer)  
Supersedes: none

## Context
- The owner asked for a "highly comprehensive, user friendly" saturation test whose UI/UX uses the DevOps engine.
- Engine consultation C-04 requires, before code exists:
  - user roles, jobs to be done, task flows and information architecture;
  - a screen and state inventory;
  - accessibility and feedback;
  - error, empty and loading states;
  - usability validation and UX acceptance criteria.
- C-05 contributes Fitts's law, Hick's law, Gestalt grouping and visible system status.

## Decision
- A new top-level tab, **SATURATION TEST**, implemented as a separate `SaturationTestView` UserControl with its own `SaturationTestViewModel`. This avoids growing the 1,746-line `MainWindowViewModel`.
- **Three steps:**
  1. Setup: What (scope and targets) / How long (duration and interval) / Where (location and notes), with *Advanced* thresholds collapsed;
  2. Running;
  3. Results.

  A history list lets past tests be reopened.
- The screen and state inventory, validation messages, accessibility rules and acceptance criteria are exactly as in `docs/SATURATION_TEST_SPEC.md` §7 and §10.
- The visual language reuses the app's amber-on-obsidian HUD resources, for consistency.

## Evidence considered

| Evidence | Version/snapshot | Observation | Scope limitation |
| --- | --- | --- | --- |
| Engine C-04, C-05, C-06 | 2026-10-15 | UX expectations and principles | The principles source has degraded OCR |
| Existing `MainWindow.xaml` theme and tab structure | BL-2026-10-15 | Amber HUD palette, three tabs | — |

## Alternatives

### Add saturation options to the existing "Stationary" session dialog
Rejected:
- the dialog has no room for target selection, duration or thresholds;
- results need their own screen.

### A separate window
Rejected: it fragments navigation, while tabs are the app's established pattern.

## Consequences
- Benefits: a focused flow, validation that prevents errors, readability from a distance, and isolated code.
- Costs:
  - another tab to maintain;
  - usability validation with real users is still **Blocked** until the owner arranges a session.

## Exit and rollback criteria
- Success gate: ViewModel tests cover the validation states (SAT-AC-07). The owner's walkthrough has no blocking issues.
- Rollback: the tab is self-contained and can be hidden without affecting other features.

## Approval

| Role | Approver | Decision | UTC timestamp | Evidence reference |
| --- | --- | --- | --- | --- |
| Product/UX | demitri.pevzner | approved via "begin" (chat 2026-10-15) | 2026-10-15 | session transcript |

## Supersession rule
Write a new decision for a navigation model change (e.g. a WinUI 3 migration) or after usability findings that require a different flow.
