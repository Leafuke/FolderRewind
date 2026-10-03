[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$AssetsDirectory,
    [Parameter(Mandatory)][string]$TestResultsDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+$')][string]$BuildRunId,
    [Parameter(Mandatory)][ValidatePattern('^\d+$')][string]$TestRunId,
    [Parameter(Mandatory)][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$ApprovalReason,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$buildJson = gh run view $BuildRunId --json headSha,status,conclusion,workflowName,url
if ($LASTEXITCODE) { throw 'Cannot verify candidate build run.' }
$build = $buildJson | ConvertFrom-Json
$testJson = gh run view $TestRunId --json headSha,status,conclusion,workflowName,url
if ($LASTEXITCODE) { throw 'Cannot verify candidate test run.' }
$tests = $testJson | ConvertFrom-Json
if ($build.status -ne 'completed' -or $build.conclusion -ne 'success' -or $build.workflowName -ne 'Build GitHub Setup Installers' -or
    $tests.status -ne 'completed' -or $tests.conclusion -ne 'success' -or $tests.workflowName -ne 'Plugin v3 Hardening' -or
    $tests.headSha -cne $build.headSha) { throw 'Successful build and test runs must describe the same commit.' }
$reports = @(Get-ChildItem -LiteralPath $CandidateDirectory -Filter acceptance.json -Recurse -File)
if ($reports.Count -ne 2) { throw 'Expected one frozen candidate report per architecture.' }
$assets = @(); $scenarios = @(); $internal = @(); $version = $null
foreach ($file in $reports) {
    $candidate = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if (-not $version) { $version = [string]$candidate.version }
    if ($candidate.schemaVersion -ne 1 -or $candidate.version -cne $version -or $candidate.sourceRevision -cne $build.headSha -or
        $candidate.sourceDirty -or @($candidate.assets).Count -ne 1) { throw 'Candidate identity or clean source binding differs from the frozen build.' }
    $assets += $candidate.assets
    $internal += $candidate.internalAssets
    $scenarios += @($candidate.scenarios | Where-Object architecture -NE 'common')
}
$trx = @(Get-ChildItem -LiteralPath $TestResultsDirectory -Filter '*.trx' -Recurse -File)
if (-not $trx.Count) { throw 'No test results were supplied.' }
foreach ($suite in @('abstractions','runtime','host')) {
    if (-not @($trx | Where-Object { $_.Directory.Name -ceq $suite }).Count) { throw "Missing test suite: $suite" }
}
foreach ($file in $trx) {
    [xml]$result = Get-Content -LiteralPath $file.FullName -Raw
    $count = $result.TestRun.ResultSummary.Counters
    if ([int]$count.total -eq 0 -or [int]$count.passed -ne [int]$count.total) { throw "Incomplete tests: $($file.Name)" }
}
foreach ($row in $scenarios) {
    if ($row.id -in @('build','resources','resource-negative','ice')) {
        $row.status = 'passed'
        $row.evidence = $build.url
        $row.reason = 'Successful frozen build includes strict compilation, resource checks, negative tests and MSI validation.'
    }
}
foreach ($id in @('app-tests','plugin-contract','runtime-tests','msix-regression','release-policy')) {
    $url = if ($id -in @('msix-regression','release-policy')) { $build.url } else { $tests.url }
    $scenarios += @{id=$id;architecture='common';status='passed';evidence=$url}
}
$report = @{
    schemaVersion=1;version=$version;sourceRevision=$build.headSha;sourceDirty=$false
    assets=$assets;internalAssets=$internal;scenarios=$scenarios
    manualInstallerApproval=@{
        scope='installer-experience';version=$version;sourceRevision=$build.headSha
        decision='accepted-for-release';approvedBy=$ApprovedBy
        recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        reason=$ApprovalReason
        assets=@($assets | Select-Object name,architecture,sha256)
    }
}
$output = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
& "$PSScriptRoot\Test-InstallerAcceptance.ps1" -ReportPath $output -AssetsDirectory $AssetsDirectory -Version $version -SourceRevision $build.headSha
