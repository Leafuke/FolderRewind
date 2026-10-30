# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
      [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SevenZipRelease,
      [ValidatePattern('^\d+$')][string]$RunId = '0',
      [string]$ArtifactsDirectory = "$PSScriptRoot/../../artifacts")
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/build/Build-SetupCandidate.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
