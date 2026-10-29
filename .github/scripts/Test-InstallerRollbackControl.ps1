# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/test/Test-InstallerRollbackControl.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
