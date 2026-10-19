#!/usr/bin/env python3
"""Validate the registered iterative lessons-learned records."""

from __future__ import annotations

import json
from datetime import date, datetime
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
LESSONS_ROOT = PROJECT_ROOT / "LessonsLearned"
REGISTRY_PATH = LESSONS_ROOT / "lesson-register.json"
RECORDS_ROOT = LESSONS_ROOT / "records"

REQUIRED_RECORD_FIELDS = {
    "lesson_id",
    "title",
    "task",
    "project",
    "components",
    "observed_from",
    "observed_to",
    "local_date",
    "timezone",
    "updated_at",
    "status",
    "document",
    "related_run_ids",
}
ALLOWED_STATUSES = {"observed", "corrected", "validated", "superseded"}
REQUIRED_HEADINGS = [
    "## Executive outcome",
    "## Expected behavior",
    "## Initial conditions and boundaries",
    "## Iteration log",
    "## Actual behavior and deviations",
    "## Failure, root-cause, and remediation register",
    "## Root-cause narrative",
    "## Corrections and changed artifacts",
    "## Validation",
    "## Engine capability impact",
    "## What worked",
    "## What did not work",
    "## Resume and anti-duplication map",
    "## Reusable prevention",
    "## Follow-up tasks",
    "## Residual risk",
    "## Supersession",
    "## Completeness attestation",
]


def parse_timestamp(value: Any, field: str, lesson_id: str, errors: list[str]) -> None:
    if not isinstance(value, str) or not value.strip():
        errors.append(f"{lesson_id}: {field} must be a non-empty ISO-8601 timestamp")
        return
    try:
        datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        errors.append(f"{lesson_id}: {field} is not ISO-8601: {value!r}")


def validate_record(record: Any, errors: list[str]) -> str | None:
    if not isinstance(record, dict):
        errors.append("registry record must be an object")
        return None

    missing = sorted(REQUIRED_RECORD_FIELDS - set(record))
    lesson_id = str(record.get("lesson_id", "<missing-id>"))
    if missing:
        errors.append(f"{lesson_id}: missing registry fields: {', '.join(missing)}")

    if not lesson_id.startswith("LL-"):
        errors.append(f"{lesson_id}: lesson_id must start with 'LL-'")
    if record.get("status") not in ALLOWED_STATUSES:
        errors.append(
            f"{lesson_id}: status must be one of {sorted(ALLOWED_STATUSES)}"
        )
    for field in ("title", "task", "project", "timezone", "document"):
        if not isinstance(record.get(field), str) or not record[field].strip():
            errors.append(f"{lesson_id}: {field} must be a non-empty string")

    components = record.get("components")
    if (
        not isinstance(components, list)
        or not components
        or any(not isinstance(item, str) or not item.strip() for item in components)
    ):
        errors.append(f"{lesson_id}: components must be a non-empty string array")

    run_ids = record.get("related_run_ids")
    if not isinstance(run_ids, list) or any(
        not isinstance(item, str) or not item.strip() for item in run_ids
    ):
        errors.append(f"{lesson_id}: related_run_ids must be a string array")

    parse_timestamp(record.get("observed_from"), "observed_from", lesson_id, errors)
    parse_timestamp(record.get("updated_at"), "updated_at", lesson_id, errors)
    if record.get("observed_to") is not None:
        parse_timestamp(record.get("observed_to"), "observed_to", lesson_id, errors)
    try:
        date.fromisoformat(str(record.get("local_date")))
    except ValueError:
        errors.append(
            f"{lesson_id}: local_date is not an ISO-8601 date: "
            f"{record.get('local_date')!r}"
        )

    document = record.get("document")
    if not isinstance(document, str) or not document:
        return lesson_id
    document_path = (PROJECT_ROOT / document).resolve()
    records_root = RECORDS_ROOT.resolve()
    if records_root not in document_path.parents:
        errors.append(f"{lesson_id}: document must be under LessonsLearned/records")
        return lesson_id
    if not document_path.is_file():
        errors.append(f"{lesson_id}: document does not exist: {document}")
        return lesson_id

    text = document_path.read_text(encoding="utf-8")
    if lesson_id not in text:
        errors.append(f"{lesson_id}: document does not contain its lesson ID")
    for heading in REQUIRED_HEADINGS:
        if heading not in text:
            errors.append(f"{lesson_id}: missing heading {heading!r}")
    return lesson_id


def main() -> int:
    errors: list[str] = []
    if not REGISTRY_PATH.is_file():
        print(json.dumps({"passed": False, "errors": ["lesson register is missing"]}))
        return 1

    try:
        registry = json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"passed": False, "errors": [f"invalid register: {exc}"]}))
        return 1

    if registry.get("schema_version") != "1.0":
        errors.append("lesson register schema_version must be '1.0'")
    for key in ("standard", "template"):
        value = registry.get(key)
        if not isinstance(value, str) or not (PROJECT_ROOT / value).is_file():
            errors.append(f"registry {key} must reference an existing file")

    records = registry.get("records")
    if not isinstance(records, list):
        errors.append("lesson register records must be an array")
        records = []

    lesson_ids: list[str] = []
    registered_documents: set[Path] = set()
    for record in records:
        lesson_id = validate_record(record, errors)
        if lesson_id:
            lesson_ids.append(lesson_id)
        if isinstance(record, dict) and isinstance(record.get("document"), str):
            registered_documents.add((PROJECT_ROOT / record["document"]).resolve())

    duplicates = sorted(
        lesson_id for lesson_id in set(lesson_ids) if lesson_ids.count(lesson_id) > 1
    )
    if duplicates:
        errors.append(f"duplicate lesson IDs: {', '.join(duplicates)}")

    disk_documents = (
        {path.resolve() for path in RECORDS_ROOT.glob("*.md")}
        if RECORDS_ROOT.is_dir()
        else set()
    )
    for path in sorted(disk_documents - registered_documents):
        errors.append(
            f"unregistered lesson record: {path.relative_to(PROJECT_ROOT).as_posix()}"
        )
    for path in sorted(registered_documents - disk_documents):
        errors.append(
            f"registered lesson record is missing: "
            f"{path.relative_to(PROJECT_ROOT).as_posix()}"
        )

    result = {
        "schema_version": "1.0",
        "passed": not errors,
        "registered_records": len(records),
        "record_files": len(disk_documents),
        "errors": errors,
    }
    print(json.dumps(result, indent=2))
    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
