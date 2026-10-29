# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [string]$PluginRepository = (Join-Path $PSScriptRoot '../FolderRewind-Plugin-Minecraft'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/native-integration/build')
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot 'integration/Build-NativeArtifactP3.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
