#!/usr/bin/env python3
"""Validate that a KeyWars release is complete and internally consistent."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import re
import tarfile
import tempfile
from pathlib import Path


STABLE_VERSION = re.compile(r"^v\d+\.\d+\.\d+$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
CHECKSUM = re.compile(r"^[0-9a-f]{64}$")
IMAGE_REPOSITORY = re.compile(r"^ghcr\.io/[a-z0-9._-]+/keywars$")
REQUIRED_DOCKER_SAVE_ENTRIES = {"manifest.json", "repositories"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_checksums(path: Path) -> tuple[dict[str, str], list[str]]:
    checksums: dict[str, str] = {}
    errors: list[str] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        parts = line.split(maxsplit=1)
        if len(parts) != 2 or CHECKSUM.fullmatch(parts[0]) is None:
            errors.append(f"Invalid SHA256SUMS line {line_number}")
            continue
        filename = parts[1].strip().lstrip("*")
        if Path(filename).name != filename:
            errors.append(f"SHA256SUMS line {line_number} must contain a plain file name")
        elif filename in checksums:
            errors.append(f"SHA256SUMS lists {filename} more than once")
        else:
            checksums[filename] = parts[0]
    return checksums, errors


def parse_environment(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", maxsplit=1)
        values[key.strip()] = value.strip()
    return values


def validate_docker_archive(path: Path, image_ref: str) -> list[str]:
    errors: list[str] = []
    try:
        with gzip.open(path, "rb") as gzipped:
            with tarfile.open(fileobj=gzipped, mode="r:") as archive:
                names = set(archive.getnames())
                missing_entries = sorted(REQUIRED_DOCKER_SAVE_ENTRIES - names)
                if missing_entries:
                    errors.append(
                        "Offline image archive is missing docker-save entries: " + ", ".join(missing_entries)
                    )
                    return errors

                manifest_member = archive.extractfile("manifest.json")
                if manifest_member is None:
                    errors.append("Offline image archive manifest.json is not readable")
                    return errors
                docker_manifest = json.load(manifest_member)
    except (OSError, tarfile.TarError, json.JSONDecodeError) as error:
        return [f"Offline image archive is not a readable docker-save tar.gz: {error}"]

    if not isinstance(docker_manifest, list) or not docker_manifest:
        return ["Offline image archive manifest.json must contain at least one image"]

    repo_tags: set[str] = set()
    referenced_files: set[str] = set()
    for item in docker_manifest:
        if not isinstance(item, dict):
            errors.append("Offline image archive manifest.json contains an invalid image entry")
            continue
        tags = item.get("RepoTags", [])
        if isinstance(tags, list):
            repo_tags.update(tag for tag in tags if isinstance(tag, str))
        config = item.get("Config")
        if isinstance(config, str):
            referenced_files.add(config)
        layers = item.get("Layers", [])
        if isinstance(layers, list):
            referenced_files.update(layer for layer in layers if isinstance(layer, str))

    if image_ref not in repo_tags:
        errors.append(f"Offline image archive RepoTags must contain {image_ref}")
    missing_files = sorted(referenced_files - names)
    if missing_files:
        errors.append("Offline image archive is missing referenced files: " + ", ".join(missing_files))
    return errors


def validate(artifact_dir: Path, version: str) -> list[str]:
    errors: list[str] = []
    if STABLE_VERSION.fullmatch(version) is None:
        errors.append(f"Release version must use stable SemVer vMAJOR.MINOR.PATCH, got {version!r}")

    archive_name = f"keywars-{version}-linux-amd64.tar.gz"
    required_names = {
        archive_name,
        "compose.yaml",
        "default.env.example",
        "AIRGAP_INSTALL.md",
        "RELEASE_MANIFEST.json",
        "RELEASE_NOTES.md",
        "SHA256SUMS",
    }
    actual_names = {path.name for path in artifact_dir.iterdir()} if artifact_dir.is_dir() else set()
    for name in sorted(required_names):
        path = artifact_dir / name
        if not path.exists():
            errors.append(f"Missing release artifact: {name}")
        elif not path.is_file():
            errors.append(f"Release artifact is not a file: {name}")
        elif path.stat().st_size == 0:
            errors.append(f"Empty release artifact: {name}")
    for name in sorted(actual_names - required_names):
        errors.append(f"Unexpected release artifact: {name}")
    if any(not (artifact_dir / name).is_file() for name in required_names):
        return errors

    checksum_names = required_names - {"SHA256SUMS"}
    checksums, checksum_errors = parse_checksums(artifact_dir / "SHA256SUMS")
    errors.extend(checksum_errors)
    for name in sorted(checksum_names - checksums.keys()):
        errors.append(f"SHA256SUMS does not cover {name}")
    for name in sorted(checksums.keys() - checksum_names):
        errors.append(f"SHA256SUMS unexpectedly covers {name}")
    for name in sorted(checksum_names & checksums.keys()):
        actual = sha256(artifact_dir / name)
        if actual != checksums[name]:
            errors.append(f"Checksum mismatch for {name}: expected {checksums[name]}, got {actual}")

    try:
        manifest = json.loads((artifact_dir / "RELEASE_MANIFEST.json").read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        errors.append(f"RELEASE_MANIFEST.json is not valid JSON: {error}")
        manifest = {}
    if not isinstance(manifest, dict):
        errors.append("RELEASE_MANIFEST.json root must be an object")
        manifest = {}

    if manifest.get("schema_version") != 2:
        errors.append("Release manifest schema_version must be 2")
    if manifest.get("version") != version:
        errors.append(f"Release manifest version is {manifest.get('version')!r}, expected {version!r}")
    if manifest.get("release_notes") != "RELEASE_NOTES.md":
        errors.append("Release manifest must reference RELEASE_NOTES.md")

    image = manifest.get("image")
    if not isinstance(image, dict):
        image = {}
        errors.append("Release manifest image must be an object")
    repository = image.get("repository", "")
    if not isinstance(repository, str) or IMAGE_REPOSITORY.fullmatch(repository) is None:
        errors.append("Release manifest image.repository must be a lowercase ghcr.io owner/keywars path")
        repository = ""
    image_ref = f"{repository}:{version}" if repository else ""
    if image.get("image_ref") != image_ref:
        errors.append(f"Release manifest image.image_ref must be {image_ref!r}")
    expected_tags = {image_ref, f"{repository}:latest"} if repository else set()
    tags = image.get("tags")
    if (
        not isinstance(tags, list)
        or not all(isinstance(tag, str) for tag in tags)
        or set(tags) != expected_tags
    ):
        errors.append("Release manifest image.tags must contain exactly the version and latest GHCR references")
    digest = image.get("digest", "")
    if not isinstance(digest, str) or DIGEST.fullmatch(digest) is None:
        errors.append("Release manifest image.digest must be sha256 followed by 64 lowercase hex characters")

    archives = manifest.get("offline_archives")
    expected_archive = {
        "file": archive_name,
        "platform": "linux/amd64",
        "format": "docker-save-tar-gzip",
        "image_ref": image_ref,
    }
    if archives != [expected_archive]:
        errors.append("Release manifest must describe exactly the versioned linux/amd64 offline image archive")
    if manifest.get("deployment_files") != ["compose.yaml", "default.env.example"]:
        errors.append("Release manifest must list compose.yaml and default.env.example as deployment files")
    if manifest.get("documentation_files") != ["AIRGAP_INSTALL.md", "RELEASE_NOTES.md"]:
        errors.append("Release manifest must list the air-gap guide and release notes as documentation files")

    env_values = parse_environment(artifact_dir / "default.env.example")
    if env_values.get("KEYWARS_IMAGE") != repository:
        errors.append("default.env.example KEYWARS_IMAGE must match the manifest repository")
    if env_values.get("KEYWARS_VERSION") != version:
        errors.append("default.env.example KEYWARS_VERSION must match the release version")

    compose = (artifact_dir / "compose.yaml").read_text(encoding="utf-8")
    compose_image = re.search(
        r"(?m)^\s*image:\s*\$\{KEYWARS_IMAGE:-([^}]+)\}:\$\{KEYWARS_VERSION:-([^}]+)\}\s*$",
        compose,
    )
    if compose_image is None:
        errors.append("compose.yaml must use KEYWARS_IMAGE and KEYWARS_VERSION for the image reference")
    elif compose_image.group(1) != repository or compose_image.group(2) != "latest":
        errors.append("compose.yaml image defaults must match the manifest repository and latest development tag")

    if image_ref:
        errors.extend(validate_docker_archive(artifact_dir / archive_name, image_ref))
    return errors


def write_file(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")


def create_sample_archive(path: Path, image_ref: str) -> None:
    entries = {
        "manifest.json": json.dumps(
            [{"Config": "config.json", "RepoTags": [image_ref], "Layers": ["layer/layer.tar"]}]
        ),
        "repositories": "{}\n",
        "config.json": "{}\n",
        "layer/layer.tar": "sample-layer\n",
    }
    with gzip.open(path, "wb") as gzipped:
        with tarfile.open(fileobj=gzipped, mode="w:") as archive:
            for name, content in entries.items():
                data = content.encode("utf-8")
                info = tarfile.TarInfo(name)
                info.size = len(data)
                archive.addfile(info, fileobj=io.BytesIO(data))


def write_checksums(artifact_dir: Path) -> None:
    paths = sorted(path for path in artifact_dir.iterdir() if path.name != "SHA256SUMS")
    write_file(
        artifact_dir / "SHA256SUMS",
        "\n".join(f"{sha256(path)}  {path.name}" for path in paths) + "\n",
    )


def self_test() -> int:
    version = "v0.0.0"
    repository = "ghcr.io/example/keywars"
    image_ref = f"{repository}:{version}"
    with tempfile.TemporaryDirectory(prefix="keywars-release-check-") as temp:
        artifact_dir = Path(temp)
        archive = artifact_dir / f"keywars-{version}-linux-amd64.tar.gz"
        create_sample_archive(archive, image_ref)
        write_file(
            artifact_dir / "compose.yaml",
            "services:\n  keywars:\n    image: ${KEYWARS_IMAGE:-ghcr.io/example/keywars}:${KEYWARS_VERSION:-latest}\n",
        )
        write_file(
            artifact_dir / "default.env.example",
            f"KEYWARS_IMAGE={repository}\nKEYWARS_VERSION={version}\n",
        )
        write_file(artifact_dir / "AIRGAP_INSTALL.md", "# Air-Gap-Installation\n")
        write_file(artifact_dir / "RELEASE_NOTES.md", f"# KeyWars {version}\n")
        manifest = {
            "schema_version": 2,
            "version": version,
            "release_notes": "RELEASE_NOTES.md",
            "image": {
                "repository": repository,
                "image_ref": image_ref,
                "tags": [image_ref, f"{repository}:latest"],
                "digest": "sha256:" + "0" * 64,
                "sbom": "registry-attached",
                "provenance": "registry-attached",
            },
            "offline_archives": [
                {
                    "file": archive.name,
                    "platform": "linux/amd64",
                    "format": "docker-save-tar-gzip",
                    "image_ref": image_ref,
                }
            ],
            "deployment_files": ["compose.yaml", "default.env.example"],
            "documentation_files": ["AIRGAP_INSTALL.md", "RELEASE_NOTES.md"],
        }
        manifest_path = artifact_dir / "RELEASE_MANIFEST.json"
        write_file(manifest_path, json.dumps(manifest, indent=2) + "\n")
        write_checksums(artifact_dir)
        if errors := validate(artifact_dir, version):
            print("Release artifact self-test failed:")
            for error in errors:
                print(f"  {error}")
            return 1

        manifest["image"]["digest"] = "sha256:short"
        write_file(manifest_path, json.dumps(manifest, indent=2) + "\n")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("64 lowercase hex" in error for error in errors):
            print("Release artifact digest self-test failed")
            return 1

        manifest["image"]["digest"] = "sha256:" + "0" * 64
        write_file(manifest_path, json.dumps(manifest, indent=2) + "\n")
        create_sample_archive(archive, f"{repository}:wrong")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("RepoTags" in error for error in errors):
            print("Release artifact RepoTags self-test failed")
            return 1

        create_sample_archive(archive, image_ref)
        write_checksums(artifact_dir)
        (artifact_dir / "default.env.example").rename(artifact_dir / ".env.example")
        errors = validate(artifact_dir, version)
        if "Missing release artifact: default.env.example" not in errors:
            print("Release artifact published-name self-test failed")
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
