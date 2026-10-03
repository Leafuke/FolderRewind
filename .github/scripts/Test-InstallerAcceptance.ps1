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
$required = @(& "$PSScriptRoot\Get-InstallerRequiredScenarios.ps1")
$automated = @('build','resources','resource-negative','ice')
$manual = $report.PSObject.Properties['manualInstallerApproval']
$approved = $false
if ($manual -and $manual.Value) {
    $approval = $manual.Value
    if ($approval.scope -cne 'installer-experience' -or
        $approval.version -cne $Version -or $approval.sourceRevision -cne $SourceRevision -or
        $approval.decision -cne 'accepted-for-release' -or -not $approval.approvedBy -or -not $approval.reason -or
        @($approval.assets).Count -ne 2) { throw 'Manual installer approval does not authorize this exact release.' }
    $approved = $true
}
foreach ($architecture in @('x64','arm64')) {
    $name = "FolderRewind_${Version}_Setup_$architecture.exe"
    $asset = @($report.assets | Where-Object name -CEQ $name)
    if ($asset.Count -ne 1 -or $asset[0].architecture -cne $architecture) { throw "Missing/duplicate acceptance asset: $name" }
    $hash = (Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name)).Hash.ToLowerInvariant()
    if ($asset[0].sha256 -cne $hash) { throw "Acceptance hash mismatch: $name" }
    if ($approved) {
        $bound = @($approval.assets | Where-Object { $_.name -ceq $name -and $_.architecture -ceq $architecture })
        if ($bound.Count -ne 1 -or $bound[0].sha256 -cne $hash) { throw "Manual approval hash mismatch: $name" }
    }
    foreach ($id in $required) {
        $rows = @($report.scenarios | Where-Object { $_.id -ceq $id -and $_.architecture -ceq $architecture })
        if ($rows.Count -ne 1 -or $rows[0].packageSha256 -cne $hash) {
            throw "Required acceptance missing or not bound to this package: $architecture/$id"
        }
        if (($id -in $automated -or -not $approved) -and ($rows[0].status -cne 'passed' -or -not $rows[0].evidence)) {
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
    $covered = $approved -and $row.architecture -in @('x64','arm64') -and $row.id -in $required -and $row.id -notin $automated
    if ($row.status -ne 'passed' -and -not $covered) { throw "Acceptance contains an unresolved scenario: $($row.id)" }
}
if ($approved) { Write-Host 'Installer experience accepted by the project owner for these exact bytes; historical scenario statuses remain unchanged.' }
Write-Host 'Exact candidate build, tests, resources and release integrity accepted.'
