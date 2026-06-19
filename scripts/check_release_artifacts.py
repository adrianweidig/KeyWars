#!/usr/bin/env python3
"""Validate KeyWars release artifact structure and checksums."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import tarfile
import tempfile
from pathlib import Path


REQUIRED_DOCKER_SAVE_ENTRIES = {"manifest.json", "repositories"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_checksums(path: Path) -> dict[str, str]:
    checksums: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        digest, filename = line.split(maxsplit=1)
        checksums[filename.strip().lstrip("*")] = digest
    return checksums


def validate(artifact_dir: Path, version: str) -> list[str]:
    errors: list[str] = []
    expected_archive = artifact_dir / f"keywars-{version}-linux-amd64.tar.gz"
    required = [
        expected_archive,
        artifact_dir / "compose.yaml",
        artifact_dir / ".env.example",
        artifact_dir / "RELEASE_MANIFEST.json",
        artifact_dir / "SHA256SUMS",
    ]

    for path in required:
        if not path.exists():
            errors.append(f"Missing release artifact: {path.name}")
        elif path.stat().st_size == 0:
            errors.append(f"Empty release artifact: {path.name}")

    if errors:
        return errors

    checksums = parse_checksums(artifact_dir / "SHA256SUMS")
    for path in required[:-1]:
        expected = checksums.get(path.name)
        if expected is None:
            errors.append(f"SHA256SUMS does not cover {path.name}")
            continue
        actual = sha256(path)
        if actual != expected:
            errors.append(f"Checksum mismatch for {path.name}: expected {expected}, got {actual}")

    try:
        manifest = json.loads((artifact_dir / "RELEASE_MANIFEST.json").read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        errors.append(f"RELEASE_MANIFEST.json is not valid JSON: {error}")
        manifest = {}

    if manifest.get("version") != version:
        errors.append(f"Release manifest version is {manifest.get('version')!r}, expected {version!r}")
    image = manifest.get("image", {})
    digest = image.get("digest", "")
    if not isinstance(digest, str) or not digest.startswith("sha256:"):
        errors.append("Release manifest image.digest must be a sha256 digest")
    if not image.get("tags"):
        errors.append("Release manifest image.tags must list pushed GHCR tags")
    archives = manifest.get("offline_archives", [])
    if not any(item.get("file") == expected_archive.name and item.get("platform") == "linux/amd64" for item in archives):
        errors.append("Release manifest must list the linux/amd64 offline image archive")

    try:
        with gzip.open(expected_archive, "rb") as gzipped:
            with tarfile.open(fileobj=gzipped, mode="r:") as archive:
                names = {Path(member.name).parts[0] for member in archive.getmembers() if member.name}
    except (OSError, tarfile.TarError) as error:
        errors.append(f"Offline image archive is not a readable docker-save tar.gz: {error}")
    else:
        missing_entries = sorted(REQUIRED_DOCKER_SAVE_ENTRIES - names)
        if missing_entries:
            errors.append("Offline image archive is missing docker-save entries: " + ", ".join(missing_entries))

    return errors


def write_file(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")


def create_sample_archive(path: Path) -> None:
    with gzip.open(path, "wb") as gzipped:
        with tarfile.open(fileobj=gzipped, mode="w:") as archive:
            for name, content in {
                "manifest.json": "[]\n",
                "repositories": "{}\n",
            }.items():
                data = content.encode("utf-8")
                info = tarfile.TarInfo(name)
                info.size = len(data)
                archive.addfile(info, fileobj=io.BytesIO(data))


def self_test() -> int:
    version = "v0.0.0-test"
    with tempfile.TemporaryDirectory(prefix="keywars-release-check-") as temp:
        artifact_dir = Path(temp)
        archive = artifact_dir / f"keywars-{version}-linux-amd64.tar.gz"
        create_sample_archive(archive)
        write_file(artifact_dir / "compose.yaml", "services:\n  keywars:\n    image: ghcr.io/example/keywars:v0.0.0-test\n")
        write_file(artifact_dir / ".env.example", "ASPNETCORE_ENVIRONMENT=Production\n")
        write_file(
            artifact_dir / "RELEASE_MANIFEST.json",
            json.dumps(
                {
                    "schema_version": 1,
                    "version": version,
                    "image": {
                        "repository": "ghcr.io/example/keywars",
                        "tags": ["ghcr.io/example/keywars:v0.0.0-test"],
                        "digest": "sha256:" + "0" * 64,
                        "sbom": "registry-attached",
                        "provenance": "registry-attached",
                    },
                    "offline_archives": [
                        {"file": archive.name, "platform": "linux/amd64", "format": "docker-save-tar-gzip"}
                    ],
                },
                indent=2,
            )
            + "\n",
        )
        checksum_lines = []
        for path in sorted(item for item in artifact_dir.iterdir() if item.name != "SHA256SUMS"):
            checksum_lines.append(f"{sha256(path)}  {path.name}")
        write_file(artifact_dir / "SHA256SUMS", "\n".join(checksum_lines) + "\n")
        errors = validate(artifact_dir, version)
        if errors:
            print("Release artifact self-test failed:")
            for error in errors:
                print(f"  {error}")
            return 1

    print("Release artifact self-test: OK")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_dir", nargs="?", type=Path)
    parser.add_argument("version", nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.artifact_dir is None or args.version is None:
        parser.error("artifact_dir and version are required unless --self-test is used")

    errors = validate(args.artifact_dir, args.version)
    if errors:
        print("Release artifact check failed:")
        for error in errors:
            print(f"  {error}")
        return 1

    print("Release artifact check: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
