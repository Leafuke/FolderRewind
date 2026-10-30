# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/core-tests')
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/test/Invoke-SetupCoreTests.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
