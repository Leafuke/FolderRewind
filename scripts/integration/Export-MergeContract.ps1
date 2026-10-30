param([string]$PluginRoot = (Join-Path $PSScriptRoot '../../FolderRewind-Plugin-Minecraft'))
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($PluginRoot) { $PluginRoot = Resolve-FolderRewindPath -Path $PluginRoot -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$hostRoot = Get-FolderRewindRoot -Start $PSScriptRoot
$PluginRoot = (Resolve-Path -LiteralPath $PluginRoot).Path
$sourceRoot = Join-Path $hostRoot 'src/FolderRewind.Plugin.Abstractions'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.md' } | Sort-Object Name | ForEach-Object {
    [ordered]@{ path = 'src/FolderRewind.Plugin.Abstractions/' + $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$sourceJson = ConvertTo-Json -InputObject $sources -Depth 5 -Compress
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceJson))).ToLowerInvariant()
[xml]$apiProject = Get-Content -LiteralPath (Join-Path $sourceRoot 'FolderRewind.Plugin.Abstractions.csproj')
$baseVersion = [string]$apiProject.Project.PropertyGroup.Version
$apiVersion = (($baseVersion -split '\.')[0..1] -join '.')
$version = $baseVersion + '-preview.' + $digest.Substring(0, 16)
$feed = Join-Path $PluginRoot 'contracts/feed'
dotnet pack (Join-Path $sourceRoot 'FolderRewind.Plugin.Abstractions.csproj') -c Release -o $feed "-p:PackageVersion=$version"
if ($LASTEXITCODE) { throw 'Contract pack failed.' }
$package = Join-Path $feed "FolderRewind.Plugin.Abstractions.$version.nupkg"
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $input = $archive.GetEntry('lib/net10.0/FolderRewind.Plugin.Abstractions.dll').Open()
    try { $assemblyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant() }
    finally { $input.Dispose() }
} finally { $archive.Dispose() }
$provenance = [ordered]@{
    packageId = 'FolderRewind.Plugin.Abstractions'; version = $version; apiVersion = $apiVersion; assemblyVersion = '3.0.0.0'
    sourceCommit = (git -C $hostRoot rev-parse HEAD).Trim(); sourceState = 'working-tree-snapshot'
    sourceDigestSha256 = $digest
    sourceDigestEncoding = 'SHA256 of UTF-8 compact JSON entries ordered by filename; hashes cover working-tree file bytes (sourceCommit is the base, not the snapshot)'
    sources = $sources; packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant(); assemblySha256 = $assemblyHash
}
[IO.File]::WriteAllText((Join-Path $PluginRoot 'contracts/source-manifest.json'), ($provenance | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
$project = Join-Path $PluginRoot 'MineRewind/MineRewind.csproj'
$xml = [IO.File]::ReadAllText($project)
$xml = [regex]::Replace($xml, '(<PluginApiPackageVersion[^>]*>)[^<]+', { param($m) $m.Groups[1].Value + $version })
[IO.File]::WriteAllText($project, $xml, [Text.UTF8Encoding]::new($false))
Write-Output "Exported exact working-tree contract $version ($digest)."

} finally { Pop-Location }
