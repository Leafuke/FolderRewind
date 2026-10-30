# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param([Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform, [string]$OutputDirectory, [string]$ReleaseTag)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/build/Stage-SevenZip.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
