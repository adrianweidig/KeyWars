#!/usr/bin/env python3
"""Verify a published KeyWars multi-arch image and its registry attestations."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from datetime import datetime
from typing import Any


DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
VERSION = re.compile(r"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
REVISION = re.compile(r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")
IMAGE_REPOSITORY = re.compile(r"^ghcr\.io/[a-z0-9._-]+/keywars$")
SOURCE_REPOSITORY = re.compile(r"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
ATTESTATION_TYPE = "vnd.docker.reference.type"
ATTESTATION_VALUE = "attestation-manifest"
REFERENCE_DIGEST = "vnd.docker.reference.digest"
PREDICATE_TYPE = "in-toto.io/predicate-type"
EXPECTED_PLATFORMS = {"linux/amd64", "linux/arm64"}


def created_is_valid(value: str) -> bool:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return False
    return parsed.tzinfo is not None


def inspect_json(reference: str, *arguments: str) -> Any:
    result = subprocess.run(
        ["docker", "buildx", "imagetools", "inspect", reference, *arguments],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return json.loads(result.stdout)


def platform_name(descriptor: dict[str, Any]) -> str:
    platform = descriptor.get("platform")
    if not isinstance(platform, dict):
        return ""
    operating_system = platform.get("os")
    architecture = platform.get("architecture")
    if not isinstance(operating_system, str) or not isinstance(architecture, str):
        return ""
    return f"{operating_system}/{architecture}"


def validate_index(
    index: object,
) -> tuple[list[str], dict[str, dict[str, Any]], list[dict[str, Any]]]:
    errors: list[str] = []
    runtime: dict[str, dict[str, Any]] = {}
    attestations: list[dict[str, Any]] = []
    if not isinstance(index, dict) or not isinstance(index.get("manifests"), list):
        return ["Published image must be an OCI index with a manifests list"], runtime, attestations

    for descriptor in index["manifests"]:
        if not isinstance(descriptor, dict):
            errors.append("Published image index contains an invalid descriptor")
            continue
        annotations = descriptor.get("annotations")
        annotations = annotations if isinstance(annotations, dict) else {}
        if annotations.get(ATTESTATION_TYPE) == ATTESTATION_VALUE:
            attestations.append(descriptor)
            continue
        platform = platform_name(descriptor)
        if not platform or platform == "unknown/unknown":
            errors.append("Published image contains a runtime descriptor without a valid platform")
            continue
        if platform in runtime:
            errors.append(f"Published image contains platform {platform} more than once")
        runtime[platform] = descriptor

    missing = sorted(EXPECTED_PLATFORMS - runtime.keys())
    unexpected = sorted(runtime.keys() - EXPECTED_PLATFORMS)
    if missing:
        errors.append("Published image is missing platforms: " + ", ".join(missing))
    if unexpected:
        errors.append(
            "Published image contains unexpected runtime platforms: " + ", ".join(unexpected)
        )

    runtime_digests = {item.get("digest") for item in runtime.values()}
    if any(
        not isinstance(digest, str) or DIGEST.fullmatch(digest) is None
        for digest in runtime_digests
    ):
        errors.append("Published runtime platform descriptors must use SHA-256 digests")
    attested_digests = []
    for descriptor in attestations:
        digest = descriptor.get("digest")
        annotations = descriptor.get("annotations")
        reference = annotations.get(REFERENCE_DIGEST) if isinstance(annotations, dict) else None
        if not isinstance(digest, str) or DIGEST.fullmatch(digest) is None:
            errors.append("Published attestation descriptor must use a SHA-256 digest")
        if not isinstance(reference, str) or DIGEST.fullmatch(reference) is None:
            errors.append("Published attestation must reference one runtime manifest digest")
        else:
            attested_digests.append(reference)
    for digest in sorted(value for value in runtime_digests if isinstance(value, str)):
        if attested_digests.count(digest) != 1:
            errors.append(f"Runtime manifest {digest} must have exactly one attestation manifest")
    if set(attested_digests) - runtime_digests:
        errors.append("Published attestations reference manifests outside the runtime platform set")
    return errors, runtime, attestations


def validate_image_config(
    config: object,
    platform: str,
    expected_labels: dict[str, str],
) -> list[str]:
    errors: list[str] = []
    if not isinstance(config, dict):
        return [f"Published {platform} image config is not a JSON object"]
    operating_system, architecture = platform.split("/", maxsplit=1)
    if config.get("os") != operating_system or config.get("architecture") != architecture:
        errors.append(f"Published {platform} image config reports a different platform")
    image_config = config.get("config")
    labels = image_config.get("Labels") if isinstance(image_config, dict) else None
    if not isinstance(labels, dict):
        return [*errors, f"Published {platform} image config has no OCI labels"]
    for name, expected in expected_labels.items():
        if labels.get(name) != expected:
            errors.append(f"Published {platform} OCI label {name} must be {expected!r}")
    return errors


def validate_attestation_manifest(manifest: object, runtime_digest: str) -> list[str]:
    if not isinstance(manifest, dict) or not isinstance(manifest.get("layers"), list):
        return [f"Attestation for {runtime_digest} must contain an OCI layer list"]
    predicates: list[str] = []
    for layer in manifest["layers"]:
        annotations = layer.get("annotations") if isinstance(layer, dict) else None
        predicate = annotations.get(PREDICATE_TYPE) if isinstance(annotations, dict) else None
        if isinstance(predicate, str):
            predicates.append(predicate.lower())
    errors = []
    if not any("spdx" in predicate for predicate in predicates):
        errors.append(f"Attestation for {runtime_digest} has no SPDX SBOM predicate")
    if not any("slsa.dev/provenance" in predicate for predicate in predicates):
        errors.append(f"Attestation for {runtime_digest} has no SLSA provenance predicate")
    return errors


def validate_published_image(
    image_ref: str,
    digest: str,
    version: str,
    revision: str,
    created: str,
    source_repository: str,
) -> list[str]:
    errors: list[str] = []
    if DIGEST.fullmatch(digest) is None:
        return ["Expected multi-arch digest must be sha256 followed by 64 lowercase hex characters"]
    if VERSION.fullmatch(version) is None:
        return ["Expected version must use stable SemVer vMAJOR.MINOR.PATCH"]
    expected_suffix = f":{version}"
    if not image_ref.endswith(expected_suffix):
        return [f"Published image reference must end with the release tag {version}"]
    repository = image_ref[: -len(expected_suffix)]
    if IMAGE_REPOSITORY.fullmatch(repository) is None:
        return ["Published image repository must be a lowercase ghcr.io owner/keywars path"]
    if REVISION.fullmatch(revision) is None:
        return ["Expected revision must be a lowercase 40- or 64-character Git object ID"]
    if not created_is_valid(created):
        return ["Expected creation time must be an ISO-8601 timestamp with timezone"]
    if SOURCE_REPOSITORY.fullmatch(source_repository) is None:
        return ["Expected source must be a canonical HTTPS GitHub repository URL"]
    canonical_ref = f"{repository}@{digest}"
    try:
        index = inspect_json(canonical_ref, "--raw")
        index_errors, runtime, attestations = validate_index(index)
        errors.extend(index_errors)
        expected_labels = {
            "org.opencontainers.image.created": created,
            "org.opencontainers.image.revision": revision,
            "org.opencontainers.image.source": source_repository,
            "org.opencontainers.image.version": version[1:],
        }
        for platform, descriptor in runtime.items():
            platform_digest = descriptor.get("digest")
            if not isinstance(platform_digest, str) or DIGEST.fullmatch(platform_digest) is None:
                continue
            image = inspect_json(
                f"{repository}@{platform_digest}",
                "--format",
                "{{json .Image}}",
            )
            errors.extend(validate_image_config(image, platform, expected_labels))

        for descriptor in attestations:
            attestation_digest = descriptor.get("digest")
            annotations = descriptor.get("annotations")
            runtime_digest = (
                annotations.get(REFERENCE_DIGEST) if isinstance(annotations, dict) else None
            )
            if not isinstance(attestation_digest, str) or not isinstance(runtime_digest, str):
                continue
            attestation = inspect_json(f"{repository}@{attestation_digest}", "--raw")
            errors.extend(validate_attestation_manifest(attestation, runtime_digest))
    except (json.JSONDecodeError, OSError, subprocess.CalledProcessError) as error:
        errors.append(f"Could not inspect published image {canonical_ref}: {error}")
    return errors


def self_test() -> int:
    amd64_digest = "sha256:" + "1" * 64
    arm64_digest = "sha256:" + "2" * 64
    index = {
        "manifests": [
            {"digest": amd64_digest, "platform": {"os": "linux", "architecture": "amd64"}},
            {"digest": arm64_digest, "platform": {"os": "linux", "architecture": "arm64"}},
            {
                "digest": "sha256:" + "3" * 64,
                "platform": {"os": "unknown", "architecture": "unknown"},
                "annotations": {
                    ATTESTATION_TYPE: ATTESTATION_VALUE,
                    REFERENCE_DIGEST: amd64_digest,
                },
            },
            {
                "digest": "sha256:" + "4" * 64,
                "platform": {"os": "unknown", "architecture": "unknown"},
                "annotations": {
                    ATTESTATION_TYPE: ATTESTATION_VALUE,
                    REFERENCE_DIGEST: arm64_digest,
                },
            },
        ]
    }
    errors, runtime, attestations = validate_index(index)
    if errors or set(runtime) != EXPECTED_PLATFORMS or len(attestations) != 2:
        print("Published image index self-test failed:", errors)
        return 1

    labels = {
        "org.opencontainers.image.created": "2026-08-11T12:34:56Z",
        "org.opencontainers.image.revision": "a" * 40,
        "org.opencontainers.image.source": "https://github.com/example/keywars",
        "org.opencontainers.image.version": "0.5.0",
    }
    config = {"os": "linux", "architecture": "amd64", "config": {"Labels": labels}}
    if errors := validate_image_config(config, "linux/amd64", labels):
        print("Published image OCI-label self-test failed:", errors)
        return 1

    attestation = {
        "layers": [
            {"annotations": {PREDICATE_TYPE: "https://spdx.dev/Document"}},
            {"annotations": {PREDICATE_TYPE: "https://slsa.dev/provenance/v1"}},
        ]
    }
    if errors := validate_attestation_manifest(attestation, amd64_digest):
        print("Published image attestation self-test failed:", errors)
        return 1

    incomplete_index = {"manifests": index["manifests"][:1]}
    errors, _, _ = validate_index(incomplete_index)
    if not any("missing platforms" in error for error in errors):
        print("Published image missing-platform self-test failed")
        return 1
    unexpected_index = {
        "manifests": [
            *index["manifests"],
            {
                "digest": "sha256:" + "5" * 64,
                "platform": {"os": "linux", "architecture": "s390x"},
            },
        ]
    }
    errors, _, _ = validate_index(unexpected_index)
    if not any("unexpected runtime platforms" in error for error in errors):
        print("Published image unexpected-platform self-test failed")
        return 1
    if not validate_attestation_manifest({"layers": []}, amd64_digest):
        print("Published image missing-attestation self-test failed")
        return 1
    errors = validate_published_image(
        "ghcr.io/example/keywars:v0.4.9",
        "sha256:" + "0" * 64,
        "v0.5.0",
        "a" * 40,
        "2026-08-11T12:34:56Z",
        "https://github.com/example/keywars",
    )
    if not any("release tag" in error for error in errors):
        print("Published image tag-binding self-test failed")
        return 1
    print("Published image self-test: OK")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image_ref", nargs="?")
    parser.add_argument("--digest")
    parser.add_argument("--version")
    parser.add_argument("--revision")
    parser.add_argument("--created")
    parser.add_argument("--source-repository")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    required = [
        args.image_ref,
        args.digest,
        args.version,
        args.revision,
        args.created,
        args.source_repository,
    ]
    if any(value is None for value in required):
        parser.error(
            "image_ref, --digest, --version, --revision, --created and "
            "--source-repository are required"
        )
    errors = validate_published_image(
        args.image_ref,
        args.digest,
        args.version,
        args.revision,
        args.created,
        args.source_repository,
    )
    if errors:
        print("Published image check failed:")
        for error in errors:
            print(f"  {error}")
        return 1
    print("Published image check: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
