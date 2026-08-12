[CmdletBinding()]
param(
    [switch]$RequireExternalTools
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

$temporaryEnvironment = @{}
$requiredEnvironment = @{
    KEYWARS_LDAP_URLS = 'ldaps://ldap.invalid:636'
    KEYWARS_LDAP_BASE_DN = 'DC=invalid'
    KEYWARS_LDAP_UPN_SUFFIX = 'invalid'
}

try {
    foreach ($entry in $requiredEnvironment.GetEnumerator()) {
        $existing = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        $temporaryEnvironment[$entry.Key] = $existing
        if ([string]::IsNullOrWhiteSpace($existing)) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    & python scripts/check_deployment_contracts.py
    if ($LASTEXITCODE -ne 0) { throw 'Deployment-Vertragsprüfung fehlgeschlagen.' }

    $yamllint = Get-Command yamllint -ErrorAction SilentlyContinue
    if ($yamllint) {
        $lintConfig = "{extends: default, rules: {line-length: {max: 140, level: warning}, truthy: {allowed-values: ['true', 'false', 'on']}}}"
        & $yamllint.Source -d $lintConfig deploy compose.yaml compose.scale.yaml
        if ($LASTEXITCODE -ne 0) { throw 'YAML-Lint fehlgeschlagen.' }
    } elseif ($RequireExternalTools) {
        throw 'yamllint fehlt.'
    } else {
        Write-Warning 'yamllint fehlt; YAML-Lint wurde übersprungen.'
    }

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($docker) {
        & $docker.Source compose -f compose.yaml config --quiet
        if ($LASTEXITCODE -ne 0) { throw 'compose.yaml ist ungültig.' }
        & $docker.Source compose -f compose.scale.yaml config --quiet
        if ($LASTEXITCODE -ne 0) { throw 'compose.scale.yaml ist ungültig.' }
        & $docker.Source stack config -c deploy/swarm/stack.yaml *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Der Swarm-Stack ist ungültig.' }
    } elseif ($RequireExternalTools) {
        throw 'Docker CLI fehlt.'
    } else {
        Write-Warning 'Docker CLI fehlt; Compose-/Stack-Schema wurde übersprungen.'
    }

    $kubectl = Get-Command kubectl -ErrorAction SilentlyContinue
    if ($kubectl) {
        & $kubectl.Source kustomize deploy/k8s *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Kubernetes-Kustomization ist ungültig.' }
        & $kubectl.Source kustomize deploy/k8s/migration *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Migration-Kustomization ist ungültig.' }
        & $kubectl.Source kustomize deploy/k8s/cutover *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Protokoll-Cutover-Kustomization ist ungültig.' }
    } elseif ($RequireExternalTools) {
        throw 'kubectl fehlt.'
    } else {
        Write-Warning 'kubectl fehlt; Kustomize-Build wurde übersprungen.'
    }

    $kubeconform = Get-Command kubeconform -ErrorAction SilentlyContinue
    if ($kubeconform) {
        & $kubeconform.Source -strict -summary -ignore-missing-schemas deploy/k8s
        if ($LASTEXITCODE -ne 0) { throw 'Kubernetes-Schemaprüfung fehlgeschlagen.' }
    } elseif ($RequireExternalTools) {
        throw 'kubeconform fehlt.'
    } else {
        Write-Warning 'kubeconform fehlt; Kubernetes-Schemaprüfung wurde übersprungen.'
    }

    Write-Host 'Deployment-Prüfung erfolgreich.' -ForegroundColor Green
}
finally {
    foreach ($entry in $temporaryEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    Pop-Location
}
