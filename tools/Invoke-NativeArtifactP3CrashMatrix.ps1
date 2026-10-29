# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$TemplateRequest,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Delta,
    [switch]$Transport
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../scripts/test/Invoke-NativeArtifactP3CrashMatrix.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
