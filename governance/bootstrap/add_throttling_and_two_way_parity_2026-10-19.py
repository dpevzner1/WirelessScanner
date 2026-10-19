"""Append findings F33 (Android scan throttling platform limit + cadence defect) and F34 (two-way Windows <-> Android parity,
hard control, DR-0017) with tasks T30 and T31 (2026-10-19). Owner: "document this limit thoroughly for future projects" and
"any change to the windows app trickles down to the Android app ... and similarly ... this is a hard control/harness".
Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F33" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"


def analysis(symptom, repro, impact, cause, contrib, rejected, counter, sig, lessons):
    return {"symptom": symptom, "reproduction": repro, "impact": impact, "root_cause": cause, "cause_confidence": "high",
            "contributing_conditions": contrib, "rejected_explanations": rejected, "counterexample_or_disconfirmation": counter,
            "recurrence_signature": sig, "lesson_ids": lessons}


c["findings"] += [
    {"id": "F33", "priority": "P2", "status": "open",
     "title": "Android throttles app Wi-Fi scans (4 per 2 min); most live cycles are STALE and the app still requests every 5 s",
     "observed": "2026-10-19 tablet SM-X200 Android 14: dumpsys wifi wifi_scan_throttle_enabled=true, 114 foreground requests throttled; "
                 "Settings.Global key null; logcat shows a request every 5 s while throttled. Owner asked whether code, permissions or install can fix it.",
     "remediation": "Platform limit documented (AndroidApp/docs/WIFI_SCAN_THROTTLING.md); owner developer option; app: pace to 30 s, per-AP age, "
                    "use system scan results, connected-link sampling, one-line banner",
     "evidence": ["EV-ANDROID-THROTTLE-PROBE-1"], "task_ids": ["T30"],
     "analysis": analysis("STALE (THROTTLED) on most cycles", "Default tablet, live view", "Lower survey cadence than Windows (30 s vs 5 s)",
                          "Android platform scan budget; exemptions are signature/privileged permissions only",
                          ["Factory default on", "Setting moved out of Settings.Global on Android 11+", "Device gate did not check request cadence"],
                          ["A missing manifest permission", "Install-time grant", "WRITE_SECURE_SETTINGS + Settings.Global"],
                          "With the developer option off, FRESH every 5 s on the device",
                          "Survey cadence promised without the developer-option caveat", ["LL-2026-10-19-ANDROID-SCAN-THROTTLING"])},
    {"id": "F34", "priority": "P1", "status": "open",
     "title": "Changes can land in one app (Windows or Android) without reaching the other; owner made two-way parity a hard control",
     "observed": "2026-10-19: Android-only changes (adaptive layout, network slide-over, multi-select, pinned row order) have no Windows counterpart yet. "
                 "Owner: this is a hard control/harness for this project.",
     "remediation": "DR-0017; PROJECT_CONVENTIONS section 10 gate (parity line, matrix row, shared-logic fixtures, layout placement, versions, "
                    "control iteration); back-fill table in DR-0017",
     "evidence": ["EV-ANDROID-CORE-PARITY-1"], "task_ids": ["T31"],
     "analysis": analysis("Platform drift", "Compare the DR-0017 back-fill table with the Windows UI", "Owner surveys behave differently per platform",
                          "No rule required a change to be carried to the other platform", ["Android UI work done in a fast device loop"], [],
                          "A change closed with a parity line and matrix row for both platforms",
                          "A change closed on one platform with no parity line", ["LL-2026-10-19-ANDROID-PORT"])},
]


def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}


def task(tid, seq, deps, fid, acc, acceptance, wps, tests, pos, neg, reg, risk, scope):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": [fid], "status": "planned", "owner": OWNER,
            "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": scope, "positive_checks": pos, "negative_checks": neg,
                                  "regression_checks": reg, "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"],
                                  "baseline_evidence_ids": ["EV-1.5-RELEASE"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}


def R(risk, cont, rb, resume, nxt):
    return {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}


c["tasks"] += [
    task("T30", 30, ["T27"], "F33", "Live within Android scan throttling (detect, guide, fall back) and pace requests correctly",
         "Throttle probe in the smoke test; <= 1 refused request per 2 min while throttled; per-AP age; system scan results used; "
         "one-line banner; FRESH every 5 s with throttling off",
         [("Owner switch", "Owner turns off Developer options > Wi-Fi scan throttling; re-run the probe", "Probe shows false; FRESH every 5 s"),
          ("Cadence fix", "Find the cadence holder that keeps 5 s; re-apply 30 s on throttle state", "logcat throttled count <= 1 per 2 min"),
          ("Per-AP age", "ScanResult.timestamp age on rows and exports", "Rows show age"),
          ("Free results", "Process system-initiated results; connected-link sampling for tests", "Device log + test report"),
          ("Banner", "One-line throttle banner with info", "Screenshots landscape + portrait")],
         ["device throttle probe", "logcat cadence check"], ["FRESH with throttling off"], ["Cached results never labelled FRESH"],
         ["Windows unaffected (platform limit)"],
         R("Cadence change hides fresh results", "STALE labelling unchanged", "Revert build", "Owner device session", "T30-WP2"),
         "AndroidApp app + owner tablet"),
    task("T31", 31, ["T27"], "F34", "Two-way Windows <-> Android parity as a hard control (DR-0017)",
         "Conventions section 10 applied to every change; back-fill table items carried or recorded as limits/exceptions",
         [("Control", "DR-0017 + PROJECT_CONVENTIONS section 10 + memory rule", "Documents in place"),
          ("Back-fill", "Windows narrow-window mode with selection bar + slide-over explorer; pinned order while pointing; active-filter count",
           "Windows renders + tests"),
          ("Audit", "Every change since 1.5.0 has a parity line and matrix row", "PARITY_MATRIX reviewed")],
         ["dotnet test", "gradlew :core:test", "AndroidParity fixtures"], ["Change appears on both platforms"],
         ["One-platform change without parity line rejected at closure"], ["Existing parity fixtures pass"],
         R("Twice the work per change", "Shared logic first; platform adaptations recorded", "None (process)", "Next change to either app",
           "T31-WP2 back-fill"),
         "Both apps"),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F33, F34, T30, T31 appended")
