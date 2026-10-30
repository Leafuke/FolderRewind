# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$BaselineExe,
    [Parameter(Mandatory)][string]$CandidateExe,
    [Parameter(Mandatory)][string]$SevenZip,
    [Parameter(Mandatory)][string]$Plugin,
    [Parameter(Mandatory)][string]$Snapshot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('native','core-world')][string]$Scenario = 'native'
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Run-RestoreServiceComparison.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
