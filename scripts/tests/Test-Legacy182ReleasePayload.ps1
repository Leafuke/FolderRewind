# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateSet('BuildOutput','UnpackedReleasePackage')][string]$EvidenceKind = 'BuildOutput',
    [string]$ReleaseAssetsDirectory,
    [string]$Version
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Test-Legacy182ReleasePayload.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
