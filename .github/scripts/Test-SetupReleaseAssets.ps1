[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ResultDirectory)
$fixtures = Join-Path $root ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$version = [version]::new((Get-Random -Minimum 1 -Maximum 255),(Get-Random -Minimum 1 -Maximum 255),(Get-Random -Minimum 1 -Maximum 100),0).ToString()
$number = [version]$version
$tag = "v$($number.Major).$($number.Minor).$($number.Build)"
$revision = [guid]::NewGuid().ToString('N')
$otherRevision = [guid]::NewGuid().ToString('N')
$combined = Join-Path $fixtures 'public'
New-Item -ItemType Directory -Path $combined -Force | Out-Null
foreach ($architecture in @('x64','arm64')) {
    $source = Join-Path $fixtures $architecture
    $staging = Join-Path $fixtures "$architecture-public"
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    # These inert fixtures are never executed.
    'Setup fixture' | Set-Content (Join-Path $source "FolderRewind_${version}_Setup_${architecture}.exe")
    'Internal MSI fixture' | Set-Content (Join-Path $source "FolderRewind_${version}_${architecture}.msi")
    & "$PSScriptRoot\Prepare-SetupReleaseAssets.ps1" -SourceDirectory $source -Version $version -Platform $architecture -OutputDirectory $staging
    $assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $staging -Version $version -Architectures $architecture)
    if ($assets.Count -ne 2) { throw 'Per-architecture staging leaked internal assets.' }
    $assets | Copy-Item -Destination $combined
    $results.Add(@{scenario="stage-$architecture-only-exe-and-checksum";passed=$true})
}
$assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version)
if ($assets.Count -ne 4) { throw 'Combined release must contain two Setup EXEs and two checksums.' }
$results.Add(@{scenario='combined-release';passed=$true})
function Expect-Rejected([string]$Name,[scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true; $reason = $_.Exception.Message }
    if (-not $rejected) { throw "Policy failed to reject: $Name" }
    $results.Add(@{scenario=$Name;passed=$true;reason=$reason})
}
foreach ($name in @('leaked.msi','leaked.7z','leaked.msix',"FolderRewind_${version}_Setup_x86.exe")) {
    $path = Join-Path $combined $name
    'Unexpected asset' | Set-Content $path
    try { Expect-Rejected "reject-$name" { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version } }
    finally { Remove-Item -LiteralPath $path }
}
$checksum = Join-Path $combined "FolderRewind_${version}_Setup_x64.exe.sha256"
$original = Get-Content $checksum -Raw
try {
    'invalid checksum' | Set-Content $checksum
    Expect-Rejected 'reject-bad-checksum' { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version }
} finally { $original | Set-Content $checksum -NoNewline }
$missing = Join-Path $combined "FolderRewind_${version}_Setup_arm64.exe"
$saved = Join-Path $fixtures 'saved-arm64.exe'
Move-Item -LiteralPath $missing -Destination $saved
try { Expect-Rejected 'reject-missing-architecture' { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version } }
finally { Move-Item -LiteralPath $saved -Destination $missing }
$stale = Join-Path $fixtures 'stale-public'
New-Item -ItemType Directory -Path $stale -Force | Out-Null
'Preserve stale evidence' | Set-Content (Join-Path $stale 'old.msi')
Expect-Rejected 'reject-stale-staging' { & "$PSScriptRoot\Prepare-SetupReleaseAssets.ps1" -SourceDirectory (Join-Path $fixtures 'x64') -Version $version -Platform x64 -OutputDirectory $stale }

# Mock gh in this test script's scope: no account/network/release mutation.
$reportPath = Join-Path $fixtures 'acceptance.json'
$report = @{schemaVersion=1;version=$version;sourceRevision=$revision;sourceDirty=$false;assets=@();scenarios=@()}
foreach ($architecture in @('x64','arm64')) {
    $name = "FolderRewind_${version}_Setup_$architecture.exe"
    $hash = (Get-FileHash (Join-Path $combined $name)).Hash.ToLowerInvariant()
    $report.assets += @{name=$name;architecture=$architecture;sha256=$hash}
    foreach ($id in (& "$PSScriptRoot\Get-InstallerRequiredScenarios.ps1")) {
        $report.scenarios += @{id=$id;architecture=$architecture;status='passed';packageSha256=$hash;evidence='synthetic policy test only'}
    }
}
foreach ($id in @('app-tests','plugin-contract','runtime-tests','msix-regression','release-policy')) {
    $report.scenarios += @{id=$id;architecture='common';status='passed';evidence='synthetic policy test only'}
}
function Save-Report { $report | ConvertTo-Json -Depth 8 | Set-Content $reportPath }
Save-Report
$global:FolderRewindSetupReleaseMock = @{calls=0;existingAsset=$null;assets=@();mutations=0;failList=$false;isDraft=$false}
function gh {
    $global:FolderRewindSetupReleaseMock.calls++
    $global:LASTEXITCODE = 0
    if ($args -contains '--clobber') { throw 'Publisher must not use --clobber.' }
    if ($args[1] -eq 'list') {
        if ($global:FolderRewindSetupReleaseMock.failList) { $global:LASTEXITCODE = 1; return }
        @(@{tagName=$tag}) | ConvertTo-Json -AsArray
    } elseif ($args[1] -eq 'view') {
        if ($global:FolderRewindSetupReleaseMock.existingAsset) { @{assets=@(@{name=$global:FolderRewindSetupReleaseMock.existingAsset})} | ConvertTo-Json -Depth 4 }
        else { @{assets=$global:FolderRewindSetupReleaseMock.assets;isDraft=$global:FolderRewindSetupReleaseMock.isDraft;targetCommitish=$revision} | ConvertTo-Json -Depth 4 }
    } elseif ($args[0] -eq 'api') {
        if ($args[1] -like '*matching-refs*') { '[]' } else { $revision }
    } else {
        $global:FolderRewindSetupReleaseMock.mutations++
        if ($args[1] -eq 'upload') {
            $global:FolderRewindSetupReleaseMock.assets = @($assets | ForEach-Object { @{name=$_.Name;digest=('sha256:'+(Get-FileHash $_.FullName).Hash.ToLowerInvariant())} })
        }
    }
}
$publishArguments = @{Tag=$tag;ReleaseName='Fixture';Version=$version;TargetCommit=$revision;AssetsDirectory=$combined;AcceptanceReport=$reportPath}
$leak = Join-Path $combined 'leaked.msi'
'Forbidden' | Set-Content $leak
try {
    Expect-Rejected 'publisher-rejects-before-github' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
    if ($global:FolderRewindSetupReleaseMock.calls) { throw 'Publisher contacted GitHub before validating its input.' }
} finally { Remove-Item -LiteralPath $leak }
$global:FolderRewindSetupReleaseMock.existingAsset = 'existing.msi'
Expect-Rejected 'publisher-rejects-mixed-existing-release' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
if ($global:FolderRewindSetupReleaseMock.mutations) { throw 'Mixed existing release caused a remote mutation.' }
$global:FolderRewindSetupReleaseMock.calls = 0; $global:FolderRewindSetupReleaseMock.existingAsset = $null
Expect-Rejected 'reject-incomplete-public-release' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$global:FolderRewindSetupReleaseMock.calls = 0
$global:FolderRewindSetupReleaseMock.isDraft = $true
& "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments
if ($global:FolderRewindSetupReleaseMock.calls -ne 5 -or $global:FolderRewindSetupReleaseMock.mutations -ne 1) { throw 'Validated draft did not upload and verify its assets.' }
$results.Add(@{scenario='publisher-accepts-valid-release-with-mock';passed=$true})
foreach ($status in @('failed','blocked','not-run')) {
    $report.scenarios[0].status = $status; Save-Report
    $global:FolderRewindSetupReleaseMock.calls = 0
    Expect-Rejected "reject-acceptance-$status" { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
    if ($global:FolderRewindSetupReleaseMock.calls) { throw 'Incomplete acceptance reached GitHub.' }
}
$report.scenarios[0].status = 'passed'
$report.assets[0].sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([guid]::NewGuid().ToByteArray())).ToLowerInvariant(); Save-Report
Expect-Rejected 'reject-acceptance-other-package' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.assets[0].sha256 = (Get-FileHash (Join-Path $combined $report.assets[0].name)).Hash.ToLowerInvariant(); Save-Report
$report.sourceDirty = $true; Save-Report
Expect-Rejected 'reject-uncommitted-source' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.sourceDirty = $false; Save-Report
$originalScenarios = $report.scenarios
$report.scenarios = @($originalScenarios | Select-Object -Skip 1); Save-Report
Expect-Rejected 'reject-missing-required-scenario' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.scenarios = @($originalScenarios) + @($originalScenarios[0]); Save-Report
Expect-Rejected 'reject-duplicate-scenario' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.scenarios = $originalScenarios
$originalScenarioHash = $report.scenarios[0].packageSha256
$report.scenarios[0].packageSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([guid]::NewGuid().ToByteArray())).ToLowerInvariant(); Save-Report
Expect-Rejected 'reject-scenario-for-other-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.scenarios[0].packageSha256 = $originalScenarioHash
$report.sourceRevision = $otherRevision; Save-Report
Expect-Rejected 'reject-other-source-revision' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.sourceRevision = $revision; Save-Report
$global:FolderRewindSetupReleaseMock.failList = $true
Expect-Rejected 'reject-github-lookup-failure' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$global:FolderRewindSetupReleaseMock.failList = $false
$global:FolderRewindSetupReleaseMock.isDraft = $false
$global:FolderRewindSetupReleaseMock.assets = @($assets | ForEach-Object { @{name=$_.Name;digest=('sha256:'+(Get-FileHash $_.FullName).Hash.ToLowerInvariant())} })
$global:FolderRewindSetupReleaseMock.mutations = 0
& "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments
if ($global:FolderRewindSetupReleaseMock.mutations) { throw 'Identical remote assets were mutated.' }
$results.Add(@{scenario='identical-remote-assets-idempotent';passed=$true})
$global:FolderRewindSetupReleaseMock.assets[0].digest = 'sha256:' + ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([guid]::NewGuid().ToByteArray())).ToLowerInvariant())
Expect-Rejected 'reject-different-remote-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$global:FolderRewindSetupReleaseMock.assets[0].Remove('digest')
Expect-Rejected 'reject-unverifiable-remote-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }

# Manual approval is release-specific and never waives build or integrity checks.
$manualVersion = [version]::new($number.Major,$number.Minor,$number.Build + 1,0).ToString()
$manualDirectory = Join-Path $fixtures 'manual-public'
New-Item -ItemType Directory -Path $manualDirectory -Force | Out-Null
$manualReport = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
$manualReport.version = $manualVersion
foreach ($asset in $manualReport.assets) {
    $oldName = $asset.name
    $asset.name = $oldName.Replace($version, $manualVersion)
    Copy-Item -LiteralPath (Join-Path $combined $oldName) -Destination (Join-Path $manualDirectory $asset.name)
    "$($asset.sha256) *$($asset.name)" | Set-Content (Join-Path $manualDirectory "$($asset.name).sha256")
}
foreach ($row in $manualReport.scenarios) {
    if ($row.architecture -ne 'common' -and $row.id -notin @('build','resources','resource-negative','ice')) {
        $row.status = 'not-run'
    }
}
$manualReport.manualInstallerApproval = @{scope='installer-experience';version=$manualVersion;sourceRevision=$revision;decision='accepted-for-release';approvedBy='fixture owner';reason='Synthetic authorization test';assets=$manualReport.assets | ConvertTo-Json -Depth 4 | ConvertFrom-Json -AsHashtable}
$manualPath = Join-Path $fixtures 'manual-acceptance.json'
function Save-ManualReport { $manualReport | ConvertTo-Json -Depth 9 | Set-Content $manualPath }
$manualArguments = @{ReportPath=$manualPath;AssetsDirectory=$manualDirectory;Version=$manualVersion;SourceRevision=$revision}
Save-ManualReport
& "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments
$results.Add(@{scenario='manual-approval-preserves-not-run-status';passed=$true})
$manualReport.manualInstallerApproval.assets[0].sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([guid]::NewGuid().ToByteArray())).ToLowerInvariant(); Save-ManualReport
Expect-Rejected 'manual-rejects-other-bytes' { & "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments }
$manualReport.manualInstallerApproval.assets[0].sha256 = $manualReport.assets[0].sha256
$manualReport.manualInstallerApproval.sourceRevision = $otherRevision; Save-ManualReport
Expect-Rejected 'manual-rejects-other-revision' { & "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments }
$manualReport.manualInstallerApproval.sourceRevision = $revision
$manualReport.manualInstallerApproval.version = [version]::new($number.Major,$number.Minor,$number.Build + 2,0).ToString(); Save-ManualReport
Expect-Rejected 'manual-rejects-other-version' { & "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments }
$manualReport.manualInstallerApproval.version = $manualVersion
$manualReport.scenarios[0].status = 'failed'; Save-ManualReport
Expect-Rejected 'manual-cannot-waive-build-failure' { & "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments }
$manualReport.scenarios[0].status = 'passed'
$common = @($manualReport.scenarios | Where-Object architecture -EQ 'common')[0]
$common.status = 'failed'; Save-ManualReport
Expect-Rejected 'manual-cannot-waive-test-failure' { & "$PSScriptRoot\Test-InstallerAcceptance.ps1" @manualArguments }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'results.json') -Encoding utf8
Write-Host "Passed $($results.Count) EXE-only release policy checks."
Remove-Variable FolderRewindSetupReleaseMock -Scope Global
