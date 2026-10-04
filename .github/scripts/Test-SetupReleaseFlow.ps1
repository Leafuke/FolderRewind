[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetFullPath($ResultDirectory)) ([guid]::NewGuid().ToString('N'))
$public = Join-Path $root 'public'
[void][IO.Directory]::CreateDirectory($public)
$version = '1.2.3.0'; $revision = 'a' * 40; $run = '123'; $tag = 'v1.2.3'
$results = [Collections.Generic.List[object]]::new()
function Expect-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true; $reason = $_.Exception.Message }
    if (-not $rejected) { throw "Failed to reject: $Name" }
    $results.Add(@{scenario=$Name;passed=$true;reason=$reason})
}
$assets = @(); $checks = @()
foreach ($arch in @('x64','arm64')) {
    $name = "FolderRewind_${version}_Setup_$arch.exe"
    'Inert release fixture' | Set-Content -LiteralPath (Join-Path $public $name)
    $hash = (Get-FileHash -LiteralPath (Join-Path $public $name)).Hash.ToLowerInvariant()
    "$hash *$name" | Set-Content -LiteralPath (Join-Path $public "$name.sha256") -Encoding ascii
    $assets += @{name=$name;architecture=$arch;sha256=$hash}
    $ids = @('build','resources','resource-negative','ice','production-identity','migration-payload')
    if ($arch -eq 'x64') { $ids += @('native','release-policy') }
    $check = @{architecture=$arch;runId=$run;sourceRevision=$revision;version=$version;packageSha256=$hash;evidence='synthetic tests';results=@($ids | ForEach-Object { @{id=$_;status='passed'} })}
    $checks += $check
    $candidate = Join-Path $root "candidate/$arch"
    [void][IO.Directory]::CreateDirectory($candidate)
    @{schemaVersion=1;version=$version;sourceRevision=$revision;sourceDirty=$false;assets=@($assets[-1])} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $candidate 'manifest.json')
    $check | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $candidate 'checks.json')
}
$testRoot = Join-Path $root 'tests'
foreach ($suite in @('abstractions','runtime','host')) {
    $directory = Join-Path $testRoot $suite
    [void][IO.Directory]::CreateDirectory($directory)
    '<TestRun><ResultSummary outcome="Completed"><Counters total="2" passed="2" /></ResultSummary></TestRun>' | Set-Content (Join-Path $directory 'tests.trx')
}
$reportPath = Join-Path $root 'release-report.json'
$newArguments = @{CandidateDirectory=(Join-Path $root 'candidate');AssetsDirectory=$public;TestResultsDirectory=$testRoot;Version=$version;SourceRevision=$revision;RunId=$run;OutputPath=$reportPath}
& "$PSScriptRoot/New-SetupReleaseReport.ps1" @newArguments
$report = Get-Content $reportPath -Raw | ConvertFrom-Json -AsHashtable
$original = $report | ConvertTo-Json -Depth 10
function Save-Report { $report | ConvertTo-Json -Depth 10 | Set-Content $reportPath }
$validate = @{ReportPath=$reportPath;AssetsDirectory=$public;Version=$version;SourceRevision=$revision;RunId=$run}
$publish = @{Tag=$tag;ReleaseName='Fixture';AssetsDirectory=$public;TargetCommit=$revision;Version=$version;ReleaseReport=$reportPath;RunId=$run;Finalize=$true}
$preflight = @{Version=$version;SourceRevision=$revision;DownloadDirectory=(Join-Path $root 'downloads')}
$results.Add(@{scenario='assemble-valid-v2-report';passed=$true})
foreach ($field in @('version','sourceRevision','runId')) {
    $report[$field] = 'wrong'; Save-Report
    Expect-Rejected "reject-report-$field" { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
    $report = $original | ConvertFrom-Json -AsHashtable
}
$report.sourceDirty = $true; Save-Report
Expect-Rejected 'reject-dirty-source' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.assets[0].sha256 = 'b' * 64; Save-Report
Expect-Rejected 'reject-package-hash' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.assets = @($report.assets[0]); Save-Report
Expect-Rejected 'reject-missing-architecture' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.checks[0].results[0].status = 'failed'; Save-Report
Expect-Rejected 'reject-build-failure' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.tests[0].passed = 1; Save-Report
Expect-Rejected 'reject-test-failure' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report.manualInstallerApproval = @{decision='accepted-for-release'}; Save-Report
Expect-Rejected 'manual-cannot-waive-v2-test-failure' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.checks[1].packageSha256 = 'b' * 64; Save-Report
Expect-Rejected 'reject-checks-for-other-bytes' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable
$report.checks[0].results += $report.checks[0].results[0]; Save-Report
Expect-Rejected 'reject-duplicate-checks' { & "$PSScriptRoot/Test-SetupReleaseReport.ps1" @validate }
$report = $original | ConvertFrom-Json -AsHashtable; Save-Report
foreach ($xml in @('<TestRun><ResultSummary outcome="Completed"><Counters total="0" passed="0" /></ResultSummary></TestRun>',
                   '<TestRun><ResultSummary outcome="Completed"><Counters total="2" passed="1" notExecuted="1" /></ResultSummary></TestRun>')) {
    $xml | Set-Content (Join-Path $testRoot 'host/tests.trx')
    Expect-Rejected 'reject-empty-or-skipped-trx' { & "$PSScriptRoot/New-SetupReleaseReport.ps1" @newArguments }
}
'<TestRun><ResultSummary outcome="Completed"><Counters total="2" passed="2" /></ResultSummary></TestRun>' | Set-Content (Join-Path $testRoot 'host/tests.trx')

# All GitHub operations are mocked; never upload or contact an account.
$global:FolderRewindReleaseFlowMock = @{exists=$false;draft=$false;tag=$false;commit=$revision;target=$revision;assets=@();mutations=0;calls=0;failUpload=$false;failList=$false;corruptDownload=$false;generated=$false}
function gh {
    $state = $global:FolderRewindReleaseFlowMock
    $state.calls++; $global:LASTEXITCODE = 0
    if ($args -contains '--clobber') { throw 'Clobber is prohibited.' }
    if ($args[0] -eq 'api') {
        if ($args[1] -like '*matching-refs*') {
            if ($state.tag) { @(@{ref="refs/tags/$tag"}) | ConvertTo-Json -AsArray } else { '[]' }
        } else { $state.commit }
    } elseif ($args[1] -eq 'list') {
        if ($state.failList) { $global:LASTEXITCODE=1; return }
        if ($state.exists) { @(@{tagName=$tag}) | ConvertTo-Json -AsArray } else { '[]' }
    } elseif ($args[1] -eq 'view') {
        @{assets=$state.assets;isDraft=$state.draft;isPrerelease=$false;targetCommitish=$state.target;url='https://example.test/release'} | ConvertTo-Json -Depth 8
    } elseif ($args[1] -eq 'download') {
        $destination = $args[[array]::IndexOf($args,'--dir') + 1]
        Get-ChildItem $public -Filter '*.sha256' | Copy-Item -Destination $destination
        if ($state.corruptDownload) { 'corrupt' | Set-Content (Join-Path $destination $state.assets[1].name) }
    } elseif ($args[1] -eq 'create') {
        $state.mutations++; $state.exists=$true; $state.draft=$true; $state.tag=$true
        $state.generated = $args -contains '--generate-notes'
    } elseif ($args[1] -eq 'upload') {
        $state.mutations++
        foreach ($arg in $args[3..($args.Count-1)]) {
            $item = Get-Item -LiteralPath $arg
            $state.assets += @{name=$item.Name;digest=('sha256:'+(Get-FileHash $item.FullName).Hash.ToLowerInvariant())}
            if ($state.failUpload) { $state.failUpload=$false; $global:LASTEXITCODE=1; return }
        }
    } elseif ($args[1] -eq 'edit') {
        $state.mutations++
        if ($args -contains '--draft=false') { $state.draft=$false }
    } else { throw "Unexpected mock operation: $args" }
}
try {
    $state = $global:FolderRewindReleaseFlowMock
    if ((& "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight).alreadyPublished) { throw 'Absent release reported as published.' }
    & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish
    if ($state.draft -or -not $state.generated -or $state.assets.Count -ne 4) { throw 'First publish did not automatically finalize with generated notes.' }
    $results.Add(@{scenario='first-publish-generated-notes-and-finalize';passed=$true})
    $state.mutations=0
    if (-not (& "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight).alreadyPublished) { throw 'Published release was not detected.' }
    & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish
    if ($state.mutations) { throw 'Repeated publish mutated release.' }
    $results.Add(@{scenario='repeat-publish-no-build-or-mutation';passed=$true})
    $state.corruptDownload=$true
    Expect-Rejected 'reject-corrupt-remote-checksum' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    $state.corruptDownload=$false
    $state.commit = 'b' * 40
    Expect-Rejected 'reject-conflicting-tag-before-build' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    Expect-Rejected 'reject-conflicting-tag-before-publish' { & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish }
    $state.commit=$revision; $state.draft=$true; $state.assets=@(); $state.failUpload=$true
    Expect-Rejected 'upload-failure-keeps-draft' { & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish }
    if (-not $state.draft -or $state.assets.Count -ne 1) { throw 'Failed upload did not preserve a partial draft.' }
    & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish
    if ($state.draft -or $state.assets.Count -ne 4) { throw 'Failed upload did not resume with identical frozen bytes.' }
    $results.Add(@{scenario='resume-partial-draft-without-replacement';passed=$true})
    $state.assets[0].digest='sha256:' + ('b' * 64)
    Expect-Rejected 'reject-overwriting-existing-bytes' { & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish }
    $state.assets[0].Remove('digest')
    Expect-Rejected 'reject-unverifiable-remote-bytes' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    $state.assets=@(@{name='leaked.msi';digest='sha256:' + ('b' * 64)})
    Expect-Rejected 'reject-mixed-remote-assets' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    $state.assets=@()
    Expect-Rejected 'reject-incomplete-public-release' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    $state.failList=$true
    Expect-Rejected 'reject-network-failure-without-create' { & "$PSScriptRoot/Test-SetupReleasePreflight.ps1" @preflight }
    $state.failList=$false; $state.calls=0
    $report.tests[0].status='failed'; Save-Report
    Expect-Rejected 'reject-bad-v2-before-github' { & "$PSScriptRoot/Publish-ReleaseAssets.ps1" @publish }
    if ($state.calls) { throw 'Failed tests reached GitHub.' }
    $report = $original | ConvertFrom-Json -AsHashtable; Save-Report
    Expect-Rejected 'v2-cannot-use-legacy-acceptance-parameter' { & "$PSScriptRoot/Publish-ReleaseAssets.ps1" -Tag $tag -ReleaseName Fixture -AssetsDirectory $public -TargetCommit $revision -Version $version -AcceptanceReport $reportPath }
} finally { Remove-Variable FolderRewindReleaseFlowMock -Scope Global }

$metadataRoot = Join-Path $root 'metadata'
[void][IO.Directory]::CreateDirectory((Join-Path $metadataRoot 'FolderRewind'))
function Save-Metadata([string]$ManifestVersion, [string]$ProjectVersion) {
    "<Package><Identity Version='$ManifestVersion'/></Package>" | Set-Content (Join-Path $metadataRoot 'FolderRewind/Package.appxmanifest')
    "<Project><PropertyGroup><Version>$ProjectVersion</Version></PropertyGroup></Project>" | Set-Content (Join-Path $metadataRoot 'FolderRewind/FolderRewind.csproj')
}
Save-Metadata $version $version
$metadata = & "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $metadataRoot
if ($metadata.tag -cne $tag -or $metadata.notesPath) { throw 'Invalid metadata fallback.' }
$notesDirectory = Join-Path $metadataRoot '.github/release-notes'
[void][IO.Directory]::CreateDirectory($notesDirectory)
'Legacy notes' | Set-Content (Join-Path $notesDirectory "$tag.md")
if ((& "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $metadataRoot).notesPath -cne ".github/release-notes/$tag.md") { throw 'Legacy notes were not selected.' }
$notesDirectory = Join-Path $metadataRoot 'docs/release'
[void][IO.Directory]::CreateDirectory($notesDirectory)
'Preferred notes' | Set-Content (Join-Path $notesDirectory 'FolderRewind-1.2.3-notes.md')
if ((& "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $metadataRoot).notesPath -cne 'docs/release/FolderRewind-1.2.3-notes.md') { throw 'Notes precedence differs.' }
$results.Add(@{scenario='metadata-and-notes-precedence';passed=$true})
Save-Metadata $version '1.2.4.0'
Expect-Rejected 'reject-project-manifest-version-mismatch' { & "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $metadataRoot }
Save-Metadata '256.2.3.0' '256.2.3.0'
Expect-Rejected 'reject-msi-version-bounds' { & "$PSScriptRoot/Get-SetupReleaseMetadata.ps1" -RepositoryRoot $metadataRoot }
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
Write-Host "Passed $($results.Count) one-click release flow checks. Results: $root"
