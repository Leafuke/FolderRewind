param(
    [string]$PluginRepository = (Join-Path $PSScriptRoot '../FolderRewind-Plugin-Minecraft'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/incremental-p3/build')
)
$ErrorActionPreference = 'Stop'
$p3HostRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$p3PluginRoot = (Resolve-Path -LiteralPath $PluginRepository).Path
$p3Output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $p3Output -Force | Out-Null
$p3Feed = Join-Path $p3Output 'feed'
New-Item -ItemType Directory -Path $p3Feed -Force | Out-Null
$p3Api = Join-Path $p3HostRoot 'FolderRewind.Plugin.Abstractions/FolderRewind.Plugin.Abstractions.csproj'
# Hash the contract sources, not a mutable global package cache or an assumed public release.
$p3Contract = (Get-ChildItem -LiteralPath (Split-Path $p3Api) -File | Where-Object Extension -in '.cs','.csproj' |
    Sort-Object Name | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
$p3Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($p3Contract))).ToLowerInvariant()
$p3Version = '3.10.0-p3.b' + $p3Hash.Substring(0, 16)
$p3Package = Join-Path $p3Feed "FolderRewind.Plugin.Abstractions.$p3Version.nupkg"
if (!(Test-Path -LiteralPath $p3Package)) {
    & dotnet pack $p3Api -c Release "-p:PackageVersion=$p3Version" -o $p3Feed
    if ($LASTEXITCODE -ne 0) { throw 'API pack failed.' }
}
$p3Nuget = Join-Path $p3Output 'NuGet.Config'
$p3EscapedFeed = [Security.SecurityElement]::Escape($p3Feed)
$p3EscapedCache = [Security.SecurityElement]::Escape((Join-Path $p3Output 'packages'))
@"
<configuration><packageSources><clear/><add key="p3" value="$p3EscapedFeed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><config><add key="globalPackagesFolder" value="$p3EscapedCache"/></config></configuration>
"@ | Set-Content -LiteralPath $p3Nuget
$p3Project = Join-Path $p3PluginRoot 'MineRewind/MineRewind.csproj'
& dotnet restore $p3Project --configfile $p3Nuget "-p:PluginApiPackageVersion=$p3Version"
if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed.' }
& dotnet build $p3Project -c Release --no-restore "-p:PluginApiPackageVersion=$p3Version"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$p3PluginOutput = Join-Path $p3PluginRoot 'MineRewind/bin/Release/net10.0'
$p3Frplugin = Join-Path $p3Output 'MineRewind-incremental.frplugin'
& dotnet run --project (Join-Path $p3PluginRoot 'tools/MineRewind.Pack/MineRewind.Pack.csproj') -c Release -- $p3PluginOutput $p3Frplugin
if ($LASTEXITCODE -ne 0) { throw 'Plugin packaging failed.' }
$p3Archive = [IO.Compression.ZipFile]::OpenRead($p3Frplugin)
try {
    if ($p3Archive.Entries.Name -contains 'FolderRewind.Plugin.Abstractions.dll') { throw 'Host contract DLL must not be bundled.' }
} finally { $p3Archive.Dispose() }
[pscustomobject]@{ ApiVersion = $p3Version; ApiSourcesSha256 = $p3Hash; Package = $p3Frplugin;
    PackageSha256 = (Get-FileHash -LiteralPath $p3Frplugin -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $p3Output 'build.json')
Get-Content -LiteralPath (Join-Path $p3Output 'build.json')
