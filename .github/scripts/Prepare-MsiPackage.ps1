[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "WiX project was not found: $ProjectPath"
}

if (-not (Test-Path -LiteralPath $PublishDirectory)) {
    throw "Self-contained publish directory was not found: $PublishDirectory"
}

$requiredFiles = @(
    "FolderRewind.exe",
    "7za.exe",
    "Assets\logo.ico"
)

foreach ($requiredFile in $requiredFiles) {
    $path = Join-Path $PublishDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required MSI payload file was not found: $path"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$normalizedPlatform = $Platform.ToLowerInvariant()
$msiName = "FolderRewind_${Version}_${normalizedPlatform}.msi"
$msiPath = Join-Path $OutputDirectory $msiName

dotnet build $ProjectPath `
    -t:Rebuild `
    -c Release `
    -p:InstallerPlatform=$normalizedPlatform `
    -p:ProductVersion=$Version `
    -p:PublishDir=$PublishDirectory `
    -p:OutputPath="$OutputDirectory\" `
    -p:OutputName="FolderRewind_${Version}_${normalizedPlatform}"

if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "Expected MSI output was not found: $msiPath"
}

# WiX emits a .wixpdb next to the package by default. It is useful for local
# diagnostics but is not a public Release asset.
Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.wixpdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$msiName" | Set-Content -LiteralPath "$msiPath.sha256" -Encoding ascii

Write-Host "MSI created: $msiPath"
Write-Host "SHA256 created: $msiPath.sha256"
