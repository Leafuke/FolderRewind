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
    "FolderRewind.dll",
    "FolderRewind.deps.json",
    "FolderRewind.runtimeconfig.json",
    "FolderRewind.pri",
    "Microsoft.UI.Xaml.dll",
    "Microsoft.UI.Xaml.Controls.pri",
    "Microsoft.WindowsAppRuntime.dll",
    "7za.exe",
    "Assets\logo.ico"
)

foreach ($requiredFile in $requiredFiles) {
    $path = Join-Path $PublishDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required MSI payload file was not found: $path"
    }
}

$debugSymbols = Get-ChildItem -LiteralPath $PublishDirectory -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue
if ($debugSymbols)
{
    $symbolList = ($debugSymbols.FullName -join [Environment]::NewLine)
    throw "MSI payload contains development debug symbols that must not be shipped:$([Environment]::NewLine)$symbolList"
}

# A plain dotnet publish can replace WinUI's dependency-merged PRI with an
# app-only PRI. That package installs successfully but crashes before creating
# a window because XamlControlsResources cannot resolve themeresources.xaml.
$buildToolsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
    '.nuget\packages\microsoft.windows.sdk.buildtools'
$makePri = Get-ChildItem -LiteralPath $buildToolsRoot -Filter 'makepri.exe' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]makepri\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if ($null -eq $makePri)
{
    throw "makepri.exe was not found below $buildToolsRoot. Restore the app project before building the MSI."
}

$priPath = Join-Path $PublishDirectory 'FolderRewind.pri'
$priDumpPath = Join-Path ([IO.Path]::GetTempPath()) ("FolderRewind-pri-{0}.xml" -f [Guid]::NewGuid().ToString('N'))
try
{
    & $makePri.FullName dump /if $priPath /of $priDumpPath /o | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw "makepri.exe could not inspect the MSI resource index (exit code $LASTEXITCODE)."
    }

    $priDump = Get-Content -LiteralPath $priDumpPath -Raw
    if ($priDump -notmatch '<ResourceMapSubtree name="Microsoft\.UI\.Xaml"' -or
        $priDump -notmatch '<NamedResource name="themeresources\.xbf"')
    {
        throw 'FolderRewind.pri is not dependency-merged; Microsoft.UI.Xaml theme resources are missing.'
    }
}
finally
{
    Remove-Item -LiteralPath $priDumpPath -Force -ErrorAction SilentlyContinue
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
