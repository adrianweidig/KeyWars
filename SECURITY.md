# Security Policy

## Supported Versions

Security fixes are applied to the current `master` branch. Tagged releases inherit the security posture of the commit they were created from.

## Reporting a Vulnerability

Do not create a public issue for a suspected vulnerability.

Report security concerns through GitHub private vulnerability reporting when available, or contact the repository owner directly through GitHub. Include:

- affected version or commit;
- deployment mode and relevant configuration without secrets;
- reproduction steps;
- expected impact;
- logs with credentials, tokens, hostnames and personal data redacted.

## Security Baseline

KeyWars is designed for self-hosted operation behind an operator-managed reverse proxy. Production requires LDAP configuration and should use TLS at the proxy boundary. The application stores runtime state under `/data` and supports read-only container roots.
