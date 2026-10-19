"""Live smoke test of an installed WirelessScanner through its local REST API (DR-0008).

Usage (project root, while the app runs with the API enabled):
  python tools/api_smoke.py check                 one-shot: auth negatives, status/version, live data sanity, sessions, stats
  python tools/api_smoke.py watch --seconds 120   poll every 5 s: live AP count, newest session sample growth (capture/pause)

Privacy and secrets:
  * The API key and port are read from the app's own database (read-only). The key is sent only to localhost
    in the X-API-Key header and is NEVER printed or written anywhere.
  * Printed output contains counts, versions, plausibility verdicts and short SHA-256 tags of identifiers only,
    never SSIDs or BSSIDs. With --raw-out, full API responses are written to a sync-excluded local-only folder.
Exit code 0 = all checks passed, 1 = a check failed, 2 = setup problem (API off, no key, app not reachable).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DB = Path(r"C:\ProgramData\WirelessScanner\wireless_scanner.db")
ROOT = Path(__file__).resolve().parents[1]


def settings() -> dict:
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    try:
        return {k: v for k, v in con.execute("select Key, Value from Settings")}
    finally:
        con.close()


def tag(s: str) -> str:
    return hashlib.sha256((s or "").encode()).hexdigest()[:8]


def get(port: int, path: str, key: str | None, query_key: str | None = None) -> tuple[int, object]:
    url = f"http://localhost:{port}{path}" + (f"?api_key={query_key}" if query_key else "")
    req = urllib.request.Request(url)
    if key:
        req.add_header("X-API-Key", key)
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read().decode("utf-8") or "null")
    except urllib.error.HTTPError as e:
        try:
            body = json.loads(e.read().decode("utf-8") or "null")
        except Exception:
            body = None
        return e.code, body


def f(d: dict, name: str):
    """Case-insensitive field access (the API serialises with the server's naming policy)."""
    for k, v in d.items():
        if k.lower() == name.lower():
            return v
    return None


def connect() -> tuple[int, str]:
    if not DB.exists():
        print("SETUP: database not found:", DB)
        sys.exit(2)
    s = settings()
    if s.get("ApiEnabled") != "true" or not s.get("ApiKey"):
        print("SETUP: REST API is not enabled with a key (REST API CONFIG tab: enable, GENERATE, APPLY & SAVE).")
        sys.exit(2)
    port = int(s.get("ApiPort") or 5005)
    return port, s["ApiKey"]


def check(raw_out: Path | None, expect_version: str) -> int:
    port, key = connect()
    results: list[tuple[str, bool, str]] = []

    def rec(name: str, ok: bool, detail: str) -> None:
        results.append((name, ok, detail))
        print(f"{'PASS' if ok else 'FAIL'}  {name}: {detail}")

    try:
        code, _ = get(port, "/api/status", None)
    except urllib.error.URLError as e:
        print(f"SETUP: API not reachable on localhost:{port} ({e.reason}). Is the app running with the API enabled?")
        return 2
    rec("auth: no key rejected", code == 401, f"HTTP {code}")
    code, _ = get(port, "/api/status", None, query_key=key)
    rec("auth: key in URL rejected", code == 401, f"HTTP {code}")
    code, _ = get(port, "/api/status", "ws_live_wrong")
    rec("auth: wrong key rejected", code == 401, f"HTTP {code}")

    code, st = get(port, "/api/status", key)
    ver = f(st, "version") if isinstance(st, dict) else None
    rec("status: healthy", code == 200 and f(st, "status") == "Healthy", f"HTTP {code}, version {ver}")
    rec(f"status: version is {expect_version}", ver == expect_version, f"reported {ver}")

    code, live = get(port, "/api/live", key)
    aps = live if isinstance(live, list) else []
    rec("live: access points present", code == 200 and len(aps) > 0, f"HTTP {code}, {len(aps)} APs")
    bad = []
    for a in aps:
        rssi, band, ch = f(a, "rssi"), f(a, "band"), f(a, "channel")
        if not (isinstance(rssi, int) and -100 <= rssi <= -10):
            bad.append(("rssi", tag(f(a, "bssid"))))
        if band not in ("2.4 GHz", "5 GHz", "6 GHz"):
            bad.append(("band", tag(f(a, "bssid"))))
        elif band == "2.4 GHz" and not (1 <= (ch or 0) <= 14) or band == "5 GHz" and not (32 <= (ch or 0) <= 177) or band == "6 GHz" and not (1 <= (ch or 0) <= 233):
            bad.append(("channel", tag(f(a, "bssid"))))
    rec("live: fields plausible (RSSI range, band/channel consistency)", not bad, "ok" if not bad else f"{len(bad)} problems: {bad[:5]}")
    if aps:
        bands = {}
        for a in aps:
            bands[f(a, "band")] = bands.get(f(a, "band"), 0) + 1
        widths = sum(1 for a in aps if f(a, "channelWidth") is not None)
        ages = sorted(x for x in (f(a, "beaconAgeMs") for a in aps) if isinstance(x, (int, float)))
        print(f"INFO  live: bands {bands}; width known {widths}/{len(aps)}; "
              f"beacon age median {round(ages[len(ages)//2]) if ages else 'n/a'} ms; "
              f"SSIDs {len({f(a, 'ssid') for a in aps})} (identifiers not printed)")

    code, sess = get(port, "/api/sessions", key)
    sessions = sess if isinstance(sess, list) else []
    rec("sessions: list", code == 200, f"HTTP {code}, {len(sessions)} sessions")
    newest = None
    if sessions:
        newest = sessions[0]
        sid = f(newest, "sessionId")
        code, detail = get(port, f"/api/sessions/{sid}", key)
        samples = f(detail, "samples") if isinstance(detail, dict) else None
        rec("sessions: newest session detail", code == 200 and isinstance(samples, list),
            f"HTTP {code}, '{tag(f(newest, 'name'))}' {len(samples or [])} samples, mode {f(newest, 'mode')}")
        if samples:
            ts = [x for x in (f(s, "timestamp") for s in samples) if x]
            rec("sessions: samples ordered by time", ts == sorted(ts), f"{len(ts)} timestamps")
        if raw_out:
            (raw_out / "session_newest.json").write_text(json.dumps(detail, indent=1), encoding="utf-8")
    code, _ = get(port, "/api/sessions/not-a-guid", key)
    rec("sessions: bad id -> 400", code == 400, f"HTTP {code}")
    code, _ = get(port, "/api/sessions/00000000-0000-0000-0000-000000000000", key)
    rec("sessions: unknown id -> 404", code == 404, f"HTTP {code}")
    code, stats = get(port, "/api/stats", key)
    rec("stats", code == 200 and isinstance(stats, dict), f"HTTP {code}, sessions {f(stats, 'totalSessions')}, samples {f(stats, 'totalSamples')}, db {round(f(stats, 'databaseSizeMB') or 0, 2)} MB")
    code, _ = get(port, "/api/nope", key)
    rec("unknown endpoint -> 404", code == 404, f"HTTP {code}")

    if raw_out:
        (raw_out / "live.json").write_text(json.dumps(aps, indent=1), encoding="utf-8")
        (raw_out / "sessions.json").write_text(json.dumps(sessions, indent=1), encoding="utf-8")
    failed = [r for r in results if not r[1]]
    print(f"SUMMARY  {len(results) - len(failed)}/{len(results)} checks passed")
    return 0 if not failed else 1


def watch(seconds: int) -> int:
    port, key = connect()
    end = time.time() + seconds
    prev = None
    while time.time() < end:
        try:
            _, live = get(port, "/api/live", key)
            _, sess = get(port, "/api/sessions", key)
        except urllib.error.URLError as e:
            print(time.strftime("%H:%M:%S"), "API not reachable:", e.reason)
            time.sleep(5)
            continue
        n_live = len(live) if isinstance(live, list) else -1
        line = f"{time.strftime('%H:%M:%S')}  live APs {n_live:3d}"
        if isinstance(sess, list) and sess:
            s = sess[0]
            _, d = get(port, f"/api/sessions/{f(s, 'sessionId')}", key)
            n = len(f(d, "samples") or []) if isinstance(d, dict) else -1
            delta = "" if prev is None else f" (+{n - prev})"
            prev = n
            line += f"  newest session '{tag(f(s, 'name'))}' samples {n}{delta}  ended {'yes' if f(s, 'endTime') else 'no'}"
        print(line, flush=True)
        time.sleep(5)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check")
    c.add_argument("--raw-out")
    c.add_argument("--expect-version", default="1.3.0")
    w = sub.add_parser("watch")
    w.add_argument("--seconds", type=int, default=120)
    a = ap.parse_args()
    if a.cmd == "check":
        raw = None
        if a.raw_out:
            raw = (ROOT / a.raw_out).resolve()
            if "local-only" not in raw.parts:
                print("--raw-out must be under governance/evidence/local-only (raw responses contain SSIDs/BSSIDs)")
                return 2
            raw.mkdir(parents=True, exist_ok=True)
        return check(raw, a.expect_version)
    return watch(a.seconds)


if __name__ == "__main__":
    sys.exit(main())
