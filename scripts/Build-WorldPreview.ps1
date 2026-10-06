param([switch]$SkipHostBuild, [switch]$SkipTests, [switch]$FullHostTests)
$ErrorActionPreference = 'Stop'
$previewRoot = Split-Path $PSScriptRoot -Parent
$pluginRoot = Join-Path $previewRoot 'FolderRewind-Plugin-Minecraft'
$apiRoot = Join-Path $previewRoot 'FolderRewind.Plugin.Abstractions'
# Content identity, not a reused local version, isolates NuGet's immutable package cache.
$apiFiles = Get-ChildItem -LiteralPath $apiRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.md' } | Sort-Object Name
$identity = ($apiFiles | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
$identityBytes = [Text.Encoding]::UTF8.GetBytes($identity)
$identityHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identityBytes)).ToLowerInvariant()
$apiVersion = '3.9.0-preview.b' + $identityHash.Substring(0, 16)
$feed = Join-Path $previewRoot 'artifacts/preview39-feed'
$packages = Join-Path $previewRoot 'artifacts/preview39-packages'
New-Item -ItemType Directory -Force -Path $feed, $packages | Out-Null
function Invoke-PreviewDotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
$apiProject = Join-Path $apiRoot 'FolderRewind.Plugin.Abstractions.csproj'
if (-not (Test-Path -LiteralPath (Join-Path $feed "FolderRewind.Plugin.Abstractions.$apiVersion.nupkg"))) {
    Invoke-PreviewDotnet pack $apiProject -c Release --output $feed "-p:PackageVersion=$apiVersion"
}
$testProject = Join-Path $pluginRoot 'MineRewind.Tests/MineRewind.Tests.csproj'
$feedConfig = Join-Path $feed 'NuGet.Config'
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath $feedConfig -Encoding utf8 -Value "<configuration><packageSources><clear/><add key=`"preview`" value=`"$escapedFeed`"/><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>"
Invoke-PreviewDotnet restore $testProject --configfile $feedConfig --packages $packages "-p:PreviewApiPackageVersion=$apiVersion"
Invoke-PreviewDotnet build $testProject --no-restore -c Release "-p:PreviewApiPackageVersion=$apiVersion"
if (-not $SkipTests) {
    Invoke-PreviewDotnet test $testProject --no-restore --no-build -c Release "-p:PreviewApiPackageVersion=$apiVersion"
    foreach ($name in 'FolderRewind.Plugin.Abstractions.Tests', 'FolderRewind.Plugin.Runtime.Tests') {
        Invoke-PreviewDotnet test (Join-Path $previewRoot "$name/$name.csproj") -c Release
    }
    $hostTest = Join-Path $previewRoot 'FolderRewind.Tests/FolderRewind.Tests.csproj'
    if ($FullHostTests) { Invoke-PreviewDotnet test $hostTest -c Release }
    else { Invoke-PreviewDotnet test $hostTest -c Release --filter 'FullyQualifiedName~PreviewRepairTests|FullyQualifiedName~SpatialPreviewViewportTests|FullyQualifiedName~LocalizationQualityTests|FullyQualifiedName~LanguageSettingPolicyTests|FullyQualifiedName~AccessibilityMarkupTests' }
}
$pluginVersion = (Get-Content -LiteralPath (Join-Path $pluginRoot 'MineRewind/manifest.json') -Raw | ConvertFrom-Json).version
$payload = Join-Path $pluginRoot 'MineRewind/bin/Release/net10.0'
$package = Join-Path $previewRoot "artifacts/MineRewind-$pluginVersion.frplugin"
Invoke-PreviewDotnet run --project (Join-Path $pluginRoot 'tools/MineRewind.Pack/MineRewind.Pack.csproj') -c Release -- $payload $package
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    if ($zip.Entries.FullName -match '(^|/)FolderRewind.Plugin.Abstractions.dll$') { throw 'Bundled host API DLL is forbidden.' }
    if ($zip.Entries.FullName -notcontains 'MineRewind.World.dll') { throw 'Missing world domain assembly.' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.version -ne $pluginVersion -or $manifest.pluginApi.minor -ne 9) { throw 'Unexpected preview manifest.' }
} finally { $zip.Dispose() }
$bundle = Join-Path $previewRoot 'FolderRewind/Assets/Plugins'
Copy-Item -LiteralPath $package -Destination $bundle -Force
Copy-Item -LiteralPath "$package.sha256" -Destination $bundle -Force
# Replace only the explicitly superseded bundled package, never the installed user plugin.
foreach ($oldName in 'MineRewind-1.9.7.frplugin', 'MineRewind-1.9.7.frplugin.sha256') {
    $oldPath = Join-Path $bundle $oldName
    if (Test-Path -LiteralPath $oldPath) { Remove-Item -LiteralPath $oldPath }
}
if (-not $SkipHostBuild) {
    Invoke-PreviewDotnet build (Join-Path $previewRoot 'FolderRewind/FolderRewind.csproj') -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
}
Write-Output "Preview API: $apiVersion; MineRewind: $pluginVersion; bundle hash: $((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash)"
