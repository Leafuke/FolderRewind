# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath, [Parameter(Mandatory)][string]$PublishDirectory,
      [Parameter(Mandatory)][string]$ResultDirectory, [string]$BaseFixtureMsi, [switch]$SkipRollbackProbes)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/test/Test-MsiInstallation.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
