# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param([string]$PluginRoot = (Join-Path $PSScriptRoot '../FolderRewind-Plugin-Minecraft'))
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot 'integration/Export-MergeContract.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
