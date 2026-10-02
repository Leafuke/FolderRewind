[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ResultDirectory)
$fixtures = Join-Path $root ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$version = '1.9.1.0'
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
foreach ($name in @('leaked.msi','leaked.7z','leaked.msix','FolderRewind_1.9.1.0_Setup_x86.exe')) {
    $path = Join-Path $combined $name
    'Unexpected asset' | Set-Content $path
    try { Expect-Rejected "reject-$name" { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version } }
    finally { Remove-Item -LiteralPath $path }
}
$checksum = Join-Path $combined 'FolderRewind_1.9.1.0_Setup_x64.exe.sha256'
$original = Get-Content $checksum -Raw
try {
    'invalid checksum' | Set-Content $checksum
    Expect-Rejected 'reject-bad-checksum' { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version }
} finally { $original | Set-Content $checksum -NoNewline }
$missing = Join-Path $combined 'FolderRewind_1.9.1.0_Setup_arm64.exe'
$saved = Join-Path $fixtures 'saved-arm64.exe'
Move-Item -LiteralPath $missing -Destination $saved
try { Expect-Rejected 'reject-missing-architecture' { & "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $combined -Version $version } }
finally { Move-Item -LiteralPath $saved -Destination $missing }
$stale = Join-Path $fixtures 'stale-public'
New-Item -ItemType Directory -Path $stale -Force | Out-Null
'Preserve stale evidence' | Set-Content (Join-Path $stale 'old.msi')
Expect-Rejected 'reject-stale-staging' { & "$PSScriptRoot\Prepare-SetupReleaseAssets.ps1" -SourceDirectory (Join-Path $fixtures 'x64') -Version $version -Platform x64 -OutputDirectory $stale }

# Mock gh in this test script's scope: no account/network/release mutation.
$global:FolderRewindSetupReleaseMock = @{calls=0;existingAsset=$null}
function gh {
    $global:FolderRewindSetupReleaseMock.calls++
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'view') {
        if ($global:FolderRewindSetupReleaseMock.existingAsset) { @{assets=@(@{name=$global:FolderRewindSetupReleaseMock.existingAsset})} | ConvertTo-Json -Depth 4 }
        else { '{"assets":[]}' }
    }
}
$publishArguments = @{Tag='v1.9.1';ReleaseName='Fixture';Version=$version;TargetCommit='fixture';AssetsDirectory=$combined}
$leak = Join-Path $combined 'leaked.msi'
'Forbidden' | Set-Content $leak
try {
    Expect-Rejected 'publisher-rejects-before-github' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
    if ($global:FolderRewindSetupReleaseMock.calls) { throw 'Publisher contacted GitHub before validating its input.' }
} finally { Remove-Item -LiteralPath $leak }
$global:FolderRewindSetupReleaseMock.existingAsset = 'existing.msi'
Expect-Rejected 'publisher-rejects-mixed-existing-release' { & "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments }
if ($global:FolderRewindSetupReleaseMock.calls -ne 1) { throw 'Mixed existing release caused a remote mutation.' }
$global:FolderRewindSetupReleaseMock.calls = 0; $global:FolderRewindSetupReleaseMock.existingAsset = $null
& "$PSScriptRoot\Publish-ReleaseAssets.ps1" @publishArguments
if ($global:FolderRewindSetupReleaseMock.calls -ne 2) { throw 'Validated release did not reach the mock upload.' }
$results.Add(@{scenario='publisher-accepts-valid-release-with-mock';passed=$true})
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'results.json') -Encoding utf8
Write-Host "Passed $($results.Count) EXE-only release policy checks."
Remove-Variable FolderRewindSetupReleaseMock -Scope Global
