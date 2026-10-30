# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SevenZipPath,
    [Parameter(Mandatory)][string]$RclonePath,
    [Parameter(Mandatory)][string]$SamplesJson,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$CrashMatrix
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../scripts/test/Test-NativeArtifactFull.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
