[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [ValidateSet('All','Build','RefreshHost','FinishBuild','FreezeBuild','Functional','Data','DataWebDav','DataImport','Performance','Complete')][string]$Stage = 'All',
    [string]$MineRepository = "$PSScriptRoot/../../FolderRewind-Plugin-Minecraft",
    [string]$FormalDirectory,
    [string]$DataSession,
    [string]$UiEvidence,
    [string]$UiCaseScript
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$run = [IO.Path]::GetFullPath($RunRoot)
$mine = [IO.Path]::GetFullPath($MineRepository)
$sdk = Join-Path $run "sdk"
$formal = if ($FormalDirectory) { [IO.Path]::GetFullPath($FormalDirectory) } else { Join-Path $run "candidate/formal" }
$fault = Join-Path $run "candidate/fault"
$expectedRunRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/redundancy-cleanup')).TrimEnd('\') + '\'
if (!$run.StartsWith($expectedRunRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Acceptance output must be under artifacts/redundancy-cleanup.' }
New-Item -ItemType Directory -Path $run -Force | Out-Null
$logRoot = Join-Path $run 'acceptance-logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
function Save-Json($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Checked([string]$Name, [scriptblock]$Action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    & $Action *> (Join-Path $logRoot "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "$Name failed ($LASTEXITCODE); see acceptance-logs/$Name.log" }
    Write-Host "$Name passed ($([math]::Round($watch.Elapsed.TotalSeconds, 1)) s)"
}
function Invoke-AcceptanceDotnet([string]$Name, [string[]]$Arguments) {
    Checked $Name { & dotnet.exe @Arguments }
}
function Require-Trx([string]$Path) {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $c = $trx.TestRun.ResultSummary.Counters
    if ([int]$c.total -le 0 -or [int]$c.passed -ne [int]$c.total -or [int]$c.executed -ne [int]$c.total) {
        throw "Empty, failed, skipped or incomplete required suite: $Path"
    }
    @{path=$Path;sha256=(Hash $Path);total=[int]$c.total;passed=[int]$c.passed}
}
function Candidate-Identity {
    $files = @(Get-ChildItem -LiteralPath $formal,$fault -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
    $files += Join-Path $run 'candidate/MineRewind.frplugin'
    @($files | ForEach-Object { @{path=[IO.Path]::GetRelativePath($run,$_).Replace('\','/');sha256=(Hash $_)} })
}
function Check-Identity {
    $frozen = Get-Content (Join-Path $run 'candidate/identity.json') -Raw | ConvertFrom-Json
    foreach ($entry in $frozen.files) { if ((Hash (Join-Path $run $entry.path)) -ne $entry.sha256) { throw "Candidate changed: $($entry.path)" } }
    foreach ($installer in $frozen.msi) { if ((Hash $installer.path) -ne $installer.sha256) { throw "Installer changed: $($installer.path)" } }
    $frozen
}
function Receipt([string]$Name, $Details) {
    Save-Json @{stage=$Name;completedUtc=[DateTimeOffset]::UtcNow;candidateIdentitySha256=(Hash (Join-Path $run 'candidate/identity.json'));details=$Details} (Join-Path $run "$Name-receipt.json")
}
Push-Location -LiteralPath $repo
try {
    if ($Stage -in @('All','Build','RefreshHost')) {
        Checked 'layout' { & "$PSScriptRoot/Test-RepositoryLayout.ps1" }
        $sdk = Join-Path $run 'sdk'
        if (!(Test-Path "$sdk/contract.candidate.json")) { throw 'Prepare an exact development-candidate SDK/feed/cache with source provenance first.' }
        if ($Stage -ne 'RefreshHost') {
        Checked 'mine' { & "$mine/tools/Build-Plugin.ps1" -Development -Provenance "$sdk/contract.candidate.json" -NuGetConfig "$sdk/NuGet.Config" -PackageCache "$sdk/cache" -EvidenceDirectory "$run/mine-final" -PackageOutput "$run/candidate/MineRewind.frplugin" }
        Require-Trx "$run/mine-final/MineRewind.Tests.trx" | Out-Null
        Require-Trx "$run/mine-final/MineRewind.World.Tests.trx" | Out-Null
        }
        Invoke-AcceptanceDotnet 'restore' @('restore','FolderRewind.slnx','-p:Platform=x64')
        foreach ($configuration in @('Debug','Release')) {
            Invoke-AcceptanceDotnet "host-$configuration" @('build','FolderRewind.slnx','-t:Rebuild','-c',$configuration,'-p:Platform=x64','-p:GenerateAppxPackageOnBuild=false','--no-restore')
        }
        $publishArgs = @('publish','src/FolderRewind/FolderRewind.csproj','-c','Release','-p:Platform=x64',
            '-p:FolderRewindDistributionChannel=Msi','-p:GenerateAppxPackageOnBuild=false','-p:CETCompat=false',
            "-p:SevenZipExecutable=$repo/7za.exe",'-p:DebugType=None','-p:DebugSymbols=false')
        Invoke-AcceptanceDotnet 'clean-formal' @('clean','src/FolderRewind/FolderRewind.csproj','-c','Release','-p:Platform=x64','-p:FolderRewindDistributionChannel=Msi','-p:GenerateAppxPackageOnBuild=false')
        Invoke-AcceptanceDotnet 'publish-formal' ($publishArgs + @('-o',"$formal"))
        Checked 'normalize-formal-resources' { & "$repo/scripts/build/Normalize-MsiResourcePaths.ps1" -Directory "$formal" }
        Checked 'formal-payload' { & "$PSScriptRoot/Test-MsiPayload.ps1" -Directory "$formal" -ResultPath "$run/formal-payload.json" }
        $formalStrings = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes("$formal/FolderRewind.dll"))
        if ($formalStrings.Contains('NativeArtifactAcceptance') -or $formalStrings.Contains('MinecraftRealUiAcceptance')) { throw 'Acceptance code leaked into formal application.' }
    }
    if ($Stage -in @('All','Build','RefreshHost','FinishBuild')) {
        $publishArgs = @('publish','src/FolderRewind/FolderRewind.csproj','-c','Release','-p:Platform=x64',
            '-p:FolderRewindDistributionChannel=Msi','-p:GenerateAppxPackageOnBuild=false','-p:CETCompat=false',
            "-p:SevenZipExecutable=$repo/7za.exe",'-p:DebugType=None','-p:DebugSymbols=false')
        Invoke-AcceptanceDotnet 'clean-fault' @('clean','src/FolderRewind/FolderRewind.csproj','-c','Release','-p:Platform=x64','-p:FolderRewindDistributionChannel=Msi','-p:FolderRewindFaultInjection=true','-p:NativeArtifactAcceptance=true','-p:MinecraftCoordinationUiAcceptance=true','-p:GenerateAppxPackageOnBuild=false')
        Invoke-AcceptanceDotnet 'publish-fault' ($publishArgs + @('-p:FolderRewindFaultInjection=true','-p:NativeArtifactAcceptance=true','-p:MinecraftCoordinationUiAcceptance=true','-o',"$fault"))
        $metadata = & "$repo/scripts/release/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $repo
        Checked 'msi' { & "$repo/scripts/build/Prepare-MsiPackage.ps1" -ProjectPath "$repo/Installer/FolderRewind.Installer.wixproj" -PublishDirectory "$formal" -Version $metadata.version -Platform x64 -OutputDirectory "$run/candidate/installer" -SkipBundle }
    }
    if ($Stage -in @('All','Build','RefreshHost','FinishBuild','FreezeBuild')) {
        $identity = @{formalDirectory=$formal;faultDirectory=$fault;hostHead=(& git rev-parse HEAD);mineHead=(& git -C $mine rev-parse HEAD);sdk=(& dotnet --version);files=(Candidate-Identity);sdkProvenanceSha256=(Hash "$sdk/contract.candidate.json");msi=@(Get-ChildItem "$run/candidate/installer" -Filter '*.msi' | ForEach-Object { @{path=$_.FullName;sha256=(Hash $_.FullName)} })}
        Save-Json $identity "$run/candidate/identity.json"
        & git diff --binary | Set-Content "$run/final-host.patch" -Encoding utf8
        & git -C $mine diff --binary | Set-Content "$run/final-mine.patch" -Encoding utf8
        Receipt 'build' @{formalPayload="$run/formal-payload.json";plugin="$run/mine-final/build-identity.json"}
    }
    if ($Stage -in @('All','Functional')) {
        $null = Check-Identity
        $env:FOLDERREWIND_TEST_MINEREWIND_PACKAGE = "$run/candidate/MineRewind.frplugin"
        $env:FOLDERREWIND_TEST_MINEREWIND_SHA256 = Hash $env:FOLDERREWIND_TEST_MINEREWIND_PACKAGE
        Checked 'functional' { & "$PSScriptRoot/Invoke-SetupCoreTests.ps1" -ResultsDirectory "$run/functional" }
        $results = @('abstractions','runtime','host','recovery') | ForEach-Object { Require-Trx "$run/functional/$_/core-tests.trx" }
        $results += Require-Trx "$run/mine-final/MineRewind.Tests.trx"
        $results += Require-Trx "$run/mine-final/MineRewind.World.Tests.trx"
        Receipt 'functional' $results
    }
    if ($Stage -in @('All','Data','DataWebDav')) {
        $null = Check-Identity
        # This independent driver copies the pinned real-world inputs, records complete manifests,
        # and invokes only the isolated fault candidate. It never targets an original world.
        if ($Stage -ne 'DataWebDav') { Checked 'real-data' { & "$PSScriptRoot/Test-RedundancyCleanupData.ps1" -RunRoot $run -ResumeSession $DataSession } }
        Checked 'webdav-data' { & "$PSScriptRoot/Test-RedundancyCleanupWebDav.ps1" -RunRoot $run }
        Checked 'import-repair-data' { & "$PSScriptRoot/Test-RedundancyCleanupImportRecovery.ps1" -RunRoot $run }
        Receipt 'data' (Get-Content "$run/data/summary.json" -Raw | ConvertFrom-Json)
    }
    if ($Stage -eq 'DataImport') {
        $null = Check-Identity
        Checked 'import-repair-data' { & "$PSScriptRoot/Test-RedundancyCleanupImportRecovery.ps1" -RunRoot $run }
        Receipt 'data' (Get-Content "$run/data/summary.json" -Raw | ConvertFrom-Json)
    }
    if ($Stage -in @('All','Performance')) {
        $null = Check-Identity
        Checked 'performance' { & "$PSScriptRoot/Test-RedundancyCleanupPerformance.ps1" -RunRoot $run }
        Receipt 'performance' (Get-Content "$run/performance/summary.json" -Raw | ConvertFrom-Json)
    }
    if ($Stage -in @('All','Complete')) {
        $identity = Check-Identity
        foreach ($name in @('build','functional','data','performance')) {
            $receipt = Get-Content "$run/$name-receipt.json" -Raw | ConvertFrom-Json
            if ($receipt.candidateIdentitySha256 -ne (Hash "$run/candidate/identity.json")) { throw "Stale evidence: $name" }
        }
        foreach ($suite in (Get-Content "$run/functional-receipt.json" -Raw | ConvertFrom-Json).details) {
            if ((Hash $suite.path) -ne $suite.sha256) { throw "Functional evidence changed: $($suite.path)" }
            $null = Require-Trx $suite.path
        }
        $dataEvidence = Get-Content "$run/data/summary.json" -Raw | ConvertFrom-Json
        if (!$dataEvidence.success -or !$dataEvidence.webdav.success -or !$dataEvidence.importRepair.success -or
            @($dataEvidence.importRepair.cases).Count -ne 6 -or
            $dataEvidence.candidateIdentitySha256 -ne (Hash "$run/candidate/identity.json") -or
            $dataEvidence.webdav.candidateIdentitySha256 -ne (Hash "$run/candidate/identity.json") -or
            $dataEvidence.importRepair.candidateIdentitySha256 -ne (Hash "$run/candidate/identity.json")) { throw 'Incomplete or stale real-data/import repair recovery evidence.' }
        if (!$UiEvidence) { $UiEvidence = "$run/ui-final/ui-results.json" }
        if (!(Test-Path -LiteralPath $UiEvidence)) {
            Save-Json @{owner='gpt-6.1-terra';script="$PSScriptRoot/Test-RedundancyCleanupUi.ps1";candidate="$formal/FolderRewind.exe";plugin="$run/candidate/MineRewind.frplugin";caseScript=$UiCaseScript;requiredReceipt=$UiEvidence} "$run/ui-handoff.json"
            throw 'Non-UI stages complete. Final UI must be executed and visually reviewed by the gpt-6.1-terra owner; then rerun -Stage Complete.'
        }
        $ui = Get-Content -LiteralPath $UiEvidence -Raw | ConvertFrom-Json
        if (!$ui.success -or $ui.candidateIdentitySha256 -ne (Hash "$run/candidate/identity.json") -or
            ![string]::Equals([IO.Path]::GetFullPath($ui.candidate), (Join-Path $identity.formalDirectory 'FolderRewind.exe'), [StringComparison]::OrdinalIgnoreCase) -or
            $ui.candidateSha256 -ne (Hash (Join-Path $identity.formalDirectory "FolderRewind.exe")) -or
            $ui.candidateDllSha256 -ne (Hash (Join-Path $identity.formalDirectory "FolderRewind.dll")) -or
            $ui.pluginSha256 -ne (Hash "$run/candidate/MineRewind.frplugin")) { throw 'UI evidence failed or belongs to a different candidate.' }
        $requiredUiCases = @('discovery','plugin-install-enable-disable-safe-mode','world-core-full','world-native-incremental',
            'history-preview-branch-merge-restore','cancel-close','cloud-profile-webdav','migration-legacy-state',
            'zh-light-dark-high-contrast','en-light-dark-high-contrast')
        if ($ui.uiOwner -ne 'gpt-6.1-terra' -or $ui.visualReview -ne 'PASS' -or @($ui.results).Count -ne $requiredUiCases.Count -or
            @($ui.results | Where-Object { $_.status -ne 'PASS' -or $_.visualReview -ne 'PASS' -or @($_.screenshots).Count -eq 0 }).Count -or
            @($ui.screenshots).Count -lt 10) { throw 'Incomplete UI execution or visual review.' }
        foreach ($caseName in $requiredUiCases) {
            if (@($ui.results | Where-Object name -eq $caseName).Count -ne 1) { throw "Missing or duplicate UI case: $caseName" }
        }
        foreach ($shot in $ui.screenshots) { if (!(Test-Path -LiteralPath $shot -PathType Leaf) -or (Get-Item -LiteralPath $shot).Length -le 0) { throw 'Missing UI screenshot.' } }
        Receipt 'ui'  @{path=$UiEvidence;sha256=(Hash $UiEvidence);owner='gpt-6.1-terra'}
        Save-Json @{success=$true;completedUtc=[DateTimeOffset]::UtcNow;identity=$identity;stages=@('build','functional','data','performance','ui')} "$run/acceptance-summary.json"
    }
} finally { Pop-Location }
