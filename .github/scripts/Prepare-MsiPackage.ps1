# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipBundle,
    [switch]$TestIdentity,
    [switch]$EnableFaultInjection,
    [ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string]$TestIdentitySuffix = [guid]::NewGuid().ToString('N')
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../scripts/build/Prepare-MsiPackage.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
