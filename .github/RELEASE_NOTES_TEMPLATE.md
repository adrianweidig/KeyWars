# KeyWars {{VERSION}}

{{CHANGES}}

## Container

- Image: `{{IMAGE_REF}}`
- Multi-Arch-Digest: `{{IMAGE_DIGEST}}`
- Plattformen: `linux/amd64`, `linux/arm64`
- OCI-Version: `{{OCI_VERSION}}`
- Revision: `{{REVISION}}`
- Erstellt: `{{CREATED}}`
- Quellcode: `{{SOURCE_REPOSITORY}}`
- SBOM und Provenance: als Registry-Attestierungen veröffentlicht

## Release-Artefakte

- `{{OFFLINE_ARCHIVE}}` für `linux/amd64`
- `{{DEPLOYMENT_BUNDLE}}` mit beiden Compose-Modi, Swarm-/Kubernetes-Dateien und Validator
- `SBOM-linux-amd64.spdx.json` und `SBOM-linux-arm64.spdx.json`
- `compose.yaml` und `default.env.example`
- `compose.scale.yaml` und `scale.env.example`
- `AIRGAP_INSTALL.md` für die Offline-Inbetriebnahme
- `SCALE_OPERATIONS.md` für Compose, Swarm und Kubernetes
- `RELEASE_MANIFEST.json` mit Image-, OCI- und Prüfsummenmetadaten
- `SHA256SUMS` zur Integritätsprüfung

Nicht im KeyWars-Offline-Archiv enthalten; für Air-Gap-Betrieb separat spiegeln:

{{EXTERNAL_IMAGES}}
