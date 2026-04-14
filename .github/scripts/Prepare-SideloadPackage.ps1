[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$SevenZipPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PackageRoot)) {
    throw "MSIX output directory not found: $PackageRoot"
}

if (-not (Test-Path -LiteralPath $SevenZipPath)) {
    throw "7za.exe not found: $SevenZipPath"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$normalizedPlatform = $Platform.ToLowerInvariant()
$packageDirectory = Get-ChildItem -Path $PackageRoot -Directory |
    Where-Object { $_.Name -like "FolderRewind_${Version}_*" -and $_.Name.ToLowerInvariant().Contains($normalizedPlatform) } |
    Select-Object -First 1

if ($null -eq $packageDirectory) {
    throw "Package directory for version $Version and platform $Platform was not found."
}

$archiveName = "FolderRewind_${Version}_${normalizedPlatform}.7z"
$archivePath = Join-Path $OutputDirectory $archiveName
$hashPath = "$archivePath.sha256"
$stageDirectory = Join-Path $OutputDirectory "stage-$normalizedPlatform"

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

if (Test-Path -LiteralPath $hashPath) {
    Remove-Item -LiteralPath $hashPath -Force
}

if (Test-Path -LiteralPath $stageDirectory) {
    Remove-Item -LiteralPath $stageDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null

$requiredFileNames = @(
    "Install.ps1",
    "Add-AppDevPackage.ps1",
    "FolderRewind_${Version}_${normalizedPlatform}.msix",
    "FolderRewind_${Version}_${normalizedPlatform}.cer"
)

foreach ($fileName in $requiredFileNames) {
    $sourcePath = Join-Path $packageDirectory.FullName $fileName
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required sideload file not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stageDirectory $fileName) -Force
}

$requiredDirectoryNames = @(
    "Dependencies",
    "Add-AppDevPackage.resources"
)

foreach ($directoryName in $requiredDirectoryNames) {
    $sourcePath = Join-Path $packageDirectory.FullName $directoryName
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required sideload directory not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stageDirectory $directoryName) -Recurse -Force
}

Push-Location $stageDirectory
try {
    # Keep only the complete sideload install set at the archive root:
    # certificate, install scripts and dependency packages.
    & $SevenZipPath a -t7z $archivePath ".\*" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "7za packaging failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location

    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
}

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$archiveName" | Set-Content -LiteralPath $hashPath -Encoding ascii

Write-Host "Release archive created: $archivePath"
Write-Host "SHA256 file created: $hashPath"
