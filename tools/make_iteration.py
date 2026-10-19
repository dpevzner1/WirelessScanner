"""Create governance/iterations/<ID>.json from a compact JSON spec, filling the engine's
REMEDIATION_ITERATION.v2 template so every required field is present (project-owned helper).

Usage: python tools/make_iteration.py <spec.json>   then   python tools/ws_control.py iteration-add --json governance/iterations/<ID>.json
Spec keys mirror the template; nested dicts are merged into the template defaults.
"""
import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "LessonsLearned/templates/REMEDIATION_ITERATION.v2.json"
ENV = {"os": "Windows 10 Pro 10.0.19045", "dotnet_sdk": "10.0.301", "python": "3.13", "shell": "Git Bash + PowerShell 5.1"}


def build(spec: dict) -> dict:
    it = copy.deepcopy(json.loads(TEMPLATE.read_text(encoding="utf-8")))
    for key, value in spec.items():
        if isinstance(value, dict) and isinstance(it.get(key), dict):
            it[key].update(value)
        else:
            it[key] = value
    it["reproduction"].setdefault("environment", ENV)
    if not it["reproduction"].get("environment"):
        it["reproduction"]["environment"] = ENV
    return it


def main() -> None:
    specs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    for spec in specs if isinstance(specs, list) else [specs]:
        it = build(spec)
        out = ROOT / "governance/iterations" / f"{it['id']}.json"
        if out.exists():
            raise SystemExit(f"iteration file exists (append a new id): {out}")
        out.write_text(json.dumps(it, indent=2) + "\n", encoding="utf-8")
        print(out.relative_to(ROOT).as_posix())


if __name__ == "__main__":
    main()
