# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/build/Prepare-SetupReleaseAssets.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
