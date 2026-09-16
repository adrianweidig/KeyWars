[CmdletBinding()]
param(
    [ValidateRange(20, 600)]
    [int]$SoakSeconds = 45,

    [ValidateRange(1024, 65535)]
    [int]$Port = 5192,

    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')]
    [string]$ProjectName = "keywars-cluster-$PID",

    [string]$Image = 'keywars:cluster-test',

    [string]$ArtifactDirectory,

    [switch]$SkipImageBuild,

    [switch]$SkipLoadBuild,

    [switch]$Keep
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$composeFile = Join-Path $repoRoot 'compose.test-cluster.yaml'
$ciComposeFile = Join-Path $repoRoot 'tests/cluster/compose.ci.yaml'
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot "TestResults/cluster-harness/$ProjectName"
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

$composePrefix = @(
    'compose',
    '--project-name', $ProjectName,
    '--file', $composeFile,
    '--file', $ciComposeFile
)
$composeWasUsed = $false
$rolloutImage = "keywars:cluster-rollout-$PID"
$soakJob = $null

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$Capture,

        [switch]$AllowFailure
    )

    $global:LASTEXITCODE = 0
    if ($Capture) {
        $lines = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    else {
        $lines = @()
        & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $detail = ($lines -join "`n").Trim()
        throw "Befehl '$FilePath $($Arguments -join ' ')' ist mit Exitcode $exitCode fehlgeschlagen.`n$detail"
    }
    if ($Capture) {
        return ($lines -join "`n").Trim()
    }
}

function Invoke-ComposeExpectFailure {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $script:composeWasUsed = $true
    $global:LASTEXITCODE = 0
    $nativeArguments = $script:composePrefix + $Arguments
    $output = @(& docker @nativeArguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join "`n").Trim()
    }
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$Capture,

        [switch]$AllowFailure
    )

    $script:composeWasUsed = $true
    return Invoke-NativeCommand `
        -FilePath 'docker' `
        -Arguments ($script:composePrefix + $Arguments) `
        -Capture:$Capture `
        -AllowFailure:$AllowFailure
}

function Invoke-Redis {
    param(
        [string]$Node = 'redis-node-2',

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    return Invoke-Compose `
        -Arguments (@('exec', '-T', $Node, 'redis-cli', '-c', '--raw') + $Arguments) `
        -Capture `
        -AllowFailure:$AllowFailure
}

function Invoke-StandaloneRedis {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    return Invoke-Compose `
        -Arguments (@('exec', '-T', 'redis-standalone', 'redis-cli', '--raw') + $Arguments) `
        -Capture `
        -AllowFailure:$AllowFailure
}

function Wait-Condition {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Condition,

        [Parameter(Mandatory)]
        [string]$FailureMessage,

        [int]$TimeoutSeconds = 60,

        [int]$IntervalSeconds = 2
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            if (& $Condition) {
                return
            }
        }
        catch {
            Write-Verbose $_
        }
        Start-Sleep -Seconds $IntervalSeconds
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Get-ContainerIds([string]$Service) {
    $raw = Invoke-Compose -Arguments @('ps', '-q', $Service) -Capture
    return @($raw -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-ServiceHealthy([string]$Service, [int]$ExpectedCount) {
    $ids = @(Get-ContainerIds $Service)
    if ($ids.Count -ne $ExpectedCount) {
        return $false
    }
    foreach ($id in $ids) {
        $health = Invoke-NativeCommand `
            -FilePath 'docker' `
            -Arguments @('inspect', '--format', '{{.State.Health.Status}}', $id) `
            -Capture `
            -AllowFailure
        if ($health -ne 'healthy') {
            return $false
        }
    }
    return $true
}

function Wait-ServiceHealthy([string]$Service, [int]$ExpectedCount, [int]$TimeoutSeconds = 120) {
    Wait-Condition `
        -TimeoutSeconds $TimeoutSeconds `
        -FailureMessage "$Service erreichte nicht $ExpectedCount gesunde Replikate." `
        -Condition { Test-ServiceHealthy $Service $ExpectedCount }
}

function Test-EdgeReady {
    try {
        $response = Invoke-WebRequest `
            -Uri "http://127.0.0.1:$Port/health/ready" `
            -TimeoutSec 5 `
            -UseBasicParsing
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Wait-EdgeReady([int]$TimeoutSeconds = 90) {
    Wait-Condition `
        -TimeoutSeconds $TimeoutSeconds `
        -FailureMessage 'Der öffentliche Readiness-Endpunkt wurde nicht rechtzeitig bereit.' `
        -Condition { Test-EdgeReady }
}

function Assert-OperationalEndpoints {
    $ready = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$Port/health/ready" `
        -TimeoutSec 10
    if ($ready.status -ne 'ok' -or $ready.redis -ne $true -or $ready.pendingMigrations -ne 0) {
        throw 'Der Readiness-Vertrag meldet PostgreSQL, Redis oder Migrationen nicht bereit.'
    }

    $persistence = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$Port/health/arena-persistence" `
        -TimeoutSec 10
    if ($null -eq $persistence.pendingJobs -or $null -eq $persistence.failedRecords) {
        throw 'Der Arena-Persistenz-Endpunkt enthält nicht den Queue-Vertrag.'
    }

    $metrics = Invoke-Compose -Arguments @(
        'exec', '-T', 'keywars-edge',
        'wget', '--quiet', '--output-document=-', 'http://keywars-arena:8080/metrics'
    ) -Capture
    if ($metrics -notmatch '(?m)^keywars_') {
        throw 'Der interne Prometheus-Endpunkt enthält keine KeyWars-Metriken.'
    }
}

function Assert-ApplicationLogsClean([string]$Name) {
    $applicationLogs = Invoke-Compose -Arguments @(
        'logs', '--no-color', 'keywars-web', 'keywars-arena', 'keywars-worker'
    ) -Capture
    $applicationLogs | Set-Content `
        -LiteralPath (Join-Path $ArtifactDirectory "$Name-application.log") `
        -Encoding utf8
    if ($applicationLogs -match '(?i)\b(CROSSSLOT|NOSCRIPT|MOVED)\b') {
        throw 'Die Anwendung hat CROSSSLOT, NOSCRIPT oder eine unaufgelöste MOVED-Antwort protokolliert.'
    }
}

function Get-ClusterSnapshot([string]$Node = 'redis-node-2') {
    $info = Invoke-Redis -Node $Node -Arguments @('CLUSTER', 'INFO')
    $nodes = Invoke-Redis -Node $Node -Arguments @('CLUSTER', 'NODES')
    return [pscustomobject]@{
        Info = $info
        Nodes = $nodes
        HealthyMasters = @($nodes -split "`r?`n" | Where-Object {
            $_ -match '\bmaster\b' -and $_ -notmatch '(?:^|[, ])fail\??(?:[, ]|$)'
        }).Count
        HealthyReplicas = @($nodes -split "`r?`n" | Where-Object {
            $_ -match '\bslave\b' -and $_ -notmatch '(?:^|[, ])fail\??(?:[, ]|$)'
        }).Count
        ConnectedNodes = @($nodes -split "`r?`n" | Where-Object {
            $_ -match '\bconnected\b' -and $_ -notmatch '(?:^|[, ])fail\??(?:[, ]|$)'
        }).Count
    }
}

function Test-ClusterReady([bool]$RequireSixNodes) {
    try {
        $snapshot = Get-ClusterSnapshot
        if ($snapshot.Info -notmatch '(?m)^cluster_state:ok\r?$' -or
            $snapshot.Info -notmatch '(?m)^cluster_slots_assigned:16384\r?$' -or
            $snapshot.HealthyMasters -ne 3) {
            return $false
        }
        if ($RequireSixNodes -and ($snapshot.HealthyReplicas -lt 3 -or $snapshot.ConnectedNodes -lt 6)) {
            return $false
        }
        return $true
    }
    catch {
        return $false
    }
}

function Wait-ClusterReady([bool]$RequireSixNodes, [int]$TimeoutSeconds = 75) {
    Wait-Condition `
        -TimeoutSeconds $TimeoutSeconds `
        -FailureMessage 'Der Redis-Cluster erreichte nicht drei gesunde Master und 16.384 Slots.' `
        -Condition { Test-ClusterReady $RequireSixNodes }
}

function Assert-CrossSlotContract {
    $sameA = 'keywars:{cluster-contract}:a'
    $sameB = 'keywars:{cluster-contract}:b'
    $other = 'keywars:{other-contract}:c'
    try {
        $sameSlotA = [int](Invoke-Redis -Arguments @('CLUSTER', 'KEYSLOT', $sameA))
        $sameSlotB = [int](Invoke-Redis -Arguments @('CLUSTER', 'KEYSLOT', $sameB))
        $otherSlot = [int](Invoke-Redis -Arguments @('CLUSTER', 'KEYSLOT', $other))
        if ($sameSlotA -ne $sameSlotB -or $sameSlotA -eq $otherSlot) {
            throw 'Redis wendet die erwartete Hash-Tag-Slotzuordnung nicht an.'
        }

        $null = Invoke-Redis -Arguments @('MSET', $sameA, '1', $sameB, '2')
        $crossSlot = Invoke-Redis -Arguments @('MGET', $sameA, $other) -AllowFailure
        if ($crossSlot -notmatch '(?i)CROSSSLOT') {
            throw "Der Negativvertrag lieferte keinen CROSSSLOT-Fehler: $crossSlot"
        }
    }
    finally {
        $null = Invoke-Redis -Arguments @('DEL', $sameA) -AllowFailure
        $null = Invoke-Redis -Arguments @('DEL', $sameB) -AllowFailure
        $null = Invoke-Redis -Arguments @('DEL', $other) -AllowFailure
    }
}

function Get-RedisBucketHex([guid]$Id) {
    $canonicalBytes = $Id.ToByteArray()
    [Array]::Reverse($canonicalBytes, 0, 4)
    [Array]::Reverse($canonicalBytes, 4, 2)
    [Array]::Reverse($canonicalBytes, 6, 2)
    $digest = [Security.Cryptography.SHA256]::HashData($canonicalBytes)
    return $digest[0].ToString('x2')
}

function Get-CompletionQueueCounts {
    $aggregationScript = @'
pending=0
failed=0
bucket=0
while [ "$bucket" -lt 256 ]; do
    hex="$(printf '%02x' "$bucket")"
    pending_value="$(redis-cli -c -h redis-node-2 --raw ZCARD "keywars:{completion-b${hex}}:pending")"
    failed_value="$(redis-cli -c -h redis-node-2 --raw ZCARD "keywars:{completion-b${hex}}:failed")"
    pending="$((pending + pending_value))"
    failed="$((failed + failed_value))"
    bucket="$((bucket + 1))"
done
printf '%s %s\n' "$pending" "$failed"
'@
    $result = Invoke-Compose -Arguments @(
        'exec', '-T', 'redis-node-2', '/bin/sh', '-ec', $aggregationScript
    ) -Capture
    if ($result -notmatch '(?m)^(?<pending>\d+)\s+(?<failed>\d+)\s*$') {
        throw "Die clusterweite Queue-Aggregation lieferte '$result'."
    }
    return [pscustomobject]@{
        Pending = [long]$Matches.pending
        Failed = [long]$Matches.failed
    }
}

function Invoke-LoadTest {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $outputPath = Join-Path $ArtifactDirectory "$Name.txt"
    $lines = @(& $script:dotnetPath $script:loadTestDll @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $lines | Set-Content -LiteralPath $outputPath -Encoding utf8
    foreach ($line in $lines) {
        Write-Host $line
    }
    if ($exitCode -ne 0) {
        throw "Lasttest '$Name' ist mit Exitcode $exitCode fehlgeschlagen."
    }
}

function Invoke-RollingImageUpdate {
    param(
        [Parameter(Mandatory)]
        [string]$Service,

        [Parameter(Mandatory)]
        [int]$DesiredCount
    )

    $baselineIds = @(Get-ContainerIds $Service)
    if ($baselineIds.Count -ne $DesiredCount) {
        throw "$Service hat vor dem Rolling Update nicht $DesiredCount Replikate."
    }
    $surgeCount = $DesiredCount + 1
    Invoke-Compose -Arguments @(
        'up', '-d', '--no-deps', '--no-recreate',
        '--scale', "$Service=$surgeCount", $Service
    )
    Wait-ServiceHealthy $Service $surgeCount

    foreach ($containerId in $baselineIds) {
        Invoke-NativeCommand -FilePath 'docker' -Arguments @('stop', '--time', '20', $containerId)
        Invoke-NativeCommand -FilePath 'docker' -Arguments @('rm', $containerId)
        Invoke-Compose -Arguments @(
            'up', '-d', '--no-deps', '--no-recreate',
            '--scale', "$Service=$surgeCount", $Service
        )
        Wait-ServiceHealthy $Service $surgeCount
        Wait-EdgeReady
    }

    Invoke-Compose -Arguments @(
        'up', '-d', '--no-deps', '--no-recreate',
        '--scale', "$Service=$DesiredCount", $Service
    )
    Wait-ServiceHealthy $Service $DesiredCount
    foreach ($containerId in @(Get-ContainerIds $Service)) {
        $configuredImage = Invoke-NativeCommand `
            -FilePath 'docker' `
            -Arguments @('inspect', '--format', '{{.Config.Image}}', $containerId) `
            -Capture
        if ($configuredImage -ne $script:rolloutImage) {
            throw "$Service enthält nach dem Rolling Update noch das Image '$configuredImage'."
        }
    }
}

function Get-DotNetPath {
    $isWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)
    $localName = if ($isWindows) { 'dotnet.exe' } else { 'dotnet' }
    $localPath = Join-Path $repoRoot ".dotnet/$localName"
    if (Test-Path -LiteralPath $localPath) {
        return (Resolve-Path $localPath).Path
    }
    $command = Get-Command dotnet -ErrorAction Stop
    return $command.Source
}

$trackedEnvironment = @(
    'KEYWARS_TEST_CLUSTER_IMAGE',
    'KEYWARS_TEST_CLUSTER_PORT',
    'KEYWARS_TEST_POSTGRES_PASSWORD',
    'KEYWARS_TEST_ROLLOUT_ID'
)
$previousEnvironment = @{}
foreach ($name in $trackedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    Push-Location $repoRoot
    $null = Get-Command docker -ErrorAction Stop
    $script:dotnetPath = Get-DotNetPath
    $script:loadTestDll = Join-Path $repoRoot 'tools/KeyWars.LoadTest/bin/Release/net10.0/KeyWars.LoadTest.dll'

    $env:KEYWARS_TEST_CLUSTER_IMAGE = $Image
    $env:KEYWARS_TEST_CLUSTER_PORT = $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:KEYWARS_TEST_POSTGRES_PASSWORD = "$([guid]::NewGuid().ToString('N'))$([guid]::NewGuid().ToString('N'))"
    $env:KEYWARS_TEST_ROLLOUT_ID = 'baseline'

    Write-Step 'Toolchain und Compose-Vertrag prüfen'
    Invoke-NativeCommand -FilePath 'docker' -Arguments @('compose', 'version')
    Invoke-Compose -Arguments @('config', '--quiet')

    if (-not $SkipImageBuild) {
        Write-Step "Anwendungsimage $Image bauen"
        Invoke-NativeCommand -FilePath 'docker' -Arguments @('build', '--tag', $Image, '.')
    }

    if (-not $SkipLoadBuild) {
        Write-Step 'SignalR-Lastclient und Redis-Store-Probe bauen'
        Invoke-NativeCommand -FilePath $script:dotnetPath -Arguments @(
            'restore', 'tools/KeyWars.LoadTest/KeyWars.LoadTest.csproj', '--locked-mode'
        )
        Invoke-NativeCommand -FilePath $script:dotnetPath -Arguments @(
            'build', 'tools/KeyWars.LoadTest/KeyWars.LoadTest.csproj',
            '-c', 'Release', '--no-restore'
        )
        Invoke-NativeCommand -FilePath $script:dotnetPath -Arguments @(
            'restore', 'tests/cluster/KeyWars.ClusterProbe/KeyWars.ClusterProbe.csproj', '--locked-mode'
        )
        Invoke-NativeCommand -FilePath $script:dotnetPath -Arguments @(
            'build', 'tests/cluster/KeyWars.ClusterProbe/KeyWars.ClusterProbe.csproj',
            '-c', 'Release', '--no-restore'
        )
    }
    if (-not (Test-Path -LiteralPath $script:loadTestDll)) {
        throw "Der Lastclient fehlt: $script:loadTestDll"
    }

    Write-Step 'PostgreSQL, Standalone-Redis und sechs Redis-Cluster-Knoten starten'
    Invoke-Compose -Arguments @(
        'up', '-d', '--wait',
        'postgres',
        'redis-standalone',
        'redis-node-1', 'redis-node-2', 'redis-node-3',
        'redis-node-4', 'redis-node-5', 'redis-node-6'
    )
    Write-Step 'PostgreSQL-Retention gegen den echten Provider prüfen'
    Invoke-Compose -Arguments @('run', '--rm', 'postgres-tests')

    Write-Step 'v2->v3-Cutover gegen den ausgelieferten Standalone-Redis-Vertrag prüfen'
    $standaloneProfileId = '0198a1b2c3d47e5f8123456789abcdef'
    $standalonePermanentDeletedKey = "keywars:{profile-access-b4e}:state:$standaloneProfileId"
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-standalone-protocol-cutover')
    $freshStandaloneProtocol = Invoke-StandaloneRedis -Arguments @('GET', 'keywars:cluster:protocol-version')
    if ($freshStandaloneProtocol -ne '3') {
        throw "Eine frische Standalone-Installation aktivierte Protokoll '$freshStandaloneProtocol' statt '3'."
    }
    $standaloneLegacyAttemptKey = 'keywars:{attempt}:legacy-v1-state'
    $null = Invoke-StandaloneRedis -Arguments @('SET', 'keywars:cluster:protocol-version', '1')
    $null = Invoke-StandaloneRedis -Arguments @('SET', $standaloneLegacyAttemptKey, 'legacy-payload')
    $rejectedLegacyCutover = Invoke-ComposeExpectFailure `
        -Arguments @('run', '--rm', 'keywars-standalone-protocol-cutover')
    if ($rejectedLegacyCutover.ExitCode -eq 0) {
        throw 'Der v3-Cutover hat den notwendigen v1->v2-Vorcutover übersprungen.'
    }
    $legacyStandaloneProtocol = Invoke-StandaloneRedis -Arguments @('GET', 'keywars:cluster:protocol-version')
    $legacyAttemptState = Invoke-StandaloneRedis -Arguments @('GET', $standaloneLegacyAttemptKey)
    if ($legacyStandaloneProtocol -ne '1' -or $legacyAttemptState -ne 'legacy-payload') {
        throw "Der abgelehnte v1-Cutover veränderte Marker oder Legacy-Daten (marker='$legacyStandaloneProtocol', state='$legacyAttemptState')."
    }
    $null = Invoke-StandaloneRedis -Arguments @('DEL', $standaloneLegacyAttemptKey)
    $null = Invoke-StandaloneRedis -Arguments @('SET', 'keywars:cluster:protocol-version', '2')
    $null = Invoke-StandaloneRedis -Arguments @('SET', $standalonePermanentDeletedKey, 'deleted')
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-standalone-protocol-cutover')
    $standaloneProtocol = Invoke-StandaloneRedis -Arguments @('GET', 'keywars:cluster:protocol-version')
    $standaloneDeletedState = Invoke-StandaloneRedis -Arguments @('GET', $standalonePermanentDeletedKey)
    $standaloneDeletedTtl = Invoke-StandaloneRedis -Arguments @('PTTL', $standalonePermanentDeletedKey)
    if (
        $standaloneProtocol -ne '3' -or
        $standaloneDeletedState -ne 'deleted' -or
        $standaloneDeletedTtl -ne '-1'
    ) {
        throw "Standalone-Cutoververtrag verletzt (protocol='$standaloneProtocol', state='$standaloneDeletedState', ttl='$standaloneDeletedTtl')."
    }
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-standalone-protocol-cutover')
    Invoke-StandaloneRedis -Arguments @('INFO', 'server') |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'redis-standalone-info.txt') -Encoding utf8

    Invoke-Compose -Arguments @('run', '--rm', 'redis-cluster-init')
    Wait-ClusterReady -RequireSixNodes $true
    $initialCluster = Get-ClusterSnapshot
    $initialCluster.Nodes | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'redis-cluster-initial.txt') -Encoding utf8
    if ($initialCluster.Nodes -notmatch '(?m)172\.28\.250\.11:6379@16379\s+.*\bmaster\b') {
        throw 'redis-node-1 wurde nicht als einer der drei initialen Master angelegt.'
    }
    Assert-CrossSlotContract

    Write-Step 'Echte Attempt-, Presence-, Progress- und Profil-Gate-Flows über mehrere Slots prüfen'
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-cluster-probe')

    Write-Step 'Cluster-Protokoll setzen und PostgreSQL migrieren'
    $legacyPendingKey = 'keywars:{completion-admission}:active'
    $legacyRoomId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $legacyDeletedProfileId = '0198a1b2c3d47e5f8123456789abcdef'
    $permanentDeletedKey = "keywars:{profile-access-b4e}:state:$legacyDeletedProfileId"
    $null = Invoke-Redis -Arguments @('SET', 'keywars:cluster:protocol-version', '2')
    $null = Invoke-Redis -Arguments @('SET', $permanentDeletedKey, 'deleted')
    $null = Invoke-Redis -Arguments @('SADD', $legacyPendingKey, $legacyRoomId)
    $rejectedCutover = Invoke-ComposeExpectFailure `
        -Arguments @('run', '--rm', 'keywars-protocol-cutover')
    if ($rejectedCutover.ExitCode -eq 0) {
        throw 'Der v3-Cutover hat eine offene Abschlussqueue akzeptiert.'
    }
    $rejectedCutover.Output | Set-Content `
        -LiteralPath (Join-Path $ArtifactDirectory 'cutover-rejected.txt') `
        -Encoding utf8
    $markerBeforeCutover = Invoke-Redis -Arguments @('GET', 'keywars:cluster:protocol-version')
    if ($markerBeforeCutover -ne '2') {
        throw "Der abgelehnte v3-Cutover veränderte den v2-Marker zu '$markerBeforeCutover'."
    }
    $null = Invoke-Redis -Arguments @('SREM', $legacyPendingKey, $legacyRoomId)
    $null = Invoke-Redis -Arguments @('DEL', $legacyPendingKey)
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-protocol-cutover')
    $activeProtocol = Invoke-Redis -Arguments @('GET', 'keywars:cluster:protocol-version')
    if ($activeProtocol -ne '3') {
        throw "Der Cutover aktivierte Cluster-Protokoll '$activeProtocol' statt '3'."
    }
    $migratedDeletedState = Invoke-Redis -Arguments @('GET', $permanentDeletedKey)
    $migratedDeletedTtl = Invoke-Redis -Arguments @('PTTL', $permanentDeletedKey)
    if ($migratedDeletedState -ne 'deleted' -or $migratedDeletedTtl -ne '-1') {
        throw "Der v2->v3-Cutover bewahrte keinen dauerhaften Löschmarker (state='$migratedDeletedState', ttl='$migratedDeletedTtl')."
    }
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-protocol-cutover')
    Invoke-Compose -Arguments @('run', '--rm', 'keywars-migrate')

    Write-Step 'Je zwei Web-, Arena- und Worker-Replikate starten'
    Invoke-Compose -Arguments @(
        'up', '-d',
        '--scale', 'keywars-web=2',
        '--scale', 'keywars-arena=2',
        '--scale', 'keywars-worker=2',
        'keywars-web', 'keywars-arena', 'keywars-worker', 'keywars-edge'
    )
    Wait-ServiceHealthy 'keywars-web' 2
    Wait-ServiceHealthy 'keywars-arena' 2
    Wait-ServiceHealthy 'keywars-worker' 2
    Wait-ServiceHealthy 'keywars-edge' 1
    Wait-EdgeReady
    Assert-OperationalEndpoints

    Write-Step 'Worker-Stillstand und Pending-Completion-Recovery prüfen'
    Invoke-Compose -Arguments @('stop', '--timeout', '20', 'keywars-worker')
    $pendingReportPath = Join-Path $ArtifactDirectory 'pending-recovery.json'
    Invoke-LoadTest -Name 'pending-recovery' -Arguments @(
        '--signalr', '--scenario', 'smoke',
        '--base-url', "http://127.0.0.1:$Port",
        '--rooms', '3', '--participants', '3', '--steps', '12',
        '--typing-cps', '8', '--jitter-ms', '20', '--reconnect-percent', '34',
        '--operation-timeout-ms', '15000', '--timeout-seconds', '240',
        '--slo-p95-ms', '4000', '--slo-p99-ms', '8000', '--slo-fanout-p95-ms', '5000',
        '--slo-error-rate-percent', '0', '--slo-missing-broadcasts', '0',
        '--json', $pendingReportPath
    )
    $queueCounts = Get-CompletionQueueCounts
    $pendingCount = $queueCounts.Pending
    if ($pendingCount -lt 3) {
        throw "Ohne Worker wurden nur $pendingCount Pending-Abschlüsse gefunden."
    }

    Invoke-Compose -Arguments @(
        'up', '-d', '--no-deps', '--scale', 'keywars-worker=2', 'keywars-worker'
    )
    Wait-ServiceHealthy 'keywars-worker' 2
    Wait-Condition `
        -TimeoutSeconds 90 `
        -FailureMessage 'Die wieder gestarteten Worker haben die Abschlussqueue nicht geleert.' `
        -Condition {
            $counts = Get-CompletionQueueCounts
            $counts.Pending -eq 0 -and $counts.Failed -eq 0
        }

    $pendingReport = Get-Content -LiteralPath $pendingReportPath -Raw | ConvertFrom-Json -Depth 100
    $roomResultsProperty = if ($pendingReport.PSObject.Properties['RoomResults']) {
        'RoomResults'
    }
    elseif ($pendingReport.PSObject.Properties['Rooms']) {
        'Rooms'
    }
    else {
        throw 'Der Lasttestbericht enthält keine Raumresultate.'
    }
    $roomId = [guid]$pendingReport.$roomResultsProperty[0].RoomId
    $completionBucket = Get-RedisBucketHex $roomId
    $statusKey = "keywars:{completion-b$completionBucket}:status:$($roomId.ToString('N'))"
    $statusTtl = [long](Invoke-Redis -Arguments @('PTTL', $statusKey))
    if ($statusTtl -le 0 -or $statusTtl -gt [long][TimeSpan]::FromDays(7).TotalMilliseconds) {
        throw "Der persistierte Abschlussstatus hat keine gültige Sieben-Tage-TTL: $statusTtl ms."
    }
    $persistedRooms = [long](Invoke-Compose -Arguments @(
        'exec', '-T', 'postgres', 'psql', '-U', 'keywars', '-d', 'keywars',
        '-tAc', 'SELECT count(*) FROM "LiveRoomSummaries";'
    ) -Capture)
    if ($persistedRooms -lt 3) {
        throw "PostgreSQL enthält nach Recovery nur $persistedRooms Arena-Abschlüsse."
    }

    Write-Step 'Web- und Arena-Container-Kill mit automatischem Restart prüfen'
    foreach ($service in @('keywars-web', 'keywars-arena')) {
        $victim = @(Get-ContainerIds $service)[0]
        Invoke-NativeCommand -FilePath 'docker' -Arguments @('kill', $victim)
        Wait-ServiceHealthy $service 2
        $restartCount = [int](Invoke-NativeCommand `
            -FilePath 'docker' `
            -Arguments @('inspect', '--format', '{{.RestartCount}}', $victim) `
            -Capture)
        if ($restartCount -lt 1) {
            throw "$service wurde nach docker kill nicht durch die Restart-Policy wiederhergestellt."
        }
        Wait-EdgeReady
    }

    Write-Step "Bounded Soak ($SoakSeconds s) mit Redis-Master-Failover starten"
    $soakSteps = [Math]::Max(160, $SoakSeconds * 8)
    $soakReportPath = Join-Path $ArtifactDirectory 'soak-failover.json'
    $soakArguments = @(
        '--signalr', '--scenario', 'soak',
        '--base-url', "http://127.0.0.1:$Port",
        '--rooms', '3', '--participants', '4', '--steps', $soakSteps.ToString(),
        '--typing-cps', '8', '--jitter-ms', '30', '--reconnect-percent', '25',
        '--operation-timeout-ms', '20000',
        '--timeout-seconds', ($SoakSeconds + 240).ToString(),
        '--slo-p95-ms', '5000', '--slo-p99-ms', '15000', '--slo-fanout-p95-ms', '8000',
        '--slo-error-rate-percent', '0', '--slo-missing-broadcasts', '0',
        '--json', $soakReportPath
    )
    $soakArgumentsJson = ConvertTo-Json -InputObject $soakArguments -Compress
    $soakJob = Start-Job -ScriptBlock {
        param($DotNet, $Dll, $ArgumentsJson, $WorkingDirectory)
        Set-Location $WorkingDirectory
        $arguments = @(ConvertFrom-Json -InputObject $ArgumentsJson)
        $output = @(& $DotNet $Dll @arguments 2>&1)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    } -ArgumentList $script:dotnetPath, $script:loadTestDll, $soakArgumentsJson, $repoRoot
    Start-Sleep -Seconds 5
    if ($soakJob.State -notin @('Running', 'NotStarted')) {
        throw "Der Soak ist vor dem Failover beendet worden: $($soakJob.State)"
    }

    Invoke-Compose -Arguments @('stop', '--timeout', '2', 'redis-node-1')
    Wait-ClusterReady -RequireSixNodes $false -TimeoutSeconds 75
    $failedOverCluster = Get-ClusterSnapshot
    $failedOverCluster.Nodes | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'redis-cluster-failed-over.txt') -Encoding utf8
    Invoke-Compose -Arguments @('start', 'redis-node-1')
    Wait-ServiceHealthy 'redis-node-1' 1
    Wait-ClusterReady -RequireSixNodes $true -TimeoutSeconds 75

    $completedJob = Wait-Job -Job $soakJob -Timeout ($SoakSeconds + 240)
    if ($null -eq $completedJob) {
        Stop-Job -Job $soakJob
        throw 'Der bounded Soak hat sein Zeitlimit überschritten.'
    }
    $soakResult = Receive-Job -Job $soakJob
    @($soakResult.Output) | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'soak-failover.txt') -Encoding utf8
    foreach ($line in @($soakResult.Output)) {
        Write-Host $line
    }
    if ([int]$soakResult.ExitCode -ne 0) {
        throw "Der Soak-/Failover-Lauf ist mit Exitcode $($soakResult.ExitCode) fehlgeschlagen."
    }
    Remove-Job -Job $soakJob -Force
    $soakJob = $null

    Write-Step 'Arena-Replik während aktiver Progress-Flows hart neu starten'
    $arenaRestartReportPath = Join-Path $ArtifactDirectory 'arena-restart.json'
    $arenaRestartSteps = [Math]::Max(240, $SoakSeconds * 10)
    $arenaRestartArguments = @(
        '--signalr', '--scenario', 'steady',
        '--base-url', "http://127.0.0.1:$Port",
        '--rooms', '2', '--participants', '4', '--steps', $arenaRestartSteps.ToString(),
        '--typing-cps', '10', '--jitter-ms', '25', '--reconnect-percent', '25',
        '--operation-timeout-ms', '20000', '--timeout-seconds', '240',
        '--slo-p95-ms', '5000', '--slo-p99-ms', '15000', '--slo-fanout-p95-ms', '8000',
        '--slo-error-rate-percent', '0', '--slo-missing-broadcasts', '0',
        '--json', $arenaRestartReportPath
    )
    $arenaRestartJob = $null
    try {
        $arenaRestartArgumentsJson = ConvertTo-Json -InputObject $arenaRestartArguments -Compress
        $arenaRestartJob = Start-Job -ScriptBlock {
            param($DotNet, $Dll, $ArgumentsJson, $WorkingDirectory)
            Set-Location $WorkingDirectory
            $arguments = @(ConvertFrom-Json -InputObject $ArgumentsJson)
            $output = @(& $DotNet $Dll @arguments 2>&1)
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
        } -ArgumentList $script:dotnetPath, $script:loadTestDll, $arenaRestartArgumentsJson, $repoRoot
        Start-Sleep -Seconds 10
        $arenaVictim = @(Get-ContainerIds 'keywars-arena')[0]
        Invoke-NativeCommand -FilePath 'docker' -Arguments @('kill', $arenaVictim)
        Wait-ServiceHealthy 'keywars-arena' 2
        Wait-EdgeReady
        $completedArenaJob = Wait-Job -Job $arenaRestartJob -Timeout 240
        if ($null -eq $completedArenaJob) {
            Stop-Job -Job $arenaRestartJob
            throw 'Der Progress-Recovery-Lauf hat sein Zeitlimit überschritten.'
        }
        $arenaRestartResult = Receive-Job -Job $arenaRestartJob
        @($arenaRestartResult.Output) | Set-Content `
            -LiteralPath (Join-Path $ArtifactDirectory 'arena-restart.txt') `
            -Encoding utf8
        foreach ($line in @($arenaRestartResult.Output)) {
            Write-Host $line
        }
        if ([int]$arenaRestartResult.ExitCode -ne 0) {
            throw "Der Arena-Progress-Recovery-Lauf ist mit Exitcode $($arenaRestartResult.ExitCode) fehlgeschlagen."
        }
    }
    finally {
        if ($null -ne $arenaRestartJob) {
            Remove-Job -Job $arenaRestartJob -Force -ErrorAction SilentlyContinue
        }
    }

    Assert-ApplicationLogsClean 'pre-rollout'
    Write-Step 'Rolling Image Update mit einem Surge-Replikat je Rolle prüfen'
    Invoke-NativeCommand -FilePath 'docker' -Arguments @('tag', $Image, $rolloutImage)
    $env:KEYWARS_TEST_CLUSTER_IMAGE = $rolloutImage
    $env:KEYWARS_TEST_ROLLOUT_ID = "rollout-$PID"
    Invoke-RollingImageUpdate -Service 'keywars-web' -DesiredCount 2
    Invoke-RollingImageUpdate -Service 'keywars-arena' -DesiredCount 2
    Invoke-RollingImageUpdate -Service 'keywars-worker' -DesiredCount 2
    Wait-EdgeReady
    Assert-OperationalEndpoints

    Write-Step 'Abschließenden Cluster-Smoke und CROSSSLOT-Logvertrag prüfen'
    Invoke-LoadTest -Name 'post-rollout-smoke' -Arguments @(
        '--signalr', '--scenario', 'smoke',
        '--base-url', "http://127.0.0.1:$Port",
        '--rooms', '2', '--participants', '3', '--steps', '10',
        '--typing-cps', '8', '--jitter-ms', '20', '--reconnect-percent', '34',
        '--operation-timeout-ms', '15000', '--timeout-seconds', '180',
        '--slo-p95-ms', '4000', '--slo-p99-ms', '8000', '--slo-fanout-p95-ms', '5000',
        '--slo-error-rate-percent', '0', '--slo-missing-broadcasts', '0',
        '--json', (Join-Path $ArtifactDirectory 'post-rollout-smoke.json')
    )
    Wait-Condition `
        -TimeoutSeconds 90 `
        -FailureMessage 'Nach dem Rolling Update blieb die Abschlussqueue belegt.' `
        -Condition {
            $counts = Get-CompletionQueueCounts
            $counts.Pending -eq 0 -and $counts.Failed -eq 0
        }

    Assert-ApplicationLogsClean 'post-rollout'

    $finalCluster = Get-ClusterSnapshot
    $finalCluster.Nodes | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'redis-cluster-final.txt') -Encoding utf8
    [ordered]@{
        status = 'passed'
        redisMasters = $finalCluster.HealthyMasters
        redisReplicas = $finalCluster.HealthyReplicas
        redisSlots = 16384
        webReplicas = @(Get-ContainerIds 'keywars-web').Count
        arenaReplicas = @(Get-ContainerIds 'keywars-arena').Count
        workerReplicas = @(Get-ContainerIds 'keywars-worker').Count
        pendingRecovered = $pendingCount
        completionStatusTtlMilliseconds = $statusTtl
        persistedRooms = $persistedRooms
        soakSeconds = $SoakSeconds
        rollingImage = $rolloutImage
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'harness-summary.json') -Encoding utf8

    Write-Host "`nRedis-Cluster-Harness erfolgreich. Artefakte: $ArtifactDirectory" -ForegroundColor Green
}
finally {
    if ($null -ne $soakJob) {
        Stop-Job -Job $soakJob -ErrorAction SilentlyContinue
        Remove-Job -Job $soakJob -Force -ErrorAction SilentlyContinue
    }
    if ($composeWasUsed) {
        try {
            $psOutput = Invoke-Compose -Arguments @('ps', '--all') -Capture -AllowFailure
            $psOutput | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'compose-ps.txt') -Encoding utf8
            $composeLogs = Invoke-Compose -Arguments @('logs', '--no-color') -Capture -AllowFailure
            $composeLogs | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'compose.log') -Encoding utf8
        }
        catch {
            Write-Warning "Diagnoseartefakte konnten nicht vollständig geschrieben werden: $_"
        }
        if (-not $Keep) {
            $null = Invoke-Compose -Arguments @('down', '--volumes', '--remove-orphans') -Capture -AllowFailure
            $null = Invoke-NativeCommand `
                -FilePath 'docker' `
                -Arguments @('image', 'rm', $rolloutImage) `
                -Capture `
                -AllowFailure
        }
        else {
            Write-Host "Cluster bleibt mit Projektname '$ProjectName' aktiv." -ForegroundColor Yellow
        }
    }
    foreach ($name in $trackedEnvironment) {
        $value = $previousEnvironment[$name]
        if ($null -eq $value) {
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
    Pop-Location -ErrorAction SilentlyContinue
}
