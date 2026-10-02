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
    [Parameter(Mandatory = $true)][string]$AcceptanceReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Enforce the public EXE-only policy before any GitHub operation.
$assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $AssetsDirectory -Version $Version)
& "$PSScriptRoot\Test-InstallerAcceptance.ps1" -ReportPath $AcceptanceReport -AssetsDirectory $AssetsDirectory -Version $Version -SourceRevision $TargetCommit
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
if ($releaseExists) {
    $existingAssetsJson = gh release view $Tag --json assets
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify existing release assets.' }
    $existingAssets = ($existingAssetsJson | ConvertFrom-Json).assets
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
} else {
    foreach ($asset in $assets) { $pending.Add($asset.FullName) }
}

if (-not $releaseExists) {
    # Create the tag and release automatically on the first publish.
    if ($ReleaseNotesPath) {
        gh release create $Tag --title $ReleaseName --notes-file $ReleaseNotesPath --target $TargetCommit
    }
    else {
        gh release create $Tag --title $ReleaseName --notes "Automated FolderRewind Setup build." --target $TargetCommit
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the release."
    }
}
elseif ($ReleaseNotesPath) {
    gh release edit $Tag --title $ReleaseName --notes-file $ReleaseNotesPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to update release notes."
    }
}

if ($pending.Count) {
    gh release upload $Tag @pending
    if ($LASTEXITCODE -ne 0) { throw 'Failed to upload release assets; existing assets were not overwritten.' }
}

Write-Host "Release assets published to $Tag."
