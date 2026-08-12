#!/usr/bin/env python3
"""Create deterministic metadata and checksums for a KeyWars release bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from datetime import datetime
from pathlib import Path


SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
REVISION = re.compile(r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")
SOURCE_REPOSITORY = re.compile(r"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
IMAGE_REPOSITORY = re.compile(r"^ghcr\.io/[a-z0-9._-]+/keywars$")
VERSIONED_IMAGE = re.compile(r"^(ghcr\.io/[a-z0-9._-]+/keywars):(v[0-9]+\.[0-9]+\.[0-9]+)$")
TEMPLATE_TOKEN = re.compile(r"\{\{([A-Z_]+)\}\}")
REQUIRED_TEMPLATE_TOKENS = {
    "CHANGES",
    "CREATED",
    "DEPLOYMENT_BUNDLE",
    "EXTERNAL_IMAGES",
    "IMAGE_DIGEST",
    "IMAGE_REF",
    "OCI_VERSION",
    "OFFLINE_ARCHIVE",
    "REVISION",
    "SOURCE_REPOSITORY",
    "VERSION",
}
SBOM_FILES = {
    "linux/amd64": "SBOM-linux-amd64.spdx.json",
    "linux/arm64": "SBOM-linux-arm64.spdx.json",
}
DEPLOYMENT_BUNDLE_FILES = (
    ".env.example",
    ".env.scale.example",
    "VERSION",
    "compose.yaml",
    "compose.scale.yaml",
    "deploy/images.txt",
    "deploy/k8s/arena.yaml",
    "deploy/k8s/cutover/job.yaml",
    "deploy/k8s/cutover/kustomization.yaml",
    "deploy/k8s/edge.yaml",
    "deploy/k8s/hpa.yaml",
    "deploy/k8s/kustomization.yaml",
    "deploy/k8s/migration/job.yaml",
    "deploy/k8s/migration/kustomization.yaml",
    "deploy/k8s/namespace.yaml",
    "deploy/k8s/network-policy.yaml",
    "deploy/k8s/pdb.yaml",
    "deploy/k8s/runtime-config.yaml",
    "deploy/k8s/web.yaml",
    "deploy/k8s/worker.yaml",
    "deploy/swarm/Caddyfile",
    "deploy/swarm/create-secrets.sh",
    "deploy/swarm/stack.yaml",
    "deploy/validate.ps1",
    "docs/airgap-install.md",
    "docs/backup-restore.md",
    "docs/load-testing.md",
    "docs/reverse-proxy.md",
    "docs/scale-operations.md",
    "scripts/check_deployment_contracts.py",
)
PINNED_IMAGE = re.compile(r"^[^\s@]+@sha256:[0-9a-f]{64}$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_created(value: str) -> None:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise ValueError(f"created must be an ISO-8601 timestamp, got {value!r}") from error
    if parsed.tzinfo is None:
        raise ValueError("created must include a timezone")


def validate_template(template: str) -> None:
    tokens = TEMPLATE_TOKEN.findall(template)
    token_set = set(tokens)
    missing = sorted(REQUIRED_TEMPLATE_TOKENS - token_set)
    unexpected = sorted(token_set - REQUIRED_TEMPLATE_TOKENS)
    duplicated = sorted(token for token in token_set if tokens.count(token) != 1)
    if missing or unexpected or duplicated:
        details = []
        if missing:
            details.append("missing " + ", ".join(missing))
        if unexpected:
            details.append("unexpected " + ", ".join(unexpected))
        if duplicated:
            details.append("not exactly once " + ", ".join(duplicated))
        raise ValueError("Invalid release-notes template tokens: " + "; ".join(details))


def extract_changes(changelog: str, version: str) -> str:
    match = re.search(
        rf"(?ms)^## {re.escape(version)}(?:\s+-\s+[^\n]+)?\n(?P<body>.*?)(?=^## |\Z)",
        changelog,
    )
    if match is None or not match.group("body").strip():
        raise ValueError(f"CHANGELOG.md has no non-empty section for {version}")
    return match.group("body").strip()


def read_current_version(version_file: Path) -> str:
    value = version_file.read_text(encoding="utf-8").strip()
    if SEMVER.fullmatch(value) is None:
        raise ValueError(f"VERSION must contain stable SemVer without a v prefix, got {value!r}")
    return value


def check_repository_metadata(
    version_file: Path,
    changelog_path: Path,
    template_path: Path,
    source_root: Path = Path("."),
    expected_image_repository: str | None = None,
) -> str:
    semver = read_current_version(version_file)
    version = f"v{semver}"
    extract_changes(changelog_path.read_text(encoding="utf-8"), version)
    validate_template(template_path.read_text(encoding="utf-8"))
    for relative_name in DEPLOYMENT_BUNDLE_FILES:
        source_path = source_root / relative_name
        if not source_path.is_file() or source_path.stat().st_size == 0:
            raise ValueError(f"missing or empty deployment bundle input: {relative_name}")
    validate_deployment_file_inventory(source_root)
    for compose_name in ("compose.yaml", "compose.scale.yaml"):
        compose = (source_root / compose_name).read_text(encoding="utf-8")
        defaults = re.findall(r"\$\{KEYWARS_VERSION:-([^}]+)\}", compose)
        if not defaults or any(default != version for default in defaults):
            raise ValueError(
                f"{compose_name} must default every KeyWars image to {version}"
            )
    image_lines = [
        line.strip()
        for line in (source_root / "deploy/images.txt").read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if not image_lines:
        raise ValueError("deploy/images.txt must contain the KeyWars release image")
    image_match = VERSIONED_IMAGE.fullmatch(image_lines[0])
    if image_match is None or image_match.group(2) != version:
        raise ValueError(f"deploy/images.txt must start with a keywars:{version} GHCR image")
    if (
        expected_image_repository is not None
        and image_match.group(1) != expected_image_repository
    ):
        raise ValueError(
            "deploy/images.txt repository does not match the release image repository"
        )
    read_deployment_images(source_root / "deploy/images.txt", image_lines[0])
    return version


def rewrite_compose(path: Path, repository: str) -> None:
    content = path.read_text(encoding="utf-8")
    content, replacements = re.subn(
        r"\$\{KEYWARS_IMAGE:-[^}]+\}",
        "${KEYWARS_IMAGE:-" + repository + "}",
        content,
    )
    if replacements < 1:
        raise ValueError("compose.yaml does not expose a KEYWARS_IMAGE default")
    path.write_text(content, encoding="utf-8")


def rewrite_environment(path: Path, repository: str, version: str) -> None:
    content = path.read_text(encoding="utf-8")
    content, image_replacements = re.subn(
        r"(?m)^KEYWARS_IMAGE=.*$", f"KEYWARS_IMAGE={repository}", content
    )
    content, version_replacements = re.subn(
        r"(?m)^KEYWARS_VERSION=.*$", f"KEYWARS_VERSION={version}", content
    )
    if image_replacements != 1 or version_replacements != 1:
        raise ValueError(
            "default.env.example must define KEYWARS_IMAGE and KEYWARS_VERSION exactly once"
        )
    path.write_text(content, encoding="utf-8")


def rewrite_scale_operations(path: Path) -> None:
    content = path.read_text(encoding="utf-8")
    content, replacements = re.subn(
        r"(?<![\w.])\.env\.scale\.example\b",
        "scale.env.example",
        content,
    )
    if replacements < 1 and "scale.env.example" not in content:
        raise ValueError("SCALE_OPERATIONS.md does not reference .env.scale.example")
    path.write_text(content, encoding="utf-8")


def read_deployment_images(path: Path, image_ref: str) -> list[str]:
    lines = [line.strip() for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not lines or lines[0] != image_ref:
        raise ValueError(f"deploy/images.txt must start with {image_ref}")
    external_images = lines[1:]
    if not external_images or any(
        PINNED_IMAGE.fullmatch(image) is None for image in external_images
    ):
        raise ValueError("every external deploy/images.txt entry must be pinned by SHA-256 digest")
    if len(set(external_images)) != len(external_images):
        raise ValueError("deploy/images.txt must not contain duplicate external images")
    return external_images


def validate_deployment_file_inventory(source_root: Path) -> None:
    expected = {
        relative_name
        for relative_name in DEPLOYMENT_BUNDLE_FILES
        if relative_name.startswith("deploy/")
    }
    deploy_root = source_root / "deploy"
    actual = {
        path.relative_to(source_root).as_posix()
        for path in deploy_root.rglob("*")
        if path.is_file()
    }
    missing = sorted(expected - actual)
    unexpected = sorted(actual - expected)
    if missing or unexpected:
        details = []
        if missing:
            details.append("missing " + ", ".join(missing))
        if unexpected:
            details.append("not bundled " + ", ".join(unexpected))
        raise ValueError("deployment bundle inventory mismatch: " + "; ".join(details))


def create_deployment_bundle(
    *,
    artifact_dir: Path,
    source_root: Path,
    version: str,
    image_ref: str,
) -> tuple[str, list[str]]:
    validate_deployment_file_inventory(source_root)
    external_images = read_deployment_images(source_root / "deploy/images.txt", image_ref)
    source_files: dict[str, bytes] = {}
    for relative_name in DEPLOYMENT_BUNDLE_FILES:
        source_path = source_root / relative_name
        if not source_path.is_file() or source_path.stat().st_size == 0:
            raise ValueError(f"missing or empty deployment bundle input: {relative_name}")
        source_files[relative_name] = source_path.read_bytes()

    source_files.update(
        {
            "compose.yaml": (artifact_dir / "compose.yaml").read_bytes(),
            "compose.scale.yaml": (artifact_dir / "compose.scale.yaml").read_bytes(),
            ".env.example": (artifact_dir / "default.env.example").read_bytes(),
            ".env.scale.example": (artifact_dir / "scale.env.example").read_bytes(),
        }
    )
    bundle_name = f"keywars-{version}-deployment-bundle.zip"
    root_directory = f"keywars-{version}-deployment"
    with zipfile.ZipFile(
        artifact_dir / bundle_name,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as archive:
        for relative_name in sorted(source_files):
            entry = zipfile.ZipInfo(f"{root_directory}/{relative_name}")
            entry.date_time = (1980, 1, 1, 0, 0, 0)
            entry.create_system = 3
            mode = 0o100755 if relative_name.endswith((".py", ".sh")) else 0o100644
            entry.external_attr = mode << 16
            archive.writestr(
                entry,
                source_files[relative_name],
                compress_type=zipfile.ZIP_DEFLATED,
                compresslevel=9,
            )
    return bundle_name, external_images


def render_release_notes(template: str, values: dict[str, str]) -> str:
    validate_template(template)
    rendered = template
    for token in REQUIRED_TEMPLATE_TOKENS:
        rendered = rendered.replace(f"{{{{{token}}}}}", values[token])
    if TEMPLATE_TOKEN.search(rendered):
        raise ValueError("Rendered release notes still contain template tokens")
    return rendered.rstrip() + "\n"


def write_checksums(artifact_dir: Path, asset_names: list[str]) -> None:
    lines = [f"{sha256(artifact_dir / name)}  {name}" for name in sorted(asset_names)]
    (artifact_dir / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")


def prepare_release_artifacts(
    *,
    artifact_dir: Path,
    version: str,
    image_repository: str,
    image_digest: str,
    revision: str,
    created: str,
    source_repository: str,
    version_file: Path,
    changelog_path: Path,
    template_path: Path,
    source_root: Path = Path("."),
) -> None:
    if not version.startswith("v") or SEMVER.fullmatch(version[1:]) is None:
        raise ValueError(f"version must use stable SemVer vMAJOR.MINOR.PATCH, got {version!r}")
    semver = version[1:]
    if read_current_version(version_file) != semver:
        raise ValueError(f"VERSION does not match release tag {version}")
    if IMAGE_REPOSITORY.fullmatch(image_repository) is None:
        raise ValueError("image repository must be a lowercase ghcr.io owner/keywars path")
    if DIGEST.fullmatch(image_digest) is None:
        raise ValueError("image digest must be sha256 followed by 64 lowercase hex characters")
    if REVISION.fullmatch(revision) is None:
        raise ValueError("revision must be a 40- or 64-character lowercase Git object ID")
    validate_created(created)
    if SOURCE_REPOSITORY.fullmatch(source_repository) is None:
        raise ValueError("source repository must be a canonical HTTPS GitHub repository URL")

    archive_name = f"keywars-{version}-linux-amd64.tar.gz"
    required_inputs = [
        "compose.yaml",
        "compose.scale.yaml",
        "default.env.example",
        "scale.env.example",
        "AIRGAP_INSTALL.md",
        "SCALE_OPERATIONS.md",
        archive_name,
        *SBOM_FILES.values(),
    ]
    for name in required_inputs:
        path = artifact_dir / name
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"missing or empty release input: {name}")

    rewrite_compose(artifact_dir / "compose.yaml", image_repository)
    rewrite_compose(artifact_dir / "compose.scale.yaml", image_repository)
    rewrite_environment(artifact_dir / "default.env.example", image_repository, version)
    rewrite_environment(artifact_dir / "scale.env.example", image_repository, version)
    rewrite_scale_operations(artifact_dir / "SCALE_OPERATIONS.md")

    image_ref = f"{image_repository}:{version}"
    archive_path = artifact_dir / archive_name
    bundle_name, external_images = create_deployment_bundle(
        artifact_dir=artifact_dir,
        source_root=source_root,
        version=version,
        image_ref=image_ref,
    )
    oci_labels = {
        "org.opencontainers.image.created": created,
        "org.opencontainers.image.revision": revision,
        "org.opencontainers.image.source": source_repository,
        "org.opencontainers.image.version": semver,
    }
    asset_names = [
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
    ]
    manifest = {
        "schema_version": 3,
        "version": version,
        "semver": semver,
        "release_notes": "RELEASE_NOTES.md",
        "source": {
            "repository": source_repository,
            "revision": revision,
            "created": created,
        },
        "image": {
            "repository": image_repository,
            "image_ref": image_ref,
            "tags": [image_ref, f"{image_repository}:latest"],
            "digest": image_digest,
            "platforms": ["linux/amd64", "linux/arm64"],
            "oci_labels": oci_labels,
            "attestations": {
                "sbom": "registry-attached",
                "provenance": "registry-attached",
            },
        },
        "offline_archives": [
            {
                "file": archive_name,
                "platform": "linux/amd64",
                "format": "docker-save-tar-gzip",
                "image_ref": image_ref,
                "sha256": sha256(archive_path),
                "size_bytes": archive_path.stat().st_size,
            }
        ],
        "sboms": [
            {
                "file": file_name,
                "platform": platform,
                "format": "SPDX-JSON",
                "sha256": sha256(artifact_dir / file_name),
                "size_bytes": (artifact_dir / file_name).stat().st_size,
            }
            for platform, file_name in SBOM_FILES.items()
        ],
        "deployment_bundle": {
            "file": bundle_name,
            "format": "zip",
            "root_directory": f"keywars-{version}-deployment",
            "included_files": list(DEPLOYMENT_BUNDLE_FILES),
            "sha256": sha256(artifact_dir / bundle_name),
            "size_bytes": (artifact_dir / bundle_name).stat().st_size,
            "external_images": [
                {"reference": reference, "included": False}
                for reference in external_images
            ],
        },
        "deployment_files": [
            "compose.yaml",
            "default.env.example",
            "compose.scale.yaml",
            "scale.env.example",
        ],
        "documentation_files": [
            "AIRGAP_INSTALL.md",
            "SCALE_OPERATIONS.md",
            "RELEASE_NOTES.md",
        ],
        "release_assets": sorted([*asset_names, "SHA256SUMS"]),
    }
    (artifact_dir / "RELEASE_MANIFEST.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )

    changes = extract_changes(changelog_path.read_text(encoding="utf-8"), version)
    template = template_path.read_text(encoding="utf-8")
    notes = render_release_notes(
        template,
        {
            "CHANGES": changes,
            "CREATED": created,
            "DEPLOYMENT_BUNDLE": bundle_name,
            "EXTERNAL_IMAGES": "\n".join(f"- `{image}`" for image in external_images),
            "IMAGE_DIGEST": image_digest,
            "IMAGE_REF": image_ref,
            "OCI_VERSION": semver,
            "OFFLINE_ARCHIVE": archive_name,
            "REVISION": revision,
            "SOURCE_REPOSITORY": source_repository,
            "VERSION": version,
        },
    )
    (artifact_dir / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8")
    write_checksums(artifact_dir, asset_names)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-repository", action="store_true")
    parser.add_argument("--artifact-dir", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--image-repository")
    parser.add_argument("--image-digest")
    parser.add_argument("--revision")
    parser.add_argument("--created")
    parser.add_argument("--source-repository")
    parser.add_argument("--source-root", type=Path, default=Path("."))
    parser.add_argument("--expected-image-repository")
    parser.add_argument("--version-file", type=Path, default=Path("VERSION"))
    parser.add_argument("--changelog", type=Path, default=Path("CHANGELOG.md"))
    parser.add_argument(
        "--notes-template", type=Path, default=Path(".github/RELEASE_NOTES_TEMPLATE.md")
    )
    args = parser.parse_args()

    try:
        if args.check_repository:
            version = check_repository_metadata(
                args.version_file,
                args.changelog,
                args.notes_template,
                args.source_root,
                args.expected_image_repository,
            )
            print(f"Release metadata check: OK ({version})")
            return 0

        required = {
            "artifact-dir": args.artifact_dir,
            "version": args.version,
            "image-repository": args.image_repository,
            "image-digest": args.image_digest,
            "revision": args.revision,
            "created": args.created,
            "source-repository": args.source_repository,
        }
        missing = [name for name, value in required.items() if value is None]
        if missing:
            parser.error("missing required arguments: " + ", ".join(missing))

        prepare_release_artifacts(
            artifact_dir=args.artifact_dir,
            version=args.version,
            image_repository=args.image_repository,
            image_digest=args.image_digest,
            revision=args.revision,
            created=args.created,
            source_repository=args.source_repository,
            version_file=args.version_file,
            changelog_path=args.changelog,
            template_path=args.notes_template,
            source_root=args.source_root,
        )
    except (OSError, ValueError) as error:
        parser.exit(1, f"Release metadata preparation failed: {error}\n")

    print("Release metadata preparation: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
