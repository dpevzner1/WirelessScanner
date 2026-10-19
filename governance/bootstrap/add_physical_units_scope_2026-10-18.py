"""Append findings F27-F28 and tasks T22-T23 to the control (2026-10-18):
F27 = REST API settings screen defects from the owner's live use of 1.3.0 (fixed in 1.3.1, never installed, shipped in 1.4.0);
F28 = no way to select or test by physical access point (owner request; DR-0013 MVP in 1.4.0). Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F27" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"
L13 = "LL-2026-10-16-RELEASE-REPORTS"
L14 = "LL-2026-10-18-PHYSICAL-UNITS"

def A(symptom, repro, impact, cause, conf, sig, contributing, disconfirm, lesson):
    return {"symptom": symptom, "reproduction": repro, "impact": impact, "root_cause": cause, "cause_confidence": conf,
            "contributing_conditions": contributing, "rejected_explanations": [], "counterexample_or_disconfirmation": disconfirm,
            "recurrence_signature": sig, "lesson_ids": [lesson]}

def F(fid, prio, title, observed, remediation, tasks, analysis):
    return {"id": fid, "priority": prio, "title": title, "status": "open", "observed": observed, "remediation": remediation,
            "evidence": ["EV-1.3-TEST"], "task_ids": tasks, "analysis": analysis}

c["findings"] += [
    F("F27", "P2", "REST API settings screen misreported state and export appeared to do nothing (live use of 1.3.0)",
      "Owner screenshot 2026-10-17: status 'Stopped' while the API answered 12/12 live checks; EXPORT wrote silently to a ProgramData file readable by every user and APPLY overwrote the only message; full key visible on screen",
      "IRestApiServer.StateChanged + live status text; EXPORT via Save dialog to the user's Documents with inline result and OPEN FOLDER; APPLY no longer writes the key; DELETE removes the legacy file; key masked with SHOW/COPY (1.3.1)", ["T22"],
      A("Owner believed the API was disabled and export broken", "ApiSettingsScreenTests (status follows server events; export result shown; apply writes no file)",
        "Feature looked broken; key exposure risk (shared folder, screenshot)", "Status label read once at start while the server starts in the background; export result only in a status bar that the next action overwrote", "high",
        "UI state read once instead of bound to an event; results reported in a shared transient channel", ["Server start is asynchronous at launch"],
        "A 1.3.1 screen showing RUNNING within one server event of start", L13)),
    F("F28", "P2", "No way to select or test by physical access point (all SSIDs of one router; mesh nodes)",
      "Owner 2026-10-18: selection is by SSID or single BSSID only; Windows reports no device identity; live inventory of 20 BSSIDs showed one router with 2 radios and 4 SSIDs, and LA-bit / octet-4 MAC schemes",
      "DR-0013 stage-1 MVP: TSF same-radio + WPS UUID + MAC-family inference with confidence and evidence, user overrides, unit selection and scopes, explorer hardware view, SAT-ANALYTICS-3 unit summaries, report schema v3", ["T23"],
      A("Single-AP-all-SSIDs and mesh tests impossible; SSID-level results mix different devices", "HardwareIeInventoryProbe (owner environment) + PhysicalUnitTests fixtures",
        "Owner cannot diagnose one device or a mesh system", "Data model had no device concept; OS API exposes BSSIDs only", "high",
        "Domain modelled on what the OS reports rather than on the physical system the user manages", ["No standard element (Multiple-BSSID, EasyMesh) present on the owner's APs"],
        "Owner's router grouped as one unit with 2 radios and 4 SSIDs; no false merge across OUIs", L14)),
]

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, fids, acc, acceptance, wps, tests, pos, neg, reg, risk, baseline):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": fids, "status": "planned", "owner": OWNER, "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": "WirelessScanner.Tests + live install review", "positive_checks": pos, "negative_checks": neg, "regression_checks": reg,
                                  "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": baseline},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
c["tasks"] += [
    task("T22", 22, ["T07"], ["F27"], "REST API settings screen tells the truth and exports safely", "Status follows the server; export to the user's Documents with visible result; key masked; apply writes no file",
         [("Status", "StateChanged event + UpdateApiServerStatusText", "ApiSettingsScreenTests"), ("Export/key", "Save dialog hook, OPEN FOLDER, masked key, DELETE cleanup", "ApiSettingsScreenTests; no headless dialog"),
          ("Live check", "Owner installs 1.4.0 (contains 1.3.1), regenerates the key; api_smoke check/watch", "Status RUNNING; export file in Documents; 12/12 API checks")],
         ["ApiSettingsScreenTests"], ["RUNNING shown when the server runs"], ["Apply writes no key file; no dialog without an app"], ["ApiTests"],
         R("Key exposure", "Masked display; private Documents path", "Revert to 1.3.0 screen", "Owner installs 1.4.0", "Live API check"), ["EV-1.3-TEST"]),
    task("T23", 23, ["T14", "T21"], ["F28"], "Select and test by physical access point (DR-0013 MVP)",
         "Owner's router = one unit (2 radios, 4 SSIDs); no false merge across OUIs; every grouping shows confidence + evidence; corrections persist and win; unit tests report per-unit metrics; nothing existing lost",
         [("Capture", "TSF offset + WPS parse on every BSS entry", "PhysicalUnitTests parsers"), ("Inference", "PhysicalUnitGrouper + overrides persisted in Settings", "PhysicalUnitTests"),
          ("Selection/UI", "NetworkSelection units, SingleUnit/MultipleUnits, explorer Group by Access point, context-menu corrections", "PhysicalUnitIntegrationTests + render review"),
          ("Analytics/reports", "SAT-ANALYTICS-3 unit summaries; schema v3; _units.csv; PDF 6b", "PhysicalUnitIntegrationTests exports"),
          ("Docs/release", "README, USER_GUIDE, TECHNICAL_REFERENCE, SPEC, STATUS, CHANGELOG, DR-0013; release 1.4.0", "Doc audit; release ledger sealed"),
          ("Live check", "Owner groups own environment in the installed build", "Router shown as one unit; owner confirms")],
         ["PhysicalUnitTests", "PhysicalUnitIntegrationTests", "LiveSurvey_AllModesAndViews_LoadAndRender"],
         ["Router fixtures group correctly", "Unit test config fixed at start"], ["Coincident TSF outvoted; different bands never clock-linked; different WPS UUIDs never merge"],
         ["Full suite incl. parity tests"], R("False merges mislead unit results", "Confidence + evidence everywhere; consistency finding flags misgrouping; manual override", "Group by Network unchanged",
         "Owner installs 1.4.0", "Owner live review"), ["EV-1.3-TEST"]),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F27-F28, T22-T23 appended")
