[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$AssetsDirectory,
      [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SourceRevision)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $AssetsDirectory -Version $Version)
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.schemaVersion -ne 1 -or $report.version -cne $Version -or $report.sourceRevision -cne $SourceRevision -or $report.sourceDirty) {
    throw 'Acceptance does not describe the exact clean source revision and version.'
}
if (@($report.assets).Count -ne 2) { throw 'Acceptance must bind exactly two Setup EXEs.' }
foreach ($architecture in @('x64','arm64')) {
    $name = "FolderRewind_${Version}_Setup_$architecture.exe"
    $asset = @($report.assets | Where-Object name -CEQ $name)
    if ($asset.Count -ne 1 -or $asset[0].architecture -cne $architecture) { throw "Missing/duplicate acceptance asset: $name" }
    $hash = (Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name)).Hash.ToLowerInvariant()
    if ($asset[0].sha256 -cne $hash) { throw "Acceptance hash mismatch: $name" }
    foreach ($id in (& "$PSScriptRoot\Get-InstallerRequiredScenarios.ps1")) {
        $rows = @($report.scenarios | Where-Object { $_.id -ceq $id -and $_.architecture -ceq $architecture })
        if ($rows.Count -ne 1 -or $rows[0].status -cne 'passed' -or $rows[0].packageSha256 -cne $hash -or -not $rows[0].evidence) {
            throw "Required acceptance missing, failed or not bound to this package: $architecture/$id"
        }
    }
}
foreach ($id in @('app-tests','plugin-contract','runtime-tests','msix-regression','release-policy')) {
    $rows = @($report.scenarios | Where-Object { $_.id -ceq $id -and $_.architecture -ceq 'common' })
    if ($rows.Count -ne 1 -or $rows[0].status -cne 'passed' -or -not $rows[0].evidence) { throw "Required common acceptance missing: $id" }
}
foreach ($row in $report.scenarios) {
    if ($row.status -notin @('passed','failed','blocked','not-run')) { throw "Invalid acceptance status: $($row.status)" }
    if ($row.status -ne 'passed') { throw "Acceptance contains an unresolved scenario: $($row.id)" }
}
Write-Host 'Exact candidate package acceptance passed.'
