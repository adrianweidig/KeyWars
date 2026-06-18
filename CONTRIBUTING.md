# Contributing

Thanks for improving KeyWars.

## Development

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

## Pull Requests

- Keep changes scoped and explain operational impact.
- Update documentation when behavior, configuration or deployment changes.
- Add or update tests for changed behavior.
- Do not commit generated archives, databases, logs, secrets or local runtime files.
- Keep production authentication tied to LDAP/AD; Development login must remain non-production only.

## Commit Style

Use short imperative commit messages, for example:

```text
Add container vulnerability scan
Fix LDAP startup validation
```
