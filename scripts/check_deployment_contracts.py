#!/usr/bin/env python3
"""Prüft den gemeinsamen Compose-, Swarm- und Kubernetes-Vertrag."""

from __future__ import annotations

import pathlib
import re
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
ERRORS: list[str] = []


def load(relative_path: str) -> str:
    path = ROOT / relative_path
    if not path.is_file():
        ERRORS.append(f"Fehlende Datei: {relative_path}")
        return ""
    return path.read_text(encoding="utf-8")


def require(relative_path: str, fragments: list[str]) -> None:
    content = load(relative_path)
    for fragment in fragments:
        if fragment not in content:
            ERRORS.append(f"{relative_path}: fehlt {fragment!r}")


required_files = [
    ".github/workflows/performance.yml",
    "compose.yaml",
    "compose.scale.yaml",
    "deploy/swarm/stack.yaml",
    "deploy/swarm/Caddyfile",
    "deploy/k8s/kustomization.yaml",
    "deploy/k8s/cutover/kustomization.yaml",
    "deploy/k8s/cutover/job.yaml",
    "deploy/k8s/migration/kustomization.yaml",
    "deploy/k8s/migration/job.yaml",
    "deploy/k8s/hpa.yaml",
    "deploy/k8s/pdb.yaml",
    "deploy/k8s/network-policy.yaml",
    "deploy/images.txt",
    "docs/scale-operations.md",
    "tests/cluster/compose.ci.yaml",
]
for required_file in required_files:
    load(required_file)

performance_workflow = load(".github/workflows/performance.yml")
cutover_step = 'run --rm keywars-protocol-cutover'
migration_step = 'run --rm keywars-migrate'
if (
    performance_workflow.count(cutover_step) < 4
    or migration_step not in performance_workflow
    or performance_workflow.index(cutover_step) > performance_workflow.index(migration_step)
    or "redis-cli --raw GET keywars:cluster:protocol-version" not in performance_workflow
):
    ERRORS.append(
        ".github/workflows/performance.yml: Cluster-Cutover braucht Idempotenz-, Mismatch- und Reihenfolgetest"
    )

require("compose.yaml", ["${KEYWARS_BIND_ADDRESS:-127.0.0.1}"])
require("compose.scale.yaml", ["${KEYWARS_BIND_ADDRESS:-127.0.0.1}"])
require(
    "compose.scale.yaml",
    [
        "keywars-edge:",
        "keywars-web:",
        "keywars-arena:",
        "keywars-worker:",
        "keywars-protocol-cutover:",
        "keywars-migrate:",
        "postgres:",
        "redis:",
        "KEYWARS__DATABASE__PROVIDER",
        "ConnectionStrings__KeyWars",
        "KEYWARS__REDIS__CONNECTION_STRING",
        "maintenance cluster-protocol cutover --confirm-apps-stopped",
        "profiles: [operations]",
        "caddy:2.11.4-alpine@sha256:",
        "postgres:18.4-alpine@sha256:",
        "redis:8.6.5-alpine@sha256:",
    ],
)
require(
    "deploy/swarm/stack.yaml",
    [
        "keywars-edge:",
        "keywars-web:",
        "keywars-arena:",
        "keywars-worker:",
        "keywars-protocol-cutover:",
        "keywars-migrate:",
        "keywars-postgres:",
        "keywars-redis:",
        "update_config:",
        "rollback_config:",
        "stop_grace_period:",
        "nofile:",
        "mode: replicated-job",
        "KEYWARS_CUTOVER_REPLICAS:-0",
        "KEYWARS_MIGRATE_REPLICAS:-0",
        "maintenance cluster-protocol cutover --confirm-apps-stopped",
        "caddy:2.11.4-alpine@sha256:",
        "postgres:18.4-alpine@sha256:",
        "redis:8.6.5-alpine@sha256:",
    ],
)
require(
    "deploy/k8s/web.yaml",
    ["value: web", "/health/live", "/health/ready", "preStop:", "resources:", "type: Recreate"],
)
require(
    "deploy/k8s/arena.yaml",
    ["replicas: 2", "type: Recreate", "value: arena"],
)
require("deploy/k8s/worker.yaml", ["value: worker", "/health/live", "/health/ready", "type: Recreate"])
require(
    "deploy/k8s/cutover/job.yaml",
    ["name: keywars-protocol-cutover", "value: migrate", "--confirm-apps-stopped", "ttlSecondsAfterFinished:"],
)
require("deploy/k8s/migration/job.yaml", ["value: migrate", "ttlSecondsAfterFinished:"])
require(
    "docs/scale-operations.md",
    [
        "delete hpa keywars-web keywars-arena keywars-worker",
        "run --rm keywars-protocol-cutover",
        "apply -f deploy/k8s/runtime-config.yaml",
        "apply -k deploy/k8s/cutover",
        "KEYWARS_CUTOVER_REPLICAS=1",
    ],
)
require(
    "deploy/k8s/hpa.yaml",
    ["apiVersion: autoscaling/v2", "kind: HorizontalPodAutoscaler", "name: keywars-arena"],
)
require("deploy/k8s/pdb.yaml", ["apiVersion: policy/v1", "kind: PodDisruptionBudget"])
require(
    "deploy/k8s/network-policy.yaml",
    [
        "apiVersion: networking.k8s.io/v1",
        "kind: NetworkPolicy",
        "keywars-default-deny",
        "values: [web, arena, worker, migrate, protocol-cutover]",
    ],
)

route_fragments = [
    "/arena*",
    "/hubs/arena*",
    "/api/arena*",
    "/profil/loeschen*",
    "/profil/statistik-zuruecksetzen*",
]
require("deploy/swarm/Caddyfile", route_fragments)
require("deploy/k8s/runtime-config.yaml", route_fragments)
require("deploy/swarm/Caddyfile", ['@metrics path /metrics', 'respond @metrics "not found" 404'])
require("deploy/k8s/runtime-config.yaml", ['@metrics path /metrics', 'respond @metrics "not found" 404'])
require("deploy/k8s/edge.yaml", ["caddy:2.11.4-alpine@sha256:"])
require(
    "tests/cluster/compose.ci.yaml",
    [
        "postgres-tests:",
        "KEYWARS_TEST_POSTGRES_CONNECTION_STRING",
        "PostgreSqlPathUsesNativeRangesAndSkipsSqliteBackupRetention",
        "KEYWARS__AUTH__DEVELOPMENT_LOGIN",
    ],
)

image_manifest = load("deploy/images.txt")
keywars_image = "ghcr.io/adrianweidig/keywars:v0.5.0"
caddy_image = "caddy:2.11.4-alpine@sha256:5f5c8640aae01df9654968d946d8f1a56c497f1dd5c5cda4cf95ab7c14d58648"
postgres_image = "postgres:18.4-alpine@sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15"
redis_image = "redis:8.6.5-alpine@sha256:cd218f4b106a332c5c992e38a9480bfb9d7e9f8f7b0ec9a0023bfa36d9a408f9"
for image_reference in (keywars_image, caddy_image, postgres_image, redis_image):
    if image_reference not in image_manifest:
        ERRORS.append(f"deploy/images.txt: fehlt {image_reference}")

require("compose.scale.yaml", [caddy_image, postgres_image, redis_image])
require("deploy/swarm/stack.yaml", [caddy_image, postgres_image, redis_image])
require("deploy/k8s/edge.yaml", [caddy_image])

swarm = load("deploy/swarm/stack.yaml")
if not re.search(r"keywars-arena:.*?replicas: \$\{KEYWARS_ARENA_REPLICAS:-2\}", swarm, re.DOTALL):
    ERRORS.append("deploy/swarm/stack.yaml: Arena-Standard erlaubt keine mehreren Replikate")
for service_name in ("keywars-web", "keywars-arena", "keywars-worker"):
    service_match = re.search(
        rf"^  {service_name}:$(.*?)(?=^  [a-z0-9-]+:$)",
        swarm,
        re.DOTALL | re.MULTILINE,
    )
    if service_match is None:
        ERRORS.append(f"deploy/swarm/stack.yaml: Dienst {service_name} fehlt")
        continue
    service = service_match.group(1)
    if "order: stop-first" not in service or "failure_action: pause" not in service:
        ERRORS.append(
            f"deploy/swarm/stack.yaml: {service_name} muss Protokoll-Upgrades stop-first und ohne Auto-Rollback ausführen"
        )

for path in (ROOT / "deploy" / "k8s").rglob("*.yaml"):
    content = path.read_text(encoding="utf-8")
    relative = path.relative_to(ROOT).as_posix()
    if re.search(r"^kind:\s*Secret\s*$", content, re.MULTILINE):
        ERRORS.append(f"{relative}: Klartext-Secret-Objekte gehören nicht ins Repository")
    if re.search(r"^\s*type:\s*(NodePort|LoadBalancer)\s*$", content, re.MULTILINE):
        ERRORS.append(f"{relative}: öffentliche Service-Art muss Site-Konfiguration bleiben")
    if re.search(r"^\s*image:\s*\S+:latest\s*$", content, re.MULTILINE):
        ERRORS.append(f"{relative}: Image darf nicht unversioniert auf latest zeigen")

try:
    import yaml  # type: ignore[import-not-found]
except ImportError:
    yaml = None

if yaml is not None:
    yaml_files = [
        ROOT / "compose.yaml",
        ROOT / "compose.scale.yaml",
        ROOT / "tests/cluster/compose.ci.yaml",
    ]
    yaml_files.extend((ROOT / "deploy").rglob("*.yaml"))
    for path in yaml_files:
        if not path.is_file():
            continue
        try:
            list(yaml.safe_load_all(path.read_text(encoding="utf-8")))
        except yaml.YAMLError as error:
            ERRORS.append(f"{path.relative_to(ROOT).as_posix()}: ungültiges YAML: {error}")

    for relative_path in (
        "deploy/k8s/kustomization.yaml",
        "deploy/k8s/cutover/kustomization.yaml",
        "deploy/k8s/migration/kustomization.yaml",
    ):
        path = ROOT / relative_path
        if not path.is_file():
            continue
        document = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
        for resource in document.get("resources", []):
            resource_path = path.parent / resource
            if not resource_path.exists():
                ERRORS.append(f"{relative_path}: Ressource fehlt: {resource}")

scale_operations = load("docs/scale-operations.md")
runtime_config_step = "kubectl apply -f deploy/k8s/runtime-config.yaml"
kubernetes_cutover_step = "kubectl apply -k deploy/k8s/cutover"
if (
    runtime_config_step in scale_operations
    and kubernetes_cutover_step in scale_operations
    and scale_operations.rindex(runtime_config_step) > scale_operations.index(kubernetes_cutover_step)
):
    ERRORS.append(
        "docs/scale-operations.md: neue Runtime-Konfiguration muss vor dem Kubernetes-Cutover angewandt werden"
    )

if ERRORS:
    for error in sorted(set(ERRORS)):
        print(f"ERROR: {error}", file=sys.stderr)
    raise SystemExit(1)

parser_note = "inklusive YAML-Parsing" if yaml is not None else "ohne optionales PyYAML"
print(f"Deployment-Verträge sind konsistent ({parser_note}).")
