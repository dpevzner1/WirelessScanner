"""Mirror the canonical main folder into `GitHub Export/` (the GitHub repository readiness folder).

Decision: docs/decisions/DR-0002-canonical-folder-and-sync.md
- Main folder is canonical; GitHub Export is generated. Never edit the mirror by hand.
- Only allow-listed paths are mirrored; build output, local data, logs and secrets never are.
- Inside mirrored roots, files that no longer exist canonically are removed from the mirror.
- `.git/` and mirror-owned generated output (EXE INSTALLER/) are never deleted by the sync.
- The sync never commits or pushes.

Usage (project root):
  python tools/sync_github_export.py --check     # exit 1 if the mirror differs
  python tools/sync_github_export.py --dry-run   # show actions
  python tools/sync_github_export.py             # apply
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MIRROR = ROOT / "GitHub Export"

# Top-level files mirrored verbatim.
ROOT_FILES = [
    ".gitignore", "README.md", "CHANGELOG.md", "LICENSE", "WirelessScanner.slnx", "publish.bat", "Directory.Build.props",
    "Screenshot1.jpg", "Screenshot2.jpg", "Screenshot3.jpg",
    "index.html", "package.json", "package-lock.json",
]
# Directories mirrored recursively (subject to EXCLUDE).
ROOT_DIRS = [
    "WirelessScanner.Domain", "WirelessScanner.Application", "WirelessScanner.Infrastructure",
    "WirelessScanner.Presentation", "WirelessScanner.Tests", "WirelessScanner.Setup", "WirelessScanner.Setup.Core",
    "WirelessScanner.Responder.Core", "WirelessScanner.Responder",
    "PLAN", "docs", "LessonsLearned", "config", "tools", "governance", "src", "branding",
    "AndroidApp",
]
# Never mirrored, anywhere in the tree.
EXCLUDE = [
    "bin", "obj", ".vs", "node_modules", "publish", "dist", "__pycache__", "TestResults",
    "*.db", "*.db-shm", "*.db-wal", "*.sqlite", "API", "api_key.*", "logs", "*.log",
    "*.user", "*.suo", "*.pdb", ".claude",
    # Raw RF captures name nearby SSIDs/BSSIDs (geolocatable); evidence cites redacted summaries instead.
    "local-only",
    # Android: build output, machine paths, the sideload signing key, built APKs and device run records (APKs ship as Release assets).
    ".gradle", ".kotlin", "build", "local.properties", "*.keystore", "*.jks", "*.apk", "*.aab", "artifacts",
]
# Mirror-owned paths the sync must never delete.
PROTECTED = [".git", "EXE INSTALLER", "ANDROID APK", "WirelessScannerCompiled.zip"]


def _unsealed_review(rel: Path) -> bool:
    """Review ledgers change with every attested step; mirror them only once sealed
    (lesson LL-2026-10-15-BASELINE-GOVERNANCE, iteration SYNC-CHECK-1)."""
    parts = rel.parts
    if len(parts) < 3 or parts[0] != "governance" or parts[1] != "reviews":
        return False
    ledger = ROOT / "governance" / "reviews" / parts[2] / "ATTESTATION-LEDGER.jsonl"
    try:
        lines = ledger.read_text(encoding="utf-8").strip().splitlines()
        return not lines or json.loads(lines[-1]).get("step", {}).get("kind") != "seal"
    except (OSError, ValueError):
        return True


def excluded(rel: Path) -> bool:
    return any(fnmatch.fnmatch(part, pat) for part in rel.parts for pat in EXCLUDE) or _unsealed_review(rel)


def digest(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def canonical_files() -> dict[str, Path]:
    files: dict[str, Path] = {}
    for name in ROOT_FILES:
        p = ROOT / name
        if p.is_file():
            files[name] = p
    for d in ROOT_DIRS:
        base = ROOT / d
        if not base.is_dir():
            continue
        for p in base.rglob("*"):
            if p.is_file():
                rel = p.relative_to(ROOT)
                if not excluded(rel):
                    files[rel.as_posix()] = p
    return files


def mirror_files() -> dict[str, Path]:
    files: dict[str, Path] = {}
    if not MIRROR.is_dir():
        return files
    for name in ROOT_FILES:
        p = MIRROR / name
        if p.is_file():
            files[name] = p
    for d in ROOT_DIRS:
        base = MIRROR / d
        if base.is_dir():
            for p in base.rglob("*"):
                if p.is_file():
                    rel = p.relative_to(MIRROR)
                    if not excluded(rel):
                        files[rel.as_posix()] = p
    return files


def plan() -> dict[str, list[str]]:
    src, dst = canonical_files(), mirror_files()
    add = sorted(k for k in src if k not in dst)
    update = sorted(k for k in src if k in dst and digest(src[k]) != digest(dst[k]))
    remove = sorted(k for k in dst if k not in src and k.split("/")[0] not in PROTECTED)
    unmanaged = sorted(p.name for p in MIRROR.iterdir() if p.name not in PROTECTED and p.name not in ROOT_FILES and p.name not in ROOT_DIRS) if MIRROR.is_dir() else []
    return {"add": add, "update": update, "remove": remove, "unmanaged_top_level": unmanaged}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not (MIRROR / ".git").exists():
        print(f"mirror is not a git clone: {MIRROR}", file=sys.stderr)
        return 2
    p = plan()
    differs = bool(p["add"] or p["update"] or p["remove"])
    summary = {k: len(v) for k, v in p.items()}
    if a.check or a.dry_run:
        print(json.dumps({"in_sync": not differs, "summary": summary, **({"actions": p} if a.dry_run else {"sample": {k: v[:10] for k, v in p.items()}})}, indent=2))
        return 1 if (a.check and differs) else 0
    src = canonical_files()
    for k in p["add"] + p["update"]:
        target = MIRROR / k
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src[k], target)
    for k in p["remove"]:
        (MIRROR / k).unlink()
    # prune empty directories left behind inside mirrored roots
    for d in ROOT_DIRS:
        base = MIRROR / d
        if base.is_dir():
            for sub in sorted((x for x in base.rglob("*") if x.is_dir()), key=lambda x: len(x.parts), reverse=True):
                if not any(sub.iterdir()):
                    sub.rmdir()
    after = plan()
    print(json.dumps({"applied": summary, "in_sync_after": not (after["add"] or after["update"] or after["remove"]), "unmanaged_top_level": after["unmanaged_top_level"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
