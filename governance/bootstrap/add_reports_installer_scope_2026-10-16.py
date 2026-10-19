"""Append findings F21-F26 and tasks T18-T21 (live-test fix, installer detection, icon, report precision) to the control
(2026-10-16). Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F21" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"
LESSON = "LL-2026-10-16-RELEASE-REPORTS"

def A(symptom, repro, impact, cause, conf, sig, contributing, disconfirm):
    return {"symptom": symptom, "reproduction": repro, "impact": impact, "root_cause": cause, "cause_confidence": conf,
            "contributing_conditions": contributing, "rejected_explanations": [], "counterexample_or_disconfirmation": disconfirm,
            "recurrence_signature": sig, "lesson_ids": [LESSON]}

def F(fid, prio, title, observed, remediation, tasks, analysis):
    return {"id": fid, "priority": prio, "title": title, "status": "open", "observed": observed, "remediation": remediation,
            "evidence": ["EV-RV-REDESIGN-2"], "task_ids": tasks, "analysis": analysis}

c["findings"] += [
    F("F21", "P1", "Legacy display list echoed whole-SSID selections back as fixed BSSIDs (scope recorded as Mixed)",
      "Live 1.2.0 saturation test: config held 3 whole SSIDs AND their 3 BSSIDs (hash comparison of the stored config)",
      "Mirror selection into the legacy list without firing its change callback; no-op guard in the bridge", ["T18"],
      A("Scope Mixed instead of MultipleSsids; unticking a network could not clear it", "WholeSsidSelection_SurvivesScans_WithoutLegacyListEchoingBssids (failed before the fix)",
        "Wrong scope label; selection could not be cleared", "UpdateDisplayAccessPoints set IsChecked, whose setter invokes the user-check callback", "high",
        "UI state mirroring through a setter that has side effects", ["Legacy API kept as a bridge (DR-0010)"], "A scan after ticking a group leaving Selection.Bssids empty with the old code")),
    F("F22", "P2", "Setup detected an existing install only from its own registry key",
      "DetectState read HKLM\\SOFTWARE\\WirelessScanner only; the Apps & features entry and the 32-bit view were ignored; no repair hint for missing files",
      "Setup.Core.InstallDetector over app key + Uninstall entry in both views, cross-checked with the install manifest and the exe; notes on the maintenance page", ["T19"],
      A("Update/repair not offered when only the Apps & features entry exists", "InstallDetectorTests.OnlyAppsAndFeaturesEntry_IsStillDetected", "Fresh install over an existing one; duplicates",
        "Single detection source", "medium", "One registry source of truth for a two-key registration", ["1.0 installer wrote different keys"], "A machine where the app key alone is missing still detected by 1.2 setup")),
    F("F23", "P2", "App icon absent (generic icon in Apps & features, shortcuts, taskbar); setup carried an unrelated 'NEXUS Terminal' icon",
      "Presentation had no ApplicationIcon; DisplayIcon and shortcuts point at the exe icon; setup icon.ico is another project's logo",
      "Original icon + make_icons pipeline (Windows + Android sets); setup icon replaced; app embedding ON HOLD - Malwarebytes ML quarantines the icon-embedded build", ["T20"],
      A("Generic icon everywhere", "Icon.ExtractAssociatedIcon on the 1.2 exe; view of WirelessScanner.Setup/icon.ico", "Unprofessional, unrecognisable app",
        "No branding asset ever created; setup icon copied from another project", "high", "Assets copied between projects without review",
        ["Malwarebytes MachineLearning/Anomalous.93% quarantines the icon-embedded DLL (9 detections)"], "An icon-embedded build surviving real-time protection")),
    F("F24", "P1", "Saturation reports imprecise/incomplete for diagnostics and performance tracking",
      "Live 1.2.0 report: active 296.7 s of 300; longest-below 301 s > test; trend +35.6 dB/h from 5 min; co-channel counted cached entries, ignored bonding; no provenance (app/driver/connection/time zone); chart labels ~2 pt",
      "SAT-ANALYTICS-2 + report schema v2 + environment capture + BSS Load/PHY/beacon capture + chart rendering in points (DR-0012)", ["T21"],
      A("Numbers contradict each other; reports not comparable across machines", "ReportPrecisionTests (live case reproduced) + regeneration of the owner's report from a DB copy",
        "Wrong conclusions in diagnostics; untraceable reports", "Definitions written for the analyzer, not audited against a report data contract", "high",
        "Derived numbers without bounds checks (part > whole) and extrapolations without validity gates", ["No provenance requirement in SPEC-SAT-001 v1"], "A regenerated 1.2 report with longest-below <= active and no short-span trend")),
    F("F25", "P2", "Version mismatch: REST API and PDF reported 1.1.0 on a 1.2.0 install",
      "Infrastructure.csproj kept <Version>1.1.0</Version>; /api/status reads RestApiServer's assembly version",
      "Directory.Build.props single version; header reads the assembly version", ["T21"],
      A("Wrong version over the API and in reports", "Regenerated PDF footer showed 1.1.0 before the fix", "Untraceable deployments", "Per-project version properties", "high",
        "Version set in more than one place", ["Only Presentation was bumped for 1.2"], "All assemblies report the props version")),
    F("F26", "P2", "Capture-session CSV culture-dependent and UTC unlabelled",
      "ExportService used current-culture ToString (de-DE writes 1,500 in a comma-separated file); Timestamp column and header times were UTC without saying so",
      "Invariant culture, Timestamp_UTC, labelled UTC + local times, generator line", ["T21"],
      A("Broken CSV on comma-decimal locales; ambiguous times", "SessionCsv_IsCultureInvariant_AndLabelsUtc", "Data loss/misparse for consumers", "Implicit culture in formatting", "high",
        "ToString() without IFormatProvider in data files", ["Developed on an en-US machine"], "de-DE export containing 1,500")),
]

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, fids, acc, acceptance, wps, tests, pos, neg, reg, risk):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": fids, "status": "planned", "owner": OWNER, "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": "WirelessScanner.Tests + live install review", "positive_checks": pos, "negative_checks": neg, "regression_checks": reg,
                                  "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": ["EV-RV-REDESIGN-2"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
c["tasks"] += [
    task("T18", 18, ["T14"], ["F21"], "Fix legacy selection echo", "Whole-SSID selections survive scans; scope derived correctly",
         [("Reproduce", "Regression test that fails on 1.2.0 code", "Test fails before fix"), ("Fix", "SetCheckedSilently + bridge guard", "Test passes; legacy tests pass"), ("Live re-check", "Re-run a whole-SSID saturation test on the installed build", "Stored scope is MultipleSsids")],
         ["WholeSsidSelection_SurvivesScans_WithoutLegacyListEchoingBssids"], ["Scope MultipleSsids after scans"], ["Unticking clears everything"], ["Legacy bridge tests"],
         R("Bridge regressions", "Legacy tests", "Revert", "Tests green", "T19")),
    task("T19", 19, ["T09"], ["F22"], "Installer detects existing installs from all registry entries", "App key + Apps & features, both views, manifest/exe cross-check, repair hint, notes",
         [("Detector", "Setup.Core.InstallDetector + tests", "InstallDetectorTests"), ("Wizard", "Use detector; explicit 64-bit writes; stale 32-bit cleanup; icon cache refresh", "Build + owner run"), ("Owner run", "Owner runs the 1.3.0 setup over 1.2.0 and checks the maintenance page", "Update offered; Found via shows both entries")],
         ["InstallDetectorTests"], ["Detected via Uninstall entry only"], ["Missing exe -> NeedsRepair; nothing registered -> Fresh"], ["InstallerCoreTests"],
         R("Wrong detection deletes/duplicates", "Pure tested logic; notes shown", "Revert wizard", "Owner runs setup", "T20")),
    task("T20", 20, [], ["F23"], "App icon across Windows (and Android-ready assets)", "Original icon in setup, exe, shortcuts, Apps & features; Android sets generated",
         [("Design + pipeline", "make_icons.py, branding/", "Assets generated and reviewed"), ("Embed", "ApplicationIcon + window icon", "Icon visible; app not quarantined"), ("Android set", "Generate mipmap/adaptive/Play Store assets for the future port", "Files present in branding/android")],
         ["make_icons", "ExtractAssociatedIcon"], ["Exe icon extracted"], ["Small sizes legible"], ["Full suite"],
         R("AV false positive quarantines the app", "Embedding on hold; owner decision", "Remove ApplicationIcon", "Owner decides exclusion/signing", "Owner decision")),
    task("T21", 21, ["T06"], ["F24", "F25", "F26"], "Report precision, completeness and provenance", "Report data contract met (SPEC §8); SAT-ANALYTICS-2; schema v2; single version; invariant CSV",
         [("Capture", "PHY, beacon, centre freq, BSS Load, connection, driver", "Parser tests"), ("Analytics", "SAT-ANALYTICS-2", "ReportPrecisionTests"),
          ("Exports", "PDF/CSV/JSON v2, charts in points, legacy 'not recorded'", "Contract tests + regenerated live report review"), ("Storage", "Schema 3 add-column migration", "Upgrade test")],
         ["ReportPrecisionTests"], ["All contract fields present"], ["Part never exceeds whole; no short-span trend; missing data never shown as absent"], ["All saturation and export tests"],
         R("Breaking CSV/JSON consumers", "CHANGELOG breaking section; schema v2", "Re-export with 1.2", "Owner review", "Release 1.3.0")),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F21-F26, T18-T21 appended")
