[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
      [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SevenZipRelease,
      [ValidatePattern('^\d+$')][string]$RunId = '0',
      [string]$ArtifactsDirectory = "$PSScriptRoot/../../artifacts")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$arch = $Platform.ToLowerInvariant()
$metadata = & "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $root
if ($Version -cne $metadata.version) { throw 'Build version differs from source metadata.' }
$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot resolve build revision.' }
if ($env:GITHUB_SHA -and $revision -cne $env:GITHUB_SHA) { throw 'Checkout differs from the triggering revision.' }
$publish = Join-Path $artifacts "msi-publish/$arch"
$packages = Join-Path $artifacts "installer-packages/$arch"
$candidate = Join-Path $artifacts "candidate/$arch"
$validation = Join-Path $artifacts "validation/$arch"
[void][IO.Directory]::CreateDirectory($validation)
$timings = @()
function Invoke-BuildStage([string]$Name, [scriptblock]$Action) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $Action | Out-Host
    $script:timings += @{stage=$Name;seconds=[math]::Round($timer.Elapsed.TotalSeconds,2)}
}
$sevenzip = & "$PSScriptRoot/Stage-SevenZip.ps1" -Platform $Platform -ReleaseTag $SevenZipRelease | Select-Object -Last 1
Invoke-BuildStage 'publish' {
    dotnet publish (Join-Path $root 'FolderRewind/FolderRewind.csproj') -c Release "-p:Platform=$Platform" `
        -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false "-p:SevenZipExecutable=$sevenzip" `
        -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:AssemblyVersion=$Version" `
        "-p:FileVersion=$Version" "-p:InformationalVersion=$Version" -p:GenerateAppxPackageOnBuild=false -o $publish /warnaserror
    if ($LASTEXITCODE) { throw 'Self-contained publish failed.' }
}
Invoke-BuildStage 'msi-and-setup' {
    & "$PSScriptRoot/Prepare-MsiPackage.ps1" -ProjectPath (Join-Path $root 'Installer/FolderRewind.Installer.wixproj') `
        -PublishDirectory $publish -Version $Version -Platform $Platform -OutputDirectory $packages
}
Invoke-BuildStage 'payload-checks' {
    & "$PSScriptRoot/Test-MsiPayloadFailures.ps1" -Directory $publish -ResultPath (Join-Path $validation 'resource-negative.json')
    & "$root/scripts/tests/Test-Legacy182ReleasePayload.ps1" -PayloadDirectory $publish -ReportPath (Join-Path $validation 'migration-payload.json')
}
if ($arch -eq 'x64') {
    Invoke-BuildStage 'native-and-release-policy' {
        & "$PSScriptRoot/Test-InstallerNative.ps1" -ResultDirectory (Join-Path $validation 'native')
        & "$PSScriptRoot/Test-SetupReleaseAssets.ps1" -ResultDirectory (Join-Path $validation 'release-policy')
        & "$PSScriptRoot/Test-SetupReleaseFlow.ps1" -ResultDirectory (Join-Path $validation 'release-flow')
    }
}
Invoke-BuildStage 'production-identity' {
    & "$PSScriptRoot/New-InstallerCandidateManifest.ps1" -PackageDirectory (Join-Path $artifacts 'installer-packages') `
        -OutputDirectory $candidate -Version $Version -Architectures $arch -IdentityOnly
}
& "$PSScriptRoot/Prepare-SetupReleaseAssets.ps1" -SourceDirectory $packages -Version $Version -Platform $Platform -OutputDirectory (Join-Path $artifacts "release/$arch")
$manifest = Get-Content -LiteralPath (Join-Path $candidate 'manifest.json') -Raw | ConvertFrom-Json
$ids = @('build','resources','resource-negative','ice','production-identity','migration-payload')
if ($arch -eq 'x64') { $ids += @('native','release-policy') }
if ($manifest.sourceRevision -cne $revision) { throw 'Source revision changed while building the candidate.' }
@{version=$Version;sourceRevision=$revision;architecture=$arch;runId=$RunId;packageSha256=$manifest.assets[0].sha256;sevenZipRelease=$SevenZipRelease;
    evidence="https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$RunId";
    results=@($ids | ForEach-Object { @{id=$_;status='passed'} });timings=$timings} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $candidate 'checks.json') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) {
    "### $arch candidate`n`nVersion: $Version; commit: $revision`n`n| Stage | Seconds |`n| --- | ---: |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    foreach ($row in $timings) { "| $($row.stage) | $($row.seconds) |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
