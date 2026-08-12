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
import zipfile
from datetime import datetime
from pathlib import Path
from pathlib import PurePosixPath

from prepare_release_artifacts import (
    DEPLOYMENT_BUNDLE_FILES,
    PINNED_IMAGE,
    SBOM_FILES,
    check_repository_metadata,
    prepare_release_artifacts,
)


STABLE_VERSION = re.compile(r"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
CHECKSUM = re.compile(r"^[0-9a-f]{64}$")
IMAGE_REPOSITORY = re.compile(r"^ghcr\.io/[a-z0-9._-]+/keywars$")
REVISION = re.compile(r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")
SOURCE_REPOSITORY = re.compile(r"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
TEMPLATE_TOKEN = re.compile(r"\{\{[A-Z_]+\}\}")
REQUIRED_DOCKER_SAVE_ENTRIES = {"manifest.json", "repositories"}
EXPECTED_PLATFORMS = ["linux/amd64", "linux/arm64"]
EXPECTED_ATTESTATIONS = {
    "sbom": "registry-attached",
    "provenance": "registry-attached",
}


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


def safe_archive_name(name: str) -> bool:
    path = PurePosixPath(name)
    return not path.is_absolute() and ".." not in path.parts and "\\" not in name


def validate_docker_archive(
    path: Path,
    image_ref: str,
    expected_labels: dict[str, str],
) -> list[str]:
    errors: list[str] = []
    docker_manifest: object = None
    image_config: object = None
    names: set[str] = set()
    try:
        with gzip.open(path, "rb") as gzipped:
            with tarfile.open(fileobj=gzipped, mode="r:") as archive:
                names = set(archive.getnames())
                unsafe_names = sorted(name for name in names if not safe_archive_name(name))
                if unsafe_names:
                    errors.append(
                        "Offline image archive contains unsafe member names: "
                        + ", ".join(unsafe_names)
                    )

                missing_entries = sorted(REQUIRED_DOCKER_SAVE_ENTRIES - names)
                if missing_entries:
                    errors.append(
                        "Offline image archive is missing docker-save entries: "
                        + ", ".join(missing_entries)
                    )
                    return errors

                manifest_member = archive.extractfile("manifest.json")
                if manifest_member is None:
                    errors.append("Offline image archive manifest.json is not readable")
                    return errors
                docker_manifest = json.load(manifest_member)

                if isinstance(docker_manifest, list) and len(docker_manifest) == 1:
                    item = docker_manifest[0]
                    config_name = item.get("Config") if isinstance(item, dict) else None
                    if isinstance(config_name, str) and config_name in names:
                        config_member = archive.extractfile(config_name)
                        if config_member is not None:
                            image_config = json.load(config_member)
    except (OSError, tarfile.TarError, json.JSONDecodeError) as error:
        return [f"Offline image archive is not a readable docker-save tar.gz: {error}"]

    if not isinstance(docker_manifest, list) or len(docker_manifest) != 1:
        return ["Offline image archive manifest.json must contain exactly one image"]
    item = docker_manifest[0]
    if not isinstance(item, dict):
        return ["Offline image archive manifest.json contains an invalid image entry"]

    tags = item.get("RepoTags")
    if tags != [image_ref]:
        errors.append(f"Offline image archive RepoTags must contain exactly {image_ref}")

    referenced_files: set[str] = set()
    config_name = item.get("Config")
    if isinstance(config_name, str):
        referenced_files.add(config_name)
    else:
        errors.append("Offline image archive manifest.json must reference one image config")
    layers = item.get("Layers")
    if isinstance(layers, list) and all(isinstance(layer, str) for layer in layers):
        referenced_files.update(layers)
    else:
        errors.append("Offline image archive manifest.json must contain a layer list")
    unsafe_references = sorted(name for name in referenced_files if not safe_archive_name(name))
    if unsafe_references:
        errors.append(
            "Offline image archive references unsafe paths: " + ", ".join(unsafe_references)
        )
    missing_files = sorted(referenced_files - names)
    if missing_files:
        errors.append(
            "Offline image archive is missing referenced files: " + ", ".join(missing_files)
        )

    if not isinstance(image_config, dict):
        errors.append("Offline image archive config is not a readable JSON object")
        return errors
    if image_config.get("os") != "linux" or image_config.get("architecture") != "amd64":
        errors.append("Offline image archive config must target linux/amd64")
    config = image_config.get("config")
    labels = config.get("Labels") if isinstance(config, dict) else None
    if not isinstance(labels, dict):
        errors.append("Offline image archive config must contain OCI labels")
    else:
        for name, expected in expected_labels.items():
            if labels.get(name) != expected:
                errors.append(f"Offline image archive OCI label {name} must be {expected!r}")
    return errors


def validate_deployment_bundle(
    *,
    path: Path,
    artifact_dir: Path,
    version: str,
    image_ref: str,
    metadata: object,
) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    external_images: list[str] = []
    bundle_name = f"keywars-{version}-deployment-bundle.zip"
    root_directory = f"keywars-{version}-deployment"
    if not isinstance(metadata, dict):
        return ["Release manifest deployment_bundle must be an object"], external_images
    expected_scalars = {
        "file": bundle_name,
        "format": "zip",
        "root_directory": root_directory,
        "sha256": sha256(path),
        "size_bytes": path.stat().st_size,
    }
    for name, expected in expected_scalars.items():
        if metadata.get(name) != expected:
            errors.append(f"Release manifest deployment_bundle.{name} must be {expected!r}")
    if metadata.get("included_files") != list(DEPLOYMENT_BUNDLE_FILES):
        errors.append("Release manifest deployment bundle must list the exact curated file set")

    external_entries = metadata.get("external_images")
    if not isinstance(external_entries, list):
        errors.append("Release manifest deployment bundle external_images must be a list")
    else:
        for entry in external_entries:
            if not isinstance(entry, dict):
                errors.append("Release manifest contains an invalid external image entry")
                continue
            reference = entry.get("reference")
            if entry.get("included") is not False:
                errors.append("Every external deployment image must be marked as not included")
            if not isinstance(reference, str) or PINNED_IMAGE.fullmatch(reference) is None:
                errors.append("Every external deployment image must be pinned by SHA-256 digest")
            else:
                external_images.append(reference)
    repositories = {reference.split(":", maxsplit=1)[0] for reference in external_images}
    if repositories != {"caddy", "postgres", "redis"}:
        errors.append("Deployment bundle must declare pinned caddy, postgres and redis images")
    if len(external_images) != len(set(external_images)):
        errors.append("Deployment bundle external images must be unique")

    expected_names = {
        f"{root_directory}/{relative_name}" for relative_name in DEPLOYMENT_BUNDLE_FILES
    }
    contents: dict[str, bytes] = {}
    try:
        with zipfile.ZipFile(path) as archive:
            raw_names = [entry.filename for entry in archive.infolist()]
            if len(raw_names) != len(set(raw_names)):
                errors.append("Deployment bundle contains duplicate ZIP entries")
            unsafe_names = sorted(name for name in raw_names if not safe_archive_name(name))
            if unsafe_names:
                errors.append(
                    "Deployment bundle contains unsafe ZIP paths: " + ", ".join(unsafe_names)
                )
            actual_names = set(raw_names)
            missing = sorted(expected_names - actual_names)
            unexpected = sorted(actual_names - expected_names)
            if missing:
                errors.append("Deployment bundle is missing files: " + ", ".join(missing))
            if unexpected:
                errors.append(
                    "Deployment bundle contains unexpected files: " + ", ".join(unexpected)
                )
            for relative_name in DEPLOYMENT_BUNDLE_FILES:
                archive_name = f"{root_directory}/{relative_name}"
                if archive_name in actual_names:
                    contents[relative_name] = archive.read(archive_name)
    except (OSError, zipfile.BadZipFile) as error:
        errors.append(f"Deployment bundle is not a readable ZIP archive: {error}")
        return errors, external_images

    artifact_mappings = {
        "compose.yaml": "compose.yaml",
        "compose.scale.yaml": "compose.scale.yaml",
        ".env.example": "default.env.example",
        ".env.scale.example": "scale.env.example",
    }
    for bundle_file, artifact_file in artifact_mappings.items():
        if contents.get(bundle_file) != (artifact_dir / artifact_file).read_bytes():
            errors.append(f"Deployment bundle {bundle_file} differs from {artifact_file}")
    try:
        bundled_version = contents.get("VERSION", b"").decode("utf-8").strip()
    except UnicodeDecodeError:
        bundled_version = ""
    if bundled_version != version[1:]:
        errors.append("Deployment bundle VERSION must match the release version")
    try:
        image_lines = [
            line.strip()
            for line in contents.get("deploy/images.txt", b"").decode("utf-8").splitlines()
            if line.strip()
        ]
    except UnicodeDecodeError:
        image_lines = []
    if image_lines != [image_ref, *external_images]:
        errors.append(
            "Deployment bundle deploy/images.txt must match the release and external images"
        )
    deployment_text = "\n".join(
        content.decode("utf-8", errors="replace")
        for relative_name, content in contents.items()
        if relative_name.startswith("deploy/") and relative_name != "deploy/images.txt"
    )
    if image_ref not in deployment_text:
        errors.append(f"Deployment bundle does not use release image {image_ref}")
    for reference in external_images:
        if reference not in deployment_text:
            errors.append(f"Deployment bundle does not use pinned external image {reference}")
    return errors, external_images


def created_is_valid(value: object) -> bool:
    if not isinstance(value, str):
        return False
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return False
    return parsed.tzinfo is not None


def validate(
    artifact_dir: Path,
    version: str,
    *,
    expected_revision: str | None = None,
    expected_created: str | None = None,
    expected_source: str | None = None,
) -> list[str]:
    errors: list[str] = []
    if STABLE_VERSION.fullmatch(version) is None:
        errors.append(f"Release version must use stable SemVer vMAJOR.MINOR.PATCH, got {version!r}")
    semver = version[1:] if STABLE_VERSION.fullmatch(version) else ""

    archive_name = f"keywars-{version}-linux-amd64.tar.gz"
    bundle_name = f"keywars-{version}-deployment-bundle.zip"
    required_names = {
        archive_name,
        bundle_name,
        "compose.yaml",
        "compose.scale.yaml",
        "default.env.example",
        "scale.env.example",
        "AIRGAP_INSTALL.md",
        "SCALE_OPERATIONS.md",
        *SBOM_FILES.values(),
        "RELEASE_MANIFEST.json",
        "RELEASE_NOTES.md",
        "SHA256SUMS",
    }
    actual_names = (
        {path.name for path in artifact_dir.iterdir()} if artifact_dir.is_dir() else set()
    )
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

    if manifest.get("schema_version") != 3:
        errors.append("Release manifest schema_version must be 3")
    if manifest.get("version") != version:
        errors.append(
            f"Release manifest version is {manifest.get('version')!r}, expected {version!r}"
        )
    if manifest.get("semver") != semver:
        errors.append(f"Release manifest semver is {manifest.get('semver')!r}, expected {semver!r}")
    if manifest.get("release_notes") != "RELEASE_NOTES.md":
        errors.append("Release manifest must reference RELEASE_NOTES.md")

    source = manifest.get("source")
    if not isinstance(source, dict):
        source = {}
        errors.append("Release manifest source must be an object")
    source_repository = source.get("repository", "")
    if (
        not isinstance(source_repository, str)
        or SOURCE_REPOSITORY.fullmatch(source_repository) is None
    ):
        errors.append("Release manifest source.repository must be a canonical HTTPS GitHub URL")
        source_repository = ""
    revision = source.get("revision", "")
    if not isinstance(revision, str) or REVISION.fullmatch(revision) is None:
        errors.append(
            "Release manifest source.revision must be a lowercase 40- or 64-character Git object ID"
        )
        revision = ""
    created = source.get("created", "")
    if not created_is_valid(created):
        errors.append("Release manifest source.created must be an ISO-8601 timestamp with timezone")
        created = ""
    if expected_revision is not None and revision != expected_revision:
        errors.append(f"Release manifest revision is {revision!r}, expected {expected_revision!r}")
    if expected_created is not None and created != expected_created:
        errors.append(
            f"Release manifest created timestamp is {created!r}, expected {expected_created!r}"
        )
    if expected_source is not None and source_repository != expected_source:
        errors.append(
            f"Release manifest source repository is {source_repository!r}, "
            f"expected {expected_source!r}"
        )

    image = manifest.get("image")
    if not isinstance(image, dict):
        image = {}
        errors.append("Release manifest image must be an object")
    repository = image.get("repository", "")
    if not isinstance(repository, str) or IMAGE_REPOSITORY.fullmatch(repository) is None:
        errors.append(
            "Release manifest image.repository must be a lowercase ghcr.io owner/keywars path"
        )
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
        errors.append(
            "Release manifest image.tags must contain exactly the version and "
            "latest GHCR references"
        )
    digest = image.get("digest", "")
    if not isinstance(digest, str) or DIGEST.fullmatch(digest) is None:
        errors.append(
            "Release manifest image.digest must be sha256 followed by 64 lowercase hex characters"
        )
    if image.get("platforms") != EXPECTED_PLATFORMS:
        errors.append("Release manifest image.platforms must contain linux/amd64 and linux/arm64")
    if image.get("attestations") != EXPECTED_ATTESTATIONS:
        errors.append(
            "Release manifest must record registry-attached SBOM and provenance attestations"
        )

    expected_labels = {
        "org.opencontainers.image.created": created,
        "org.opencontainers.image.revision": revision,
        "org.opencontainers.image.source": source_repository,
        "org.opencontainers.image.version": semver,
    }
    if image.get("oci_labels") != expected_labels:
        errors.append(
            "Release manifest image.oci_labels must match source and version metadata exactly"
        )

    archives = manifest.get("offline_archives")
    archive_path = artifact_dir / archive_name
    expected_archive = {
        "file": archive_name,
        "platform": "linux/amd64",
        "format": "docker-save-tar-gzip",
        "image_ref": image_ref,
        "sha256": sha256(archive_path),
        "size_bytes": archive_path.stat().st_size,
    }
    if archives != [expected_archive]:
        errors.append(
            "Release manifest must describe exactly the versioned linux/amd64 "
            "archive, size and SHA-256"
        )
    expected_sboms = [
        {
            "file": file_name,
            "platform": platform,
            "format": "SPDX-JSON",
            "sha256": sha256(artifact_dir / file_name),
            "size_bytes": (artifact_dir / file_name).stat().st_size,
        }
        for platform, file_name in SBOM_FILES.items()
    ]
    if manifest.get("sboms") != expected_sboms:
        errors.append("Release manifest must describe the exact platform SPDX SBOM assets")
    for platform, file_name in SBOM_FILES.items():
        try:
            sbom = json.loads((artifact_dir / file_name).read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            errors.append(f"{file_name} is not valid JSON: {error}")
            continue
        if not isinstance(sbom, dict):
            errors.append(f"{file_name} must contain an SPDX JSON object")
            continue
        if sbom.get("SPDXID") != "SPDXRef-DOCUMENT":
            errors.append(f"{file_name} must identify an SPDX document")
        spdx_version = sbom.get("spdxVersion")
        if not isinstance(spdx_version, str) or not spdx_version.startswith("SPDX-"):
            errors.append(f"{file_name} must declare an SPDX version")
        packages = sbom.get("packages")
        if not isinstance(packages, list) or not packages:
            errors.append(f"{file_name} must contain at least one package for {platform}")
    bundle_errors, external_images = validate_deployment_bundle(
        path=artifact_dir / bundle_name,
        artifact_dir=artifact_dir,
        version=version,
        image_ref=image_ref,
        metadata=manifest.get("deployment_bundle"),
    )
    errors.extend(bundle_errors)
    if manifest.get("deployment_files") != [
        "compose.yaml",
        "default.env.example",
        "compose.scale.yaml",
        "scale.env.example",
    ]:
        errors.append("Release manifest must list both standalone and scale deployment files")
    if manifest.get("documentation_files") != [
        "AIRGAP_INSTALL.md",
        "SCALE_OPERATIONS.md",
        "RELEASE_NOTES.md",
    ]:
        errors.append("Release manifest must list the air-gap, scale and release documentation")
    if manifest.get("release_assets") != sorted(required_names):
        errors.append("Release manifest release_assets must list the exact public asset set")

    env_values = parse_environment(artifact_dir / "default.env.example")
    if env_values.get("KEYWARS_IMAGE") != repository:
        errors.append("default.env.example KEYWARS_IMAGE must match the manifest repository")
    if env_values.get("KEYWARS_VERSION") != version:
        errors.append("default.env.example KEYWARS_VERSION must match the release version")
    scale_env_values = parse_environment(artifact_dir / "scale.env.example")
    if scale_env_values.get("KEYWARS_IMAGE") != repository:
        errors.append("scale.env.example KEYWARS_IMAGE must match the manifest repository")
    if scale_env_values.get("KEYWARS_VERSION") != version:
        errors.append("scale.env.example KEYWARS_VERSION must match the release version")

    for compose_name in ("compose.yaml", "compose.scale.yaml"):
        compose = (artifact_dir / compose_name).read_text(encoding="utf-8")
        compose_images = re.findall(
            r"(?m)^\s*image:\s*\$\{KEYWARS_IMAGE:-([^}]+)\}:\$\{KEYWARS_VERSION:-([^}]+)\}\s*$",
            compose,
        )
        if not compose_images:
            errors.append(
                f"{compose_name} must use KEYWARS_IMAGE and KEYWARS_VERSION for the image reference"
            )
        elif any(image != (repository, version) for image in compose_images):
            errors.append(
                f"Every KeyWars image default in {compose_name} must match the "
                "repository and exact release version"
            )

    scale_operations = (artifact_dir / "SCALE_OPERATIONS.md").read_text(encoding="utf-8")
    if ".env.scale.example" in scale_operations:
        errors.append("SCALE_OPERATIONS.md must not reference the hidden source env filename")
    for required_reference in ("scale.env.example", "compose.scale.yaml"):
        if required_reference not in scale_operations:
            errors.append(f"SCALE_OPERATIONS.md must reference {required_reference}")

    release_notes = (artifact_dir / "RELEASE_NOTES.md").read_text(encoding="utf-8")
    if not release_notes.startswith(f"# KeyWars {version}\n"):
        errors.append("RELEASE_NOTES.md must start with the exact release version")
    if TEMPLATE_TOKEN.search(release_notes):
        errors.append("RELEASE_NOTES.md must not contain unresolved template tokens")
    notes_before_container = release_notes.partition("\n## Container\n")[0]
    if not any(line.startswith("- ") for line in notes_before_container.splitlines()):
        errors.append("RELEASE_NOTES.md must contain at least one changelog bullet")
    required_note_values = [
        image_ref,
        digest,
        semver,
        revision,
        created,
        source_repository,
        archive_name,
        "linux/amd64",
        "linux/arm64",
        *SBOM_FILES.values(),
        bundle_name,
        "compose.scale.yaml",
        "scale.env.example",
        "SCALE_OPERATIONS.md",
        *external_images,
    ]
    for value in required_note_values:
        if value and value not in release_notes:
            errors.append(f"RELEASE_NOTES.md does not contain required metadata {value!r}")

    if image_ref:
        errors.extend(validate_docker_archive(archive_path, image_ref, expected_labels))
    return errors


def write_file(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8")


def create_sample_archive(path: Path, image_ref: str, labels: dict[str, str]) -> None:
    entries = {
        "manifest.json": json.dumps(
            [{"Config": "config.json", "RepoTags": [image_ref], "Layers": ["layer/layer.tar"]}]
        ),
        "repositories": "{}\n",
        "config.json": json.dumps(
            {
                "architecture": "amd64",
                "os": "linux",
                "config": {"Labels": labels},
            }
        ),
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
    version = "v0.5.0"
    semver = version[1:]
    repository = "ghcr.io/example/keywars"
    image_ref = f"{repository}:{version}"
    digest = "sha256:" + "0" * 64
    revision = "a" * 40
    created = "2026-08-11T12:34:56Z"
    source_repository = "https://github.com/example/keywars"
    labels = {
        "org.opencontainers.image.created": created,
        "org.opencontainers.image.revision": revision,
        "org.opencontainers.image.source": source_repository,
        "org.opencontainers.image.version": semver,
    }
    with tempfile.TemporaryDirectory(prefix="keywars-release-check-") as temp:
        root = Path(temp)
        artifact_dir = root / "artifacts"
        artifact_dir.mkdir()
        archive = artifact_dir / f"keywars-{version}-linux-amd64.tar.gz"
        version_file = root / "VERSION"
        changelog = root / "CHANGELOG.md"
        template = root / "RELEASE_NOTES_TEMPLATE.md"
        write_file(version_file, semver + "\n")
        write_file(changelog, f"# Changelog\n\n## {version} - 2026-08-11\n\n- Test release.\n")
        write_file(
            template,
            """# KeyWars {{VERSION}}

{{CHANGES}}

## Container

- Image: `{{IMAGE_REF}}`
- Digest: `{{IMAGE_DIGEST}}`
- Plattformen: `linux/amd64`, `linux/arm64`
- OCI-Version: `{{OCI_VERSION}}`
- Revision: `{{REVISION}}`
- Erstellt: `{{CREATED}}`
- Quellcode: `{{SOURCE_REPOSITORY}}`

## Artefakte

- `{{OFFLINE_ARCHIVE}}`
- `{{DEPLOYMENT_BUNDLE}}`
- `SBOM-linux-amd64.spdx.json`
- `SBOM-linux-arm64.spdx.json`
- `compose.scale.yaml`
- `scale.env.example`
- `SCALE_OPERATIONS.md`

{{EXTERNAL_IMAGES}}
""",
        )
        write_file(
            artifact_dir / "compose.yaml",
            "services:\n  keywars:\n"
            "    image: ${KEYWARS_IMAGE:-ghcr.io/example/keywars}:"
            f"${{KEYWARS_VERSION:-{version}}}\n",
        )
        write_file(
            artifact_dir / "compose.scale.yaml",
            "x-keywars: &keywars\n"
            "  image: ${KEYWARS_IMAGE:-ghcr.io/example/keywars}:"
            f"${{KEYWARS_VERSION:-{version}}}\n",
        )
        write_file(
            artifact_dir / "default.env.example",
            f"KEYWARS_IMAGE={repository}\nKEYWARS_VERSION={version}\n",
        )
        write_file(
            artifact_dir / "scale.env.example",
            f"KEYWARS_IMAGE={repository}\nKEYWARS_VERSION={version}\n",
        )
        write_file(artifact_dir / "AIRGAP_INSTALL.md", "# Air-Gap-Installation\n")
        write_file(
            artifact_dir / "SCALE_OPERATIONS.md",
            "# Scale\n\nCopy `.env.scale.example` and use `compose.scale.yaml`.\n",
        )
        source_root = root / "source"
        for relative_name in DEPLOYMENT_BUNDLE_FILES:
            source_path = source_root / relative_name
            source_path.parent.mkdir(parents=True, exist_ok=True)
            write_file(source_path, f"sample {relative_name}\n")
        write_file(
            source_root / "compose.yaml",
            (artifact_dir / "compose.yaml").read_text(encoding="utf-8"),
        )
        write_file(
            source_root / "compose.scale.yaml",
            (artifact_dir / "compose.scale.yaml").read_text(encoding="utf-8"),
        )
        write_file(source_root / "VERSION", semver + "\n")
        external_images = [
            "caddy:1-alpine@sha256:" + "1" * 64,
            "postgres:1-alpine@sha256:" + "2" * 64,
            "redis:1-alpine@sha256:" + "3" * 64,
        ]
        write_file(
            source_root / "deploy/images.txt",
            "\n".join([image_ref, *external_images]) + "\n",
        )
        write_file(
            source_root / "deploy/swarm/stack.yaml",
            "\n".join([image_ref, *external_images]) + "\n",
        )
        if (
            check_repository_metadata(
                version_file,
                changelog,
                template,
                source_root,
                repository,
            )
            != version
        ):
            print("Release repository-metadata self-test failed")
            return 1

        def prepare_valid() -> None:
            create_sample_archive(archive, image_ref, labels)
            for platform, file_name in SBOM_FILES.items():
                write_file(
                    artifact_dir / file_name,
                    json.dumps(
                        {
                            "SPDXID": "SPDXRef-DOCUMENT",
                            "spdxVersion": "SPDX-2.3",
                            "packages": [
                                {
                                    "SPDXID": "SPDXRef-Package",
                                    "name": f"sample-{platform.replace('/', '-')}",
                                }
                            ],
                        }
                    )
                    + "\n",
                )
            prepare_release_artifacts(
                artifact_dir=artifact_dir,
                version=version,
                image_repository=repository,
                image_digest=digest,
                revision=revision,
                created=created,
                source_repository=source_repository,
                version_file=version_file,
                changelog_path=changelog,
                template_path=template,
                source_root=source_root,
            )

        prepare_valid()
        manifest_path = artifact_dir / "RELEASE_MANIFEST.json"
        if errors := validate(
            artifact_dir,
            version,
            expected_revision=revision,
            expected_created=created,
            expected_source=source_repository,
        ):
            print("Release artifact self-test failed:")
            for error in errors:
                print(f"  {error}")
            return 1

        bundle_path = artifact_dir / f"keywars-{version}-deployment-bundle.zip"
        bundle_checksum = sha256(bundle_path)
        prepare_valid()
        if sha256(bundle_path) != bundle_checksum:
            print("Release deployment-bundle determinism self-test failed")
            return 1

        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["image"]["digest"] = "sha256:short"
        write_file(manifest_path, json.dumps(manifest, indent=2) + "\n")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("64 lowercase hex" in error for error in errors):
            print("Release artifact digest self-test failed")
            return 1

        prepare_valid()
        create_sample_archive(archive, f"{repository}:wrong", labels)
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("RepoTags" in error for error in errors):
            print("Release artifact RepoTags self-test failed")
            return 1

        prepare_valid()
        wrong_labels = {**labels, "org.opencontainers.image.revision": "b" * 40}
        create_sample_archive(archive, image_ref, wrong_labels)
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("OCI label" in error for error in errors):
            print("Release artifact OCI-label self-test failed")
            return 1

        prepare_valid()
        write_file(artifact_dir / SBOM_FILES["linux/amd64"], "{}\n")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("SPDX document" in error for error in errors):
            print("Release artifact SPDX self-test failed")
            return 1

        prepare_valid()
        notes_path = artifact_dir / "RELEASE_NOTES.md"
        write_file(notes_path, notes_path.read_text(encoding="utf-8") + "{{VERSION}}\n")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version)
        if not any("template tokens" in error for error in errors):
            print("Release notes template-token self-test failed")
            return 1

        prepare_valid()
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["source"]["revision"] = "b" * 40
        write_file(manifest_path, json.dumps(manifest, indent=2) + "\n")
        write_checksums(artifact_dir)
        errors = validate(artifact_dir, version, expected_revision=revision)
        if not any("expected" in error and "revision" in error for error in errors):
            print("Release revision binding self-test failed")
            return 1

        prepare_valid()
        (artifact_dir / "default.env.example").rename(artifact_dir / ".env.example")
        errors = validate(artifact_dir, version)
        if "Missing release artifact: default.env.example" not in errors:
            print("Release artifact published-name self-test failed")
            return 1

        (artifact_dir / ".env.example").rename(artifact_dir / "default.env.example")
        prepare_valid()
        write_file(artifact_dir / "unexpected.txt", "unexpected\n")
        errors = validate(artifact_dir, version)
        if "Unexpected release artifact: unexpected.txt" not in errors:
            print("Release artifact exact-set self-test failed")
            return 1

        errors = validate(artifact_dir, "v01.0.0")
        if not any("stable SemVer" in error for error in errors):
            print("Release artifact strict-SemVer self-test failed")
            return 1

    print("Release artifact self-test: OK")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_dir", nargs="?", type=Path)
    parser.add_argument("version", nargs="?")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--expected-revision")
    parser.add_argument("--expected-created")
    parser.add_argument("--expected-source")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.artifact_dir is None or args.version is None:
        parser.error("artifact_dir and version are required unless --self-test is used")

    errors = validate(
        args.artifact_dir,
        args.version,
        expected_revision=args.expected_revision,
        expected_created=args.expected_created,
        expected_source=args.expected_source,
    )
    if errors:
        print("Release artifact check failed:")
        for error in errors:
            print(f"  {error}")
        return 1

    print("Release artifact check: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
