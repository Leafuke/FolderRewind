# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/update-p0/ui')
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Test-AppUpdateUi.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
