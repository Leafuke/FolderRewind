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
$source = Get-Content -Raw -LiteralPath (Join-Path $repo 'FolderRewind/Services/Plugins/V3/PluginV3OfflineUpgradeService.cs')
$bundleName = [regex]::Match($source, 'BundledFileName\s*=\s*"([^"]+)"').Groups[1].Value
$expectedHash = [regex]::Match($source, 'BundledSha256\s*=\s*"([a-fA-F0-9]{64})"').Groups[1].Value
if (-not $bundleName -or -not $expectedHash) { throw 'Cannot resolve bundled plugin contract.' }
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$plugin = Join-Path $payload "Assets/Plugins/$bundleName"
if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) { throw "Bundled plugin missing from payload: $plugin" }
$actualHash = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $expectedHash) { throw 'Bundled plugin bytes differ from the host migration hash.' }
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
