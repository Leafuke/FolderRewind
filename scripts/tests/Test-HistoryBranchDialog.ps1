# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$RunId,
    [string]$BranchName = ('ui-dialog-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Test-HistoryBranchDialog.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
