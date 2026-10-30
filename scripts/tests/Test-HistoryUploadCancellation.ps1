# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [string]$ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'folderrewind-stage-a-cancellation'),
    [ValidateRange(1, 100)] [int]$Iterations = 20,
    [string]$HangTimeout = '2m'
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Test-HistoryUploadCancellation.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
