"""Append redesign findings F18-F20 and tasks T13-T17 to the control (2026-10-16). Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F18" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"

def A(symptom, repro, impact, cause, conf, sig, lesson="LL-2026-10-16-LIVE-SURVEY-REDESIGN"):
    return {"symptom": symptom, "reproduction": repro, "impact": impact, "root_cause": cause, "cause_confidence": conf,
            "contributing_conditions": ["Two tabs evolved separately; no shared acquisition service"],
            "rejected_explanations": [], "counterexample_or_disconfirmation": "A test showing concurrent callers both receive Fresh cycles from the unmodified adapter would disprove F18",
            "recurrence_signature": sig, "lesson_ids": [lesson]}

c["findings"] += [
    {"id": "F18", "priority": "P1", "title": "Concurrent ScanAsync callers clash on the shared adapter (pending-scan slot overwritten)", "status": "open",
     "observed": "Live loop, saturation runner and DISCOVER call ScanAsync on one WlanAdapter singleton; _pendingScans[interface] = tcs overwrites an in-flight waiter",
     "remediation": "ScanScheduler single-flight loop shared by all consumers + adapter-level coalescing (DR-0011)", "evidence": ["EV-DESIGN-REDESIGN"], "task_ids": ["T13"],
     "analysis": A("First caller times out -> STALE cycle while another caller scans", "Run a saturation test while Live Survey is scanning in the app (code reading: WlanAdapter.cs:268)", "Lost fresh cycles; biased availability", "No single owner of acquisition", "high", "Multiple independent pollers of one device")},
    {"id": "F19", "priority": "P2", "title": "Network selection UX duplicated and outdated (flat list, mode switch, separate saturation selection)", "status": "open",
     "observed": "Owner: selection 'looks somewhat outdated'; separate saturation scope menu 'too confusing'",
     "remediation": "Grouped network explorer + NetworkSelection shared by live view, capture and saturation (DR-0010, DR-0011)", "evidence": ["EV-DESIGN-REDESIGN"], "task_ids": ["T14", "T16"],
     "analysis": A("Two selection UIs with different rules", "Compare Live Survey list and Saturation candidate table", "Confusion, double work", "Features added as separate tabs", "high", "Parallel UIs for one concept")},
    {"id": "F20", "priority": "P2", "title": "Spectrum is monotone and geometrically inaccurate", "status": "open",
     "observed": "Owner: 'a little monotone'; tier-only colours, parabolic shapes, colliding labels",
     "remediation": "Focus+context identity colours (validated, max 3), width-true masks, bands, load strip, waterfall (DR-0010)", "evidence": ["EV-DESIGN-REDESIGN"], "task_ids": ["T15"],
     "analysis": A("Networks indistinguishable; widths misrepresented", "Screenshot2.jpg", "Misreading of congestion and identity", "Colour encodes tier instead of identity", "high", "Colour used for the wrong data job")},
]

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, fids, acc, acceptance, wps, tests, pos, neg, reg, risk):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": fids, "status": "planned", "owner": OWNER, "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": "WirelessScanner.Tests + UI render review", "positive_checks": pos, "negative_checks": neg, "regression_checks": reg,
                                  "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": ["EV-BL-AUDIT", "EV-FINAL-TEST"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
c["tasks"] += [
    task("T13", 13, ["T03"], ["F18"], "Single-flight shared scan scheduler", "One in-flight scan per adapter; all consumers served; reproduction test fails on old behaviour",
         [("Scheduler", "IScanScheduler/ScanScheduler with subscribe, ScanNow, cadence = min requested interval", "Unit tests pass"),
          ("Adapter coalescing", "WlanAdapter.ScanAsync coalesces concurrent calls per interface", "Build + hardware probe"),
          ("Consumers", "Live VM and saturation runner use the scheduler; runner decimates to its interval", "Tests pass")],
         ["ScanSchedulerTests", "SaturationRunnerSchedulerTests"], ["Concurrent requests share one provider call"], ["Old behaviour reproduction: overlapping waiters time out"], ["All 108 prior tests"],
         R("Event threading bugs", "UI marshalling via dispatcher hook", "Revert consumers to direct ScanAsync", "Tests green", "T14")),
    task("T14", 14, ["T13"], ["F19"], "Network explorer + NetworkSelection", "Grouped tree, tri-state groups, follow-new SSIDs, filters/search/sort, sparklines, identity swatches, scope text; legacy API bridged",
         [("Selection model", "NetworkSelection (SSIDs + BSSIDs, lock, scope derivation, focus colours)", "NetworkSelectionTests"),
          ("Explorer VM", "Group/AP items, reconcile, filters, sort, sparkline history", "NetworkExplorerTests"),
          ("Explorer view", "NetworkExplorerView XAML + HUD theme dictionary", "UI render")],
         ["NetworkSelectionTests", "NetworkExplorerTests"], ["Group selection follows new BSSIDs"], ["Hidden SSID group not selectable; locked selection rejects edits"], ["Legacy filter tests"],
         R("State loss on refresh", "Keyed reconcile", "Revert view", "Tests green", "T15")),
    task("T15", 15, ["T14"], ["F20"], "Spectrum redesign + waterfall", "Width-true masks, focus colours (max 3, auto-focus), bands, DFS, load strip + least busy, All/2.4/5/6, Spectrum/Waterfall/Grid, tooltips, Label all",
         [("Layout math", "SpectrumLayout pure functions (frequency, width, load, least busy, waterfall bins)", "SpectrumLayoutTests"),
          ("Renderer", "SpectrumView control drawing masks, strip, waterfall", "UI render"),
          ("Waterfall history", "60-cycle ring buffer in VM", "Tests")],
         ["SpectrumLayoutTests"], ["80 MHz mask spans 16 channel numbers"], ["No 6 GHz APs -> message, no crash"], ["Telemetry grid columns unchanged"],
         R("Render performance", "Simple shapes; redraw per cycle", "Revert renderer", "Render review", "T16")),
    task("T16", 16, ["T13", "T14", "T15"], ["F19"], "Saturation merged into Live Survey; mixed scope; history merge", "Mode bar, drawers, Test/Results views, selection-derived scope incl. Mixed, lock during test, saturation history in HISTORY",
         [("Mixed scope", "Tracker/analyzer support explicit BSSIDs + SSIDs", "SaturationMixedScopeTests"),
          ("VM integration", "SaturationTestViewModel attached to explorer selection + shared adapter", "VM tests"),
          ("Views", "Mode bar, capture drawer, saturation drawer, Test/Results, history sub-tab; remove tab", "UI render")],
         ["SaturationMixedScopeTests", "SaturationViewModelTests"], ["Selection maps to scopes per DR-0011"], ["Empty selection requires confirmation"], ["All saturation tests"],
         R("Feature loss", "Parity checklist", "Restore tab from git", "Parity all Validated", "T17")),
    task("T17", 17, ["T16"], ["F18", "F19", "F20"], "Parity verification and documentation", "PARITY_CHECKLIST all Validated; docs/STATUS/CHANGELOG/USER_GUIDE/TECHNICAL_REFERENCE updated; sync clean",
         [("Parity", "Fill checklist with test/render evidence", "No pending rows"), ("Docs", "Update guides and status", "Doc audit"), ("Trail", "Iterations, lesson, sealed ledger, sync", "Validators pass")],
         ["PARITY", "DOC-AUDIT"], ["All rows Validated"], ["Any lost capability -> finding + fix"], ["Full suite"],
         R("Docs drift", "Doc audit", "n/a", "Owner review", "Owner review")),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F18-F20, T13-T17 appended")
