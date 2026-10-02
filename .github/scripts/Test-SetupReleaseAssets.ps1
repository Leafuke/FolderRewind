[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ResultDirectory)
$fixtures = Join-Path $root ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$version = '1.9.2.0'
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
$report = @{schemaVersion=1;version=$version;sourceRevision='fixture';sourceDirty=$false;assets=@();scenarios=@()}
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
$global:FolderRewindSetupReleaseMock = @{calls=0;existingAsset=$null;assets=@();mutations=0;failList=$false}
function gh {
    $global:FolderRewindSetupReleaseMock.calls++
    $global:LASTEXITCODE = 0
    if ($args -contains '--clobber') { throw 'Publisher must not use --clobber.' }
    if ($args[1] -eq 'list') {
        if ($global:FolderRewindSetupReleaseMock.failList) { $global:LASTEXITCODE = 1; return }
        '[{"tagName":"v1.9.2"}]'
    } elseif ($args[1] -eq 'view') {
        if ($global:FolderRewindSetupReleaseMock.existingAsset) { @{assets=@(@{name=$global:FolderRewindSetupReleaseMock.existingAsset})} | ConvertTo-Json -Depth 4 }
        else { @{assets=$global:FolderRewindSetupReleaseMock.assets} | ConvertTo-Json -Depth 4 }
    } else { $global:FolderRewindSetupReleaseMock.mutations++ }
}
$publishArguments = @{Tag='v1.9.2';ReleaseName='Fixture';Version=$version;TargetCommit='fixture';AssetsDirectory=$combined;AcceptanceReport=$reportPath}
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
& "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments
if ($global:FolderRewindSetupReleaseMock.calls -ne 3 -or $global:FolderRewindSetupReleaseMock.mutations -ne 1) { throw 'Validated release did not reach the mock upload.' }
$results.Add(@{scenario='publisher-accepts-valid-release-with-mock';passed=$true})
foreach ($status in @('failed','blocked','not-run')) {
    $report.scenarios[0].status = $status; Save-Report
    $global:FolderRewindSetupReleaseMock.calls = 0
    Expect-Rejected "reject-acceptance-$status" { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
    if ($global:FolderRewindSetupReleaseMock.calls) { throw 'Incomplete acceptance reached GitHub.' }
}
$report.scenarios[0].status = 'passed'
$report.assets[0].sha256 = '0' * 64; Save-Report
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
$report.scenarios[0].packageSha256 = '0' * 64; Save-Report
Expect-Rejected 'reject-scenario-for-other-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.scenarios[0].packageSha256 = $originalScenarioHash
$report.sourceRevision = 'other-revision'; Save-Report
Expect-Rejected 'reject-other-source-revision' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$report.sourceRevision = 'fixture'; Save-Report
$global:FolderRewindSetupReleaseMock.failList = $true
Expect-Rejected 'reject-github-lookup-failure' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$global:FolderRewindSetupReleaseMock.failList = $false
$global:FolderRewindSetupReleaseMock.assets = @($assets | ForEach-Object { @{name=$_.Name;digest=('sha256:'+(Get-FileHash $_.FullName).Hash.ToLowerInvariant())} })
$global:FolderRewindSetupReleaseMock.mutations = 0
& "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments
if ($global:FolderRewindSetupReleaseMock.mutations) { throw 'Identical remote assets were mutated.' }
$results.Add(@{scenario='identical-remote-assets-idempotent';passed=$true})
$global:FolderRewindSetupReleaseMock.assets[0].digest = 'sha256:' + ('0' * 64)
Expect-Rejected 'reject-different-remote-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$global:FolderRewindSetupReleaseMock.assets[0].Remove('digest')
Expect-Rejected 'reject-unverifiable-remote-bytes' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'results.json') -Encoding utf8
Write-Host "Passed $($results.Count) EXE-only release policy checks."
Remove-Variable FolderRewindSetupReleaseMock -Scope Global
