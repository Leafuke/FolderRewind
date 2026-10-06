[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateSet('BuildOutput','UnpackedReleasePackage')][string]$EvidenceKind = 'BuildOutput',
    [string]$ReleaseAssetsDirectory,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$packages = @(Get-ChildItem -LiteralPath (Join-Path $payload 'Assets/Plugins') -Filter 'MineRewind-*.frplugin' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one bundled MineRewind package.' }
$plugin = $packages[0].FullName
$bundleName = $packages[0].Name
$fields = (Get-Content -LiteralPath ($plugin + '.sha256') -Raw).Trim() -split '\s+'
if ($fields.Count -ne 2 -or $fields[1].TrimStart('*') -cne $bundleName) { throw 'Bundled checksum filename differs from the package.' }
$expectedHash = $fields[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $expectedHash) { throw 'Bundled plugin bytes differ from its published checksum.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($plugin)
try {
    $entry = $zip.GetEntry('manifest.json')
    if (-not $entry) { throw 'Bundled plugin has no manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.pluginId -ne 'com.folderrewind.minerewind' -or $manifest.manifestVersion -ne 3 -or $manifest.pluginApi.major -ne 3) { throw 'Wrong plugin identity or API.' }
    if ($bundleName -ne "MineRewind-$($manifest.version).frplugin") { throw 'Plugin filename/version mismatch.' }
    if (-not $zip.GetEntry($manifest.entryAssembly)) { throw 'Plugin entry assembly is missing.' }
} finally { $zip.Dispose() }
if ($ReleaseAssetsDirectory) {
    if (-not $Version) { throw 'Version is required when validating final release assets.' }
    & (Join-Path $repo '.github/scripts/Get-SetupReleaseAssets.ps1') -Directory $ReleaseAssetsDirectory -Version $Version | Out-Null
}
$report = [ordered]@{
    checkedUtc = [DateTime]::UtcNow.ToString('O'); evidenceKind = $EvidenceKind
    sourceRevision = (& git -C $repo rev-parse HEAD).Trim(); payloadDirectory = $payload
    bundledPlugin = $bundleName; pluginVersion = $manifest.version; pluginSha256 = $actualHash
    downloadFrom182 = 'Manual download of Setup EXE and in-place upgrade; the 1.8.2 MSI updater cannot select Setup EXE'
    dataMigration = 'Same-channel startup takeover; no old cloud archive/configuration migration'
    manualAcceptance = 'Required separately; this read-only check does not execute an installer or plugin'
}
$reportTarget = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($reportTarget)) -Force | Out-Null
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportTarget -Encoding utf8
$reportTarget
