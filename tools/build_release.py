"""Build a WirelessScanner release and assemble the GitHub-ready installer folder (DR-0007).

Steps
  1. dotnet publish the app (self-contained, single file, win-x64, no PDBs)   -> artifacts/release/app
  2. zip it                                                                    -> artifacts/release/WirelessScanner.Presentation.zip
  3. build the .NET Framework 4.8 setup wizard (Release)                       -> artifacts/release/setup
  4. split the zip into 20 MB parts + write WirelessScanner.payload.json (version, size, SHA-256, part list)
  5. assemble "GitHub Export/EXE INSTALLER" (setup + parts + payload + README)
  6. publish the LAN responder (portable, framework-dependent) -> EXE INSTALLER/WirelessScanner.Responder.zip (DR-0014 stage C)

Usage (project root):  python tools/build_release.py [--skip-tests]
Never commits or pushes; never touches ./publish (the owner's local run folder) or user data.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts" / "release"
INSTALLER_DIR = ROOT / "GitHub Export" / "EXE INSTALLER"
CHUNK = 20 * 1024 * 1024


def run(cmd: list[str]) -> None:
    print("  >>", " ".join(cmd))
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        sys.exit(f"command failed ({r.returncode}): {' '.join(cmd)}")


def version() -> str:
    text = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", text)
    if not m:
        sys.exit("No <Version> in Directory.Build.props")
    return m.group(1).strip()


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


README = """# WirelessScanner {version} — Installer

WirelessScanner is a Wi-Fi signal analysis, site-survey and **signal saturation test** tool for Windows 10 (2004+) / 11.

## Install
1. Download **every file in this folder** into one local folder:
   `WirelessScanner.Setup.exe`, `WirelessScanner.Setup.exe.config`, `WirelessScanner.Setup.Core.dll`,
   `WirelessScanner.payload.json` and all `WirelessScanner##.bin` parts ({parts} parts).
2. Run `WirelessScanner.Setup.exe` (it asks for administrator rights).
3. Choose the folder (default `C:\\Program Files\\WirelessScanner`) and shortcuts, then **Install**.

No prerequisites: the installer uses .NET Framework 4.8 (built into Windows 10/11) and the application is self-contained.
The setup verifies the reassembled package (size {size:,} bytes, SHA-256 `{sha}`) before installing.

## Update, repair, modify, uninstall
- **Update / repair / downgrade:** run this setup again; it detects the installed version and offers the right action
  (older versions require confirmation).
- **Modify shortcuts / uninstall:** *Settings → Apps → WirelessScanner* (Modify or Uninstall), or run setup again.
- **Your data** (sessions, saturation tests, logs, API key) lives in `C:\\ProgramData\\WirelessScanner` and is kept on
  update and uninstall unless you tick *Also permanently delete all WirelessScanner data*.

## Optional: LAN responder (two-way Wi-Fi tests)
`WirelessScanner.Responder.zip` (SHA-256 `{responder_sha}`) is NOT part of the app. Unzip it on a **wired** machine on the same
network (Windows, Linux or Raspberry Pi with the .NET 10 runtime) and run `dotnet WirelessScanner.Responder.dll`. It prints a
pairing key; enter its address and key in WirelessScanner: *SETTINGS → Test servers*. Allow TCP/UDP 5410 for the local network only.

Repository: https://github.com/dpevzner1/WirelessScanner — full documentation in `docs/USER_GUIDE.md`.
"""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-tests", action="store_true")
    a = ap.parse_args()
    ver = version()
    print(f"[0/5] WirelessScanner {ver}")
    if not a.skip_tests:
        run(["dotnet", "test", "WirelessScanner.Tests/WirelessScanner.Tests.csproj", "-c", "Release"])

    if OUT.exists():
        shutil.rmtree(OUT)
    app, setup = OUT / "app", OUT / "setup"

    print("[1/5] Publishing application (self-contained single file)")
    run(["dotnet", "publish", "WirelessScanner.Presentation/WirelessScanner.Presentation.csproj", "-c", "Release", "-r", "win-x64",
         "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
         "-p:DebugType=none", "-p:DebugSymbols=false", "-o", str(app)])

    print("[2/5] Zipping application")
    zip_path = OUT / "WirelessScanner.Presentation.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(app.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(app).as_posix())

    print("[3/5] Building setup wizard (.NET Framework 4.8)")
    run(["dotnet", "build", "WirelessScanner.Setup/WirelessScanner.Setup.csproj", "-c", "Release", "-p:DebugType=none", "-o", str(setup)])

    print("[4/5] Splitting payload and writing manifest")
    parts: list[str] = []
    with zip_path.open("rb") as f:
        i = 1
        while chunk := f.read(CHUNK):
            name = f"WirelessScanner{i:02d}.bin"
            (OUT / name).write_bytes(chunk)
            parts.append(name)
            i += 1
    payload = {"version": ver, "sha256": sha256(zip_path), "bytes": zip_path.stat().st_size, "parts": parts,
               "builtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    (OUT / "WirelessScanner.payload.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    print(f"[5/5] Assembling {INSTALLER_DIR}")
    if INSTALLER_DIR.exists():
        shutil.rmtree(INSTALLER_DIR)
    INSTALLER_DIR.mkdir(parents=True)
    for f in setup.iterdir():
        if f.suffix.lower() in {".exe", ".dll", ".config"}:
            shutil.copy2(f, INSTALLER_DIR / f.name)
    for name in parts + ["WirelessScanner.payload.json"]:
        shutil.copy2(OUT / name, INSTALLER_DIR / name)

    print("[6/6] Publishing the LAN responder (portable)")
    responder = OUT / "responder"
    run(["dotnet", "publish", "WirelessScanner.Responder/WirelessScanner.Responder.csproj", "-c", "Release", "-p:DebugType=none",
         "-p:DebugSymbols=false", "-o", str(responder)])
    (responder / "README.txt").write_text(
        f"WirelessScanner responder {ver} (DR-0014 stage C)\n\nRun on a WIRED machine with the .NET 10 runtime:\n  dotnet WirelessScanner.Responder.dll [--port 5410] [--name \"Lab Pi\"]\n"
        "First run prints the pairing key (later: --show-key). Pair it in WirelessScanner: SETTINGS -> Test servers.\n"
        "Allow TCP and UDP 5410 for the local network only. Only authenticated traffic is answered.\n", encoding="utf-8")
    responder_zip = INSTALLER_DIR / "WirelessScanner.Responder.zip"
    with zipfile.ZipFile(responder_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(responder.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(responder).as_posix())
    (INSTALLER_DIR / "README.md").write_text(README.format(version=ver, parts=len(parts), size=payload["bytes"], sha=payload["sha256"],
                                                          responder_sha=sha256(responder_zip)), encoding="utf-8")

    summary = {"version": ver, "payload": payload, "responder": {"file": responder_zip.name, "sha256": sha256(responder_zip), "bytes": responder_zip.stat().st_size},
               "installer_files": sorted(p.name for p in INSTALLER_DIR.iterdir()),
               "largest_file_mb": round(max(p.stat().st_size for p in INSTALLER_DIR.iterdir()) / 1048576, 1)}
    (OUT / "release-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
