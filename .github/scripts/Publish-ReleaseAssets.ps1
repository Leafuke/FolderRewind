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

    [string]$ReleaseNotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required."
}

if (-not (Test-Path -LiteralPath $AssetsDirectory)) {
    throw "Assets directory not found: $AssetsDirectory"
}

$assets = Get-ChildItem -Path $AssetsDirectory -File | Sort-Object Name
if ($assets.Count -eq 0) {
    throw "Assets directory is empty."
}

if ($ReleaseNotesPath -and -not (Test-Path -LiteralPath $ReleaseNotesPath)) {
    throw "Release notes file not found: $ReleaseNotesPath"
}

$releaseExists = $true
gh release view $Tag | Out-Null
if ($LASTEXITCODE -ne 0) {
    $releaseExists = $false
}

if (-not $releaseExists) {
    # Create the tag and release automatically on the first publish.
    if ($ReleaseNotesPath) {
        gh release create $Tag --title $ReleaseName --notes-file $ReleaseNotesPath --target $TargetCommit
    }
    else {
        gh release create $Tag --title $ReleaseName --notes "Automated GitHub sideload build." --target $TargetCommit
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
