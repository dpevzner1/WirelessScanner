"""WirelessScanner helper for the v2 remediation control (config/pipeline/wirelessscanner-remediation-control.json).

Project-owned (not vendored). Keeps evidence hashes, the hash-chained breadcrumb event
trail and iteration reproduction fingerprints consistent with the vendored engine
validator (tools/validate_project_remediation_control.py). It never edits history:
evidence is appended, events are appended, and drift is reported rather than hidden.

Usage (from the project root):
  python tools/ws_control.py evidence-add --id EV-.. --path <file> --kind <kind> --scope "<text>" --claim-limit "<text>" [--producer "<text>"]
  python tools/ws_control.py event --id E-.. --kind <kind> --details "<text>" --evidence EV-.. [--evidence EV-..]
  python tools/ws_control.py fingerprint --iteration IT-.. --input <file> --engine <file> --context <file> --test <file>
  python tools/ws_control.py drift
  python tools/ws_control.py validate
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTROL = ROOT / "config/pipeline/wirelessscanner-remediation-control.json"
KINDS = ["baseline_observation", "design", "control_validation", "change", "test_result", "review", "lesson", "measurement"]
DEFAULT_PRODUCER = "Claude Code (claude-opus-5-5), implementer, operator demitri.pevzner"


def now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def event_hash(event: dict) -> str:
    # Identical to tools/validate_project_remediation_control.py::eventhash
    body = {k: v for k, v in event.items() if k != "sha256"}
    return hashlib.sha256(json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def fingerprint(r: dict) -> str:
    # Identical to tools/remediation_audit_rules.py::fingerprint
    keys = ["input_sha256", "engine_sha256", "context_sha256", "test_sha256"]
    return hashlib.sha256(json.dumps({k: r.get(k) for k in keys}, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def load() -> dict:
    return json.loads(CONTROL.read_text(encoding="utf-8"))


def save(control: dict) -> None:
    control["updated_at"] = now()
    CONTROL.write_text(json.dumps(control, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def rel(path: str) -> str:
    p = (ROOT / path).resolve()
    if not p.is_relative_to(ROOT) or not p.is_file():
        raise SystemExit(f"evidence path must be an existing file inside the project: {path}")
    return p.relative_to(ROOT).as_posix()


def cmd_evidence_add(a) -> None:
    c = load()
    if a.kind not in KINDS:
        raise SystemExit(f"kind must be one of {KINDS}")
    if any(e["id"] == a.id for e in c["evidence"]):
        raise SystemExit(f"evidence id already exists (append a new id instead of rewriting): {a.id}")
    path = rel(a.path)
    c["evidence"].append({
        "id": a.id, "path": path, "sha256": sha_file(ROOT / path), "scope": a.scope, "kind": a.kind,
        "producer": a.producer or DEFAULT_PRODUCER, "captured_at": now(), "claim_limit": a.claim_limit,
    })
    save(c)
    print(f"added {a.id} -> {path}")


def cmd_event(a) -> None:
    c = load()
    known = {e["id"] for e in c["evidence"]}
    missing = [x for x in a.evidence if x not in known]
    if missing:
        raise SystemExit(f"unknown evidence ids: {missing}")
    event = {
        "id": a.id, "at": now(), "kind": a.kind, "details": a.details, "evidence_ids": a.evidence,
        "previous_sha256": c["events"][-1]["sha256"] if c["events"] else None,
    }
    event["sha256"] = event_hash(event)
    c["events"].append(event)
    save(c)
    print(f"event {a.id} chained ({event['sha256'][:12]})")


def cmd_fingerprint(a) -> None:
    c = load()
    it = next((i for i in c["iterations"] if i["id"] == a.iteration), None)
    if it is None:
        raise SystemExit(f"unknown iteration {a.iteration}")
    r = it["reproduction"]
    for key, path in [("input_sha256", a.input), ("engine_sha256", a.engine), ("context_sha256", a.context), ("test_sha256", a.test)]:
        r[key] = sha_file(ROOT / rel(path))
    r["fingerprint"] = fingerprint(r)
    save(c)
    print(f"{a.iteration} fingerprint {r['fingerprint']}")


def snapshot(snap_id: str, files: list[str]) -> Path:
    """Copy files into governance/evidence/snapshots/<snap_id>/ with a hash manifest; return the manifest path.
    Evidence must be immutable, so living files (code, docs) are cited through snapshots."""
    base = ROOT / "governance/evidence/snapshots" / snap_id
    if base.exists():
        raise SystemExit(f"snapshot exists (use a new id): {snap_id}")
    entries = []
    for f in files:
        src = ROOT / rel(f)
        dst = base / "files" / src.relative_to(ROOT)
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes(src.read_bytes())
        entries.append({"path": src.relative_to(ROOT).as_posix(), "sha256": sha_file(src), "bytes": src.stat().st_size})
    manifest = base / "MANIFEST.json"
    manifest.write_text(json.dumps({"snapshot_id": snap_id, "captured_at": now(), "files": entries}, indent=2) + "\n", encoding="utf-8")
    return manifest


def cmd_snapshot(a) -> None:
    m = snapshot(a.id, a.file)
    if a.evidence_id:
        c = load()
        c["evidence"].append({"id": a.evidence_id, "path": m.relative_to(ROOT).as_posix(), "sha256": sha_file(m), "scope": a.scope or f"Snapshot {a.id}",
                              "kind": a.kind or "change", "producer": DEFAULT_PRODUCER, "captured_at": now(), "claim_limit": a.claim_limit or "Point-in-time copy of the listed files"})
        save(c)
    print(f"snapshot {a.id}: {m.relative_to(ROOT).as_posix()}")


def cmd_iteration_add(a) -> None:
    """Append an iteration authored as governance/iterations/<ID>.json (kept for provenance)."""
    c = load()
    src = ROOT / rel(a.json)
    it = json.loads(src.read_text(encoding="utf-8"))
    if any(i["id"] == it["id"] for i in c["iterations"]):
        raise SystemExit(f"iteration exists (append a new id): {it['id']}")
    r = it["reproduction"]
    files = r.pop("files", None)
    if files:
        for key in ["input_sha256", "engine_sha256", "context_sha256", "test_sha256"]:
            r[key] = sha_file(ROOT / rel(files[key]))
        r["fingerprint"] = fingerprint(r)
        r["files"] = files
    seen = {i["reproduction"].get("fingerprint"): i["id"] for i in c["iterations"]}
    if r.get("fingerprint") in seen and not it.get("reuse", {}).get("rerun_justification"):
        raise SystemExit(f"duplicate fingerprint of {seen[r['fingerprint']]} without rerun_justification")
    c["iterations"].append(it)
    save(c)
    print(f"iteration {it['id']} added (fingerprint {r.get('fingerprint')})")


def cmd_task(a) -> None:
    """Update task/work-package state and link evidence; closure only with explicit reviewer data."""
    c = load()
    t = next((x for x in c["tasks"] if x["id"] == a.id), None)
    if t is None:
        raise SystemExit(f"unknown task {a.id}")
    known = {e["id"] for e in c["evidence"]}
    for x in (a.change or []) + (a.verification or []):
        if x not in known:
            raise SystemExit(f"unknown evidence {x}")
    if a.status:
        t["status"] = a.status
    t["change_evidence"] = sorted(set(t.get("change_evidence", [])) | set(a.change or []))
    t["verification_evidence"] = sorted(set(t.get("verification_evidence", [])) | set(a.verification or []))
    for spec in a.wp or []:
        wid, status, *evs = spec.split(":")
        w = next((w for w in t["work_packages"] if w["id"] == wid), None)
        if w is None:
            raise SystemExit(f"unknown work package {wid}")
        w["status"] = status
        w["evidence_ids"] = sorted(set(w["evidence_ids"]) | {e for e in evs if e})
    if a.finding_status:
        for spec in a.finding_status:
            fid, st = spec.split(":")
            next(f for f in c["findings"] if f["id"] == fid)["status"] = st
    save(c)
    print(f"task {a.id} -> {t['status']}")


def cmd_drift(_a) -> None:
    c = load()
    drifted = []
    for e in c["evidence"]:
        p = ROOT / e["path"]
        if not p.is_file():
            drifted.append((e["id"], "missing"))
        elif sha_file(p) != e["sha256"]:
            drifted.append((e["id"], "changed"))
    print(json.dumps({"drifted": drifted, "evidence": len(c["evidence"])}, indent=2))


def cmd_validate(_a) -> None:
    r = subprocess.run([sys.executable, str(ROOT / "tools/validate_project_remediation_control.py"), "--control", str(CONTROL), "--self-test"], cwd=ROOT)
    raise SystemExit(r.returncode)


def main() -> None:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("evidence-add")
    for f in ["--id", "--path", "--kind", "--scope", "--claim-limit"]:
        p.add_argument(f, required=True)
    p.add_argument("--producer")
    p = sub.add_parser("event")
    p.add_argument("--id", required=True); p.add_argument("--kind", required=True); p.add_argument("--details", required=True)
    p.add_argument("--evidence", action="append", required=True)
    p = sub.add_parser("fingerprint")
    for f in ["--iteration", "--input", "--engine", "--context", "--test"]:
        p.add_argument(f, required=True)
    p = sub.add_parser("snapshot")
    p.add_argument("--id", required=True); p.add_argument("--file", action="append", required=True)
    p.add_argument("--evidence-id"); p.add_argument("--kind"); p.add_argument("--scope"); p.add_argument("--claim-limit")
    p = sub.add_parser("iteration-add"); p.add_argument("--json", required=True)
    p = sub.add_parser("task")
    p.add_argument("--id", required=True); p.add_argument("--status")
    p.add_argument("--change", action="append"); p.add_argument("--verification", action="append")
    p.add_argument("--wp", action="append", help="WPID:status[:EV-..[:EV-..]]")
    p.add_argument("--finding-status", action="append", help="F01:in_progress")
    sub.add_parser("drift"); sub.add_parser("validate")
    a = ap.parse_args()
    {"evidence-add": cmd_evidence_add, "event": cmd_event, "fingerprint": cmd_fingerprint, "drift": cmd_drift,
     "validate": cmd_validate, "iteration-add": cmd_iteration_add, "task": cmd_task, "snapshot": cmd_snapshot}[a.cmd](a)


if __name__ == "__main__":
    main()
