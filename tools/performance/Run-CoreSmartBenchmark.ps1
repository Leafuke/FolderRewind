# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$HostExe,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SevenZipExe = (Join-Path $PSScriptRoot '../../7za.exe')
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Run-CoreSmartBenchmark.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
