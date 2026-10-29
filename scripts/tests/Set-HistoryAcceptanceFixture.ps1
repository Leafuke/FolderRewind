# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param([ValidateSet('B0', 'Main', 'Feature', 'Tiny')][string]$Phase = 'B0')
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Set-HistoryAcceptanceFixture.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
