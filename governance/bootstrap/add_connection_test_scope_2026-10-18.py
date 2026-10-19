"""Append finding F29 and tasks T24-T26 (connection test stages A, B, C; DR-0014) to the control (2026-10-18).
Owner: "lets go A, B, then C" + UX choices + "build it all". Append-only; refuses to run twice."""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
c = json.loads(P.read_text(encoding="utf-8"))
if any(f["id"] == "F29" for f in c["findings"]):
    sys.exit("already applied")
OWNER = "Claude Code (implementer) for demitri.pevzner"
LESSON = "LL-2026-10-18-CONNECTION-TEST"

c["findings"].append({
    "id": "F29", "priority": "P2", "status": "open",
    "title": "No way to measure connection / internet quality over the current Wi-Fi connection, or to locate where it fails",
    "observed": "Owner 2026-10-18: asked for a strong way to test internet/connection quality over an existing authenticated wireless connection. DR-0004 boundary: the app only listened (no latency, loss or throughput).",
    "remediation": "DR-0014 stages A (segmented probes bound to Wi-Fi with attribution), B (speed + latency under load), C (two-way tests with an authenticated LAN responder); connection test mode, reports, REST",
    "evidence": ["EV-1.4-TEST-2"], "task_ids": ["T24", "T25", "T26"],
    "analysis": {"symptom": "Users cannot tell whether a slow or dropping connection is Wi-Fi, router, ISP, DNS or the service",
                 "reproduction": "Feature absent in 1.4.0", "impact": "Diagnostics stop at RF; the most common user question is unanswered",
                 "root_cause": "Measurement boundary deliberately limited to passive RF (DR-0004)", "cause_confidence": "high",
                 "contributing_conditions": ["Engine has no IP performance-measurement sources (C-09 gap)"], "rejected_explanations": [],
                 "counterexample_or_disconfirmation": "A real run attributing an outage to the right segment",
                 "recurrence_signature": "Feature scope set by what is easy to measure rather than by the user's question", "lesson_ids": [LESSON]}})

def wp(tid, n, title, action, exit_check):
    return {"id": f"{tid}-WP{n}", "title": title, "action": action, "exit_check": exit_check, "status": "planned", "evidence_ids": []}

def task(tid, seq, deps, acc, acceptance, wps, tests, pos, neg, reg, risk):
    return {"id": tid, "sequence": seq, "depends_on": deps, "finding_ids": ["F29"], "status": "planned", "owner": OWNER, "accomplishes": acc, "acceptance": acceptance,
            "work_packages": [wp(tid, i + 1, *w) for i, w in enumerate(wps)],
            "verification_plan": {"test_ids": tests, "test_scope": "WirelessScanner.Tests + real connection run", "positive_checks": pos, "negative_checks": neg, "regression_checks": reg,
                                  "required_result_fields": ["test_name", "outcome", "duration", "evidence_path"], "baseline_evidence_ids": ["EV-1.4-TEST-2"]},
            "risk_and_resume": risk, "change_evidence": [], "verification_evidence": [], "closure": None}

R = lambda risk, cont, rb, resume, nxt: {"risk": risk, "containment": cont, "rollback": rb, "resume_condition": resume, "next_action": nxt}
c["tasks"] += [
    task("T24", 24, ["T23"], "Connection test stage A: segmented monitor with attribution",
         "Probes bound to Wi-Fi; never changes the connection; outages/spikes attributed; verdicts; reports; history; REST; mode UX per DR-0014",
         [("Probes", "WindowsConnectionProber (ICMP with source, TCP, DNS, HTTPS) + WLAN link read", "ConnectionProberTests"),
          ("Runner/analytics", "ConnectionTestRunner, PathDiscovery, ConnectionAnalyzer CONN-ANALYTICS-1", "ConnectionAnalyzerTests, ConnectionRunnerTests"),
          ("UI/reports/API", "Mode, drawer, stage, history, CONNECTED badge, linked saturation; PDF/CSV/JSON; /api/connection", "ConnectionUiTests, ApiTests, renders"),
          ("Real run", "45 s on the owner's connection; redacted output", "All segments answered; association unchanged")],
         ["ConnectionAnalyzerTests", "ConnectionRunnerTests", "ConnectionProberTests", "ConnectionUiTests"], ["Every probe bound to Wi-Fi", "Faults attributed correctly"],
         ["Refuses without Wi-Fi", "ISP rate limiting not a fault", "Gaps not outages", "Two failed rounds not an outage"], ["Full suite incl. parity"],
         R("Probe traffic misread as interference by IT", "Targets/rate printed; Light default", "Mode unused", "Owner installs 1.5.0", "Owner live review")),
    task("T25", 25, ["T24"], "Connection test stage B: speed and responsiveness",
         "Download/upload with ramp exclusion; idle vs loaded latency; bufferbloat grade; on-demand/scheduled; LAN responder or own HTTP server; no public provider preset",
         [("Clients", "ResponderClient + HttpSpeedClient bound to Wi-Fi", "Loopback throughput"), ("Runner", "Pause probes, sample latency, grade", "SpeedTest_PausesRegularProbes_…"),
          ("Owner run", "Owner runs a speed test against a chosen server", "Plausible capacity and grade")],
         ["SpeedTest_PausesRegularProbes_AndRecordsLatencyUnderLoad", "Throughput_OverLoopback_MeasuresBothDirections"], ["Mbps and grade computed"], ["Second capacity run refused while one runs"], ["Stage A unaffected"],
         R("Speed test disturbs other users", "Confirmation with duration; schedule opt-in", "Kind = None", "Owner chooses a server", "Owner run")),
    task("T26", 26, ["T24"], "Connection test stage C: LAN responder two-way tests",
         "Authenticated responder (HMAC), no amplification, per-direction loss/reordering/delay variation/MOS, LAN capacity; pairing with DPAPI-protected key",
         [("Protocol/server", "ResponderProtocol + ResponderServer + console host", "ResponderTests"), ("Client/UI", "ResponderClient + SETTINGS → TEST SERVERS pairing", "TestServers_KeyIsStoredProtected_…"),
          ("Release", "Portable responder zip in the release", "Release summary lists the zip"), ("Owner run", "Responder on a wired machine; two-way test over real Wi-Fi", "Per-direction results")],
         ["ResponderTests", "TestServers_KeyIsStoredProtected_AndResponderCheckWorks"], ["Loopback stream: 0 loss, sane RTT"], ["Wrong key rejected; forged UDP unanswered"], ["Full suite"],
         R("Open UDP/TCP port on the LAN", "Authenticated only; caps; reply ≤ request; firewall advice local-only", "Stop the responder", "Owner has a wired device", "Owner run")),
]
P.write_text(json.dumps(c, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("F29, T24-T26 appended")
