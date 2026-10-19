"""Mutation check: prove a test detects a defect by re-introducing it temporarily.

Usage (from the project root):
  python tools/mutation_check.py --file <source> --find "<exact text>" --replace "<mutant text>" --filter "<dotnet test filter>"

Steps: verify the text occurs exactly once -> apply the mutant -> build the test project -> run the filtered tests
-> ALWAYS restore the original source and rebuild. Exit code 0 = mutant killed (tests failed, as they should);
1 = mutant survived (tests passed: the test does not guard the behaviour); 2 = setup/build error.
The restore is verified by SHA-256 so the working tree is left exactly as it was.
"""
from __future__ import annotations

import argparse
import hashlib
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TESTS = "WirelessScanner.Tests/WirelessScanner.Tests.csproj"


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def run(cmd: list[str]) -> subprocess.CompletedProcess:
    print("$ " + " ".join(cmd), flush=True)
    return subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", required=True)
    ap.add_argument("--find", required=True)
    ap.add_argument("--replace", required=True)
    ap.add_argument("--filter", required=True)
    a = ap.parse_args()

    path = ROOT / a.file
    original = path.read_bytes()
    text = original.decode("utf-8")
    if text.count(a.find) != 1:
        print(f"ERROR: expected exactly one occurrence of the target text in {a.file}, found {text.count(a.find)}")
        return 2
    print(f"source {a.file} sha256 {sha(original)}")
    print(f"mutant: replace {a.find!r} -> {a.replace!r}")
    verdict = 2
    try:
        path.write_bytes(text.replace(a.find, a.replace).encode("utf-8"))
        b = run(["dotnet", "build", TESTS, "-v", "q"])
        if b.returncode != 0:
            print(b.stdout[-3000:])
            print("ERROR: mutant does not build")
            return 2
        t = run(["dotnet", "test", TESTS, "--no-build", "--filter", a.filter])
        summary = [l for l in t.stdout.splitlines() if "Passed!" in l or "Failed!" in l or l.strip().startswith("Failed ")]
        print("\n".join(summary))
        verdict = 0 if t.returncode != 0 else 1
        print("RESULT: mutant KILLED (tests detect the defect)" if verdict == 0 else "RESULT: mutant SURVIVED (tests do not detect the defect)")
    finally:
        path.write_bytes(original)
        restored = sha(path.read_bytes())
        print(f"restored {a.file} sha256 {restored} ({'identical' if restored == sha(original) else 'MISMATCH'})")
        rb = run(["dotnet", "build", TESTS, "-v", "q"])
        print("rebuild of original: " + ("ok" if rb.returncode == 0 else "FAILED"))
        if restored != sha(original) or rb.returncode != 0:
            verdict = 2
    return verdict


if __name__ == "__main__":
    sys.exit(main())
