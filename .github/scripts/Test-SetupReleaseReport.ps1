[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$AssetsDirectory,
      [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SourceRevision,
      [Parameter(Mandatory)][ValidatePattern('^\d+$')][string]$RunId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$public = @(& "$PSScriptRoot/Get-SetupReleaseAssets.ps1" -Directory $AssetsDirectory -Version $Version)
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.schemaVersion -ne 2 -or $report.version -cne $Version -or $report.sourceRevision -cne $SourceRevision -or
    $report.sourceDirty -or $report.runId -cne $RunId -or $report.installerExperience -cne 'not-run') { throw 'Release report identity differs.' }
if ($report.PSObject.Properties['manualInstallerApproval']) { throw 'Manual approval is not supported by the automated release report.' }
if (@($report.assets).Count -ne 2 -or @($report.checks).Count -ne 2 -or @($report.tests).Count -ne 3) { throw 'Incomplete release report.' }
foreach ($architecture in @('x64','arm64')) {
    $name = "FolderRewind_${Version}_Setup_$architecture.exe"
    $asset = @($report.assets | Where-Object { $_.name -ceq $name -and $_.architecture -ceq $architecture })
    $checks = @($report.checks | Where-Object architecture -CEQ $architecture)
    $hash = (Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name)).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].sha256 -cne $hash -or $checks.Count -ne 1) { throw "Missing or mismatched package: $architecture" }
    $check = $checks[0]
    if ($check.runId -cne $RunId -or $check.sourceRevision -cne $SourceRevision -or $check.version -cne $Version -or
        $check.packageSha256 -cne $hash -or -not $check.evidence) { throw 'Checks are not bound to this package and run.' }
    $required = @('build','resources','resource-negative','ice','production-identity','migration-payload')
    if ($architecture -eq 'x64') { $required += @('native','release-policy') }
    if (@($check.results).Count -ne $required.Count) { throw 'Unexpected package check set.' }
    foreach ($id in $required) {
        $row = @($check.results | Where-Object id -CEQ $id)
        if ($row.Count -ne 1 -or $row[0].status -cne 'passed') { throw "Required check did not pass: $architecture/$id" }
    }
}
foreach ($suite in @('abstractions','runtime','host')) {
    $rows = @($report.tests | Where-Object suite -CEQ $suite)
    if ($rows.Count -ne 1 -or $rows[0].status -cne 'passed' -or $rows[0].total -le 0 -or $rows[0].passed -ne $rows[0].total) { throw "Incomplete core test suite: $suite" }
}
Write-Host 'Automated release checks and exact candidate bytes accepted; installer experience was not run.'
