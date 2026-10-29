param(
    [Parameter(Mandatory)][string]$BaselineDll,
    [Parameter(Mandatory)][string]$CandidateDll,
    [Parameter(Mandatory)][string]$SevenZip,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$DatasetRoot,
    [string[]]$Scenarios = @('core-smart-20000', 'core-smart-20000-nested', 'core-overwrite-5000', 'core-full-1000', 'core-smart-5000-nested', 'core-full-10000-nested', 'owner'),
    [int]$Repeats = 5
)
. (Join-Path $PSScriptRoot '../../scripts/common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($OutputRoot) { $OutputRoot = Resolve-FolderRewindPath -Path $OutputRoot -Root $folderRewindScriptRoot }
if ($DatasetRoot) { $DatasetRoot = Resolve-FolderRewindPath -Path $DatasetRoot -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$baselineAssembly = (Resolve-Path -LiteralPath $BaselineDll).Path
$candidateAssembly = (Resolve-Path -LiteralPath $CandidateDll).Path
$env:FOLDERREWIND_TEST_7Z = (Resolve-Path -LiteralPath $SevenZip).Path
$resultRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $resultRoot) { throw 'Comparison output must be new.' }
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$env:FOLDERREWIND_RESTORE_DATASETS = [IO.Path]::GetFullPath($DatasetRoot)
$env:FOLDERREWIND_BENCHMARK_REPEATS = [string]$Repeats
$fingerprints = @(Get-FileHash -Algorithm SHA256 -LiteralPath $baselineAssembly,$candidateAssembly,$env:FOLDERREWIND_TEST_7Z)
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'assemblies.json') -Encoding utf8
$runs = @()
foreach ($round in 1,2) {
    $order = if ($round -eq 1) { @('baseline','candidate') } else { @('candidate','baseline') }
    foreach ($scenario in $Scenarios) {
        foreach ($version in $order) {
            $assembly = if ($version -eq 'baseline') { $baselineAssembly } else { $candidateAssembly }
            $env:FOLDERREWIND_RESTORE_SCENARIO = $scenario
            $name = "r$round-$version-$scenario"
            $env:FOLDERREWIND_RESTORE_OUTPUT = Join-Path $resultRoot $name
            $start = [DateTimeOffset]::UtcNow
            Write-Host "Running $name"
            & dotnet vstest $assembly '/TestCaseFilter:FullyQualifiedName~RestorePerformanceOptimizationBenchmarkTests' "/TestAdapterPath:$([IO.Path]::GetDirectoryName($assembly))" "/Logger:trx;LogFileName=$name.trx" "/ResultsDirectory:$resultRoot/trx" > (Join-Path $resultRoot "$name.log") 2>&1
            $code = $LASTEXITCODE
            $runs += [pscustomobject]@{round=$round; version=$version; scenario=$scenario; exitCode=$code; started=$start; finished=[DateTimeOffset]::UtcNow}
            $runs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultRoot 'runs.json') -Encoding utf8
            if ($code -ne 0) { Get-Content -LiteralPath (Join-Path $resultRoot "$name.log"); throw "$name failed ($code). Raw evidence retained." }
            Write-Host "Completed $name"
        }
    }
}

} finally { Pop-Location }
