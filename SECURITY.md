# Security Policy

## Supported Versions

Security fixes are applied to the current `master` branch. Tagged releases inherit the security posture of the commit they were created from.

## Reporting a Vulnerability

Do not create a public issue for a suspected vulnerability.

Report security concerns through GitHub private vulnerability reporting:

https://github.com/adrianweidig/KeyWars/security/advisories/new

If private vulnerability reporting is unavailable, contact the repository owner directly through GitHub. Include:

- affected version or commit;
- deployment mode and relevant configuration without secrets;
- reproduction steps;
- expected impact;
- logs with credentials, tokens, hostnames and personal data redacted.

## Security Baseline

KeyWars is designed for self-hosted operation behind an operator-managed reverse proxy. Production requires LDAP configuration and should use TLS at the proxy boundary. The application stores runtime state under `/data` and supports read-only container roots.

## Disclosure Process

Security reports are acknowledged as soon as practical. Confirmed vulnerabilities are fixed on `master` first and then included in the next tagged release. Public disclosure should wait until a fix is available or 90 days have passed after the initial report, unless active exploitation requires a different timeline.
