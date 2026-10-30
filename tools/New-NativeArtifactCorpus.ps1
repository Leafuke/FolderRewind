# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param([Parameter(Mandatory)][string]$OutputRoot, [Parameter(Mandatory)][string]$LevelDat)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../benchmarks/performance/New-NativeArtifactCorpus.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
