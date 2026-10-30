# Compatibility entry point; implementation lives under scripts/ or benchmarks/.
param(
    [string]$InputRoot = (Join-Path $PSScriptRoot '../../Reference/performance'),
    [string]$Output = (Join-Path $PSScriptRoot '../../artifacts/performance/backup-restore-summary.json'),
    [string]$CandidateHead = (git -C (Join-Path $PSScriptRoot '../..') rev-parse HEAD)
)
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot '../../benchmarks/performance/Summarize-BackupRestore.ps1') @PSBoundParameters @args
if (-not $?) { exit 1 }
if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
