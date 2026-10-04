[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidateDirectory, [Parameter(Mandatory)][string]$AssetsDirectory,
      [Parameter(Mandatory)][string]$TestResultsDirectory, [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$SourceRevision, [Parameter(Mandatory)][ValidatePattern('^\d+$')][string]$RunId,
      [Parameter(Mandatory)][string]$OutputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tests = @(& "$PSScriptRoot/Get-SetupTestResults.ps1" -Directory $TestResultsDirectory)
$files = @(Get-ChildItem -LiteralPath $CandidateDirectory -Filter manifest.json -Recurse -File)
if ($files.Count -ne 2) { throw 'Expected exactly two candidate manifests.' }
$assets = @(); $checks = @()
foreach ($file in $files) {
    $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Version -or $manifest.sourceRevision -cne $SourceRevision -or
        $manifest.sourceDirty -or @($manifest.assets).Count -ne 1) { throw 'Candidate source, version or clean identity differs.' }
    $evidencePath = Join-Path $file.DirectoryName 'checks.json'
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    if ($evidence.runId -cne $RunId -or $evidence.sourceRevision -cne $SourceRevision -or $evidence.version -cne $Version -or
        $evidence.architecture -cne $manifest.assets[0].architecture -or $evidence.packageSha256 -cne $manifest.assets[0].sha256) {
        throw 'Candidate checks do not describe this run and package.'
    }
    $assets += $manifest.assets
    $checks += $evidence
}
$report = @{schemaVersion=2;version=$Version;sourceRevision=$SourceRevision;sourceDirty=$false;runId=$RunId;
    assets=$assets;tests=$tests;checks=$checks;installerExperience='not-run';recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$output = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
& "$PSScriptRoot/Test-SetupReleaseReport.ps1" -ReportPath $output -AssetsDirectory $AssetsDirectory -Version $Version -SourceRevision $SourceRevision -RunId $RunId
