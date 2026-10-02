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

    [string]$ReleaseNotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Enforce the public EXE-only policy before any GitHub operation.
$assets = @(& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $AssetsDirectory -Version $Version)
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) is required." }

if ($ReleaseNotesPath -and -not (Test-Path -LiteralPath $ReleaseNotesPath)) {
    throw "Release notes file not found: $ReleaseNotesPath"
}

$releaseExists = $true
$existingAssetsJson = gh release view $Tag --json assets
if ($LASTEXITCODE -ne 0) {
    $releaseExists = $false
}
else {
    $existingAssets = ($existingAssetsJson | ConvertFrom-Json).assets
    foreach ($existing in $existingAssets) {
        if ($existing.name -notin $assets.Name) {
            throw "Existing release contains a non-policy asset: $($existing.name). Refusing to publish a mixed-format release."
        }
    }
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

$assetPaths = $assets | ForEach-Object { $_.FullName }
gh release upload $Tag @assetPaths --clobber
if ($LASTEXITCODE -ne 0) {
    throw "Failed to upload release assets."
}

Write-Host "Release assets published to $Tag."
