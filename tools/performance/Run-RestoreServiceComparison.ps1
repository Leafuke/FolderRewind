param(
    [Parameter(Mandatory)][string]$BaselineExe,
    [Parameter(Mandatory)][string]$CandidateExe,
    [Parameter(Mandatory)][string]$SevenZip,
    [Parameter(Mandatory)][string]$Plugin,
    [Parameter(Mandatory)][string]$Snapshot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('native','core-world')][string]$Scenario = 'native'
)
$ErrorActionPreference = 'Stop'
$baselineHost = (Resolve-Path -LiteralPath $BaselineExe).Path
$candidateHost = (Resolve-Path -LiteralPath $CandidateExe).Path
$resultRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $resultRoot) { throw 'Comparison output must be new.' }
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$stateRoot = Join-Path $resultRoot 'isolated-host-state'
$env:FOLDERREWIND_TEST_7Z = (Resolve-Path -LiteralPath $SevenZip).Path
$env:FOLDERREWIND_TEST_PLUGIN = (Resolve-Path -LiteralPath $Plugin).Path
$env:FOLDERREWIND_RESTORE_SNAPSHOT = (Resolve-Path -LiteralPath $Snapshot).Path
$env:FOLDERREWIND_RESTORE_SAFETY = $null
$env:FOLDERREWIND_RESTORE_MULTISOURCE = $null
$env:FOLDERREWIND_RESTORE_SERVICE_PROBE = if ($Scenario -eq 'native') { 'native-prepare' } else { 'core-world' }
$env:FOLDERREWIND_RESTORE_SERVICE_RESULT = Join-Path $resultRoot 'seed-smoke.json'
$env:FOLDERREWIND_BENCHMARK_REPEATS = '1'
Get-FileHash -Algorithm SHA256 -LiteralPath $baselineHost,$candidateHost,$env:FOLDERREWIND_TEST_7Z,$env:FOLDERREWIND_TEST_PLUGIN | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $resultRoot 'inputs.json') -Encoding utf8
Write-Host "Preparing fixed $Scenario payloads with baseline Host"
& $baselineHost $stateRoot 8 Balanced > (Join-Path $resultRoot 'prepare.log') 2>&1
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $resultRoot 'prepare.log'); throw 'Fixture preparation failed; evidence retained.' }
$env:FOLDERREWIND_RESTORE_SERVICE_PROBE = 'native-restore'
$env:FOLDERREWIND_BENCHMARK_REPEATS = '5'
$runs = @()
foreach ($round in 1,2) {
    $order = if ($round -eq 1) { @('baseline','candidate') } else { @('candidate','baseline') }
    foreach ($version in $order) {
        $executable = if ($version -eq 'baseline') { $baselineHost } else { $candidateHost }
        $name = "r$round-$version-$Scenario"
        $env:FOLDERREWIND_RESTORE_SERVICE_RESULT = Join-Path $resultRoot "$name.json"
        $start = [DateTimeOffset]::UtcNow
        Write-Host "Running $name"
        & $executable $stateRoot 8 Balanced > (Join-Path $resultRoot "$name.log") 2>&1
        $code = $LASTEXITCODE
        $runs += [pscustomobject]@{round=$round; version=$version; scenario=$Scenario; exitCode=$code; started=$start; finished=[DateTimeOffset]::UtcNow}
        $runs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultRoot 'runs.json') -Encoding utf8
        if ($code -ne 0) { Get-Content -LiteralPath (Join-Path $resultRoot "$name.log"); throw "$name failed; evidence retained." }
        Write-Host "Completed $name"
    }
}
