"""Build the WirelessScanner Android release APK and assemble "GitHub Export/ANDROID APK" (DR-0015; counterpart of
tools/build_release.py, which assembles "EXE INSTALLER").

Steps
  1. gradlew :core:test :app:assembleRelease (signing secrets come from AndroidApp/local.properties, never from source)
  2. verify the APK signature (apksigner) and record the signer certificate SHA-256
  3. copy it to AndroidApp/artifacts/deploy/WirelessScanner-Android-<version>.apk
  4. assemble "GitHub Export/ANDROID APK": the APK, a README (install guide + SHA-256 + signer) and android-release.json

Usage (project root):  python tools/build_android_release.py [--skip-build]
Never commits or pushes. Refuses an unsigned APK (users could not update over an earlier install).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ANDROID = ROOT / "AndroidApp"
OUT_DIR = ROOT / "GitHub Export" / "ANDROID APK"
BUILT = ANDROID / "app" / "build" / "outputs" / "apk" / "release" / "app-release.apk"
GUIDE = ANDROID / "docs" / "INSTALL_GUIDE.md"


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def version() -> tuple[str, int]:
    s = (ANDROID / "app" / "build.gradle.kts").read_text(encoding="utf-8")
    return re.search(r'appVersionName = "([^"]+)"', s).group(1), int(re.search(r"appVersionCode = (\d+)", s).group(1))


def apksigner() -> Path:
    tools = sorted((Path(os.environ["LOCALAPPDATA"]) / "Android" / "Sdk" / "build-tools").iterdir())
    return tools[-1] / ("apksigner.bat" if os.name == "nt" else "apksigner")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-build", action="store_true")
    a = ap.parse_args()
    name, code = version()
    if not a.skip_build:
        gradlew = ANDROID / ("gradlew.bat" if os.name == "nt" else "gradlew")
        subprocess.run([str(gradlew), ":core:test", ":app:assembleRelease", "-q"], cwd=ANDROID, check=True)
    if not BUILT.exists():
        sys.exit(f"missing {BUILT}")
    v = subprocess.run([str(apksigner()), "verify", "--print-certs", str(BUILT)], capture_output=True, text=True)
    m = re.search(r"Signer #1 certificate SHA-256 digest: ([0-9a-f]+)", v.stdout)
    if v.returncode != 0 or not m:
        sys.exit("APK is unsigned or fails verification; set sideload.* in AndroidApp/local.properties (docs/DEPLOYMENT.md, Signing)")
    signer = m.group(1)
    apk_name = f"WirelessScanner-Android-{name}.apk"
    deploy = ANDROID / "artifacts" / "deploy" / apk_name
    deploy.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BUILT, deploy)
    digest = sha256(deploy)

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for old in OUT_DIR.glob("WirelessScanner-Android-*.apk"):
        old.unlink()
    shutil.copy2(deploy, OUT_DIR / apk_name)
    guide = GUIDE.read_text(encoding="utf-8").split("\n", 1)[1].lstrip()
    readme = (
        f"# WirelessScanner {name} — Android APK\n\n"
        f"| File | `{apk_name}` |\n| --- | --- |\n"
        f"| Version | {name} (versionCode {code}) — same feature version as the Windows installer in [`EXE INSTALLER`](../EXE%20INSTALLER/) |\n"
        f"| Size | {deploy.stat().st_size:,} bytes |\n"
        f"| SHA-256 | `{digest}` |\n"
        f"| Signer certificate SHA-256 | `{signer}` (sideload key; updates install over earlier builds signed with it) |\n"
        f"| Android | 8.0 or newer (minSdk 26); tested on a Samsung Galaxy Tab A8, Android 14 |\n\n"
        "Check the download before installing (Windows PowerShell): `Get-FileHash .\\" + apk_name + "` — it must match the SHA-256 above.\n\n"
        "Source, design and parity documents: [`AndroidApp/`](../AndroidApp/README.md).\n\n"
        + guide
    )
    (OUT_DIR / "README.md").write_text(readme, encoding="utf-8")
    meta = {"version": name, "versionCode": code, "file": apk_name, "bytes": deploy.stat().st_size, "sha256": digest,
            "signerCertSha256": signer, "built_at": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
            "tool": "tools/build_android_release.py"}
    (OUT_DIR / "android-release.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(meta, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
