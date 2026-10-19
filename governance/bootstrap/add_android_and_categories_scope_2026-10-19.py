"""Append findings F30 (Android port, DR-0015) and F31 (SSID categories, DR-0016, deferred) with tasks T27 and T28 (2026-10-19).
Owner: "create an Android APK in the root folder /AndroidApp ... ensure all features one for one are ported over" and
"Add this as a future feature, document it, and build what we have in current state". Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F30" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"

c["findings"].append({
    "id": "F30", "priority": "P2", "status": "open",
    "title": "WirelessScanner only runs on Windows; the owner needs it on an Android tablet using the tablet's own Wi-Fi",
    "observed": "Owner 2026-10-19: create an Android APK in /AndroidApp, all features one for one, reports sendable by email or storable to any installed cloud app; Kotlin + Compose; REST API and responder role on the tablet; sideload.",
    "remediation": "DR-0015: core JVM module (ported analytics with C# parity fixtures) + app module (Wi-Fi platform layer, Compose UI per AndroidApp/docs/UX_SPEC.md, Sharesheet + SAF exports, foreground service, REST server, responder)",
    "evidence": ["EV-1.5-RELEASE"], "task_ids": ["T27"],
    "analysis": {"symptom": "No mobile survey tool", "reproduction": "No Android build exists", "impact": "Surveys need a laptop",
                 "root_cause": "Windows-only platform choice (WPF, Native WLAN API)", "cause_confidence": "high",
                 "contributing_conditions": ["Engine has no Android Wi-Fi API sources (C-10 gap)", "Android scan throttling and missing TSF / frame counters"],
                 "rejected_explanations": ["Cross-platform UI rewrite of Windows (owner chose native Kotlin + Compose)"],
                 "counterexample_or_disconfirmation": "Owner sideload with real scans, probes and exports",
                 "recurrence_signature": "Feature parity claimed without per-row proof", "lesson_ids": ["LL-2026-10-19-ANDROID-PORT"]}})
c["findings"].append({
    "id": "F31", "priority": "P3", "status": "open",
    "title": "The network list cannot group SSIDs by user category (Corporate, Guest, IoT, Neighbours, Hidden)",
    "observed": "Owner 2026-10-19 asked whether SSIDs can be grouped by category in the filter window; then: add as a future feature and document it.",
    "remediation": "DR-0016 (proposed): GROUP BY Category, stored assignments, optional confirmed suggestions, category chip, optional report field; Windows and Android together",
    "evidence": ["EV-1.5-RELEASE"], "task_ids": ["T28"],
    "analysis": {"symptom": "Whole classes of networks cannot be selected or hidden at once", "reproduction": "GROUP BY offers Network and Access point only",
                 "impact": "Slower selection in dense sites", "root_cause": "Not in scope of DR-0010/DR-0013", "cause_confidence": "high",
                 "contributing_conditions": [], "rejected_explanations": [], "counterexample_or_disconfirmation": "A dense-site survey where category selection is not faster than chips and search would argue against building it",
                 "recurrence_signature": "A grouping request answered by a new filter rather than by a shared, persisted model in both apps", "lesson_ids": ["LL-2026-10-19-ANDROID-PORT"]}})

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, fid, acc, acceptance, wps, tests, pos, neg, reg, risk, scope):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": [fid], "status": "planned", "owner": OWNER, "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": scope, "positive_checks": pos, "negative_checks": neg, "regression_checks": reg,
                                  "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": ["EV-1.5-RELEASE"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
c["tasks"] += [
    task("T27", 27, ["T26"], "F30", "Android port of WirelessScanner 1.5.0 (DR-0015)",
         "Every PARITY_MATRIX row built and proven (JVM / fixture / emulator / owner device); exports Share + Save; UX_SPEC acceptance criteria; signed sideload APK",
         [("Design", "DR-0015, UX_SPEC (C-11), PARITY_MATRIX", "Documents reviewed by owner"),
          ("Core", "Kotlin ports of channel math, spectrum layout, selection, units, saturation + connection analytics, reports, responder protocol", "core JVM tests + C# fixtures"),
          ("Platform", "Scanner (throttle detect), link info, probes, storage, foreground service, PDF, Share/Save, REST, responder", "Emulator tests"),
          ("UI", "Compose screens per UX_SPEC (expanded / medium / compact)", "Emulator renders"),
          ("Device", "Owner sideload; diagnostics returned", "Real scans and exports")],
         ["core:test", "AndroidParity fixtures", "emulator renders"], ["Kotlin analytics equal C#", "Share and Save produce files"],
         ["Throttled scans flagged, not shown as fresh", "No real identifiers in fixtures"], ["Windows suite unchanged"],
         R("Platform limits read as parity", "Limits stated in-app and in the matrix", "APK not installed; Windows unaffected", "Owner sideloads", "Owner device check"),
         "AndroidApp core + app; emulator; owner tablet"),
    task("T28", 28, ["T27"], "F31", "GROUP BY Category in both apps (DR-0016) — deferred",
         "Owner answers DR-0016 open questions; then category grouping, assignment, suggestions (confirmed), chip, optional export field, in Windows and Android",
         [("Decide", "Owner answers DR-0016 questions", "DR-0016 approved"), ("Build", "NetworkCategories shared logic + both UIs", "Tests + fixtures"),
          ("Validate", "Parity fixtures Windows/Android; renders of both explorers", "Fixtures equal; renders reviewed")],
         ["NetworkCategoriesTests"], ["Category selection derives scopes"], ["Suggestions never auto-applied"], ["GROUP BY Network default unchanged"],
         R("Scope creep", "Deferred until approved", "Not built", "Owner approval of DR-0016", "Ask owner the DR-0016 questions"),
         "WirelessScanner.Tests + AndroidApp core"),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F30, F31, T27, T28 appended")
