# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][ValidateSet('local', 'sandbox')][string]$Target,
    [Parameter(Mandatory)][ValidateSet('local', 'sandbox')][string]$AppScope,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot ('../../artifacts/winui-review-' + [guid]::NewGuid().ToString('N'))),
    [switch]$ContrastThemes
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../test/Test-WinUiReview.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
