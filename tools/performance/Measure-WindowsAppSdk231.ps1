# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param(
    [ValidateRange(0, 20)]
    [int]$WarmupRuns = 1,

    [ValidateRange(1, 50)]
    [int]$MeasuredRuns = 5,

    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 30,

    [switch]$SkipBuild,
    [switch]$HistoricalSdkMatrix
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Measure-WindowsAppSdk231.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
