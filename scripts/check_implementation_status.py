#!/usr/bin/env python3
"""Validate docs/implementation-status.md against the audit issue list."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
STATUS_FILE = ROOT / "docs" / "implementation-status.md"
EXPECTED_IDS = [
    "KW-000", "KW-001", "KW-002", "KW-003",
    "KW-010", "KW-011", "KW-012", "KW-013", "KW-014", "KW-015",
    "KW-016", "KW-017", "KW-018",
    "KW-020", "KW-021", "KW-022", "KW-023", "KW-024", "KW-025",
    "KW-026", "KW-027",
    "KW-030", "KW-031", "KW-032", "KW-033", "KW-034",
    "KW-040", "KW-041", "KW-042", "KW-043",
    "KW-050", "KW-051", "KW-052", "KW-053", "KW-054", "KW-055",
]
ALLOWED_STATUS = {"offen", "in arbeit", "teilweise", "blockiert", "erledigt"}


def main() -> int:
    if not STATUS_FILE.exists():
        print(f"Missing {STATUS_FILE.relative_to(ROOT)}", file=sys.stderr)
        return 1

    lines = STATUS_FILE.read_text(encoding="utf-8").splitlines()
    rows: dict[str, str] = {}
    for line in lines:
        match = re.match(r"^\|\s*(KW-\d{3})\s*\|\s*([^|]+?)\s*\|", line)
        if not match:
            continue
        issue_id, status = match.group(1), match.group(2).strip().lower()
        if issue_id in rows:
            print(f"Duplicate issue id: {issue_id}", file=sys.stderr)
            return 1
        rows[issue_id] = status

    missing = [issue_id for issue_id in EXPECTED_IDS if issue_id not in rows]
    unexpected = [issue_id for issue_id in rows if issue_id not in EXPECTED_IDS]
    invalid_status = [f"{issue_id}: {status}" for issue_id, status in rows.items() if status not in ALLOWED_STATUS]
    if missing or unexpected or invalid_status:
        if missing:
            print("Missing issue ids: " + ", ".join(missing), file=sys.stderr)
        if unexpected:
            print("Unexpected issue ids: " + ", ".join(unexpected), file=sys.stderr)
        if invalid_status:
            print("Invalid statuses: " + ", ".join(invalid_status), file=sys.stderr)
        return 1

    print("Implementation status: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
