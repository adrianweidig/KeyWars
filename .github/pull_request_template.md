# Pull Request

## Zusammenfassung

- Kurzbeschreibung der Änderung.

## Validierung

- [ ] `dotnet restore --locked-mode`
- [ ] `dotnet format --verify-no-changes --no-restore`
- [ ] `dotnet build -c Release --no-restore`
- [ ] `dotnet test -c Release --no-build`
- [ ] Docker/Compose-Checks oder begründete Nichtausführung dokumentiert

## Security

- [ ] Keine Secrets, Zugangsdaten oder lokalen Artefakte committed
- [ ] LDAP-/Authentifizierungsverhalten bei relevanten Änderungen geprüft
