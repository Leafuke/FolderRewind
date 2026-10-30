# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$Output,
    [switch]$BackupOnly,
    [switch]$RestoreComponents
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Summarize-StageTwo.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
