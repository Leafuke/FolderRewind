# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$BaselineDll,
    [Parameter(Mandatory)][string]$CandidateDll,
    [Parameter(Mandatory)][string]$SevenZip,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$DatasetRoot,
    [string[]]$Scenarios = @('core-smart-20000', 'core-smart-20000-nested', 'core-overwrite-5000', 'core-full-1000', 'core-smart-5000-nested', 'core-full-10000-nested', 'owner'),
    [int]$Repeats = 5
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Run-RestorePerformance.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
