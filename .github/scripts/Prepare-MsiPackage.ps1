[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipBundle,
    [switch]$TestIdentity,
    [switch]$EnableFaultInjection,
    [ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string]$TestIdentitySuffix = [guid]::NewGuid().ToString('N')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
if ($EnableFaultInjection -and -not $TestIdentity) { throw 'Fault injection is restricted to the isolated validation identity.' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'MSI version must have four parts with revision 0.' }
$parsed = [version]$Version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) { throw 'MSI version exceeds Windows Installer version bounds (255.255.65535.0).' }
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$arch = $Platform.ToLowerInvariant()
$work = Join-Path $root "artifacts\installer-build\$arch\$Version"
if ($TestIdentity) { $work = Join-Path $work "validation\$TestIdentitySuffix"; $SkipBundle = $true }
$packageId = if ($TestIdentity) { "Leafuke.FolderRewind.Msi.Validation.$TestIdentitySuffix" } else { 'Leafuke.FolderRewind.Msi' }
$packageName = if ($TestIdentity) { "FolderRewind MSI Validation $TestIdentitySuffix" } else { 'FolderRewind' }
$registryKey = if ($TestIdentity) { "Software\Leafuke\FolderRewind.Msi.Validation.$TestIdentitySuffix" } else { 'Software\Leafuke\FolderRewind' }
$upgradeCode = if ($TestIdentity) { Get-ValidationGuid "$TestIdentitySuffix/msi" } else { '{6BCE6B1A-ADA1-5399-91BD-238EBF552F6E}' }
$bundleFamily = if ($TestIdentity) { Get-ValidationGuid "$TestIdentitySuffix/bundle" } else { '{A941C3B8-5D6F-4C0E-B62C-32F5FF9D7E8B}' }
$native = Join-Path $root 'artifacts\installer-native'
if ($EnableFaultInjection) { $native = Join-Path $work 'native-test' }
New-Item -ItemType Directory -Path $work,$output -Force | Out-Null
& "$PSScriptRoot\Normalize-MsiResourcePaths.ps1" -Directory $publish
& "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $publish -ResultPath (Join-Path $work 'resources.json')
& "$PSScriptRoot\Build-InstallerNative.ps1" -OutputDirectory $native -EnableFaultInjection:$EnableFaultInjection | Out-Host
$components = Join-Path $work 'ApplicationFiles.wxs'
& "$PSScriptRoot\Generate-MsiFileComponents.ps1" -PublishDirectory $publish -OutputPath $components -Platform $arch -RegistryKey $registryKey
# Stable across rebuilds, distinct between versions and architectures. Both
# language databases use this exact identity before creating their transform.
$bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$packageId/$Version/$arch"))
$productCode = '{' + [guid]::new([byte[]]$bytes[0..15]).ToString().ToUpperInvariant() + '}'
$msiName = "FolderRewind_${Version}_${arch}"
foreach ($culture in @('en-US','zh-CN')) {
    $language = if ($culture -eq 'zh-CN') { 2052 } else { 1033 }
    $faults = if ($EnableFaultInjection) { 1 } else { 0 }
    $directory = Join-Path $work $culture
    dotnet build $ProjectPath -t:Rebuild -c Release "-p:InstallerPlatform=$arch" "-p:ProductVersion=$Version" "-p:ProductCode=$productCode" `
        "-p:PublishDir=$publish" "-p:GeneratedComponents=$components" "-p:NativeOutputDirectory=$native" `
        "-p:Cultures=$culture" "-p:PackageLanguage=$language" "-p:OutputPath=$directory\" "-p:OutputName=$msiName" `
        "-p:BaseIntermediateOutputPath=$directory\obj\" `
        "-p:PackageIdentifier=$packageId" "-p:PackageDisplayName=$packageName" "-p:InstallerRegistryKey=$registryKey" "-p:UpgradeCode=$upgradeCode" "-p:EnableFaultInjection=$faults" "-p:ValidationId=$(if ($TestIdentity) { $TestIdentitySuffix })" "-p:ExpectedBundleFamily=$bundleFamily" /warnaserror
    if ($LASTEXITCODE) { throw "$culture MSI build failed: $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $directory "$culture\$msiName.msi") -Destination (Join-Path $work "$culture.msi") -Force
}
$english = Join-Path $work 'en-US.msi'
$chinese = Join-Path $work 'zh-CN.msi'
$transform = Join-Path $work 'zh-CN.mst'
$wix = Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk\7.0.0\tools\net472\x64\wix.exe'
& $wix msi transform -acceptEula wix7 -t language $english $chinese -out $transform
if ($LASTEXITCODE) { throw "Language transform build failed: $LASTEXITCODE" }
$msi = Join-Path $output "$msiName.msi"
Copy-Item -LiteralPath $english -Destination $msi -Force
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msi, 1)
try {
    $view = $db.OpenView('INSERT INTO `_Storages` (`Name`, `Data`) VALUES (?, ?)')
    $record = $installer.CreateRecord(2)
    $record.StringData(1) = 'zh-CN.mst'; $record.SetStream(2, $transform)
    $view.Execute($record); $view.Close(); $db.Commit()
} finally {
    foreach ($item in @($record,$view)) { if ($item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null } }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
[GC]::Collect(); [GC]::WaitForPendingFinalizers()
# Validate the final database after embedding the transform as well.
$validationWork = Join-Path $work ('embedded-ice-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $validationWork | Out-Null
& $wix msi validate -acceptEula wix7 -intermediateFolder $validationWork -sice ICE57 -sice ICE105 $msi
if ($LASTEXITCODE) { throw "Embedded-transform MSI validation failed: $LASTEXITCODE" }
if (-not $SkipBundle) {
    dotnet build (Join-Path $root 'Installer\Bootstrapper\FolderRewind.Bootstrapper.wixproj') -t:Rebuild -c Release -p:Platform=x86 `
        "-p:ProductVersion=$Version" "-p:PayloadArchitecture=$arch" "-p:MsiPath=$msi" "-p:ProductCode=$productCode" `
        "-p:BaseIntermediateOutputPath=$work\bundle-obj\" `
        "-p:NativeOutputDirectory=$native" "-p:OutputPath=$output\" /warnaserror
    if ($LASTEXITCODE) { throw "Setup build failed: $LASTEXITCODE" }
}
foreach ($file in Get-ChildItem -LiteralPath $output -File | Where-Object Extension -In @('.msi','.exe')) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)" | Set-Content -LiteralPath ($file.FullName + '.sha256') -Encoding ascii
}
@{ version=$Version; architecture=$arch; productCode=$productCode; upgradeCode=$upgradeCode; embeddedTransform='zh-CN.mst'; testIdentity=[bool]$TestIdentity } |
    ConvertTo-Json | Set-Content (Join-Path $work 'identity.json') -Encoding utf8
Write-Host "Created MSI and Setup release assets in $output"
