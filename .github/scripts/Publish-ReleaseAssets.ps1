[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseName,

    [Parameter(Mandatory = $true)]
    [string]$AssetsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$TargetCommit,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseNotesPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Legacy')][string]$AcceptanceReport,
    [Parameter(Mandatory = $true, ParameterSetName = 'Automated')][string]$ReleaseReport,
    [Parameter(Mandatory = $true, ParameterSetName = 'Automated')][ValidatePattern('^\d+$')][string]$RunId,
    [switch]$Finalize
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$number = [version]$Version
if ($Tag -cne "v$($number.Major).$($number.Minor).$($number.Build)") { throw 'Release tag differs from the package version.' }

# Enforce the public EXE-only policy before any GitHub operation.
$assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $AssetsDirectory -Version $Version)
if ($PSCmdlet.ParameterSetName -eq 'Automated') {
    & "$PSScriptRoot\Test-SetupReleaseReport.ps1" -ReportPath $ReleaseReport -AssetsDirectory $AssetsDirectory -Version $Version -SourceRevision $TargetCommit -RunId $RunId
} else {
    & "$PSScriptRoot\Test-InstallerAcceptance.ps1" -ReportPath $AcceptanceReport -AssetsDirectory $AssetsDirectory -Version $Version -SourceRevision $TargetCommit
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) is required." }

if ($ReleaseNotesPath -and -not (Test-Path -LiteralPath $ReleaseNotesPath)) {
    throw "Release notes file not found: $ReleaseNotesPath"
}

$releaseExists = $false
# A failed lookup is not proof of absence (authentication/network failures must
# never cause a create). Listing gives explicit absence and a checked exit code.
$releaseListJson = gh release list --limit 1000 --json tagName
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify existing releases.' }
$releases = @($releaseListJson | ConvertFrom-Json)
if ($releases.Count -ge 1000) { throw 'Release listing is truncated; cannot establish absence safely.' }
$releaseExists = @($releases | Where-Object tagName -CEQ $Tag).Count -eq 1
$pending = [Collections.Generic.List[string]]::new()
# Check tags even for drafts: a draft target must never hide a conflicting tag.
$refsJson = gh api "repos/{owner}/{repo}/git/matching-refs/tags/$Tag"
if ($LASTEXITCODE) { throw 'Cannot verify existing release tags.' }
$matchingRefs = @($refsJson | ConvertFrom-Json | Where-Object ref -CEQ "refs/tags/$Tag")
if ($matchingRefs.Count) {
    $tagCommit = gh api "repos/{owner}/{repo}/commits/$Tag" --jq .sha
    if ($LASTEXITCODE -or $tagCommit -cne $TargetCommit) { throw 'Existing tag points to another source revision.' }
}
if ($releaseExists) {
    $existingAssetsJson = gh release view $Tag --json assets,isDraft,targetCommitish
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify existing release assets.' }
    $existingRelease = $existingAssetsJson | ConvertFrom-Json
    $existingAssets = $existingRelease.assets
    if ($existingRelease.isDraft) {
        if ($existingRelease.targetCommitish -cne $TargetCommit) { throw 'Existing draft targets another source revision.' }
    } else {
        $tagCommit = gh api "repos/{owner}/{repo}/commits/$Tag" --jq .sha
        if ($LASTEXITCODE -or $tagCommit -cne $TargetCommit) { throw 'Existing release tag points to another source revision.' }
    }
    foreach ($existing in $existingAssets) {
        if ($existing.name -notin $assets.Name) {
            throw "Existing release contains a non-policy asset: $($existing.name). Refusing to publish a mixed-format release."
        }
    }
    foreach ($asset in $assets) {
        $existing = @($existingAssets | Where-Object name -CEQ $asset.Name)
        if ($existing.Count -gt 1) { throw "Duplicate remote asset: $($asset.Name)" }
        if ($existing.Count -eq 0) { $pending.Add($asset.FullName); continue }
        # GitHub's SHA-256 digest is required. Never replace a published binary,
        # and never trust a same-named checksum attachment as proof of its bytes.
        $digestProperty = $existing[0].PSObject.Properties['digest']
        $expected = 'sha256:' + (Get-FileHash -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
        if (-not $digestProperty -or $digestProperty.Value -cne $expected) { throw "Remote digest unavailable or differs: $($asset.Name)" }
    }
    if (-not $existingRelease.isDraft -and $pending.Count) { throw 'A published release is incomplete; discuss recovery before adding assets.' }
} else {
    foreach ($asset in $assets) { $pending.Add($asset.FullName) }
}

if (-not $releaseExists) {
    # Create the tag and release automatically on the first publish.
    if ($ReleaseNotesPath) {
        gh release create $Tag --title $ReleaseName --notes-file $ReleaseNotesPath --target $TargetCommit --draft
    }
    else {
        gh release create $Tag --title $ReleaseName --generate-notes --target $TargetCommit --draft
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the release."
    }
}
elseif ($ReleaseNotesPath -and $existingRelease.isDraft) {
    gh release edit $Tag --title $ReleaseName --notes-file $ReleaseNotesPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to update release notes."
    }
}

if ($pending.Count) {
    gh release upload $Tag @pending
    if ($LASTEXITCODE -ne 0) { throw 'Failed to upload release assets; existing assets were not overwritten.' }
}

# Verify the complete remote set after upload, before making anything public.
$remoteJson = gh release view $Tag --json assets,isDraft
if ($LASTEXITCODE) { throw 'Cannot verify uploaded draft assets.' }
$remote = $remoteJson | ConvertFrom-Json
if (@($remote.assets).Count -ne $assets.Count) { throw 'Remote draft attachment set differs from the verified release.' }
foreach ($asset in $assets) {
    $match = @($remote.assets | Where-Object name -CEQ $asset.Name)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
    if ($match.Count -ne 1 -or $match[0].digest -cne $digest) { throw "Uploaded asset digest differs: $($asset.Name)" }
}
if ($Finalize -and $remote.isDraft) {
    gh release edit $Tag --draft=false --latest
    if ($LASTEXITCODE) { throw 'Draft promotion failed.' }
}
Write-Host "Release $Tag attachments verified. Finalize requested: $Finalize"
