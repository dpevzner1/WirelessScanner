"""One-time bootstrap of config/pipeline/wirelessscanner-remediation-control.json (2026-10-15).

Kept as provenance of how the initial control was authored. Do NOT re-run once the control
exists (it refuses); from then on the control is maintained with tools/ws_control.py and
direct, reviewed edits, and history is only appended.
"""
import copy, datetime, hashlib, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
TEMPLATE = ROOT / "LessonsLearned/templates/project-remediation-control.v2.json"
if OUT.exists() and "--force" not in sys.argv:
    raise SystemExit("control already exists; bootstrap refuses to overwrite history")

now = datetime.datetime.now(datetime.timezone.utc).isoformat()
sha = lambda p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest()
PRODUCER = "Claude Code (claude-opus-5-5), implementer, operator demitri.pevzner"
OWNER = "Claude Code (implementer) for demitri.pevzner"

def ev(i, path, kind, scope, limit):
    return {"id": i, "path": path, "sha256": sha(path), "scope": scope, "kind": kind,
            "producer": PRODUCER, "captured_at": now, "claim_limit": limit}

# Living documents are cited through an immutable snapshot (lesson iteration CTRL-3).
SNAP = ROOT / "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN"
if not SNAP.exists():
    _docs = ["docs/PROJECT_CONVENTIONS.md", "docs/consultation/ENGINE_CONSULTATION_LOG.md", "docs/SATURATION_TEST_SPEC.md"] + sorted(str(x.relative_to(ROOT).as_posix()) for x in (ROOT / "docs/decisions").glob("DR-000[1-5]*.md"))
    _entries = []
    for d in _docs:
        dst = SNAP / "files" / d; dst.parent.mkdir(parents=True, exist_ok=True); dst.write_bytes((ROOT / d).read_bytes())
        _entries.append({"path": d, "sha256": sha(d)})
    (SNAP / "MANIFEST.json").write_text(json.dumps({"snapshot_id": "SNAP-2026-10-15-DESIGN", "captured_at": now, "files": _entries}, indent=2) + "\n", encoding="utf-8")
evidence = [
    ev("EV-BL-AUDIT", "governance/evidence/baseline/BASELINE_AUDIT_2026-10-15.md", "baseline_observation", "Whole-project baseline findings F01-F14", "Single non-independent auditor; code reading plus logs, no hardware run"),
    ev("EV-BL-BUILD", "governance/evidence/baseline/build-20261015.txt", "baseline_observation", "Debug build of WirelessScanner.slnx before changes", "Tail of build output only; warnings list truncated"),
    ev("EV-BL-TEST", "governance/evidence/baseline/test-20261015.txt", "test_result", "20 NUnit tests before changes", "Tests do not cover F01-F14 behaviours"),
    ev("EV-BL-LOG-0531", "governance/evidence/baseline/runtime-log-copies/wireless_scanner_log20260531.txt", "baseline_observation", "Runtime log showing FK constraint failure", "One occurrence; frequency unknown"),
    ev("EV-BL-LOG-0528", "governance/evidence/baseline/runtime-log-copies/wireless_scanner_log20260528.txt", "baseline_observation", "Runtime log showing WlanGetNetworkBssList 1168", "Cause (adapter removed vs GUID mismatch) not established"),
    ev("EV-BL-LOG-0530", "governance/evidence/baseline/runtime-log-copies/wireless_scanner_log20260530_001.txt", "baseline_observation", "Runtime log showing port 5005 conflicts", "Conflicting registrant not identified"),
    ev("EV-VENDOR", "governance/evidence/vendored-engine-manifest.json", "control_validation", "Byte identity of 15 vendored engine governance artifacts", "Proves copy identity at capture time only"),
    ev("EV-CONVENTIONS", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/PROJECT_CONVENTIONS.md", "design", "Documentation, trail, consultation and sync conventions", "Process definition; adherence is checked at checkpoints, not automatically"),
    ev("EV-CONSULT", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/consultation/ENGINE_CONSULTATION_LOG.md", "design", "Engine consultations C-01..C-06", "Index hits are pointers, not expert competence"),
    ev("EV-SPEC-SAT", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/SATURATION_TEST_SPEC.md", "design", "Saturation test specification SPEC-SAT-001", "Design only until T06 verification"),
    ev("EV-DR-0001", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/decisions/DR-0001-governance-adoption.md", "design", "Governance adoption decision", "Approval by owner chat statement"),
    ev("EV-DR-0002", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/decisions/DR-0002-canonical-folder-and-sync.md", "design", "Canonical folder and sync decision", "Approval by owner chat statement"),
    ev("EV-DR-0003", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/decisions/DR-0003-fresh-scan-and-persistence-pipeline.md", "design", "Fresh-scan and persistence decision", "Native API behaviour to be confirmed on hardware"),
    ev("EV-DR-0004", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/decisions/DR-0004-saturation-test-measurement-model.md", "design", "Saturation measurement model decision", "RF thresholds are not engine-sourced"),
    ev("EV-DR-0005", "governance/evidence/snapshots/SNAP-2026-10-15-DESIGN/files/docs/decisions/DR-0005-saturation-test-ux.md", "design", "Saturation UX decision", "No usability study yet"),
]
E = {e["id"] for e in evidence}

def finding(fid, pri, title, tasks, evs, a):
    return {"id": fid, "priority": pri, "title": title, "status": "open", "observed": a["symptom"],
            "remediation": a.pop("remediation"), "evidence": evs, "task_ids": tasks, "analysis": a}

def A(symptom, repro, impact, cause, conf, contrib, rejected, disconf, sig, remediation):
    return {"symptom": symptom, "reproduction": repro, "impact": impact, "root_cause": cause, "cause_confidence": conf,
            "contributing_conditions": contrib, "rejected_explanations": rejected, "counterexample_or_disconfirmation": disconf,
            "recurrence_signature": sig, "lesson_ids": ["LL-2026-10-15-BASELINE-GOVERNANCE"], "remediation": remediation}

findings = [
    finding("F01", "P0", "Fresh scans never requested; cached BSS list recorded as new samples", ["T03"], ["EV-BL-AUDIT"],
        A("TriggerHardwareScanAsync has no caller; samples every 2 s are re-reads of the Windows cache", "grep TriggerHardwareScanAsync across Presentation/Application; observe PerformScanAsync", "Duplicate/stale samples; variation and statistics biased", "Scan trigger implemented but never wired; no freshness model", "high", ["No test asserting a scan is requested per cycle", "Beacon timestamps discarded"], ["Windows refreshes the cache often enough (false: background scan cadence is ~60 s when connected)"], "A hardware run showing RSSI changing every 2 s with no WlanScan would weaken the impact claim", "Sampling loop calls a read API without a preceding acquisition/freshness check", "Implement notification-driven fresh scan cycles with beacon age (DR-0003)")),
    finding("F02", "P0", "Session/sample persistence race loses samples", ["T04"], ["EV-BL-AUDIT", "EV-BL-LOG-0531"],
        A("SQLite FK constraint failure in SaveSampleAsync", "Start a session and capture immediately; see log 2026-05-31", "Lost samples at session start", "Unordered fire-and-forget Task.Run writes", "high", ["One DbContext per sample", "No writer ordering"], ["Disk/locking error (rejected: error 19 is FK)"], "FK failure while the session row already existed would disprove ordering as the cause", "Independent fire-and-forget writes with parent/child FK", "Single ordered background writer; session enqueued first (DR-0003)")),
    finding("F03", "P1", "WLAN calls on UI thread; README threading claims untrue", ["T04"], ["EV-BL-AUDIT"],
        A("DispatcherTimer drives synchronous WLAN calls on the UI thread", "Read PerformScanAsync/GetAccessPointsAsync", "UI stalls during slow driver calls; claims mislead reviewers", "Simplified implementation diverged from plan", "high", ["No responsiveness test"], [], "Profiling showing no UI-thread WLAN time would disprove", "Async signature over synchronous native call", "Background scan loop; native calls off UI thread")),
    finding("F04", "P1", "Data/log paths depend on working directory; uninstall deletes user data", ["T07"], ["EV-BL-AUDIT"],
        A("Relative 'wireless_scanner.db' and 'logs/' paths; installer deletes install dir", "Launch from a different working directory; uninstall", "Data scattered or destroyed", "Relative paths and no data-location policy", "high", ["Installer written without data model awareness"], [], "Shortcut working directory always set would only partly mitigate", "Relative paths for persistent state", "Use %ProgramData%\\WirelessScanner with one-time migration; installer retention option (DR-0006)")),
    finding("F05", "P1", "REST API key exposure and weak controls", ["T08"], ["EV-BL-AUDIT", "EV-BL-LOG-0530"],
        A("Key in README; query-string auth; CORS *; port conflict only logged", "Read README, RestApiServer.cs", "Credential leakage; cross-origin reads", "Convenience-first design", "high", ["No security review"], [], "Key proven inactive would reduce severity of README exposure", "Secrets in docs/URLs", "Header-only constant-time auth, restricted CORS, rotated key, conflict surfaced (DR-0008)")),
    finding("F06", "P1", "Installer lifecycle defects", ["T09"], ["EV-BL-AUDIT"],
        A("Uninstaller path, no repair/version check, stale files, wrong finish text, framework-dependent", "Read WirelessScanner.Setup/Program.cs and pack_installer.py", "Broken uninstall, silent downgrade, data loss", "Minimal wizard", "high", ["No installer tests"], [], "n/a", "Lifecycle not modelled", "Self-copy to install dir, version compare, repair, retention, self-contained publish (DR-0007)")),
    finding("F07", "P1", "Documentation diverges from code; no trail", ["T01", "T12"], ["EV-BL-AUDIT"],
        A("PLAN still 'Planning'; README claims unimplemented features", "Compare README/PLAN to code", "Misleading users/reviewers; repeated work", "No documentation process after planning", "high", ["No checkpoint discipline"], [], "n/a", "Docs written once, never reconciled", "Adopt full governance (DR-0001); reconcile all docs (T12)")),
    finding("F08", "P2", "Duplicated source without sync", ["T02"], ["EV-BL-AUDIT"],
        A("GitHub Export differs from main folder", "diff -rq", "Wrong code published", "Manual copying", "high", [], [], "n/a", "Two editable copies", "Generated mirror with --check (DR-0002)")),
    finding("F09", "P1", "Missing/assumed values stored as numbers", ["T05"], ["EV-BL-AUDIT"],
        A("Noise floor constant -95 shown as measured; wired sentinels -100/0", "Read WlanAdapter.cs:333, MainWindowViewModel.cs:844", "False precision", "No quality/status model", "high", [], [], "n/a", "Sentinel numbers for absent data", "Quality labels and nullable values (DR-0004)")),
    finding("F10", "P1", "Signal saturation test absent", ["T06"], ["EV-BL-AUDIT", "EV-SPEC-SAT"],
        A("Stationary mode is a label only", "Inspect SessionCaptureDialog and ViewModel", "Owner's core use case unsupported", "Not implemented", "high", [], [], "n/a", "Mode label without behaviour", "Implement SPEC-SAT-001 (DR-0004, DR-0005)")),
    finding("F11", "P3", "Repository clutter", ["T11"], ["EV-BL-AUDIT"],
        A("Stubs, .exe.old, 84 MB pdb, unused LiveCharts, unpinned prototype", "List files", "Confusion, size, NU1701 warnings", "No hygiene pass", "high", [], [], "n/a", "Leftover scaffolding", "Remove or document each item")),
    finding("F12", "P2", "Test gaps", ["T10"], ["EV-BL-AUDIT", "EV-BL-TEST"],
        A("No tests for IE parsing, channel mapping, race, freshness, installer, analytics", "Inspect WirelessScanner.Tests", "Regressions undetected", "Tests written for happy paths", "high", [], [], "n/a", "Coverage follows convenience, not risk", "Risk-based tests per task")),
    finding("F13", "P2", "Adapter loss and scan errors not handled", ["T03"], ["EV-BL-AUDIT", "EV-BL-LOG-0528"],
        A("Error 1168 repeated; UI shows generic failure", "See log 2026-05-28", "Silent gaps in captures", "No error model for provider failures", "medium", [], [], "n/a", "Provider exceptions surfaced as strings", "Failed cycle status, re-enumeration, backoff, gap recording")),
    finding("F14", "P1", "RSSI variation mislabelled as jitter in milliseconds", ["T05"], ["EV-BL-AUDIT"],
        A("|dRSSI| in dB displayed/exported with 'ms'", "MainWindow.xaml:816,1136; ExportService.cs:661", "Misinterpretation as network latency", "Unit not modelled", "high", [], [], "n/a", "Derived metric without unit definition", "Rename to RSSI variation (dB) everywhere (DR-0004)")),
]

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, fids, accomplishes, acceptance, wps, tests, pos, neg, reg, risk):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": fids, "status": "planned", "owner": OWNER,
            "accomplishes": accomplishes, "acceptance": acceptance, "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": "WirelessScanner.Tests plus listed manual checks", "positive_checks": pos, "negative_checks": neg,
                                  "regression_checks": reg, "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": ["EV-BL-AUDIT", "EV-BL-TEST"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
tasks = [
    task("T01", 1, [], ["F07"], "Full governance and live breadcrumb trail", "Control validates with all negative controls rejected; lessons validator passes; conventions, decisions, consultation log, STATUS and CHANGELOG exist",
         [("Vendor engine governance", "Copy validators, schemas, templates, standards verbatim with hash manifest", "Manifest shows all byte_identical"),
          ("Author control and records", "Create control, baseline audit, conventions, decisions DR-0001..0005, consultation log, lesson record", "Validators pass"),
          ("Status and changelog projections", "Create docs/STATUS.md and CHANGELOG.md consistent with the control", "Manual consistency review")],
         ["GOV-VALIDATE", "GOV-LESSONS", "GOV-ATTEST"], ["validate_project_remediation_control --self-test passes"], ["Each self-test mutation is rejected"], ["dotnet test still 20/20"],
         R("Process overhead slows delivery", "Helper tool ws_control.py", "Supersede DR-0001", "Validators pass", "Proceed to T02/T03")),
    task("T02", 2, ["T01"], ["F08"], "Generated GitHub mirror with drift check", "sync --check reports zero differences after sync; no secrets/build output mirrored",
         [("Sync tool", "Write tools/sync_github_export.py with allow-list and exclusions", "Script runs"),
          ("Initial sync", "Mirror main folder into GitHub Export", "--check clean"),
          ("Safety review", "Verify .git untouched and secrets excluded", "git status shows intended changes only")],
         ["SYNC-CHECK"], ["--check passes after sync"], ["A planted *.db or api_key.md is not mirrored"], ["GitHub Export/.git intact"],
         R("Accidental deletion in mirror", "Never touch .git; dry-run first", "git checkout -- . in mirror", "Dry run clean", "Run sync")),
    task("T03", 3, ["T01"], ["F01", "F13"], "Notification-driven fresh scan cycles with error model", "Each cycle requests WlanScan and reports fresh/stale/failed with beacon age; failures recorded as gaps",
         [("Domain contract", "Add ScanCycleResult and ScanAsync to INetworkAdapterProvider", "Builds"),
          ("WLAN implementation", "WlanRegisterNotification + WlanScan + wait + read list; off UI thread; error mapping", "Builds; manual run"),
          ("Mock and tests", "Mock provider supports cycles and failures; unit tests", "Tests pass")],
         ["SCAN-CYCLE-STATUS", "SCAN-FAILURE-GAP"], ["Fresh cycle returns results with status fresh"], ["Failed cycle yields status failed, no samples"], ["Existing 20 tests pass"],
         R("Notification behaviour differs on some drivers", "Timeout fallback labels stale", "Revert to previous provider methods", "Hardware run (SAT-AC-10)", "Implement T04")),
    task("T04", 4, ["T03"], ["F02", "F03"], "Ordered background persistence, background scan loop, schema upgrades", "Session row always precedes samples; batched per cycle; UI thread never runs WLAN calls; existing DBs upgraded",
         [("Persistence writer", "Channel-based single writer with ordered work items and drain on shutdown", "Ordering test passes"),
          ("SessionManager integration", "Route writes through writer", "Lifecycle tests pass"),
          ("Background live loop and SchemaMigrator", "Replace DispatcherTimer; add migrator with user_version", "Migration idempotency test passes")],
         ["PERSIST-ORDER", "SCHEMA-MIGRATE"], ["Samples persisted after session"], ["Sample enqueued before session start is rejected/ordered, never FK failure"], ["Export and history tests pass"],
         R("Data loss on abrupt exit", "Drain with timeout", "Revert writer", "Tests pass", "Implement T05/T06")),
    task("T05", 5, ["T03"], ["F09", "F14"], "Honest measurement semantics and units", "Noise floor unavailable, SNR estimated, variation labelled dB; no sentinel numbers for absent data",
         [("Model labels", "Add quality metadata and nullable semantics", "Builds"),
          ("UI and export labels", "Rename jitter to RSSI variation (dB) in UI, CSV, JSON, PDF", "Export tests assert labels"),
          ("Wired path", "Remove -100/0 sentinels", "Review")],
         ["UNITS-LABELS"], ["Exports show 'RSSI variation (dB)'"], ["No 'ms' unit on variation anywhere"], ["Export tests pass"],
         R("Breaking CSV consumers", "Document in CHANGELOG", "Revert labels", "Tests pass", "Implement T06")),
    task("T06", 6, ["T04", "T05"], ["F10"], "Signal saturation test per SPEC-SAT-001", "SAT-AC-01..09 Validated; SAT-AC-10 hardware run Observed or Blocked with reason",
         [("Domain and analyzer", "Saturation domain records and pure SaturationAnalyzer SAT-ANALYTICS-1", "Synthetic tests pass"),
          ("Runner and repository", "Background runner with keep-awake, markers, pause/stop; SQLite tables", "Runner tests with fake provider pass"),
          ("Reports", "PDF, CSV bundle, JSON exports", "Export tests pass"),
          ("UI", "SaturationTestView three-step flow, history", "ViewModel validation tests pass; owner walkthrough")],
         ["SAT-AC-01", "SAT-AC-02", "SAT-AC-03", "SAT-AC-04", "SAT-AC-05", "SAT-AC-06", "SAT-AC-07", "SAT-AC-08"], ["Known synthetic series produce exact events"], ["Noise below threshold produces no events; missing never numeric"], ["All prior tests pass"],
         R("Scope size", "Pure analyzer first; UI last", "Hide tab", "Analyzer tests green", "Implement runner")),
    task("T07", 7, ["T04"], ["F04"], "Stable data location with migration", "DB and logs under %ProgramData%\\WirelessScanner; legacy DB migrated once",
         [("Path provider", "Central AppPaths", "Builds"), ("Migration", "Copy legacy DB/logs if present", "Test with temp dirs"), ("Consumers", "DbContext, logger, API stats use AppPaths", "Tests pass")],
         ["DATA-PATHS"], ["Paths resolved independent of working directory"], ["Existing destination DB is never overwritten"], ["All tests pass"],
         R("Permission issues", "ProgramData ACL; app runs elevated", "Revert to relative", "Tests pass", "T08/T09")),
    task("T08", 8, ["T07"], ["F05"], "REST API hardening", "Header-only constant-time auth; CORS restricted; README key removed and rotation documented; port conflict surfaced",
         [("Auth", "Remove query-string key; constant-time compare", "API tests"), ("CORS and errors", "Restrict CORS; surface conflicts in UI status", "API tests"), ("Docs", "Remove key from README; rotation guidance", "Review")],
         ["API-AUTH"], ["Header key returns 200"], ["Query-string key returns 401"], ["Existing API tests updated and pass"],
         R("Breaking integrations using query key", "CHANGELOG notice", "Revert", "Tests pass", "T09")),
    task("T09", 9, ["T07"], ["F06"], "Installer lifecycle: install, update, repair, modify, uninstall", "Uninstaller copied into install dir; version compare; repair; retention prompt; correct finish text; self-contained publish",
         [("Lifecycle model", "Version/state detection and option page (Update/Repair/Modify/Uninstall)", "Build"), ("Data safety", "Keep data by default; confirm deletion", "Manual run in VM/sandbox"), ("Packaging", "Self-contained setup publish; uninstaller self-copy", "pack_installer run")],
         ["INSTALLER-LOGIC"], ["Upgrade preserves ProgramData"], ["Downgrade warns"], ["Fresh install works"],
         R("System changes on dev machine", "Test only with owner approval", "Uninstall", "Owner approves manual run", "T10")),
    task("T10", 10, ["T06"], ["F12"], "Risk-based test coverage", "Tests for IE parsing, channel mapping, freshness, ordering, analytics, exports, installer logic",
         [("Parsing tests", "Channel/band, IE width/security/rates", "Pass"), ("Pipeline tests", "Ordering, migration, cycles", "Pass"), ("Feature tests", "Saturation analytics and exports", "Pass")],
         ["TEST-SUITE"], ["All new tests pass"], ["Mutation spot-checks fail tests"], ["Prior tests pass"],
         R("Flaky timing tests", "Fake clocks", "Quarantine with finding", "Green suite", "T11")),
    task("T11", 11, ["T02"], ["F11"], "Repository hygiene", "Stubs removed; clutter removed or documented; prototype status decided",
         [("Remove stubs", "Delete Class1.cs files", "Build"), ("Package cleanup", "Drop unused LiveCharts reference if unused", "Build without NU1701"), ("Prototype decision", "Document React prototype status", "README")],
         ["BUILD-CLEAN"], ["Build succeeds"], ["No reference to removed types"], ["Tests pass"],
         R("Removing something used", "Build/test gate", "Restore from mirror", "Build green", "T12")),
    task("T12", 12, ["T06", "T08", "T09"], ["F07"], "Complete, accurate documentation of every feature", "README, USER_GUIDE, TECHNICAL_REFERENCE, STATUS, CHANGELOG reconciled with code; PLAN marked historical",
         [("User guide", "Document every screen, feature, export, setting", "Review against UI"), ("Technical reference", "Architecture, data model, API, threading, files, build/install", "Review against code"), ("Reconcile", "README/PLAN/STATUS corrections", "No claim without evidence label")],
         ["DOC-RECONCILE"], ["Each feature in STATUS has a doc section"], ["No README claim lacks implementation"], ["Sync check clean"],
         R("Docs drift again", "Checkpoint 9 in conventions", "n/a", "Owner review", "Owner review")),
]

c = json.loads(TEMPLATE.read_text(encoding="utf-8"))
c.update({
    "project_id": "WIRELESSSCANNER",
    "updated_at": now,
    "purpose": "WirelessScanner finding -> correction -> retest -> lesson trail (adopted from DevOpsAgent v2 control, DR-0001)",
    "scope": {"authorized": ["WirelessScanner code, tests, docs, installer and governance in the main folder", "read-only DevOps engine consultation", "sync to GitHub Export (no push without approval)"],
              "manual_opt_in": ["hardware_validation_on_real_wifi_adapter", "installer_execution_on_a_machine", "github_push"],
              "production_publication": False, "automatic_monitoring": False},
    "coverage_statement": "Baseline findings F01-F14 from the 2026-10-15 audit are mapped; exhaustive unknown defects not established. Expand after each retest.",
    "limitations": ["Evidence hashes and chained events detect accidental drift; not a signed ledger.", "Control validation proves record completeness, not semantic correctness.",
                    "Implementer and auditor are the same agent until an independent reviewer is assigned.", "Hardware behaviour of the WLAN API is not verified until SAT-AC-10."],
    "baseline": {"run_id": "BL-2026-10-15", "status": "not_qualified", "evidence_id": "EV-BL-AUDIT", "immutable": True},
    "evidence": evidence, "findings": findings, "tasks": tasks, "events": [], "iterations": [], "audit_reviews": [],
})
c["control_contract"]["authority"] = "docs/standards/PROJECT_REMEDIATION_AUDIT_STANDARD.md (vendored) + docs/PROJECT_CONVENTIONS.md"
c["control_contract"]["enforcement_boundary"] = "Manual checkpoints per docs/PROJECT_CONVENTIONS.md section 6; validated with tools/ws_control.py validate; not CI-enforced."
c["qualification"]["target"] = {"description": "All findings closed with verified tasks and independent review", "whole_cases": None, "minimum_passed": None}

def chain(e, prev):
    e["previous_sha256"] = prev
    e["sha256"] = hashlib.sha256(json.dumps({k: v for k, v in e.items() if k != "sha256"}, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()
    return e
c["events"].append(chain({"id": "E-0001", "at": now, "kind": "baseline", "details": "Baseline BL-2026-10-15 captured: build 0 errors/37 warnings, tests 20/20, findings F01-F14 recorded", "evidence_ids": ["EV-BL-AUDIT", "EV-BL-BUILD", "EV-BL-TEST"]}, None))
c["events"].append(chain({"id": "E-0002", "at": now, "kind": "decision", "details": "Owner chose heavy/full governance (DR-0001) and canonical main folder with GitHub Export mirror (DR-0002); designs DR-0003..0005 and SPEC-SAT-001 authored with engine consultation C-01..C-06", "evidence_ids": ["EV-DR-0001", "EV-DR-0002", "EV-DR-0003", "EV-DR-0004", "EV-DR-0005", "EV-SPEC-SAT", "EV-CONSULT", "EV-VENDOR", "EV-CONVENTIONS"]}, c["events"][-1]["sha256"]))
OUT.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("written", OUT)
