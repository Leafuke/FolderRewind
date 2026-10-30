# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureMsi, [Parameter(Mandatory)][string]$ResultDirectory,
      [ValidateSet('Owned','Foreign')][string]$StartupProbeMode = 'Owned', [switch]$IncludeFailureProbe,
      [switch]$ClearSettings)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/test/Test-MsiBundleInstallation.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
