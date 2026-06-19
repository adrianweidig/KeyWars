#!/usr/bin/env python3
"""Fail if generated runtime artifacts are tracked by git."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path


FORBIDDEN = [
    re.compile(r"(^|/)(bin|obj|App_Data|TestResults|coverage)/", re.IGNORECASE),
    re.compile(r"(^|/)dataprotection-keys/.*\.xml$", re.IGNORECASE),
    re.compile(r"\.(db|db-wal|db-shm|log|tmp|bak|cache|pid|trx)$", re.IGNORECASE),
    re.compile(r"\.(7z|zip|tar|tar\.gz|tgz|nupkg|snupkg|coverage)$", re.IGNORECASE),
    re.compile(r"\.(pem|key|pfx|p12)$", re.IGNORECASE),
    re.compile(r"(^|/)\.env($|[^.].*)", re.IGNORECASE),
]

ALLOW = {
    ".env.example",
}
REQUIRED = [
    "LICENSE",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    ".github/CODEOWNERS",
    ".github/dependabot.yml",
    ".editorconfig",
]
TEXT_FILE_SUFFIXES = {
    ".cs", ".cshtml", ".css", ".editorconfig", ".env", ".example", ".js",
    ".json", ".md", ".ps1", ".py", ".sh", ".txt", ".xml", ".yml", ".yaml",
}
MOJIBAKE_MARKERS = (
    "\u00c3", "\u00c2", "\ufffd",
    "\u00e2\u20ac", "\u00e2\u20ac\u2122", "\u00e2\u20ac\u0153",
    "\u00e2\u20ac\ufffd", "\u00e2\u20ac\u201c", "\u00e2\u20ac\u201d",
)


def tracked_files() -> list[str]:
    result = subprocess.run(
        ["git", "ls-files"],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return [line.strip().replace("\\", "/") for line in result.stdout.splitlines() if line.strip()]


def main() -> int:
    files = tracked_files()
    missing_required = [path for path in REQUIRED if path not in files]
    if missing_required:
        print("Required repository files are missing:", file=sys.stderr)
        for path in missing_required:
            print(f"  {path}", file=sys.stderr)
        return 1

    violations = [
        path
        for path in files
        if path not in ALLOW and any(pattern.search(path) for pattern in FORBIDDEN)
    ]
    if violations:
        print("Forbidden generated or local runtime files are tracked:", file=sys.stderr)
        for path in violations:
            print(f"  {path}", file=sys.stderr)
        return 1

    mojibake = [
        path
        for path in files
        if is_text_file(path) and contains_mojibake(path)
    ]
    if mojibake:
        print("Possible mojibake or replacement characters found:", file=sys.stderr)
        for path in mojibake:
            print(f"  {path}", file=sys.stderr)
        return 1

    print("Repository hygiene: OK")
    return 0


def is_text_file(path: str) -> bool:
    file_path = Path(path)
    if file_path.name in ALLOW:
        return True

    return file_path.suffix.lower() in TEXT_FILE_SUFFIXES


def contains_mojibake(path: str) -> bool:
    try:
        content = Path(path).read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return True

    return any(marker in content for marker in MOJIBAKE_MARKERS)


if __name__ == "__main__":
    raise SystemExit(main())
