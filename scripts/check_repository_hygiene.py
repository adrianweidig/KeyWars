#!/usr/bin/env python3
"""Fail if generated runtime artifacts are tracked by git."""

from __future__ import annotations

import re
import subprocess
import sys


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
    missing_required = [path for path in REQUIRED if path not in tracked_files()]
    if missing_required:
        print("Required repository files are missing:", file=sys.stderr)
        for path in missing_required:
            print(f"  {path}", file=sys.stderr)
        return 1

    violations = [
        path
        for path in tracked_files()
        if path not in ALLOW and any(pattern.search(path) for pattern in FORBIDDEN)
    ]
    if violations:
        print("Forbidden generated or local runtime files are tracked:", file=sys.stderr)
        for path in violations:
            print(f"  {path}", file=sys.stderr)
        return 1

    print("Repository hygiene: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
