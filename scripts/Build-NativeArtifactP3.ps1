param(
    [string]$PluginRepository = (Join-Path $PSScriptRoot '../FolderRewind-Plugin-Minecraft'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/native-integration/build')
)
$ErrorActionPreference = 'Stop'
$hostRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginRoot = (Resolve-Path -LiteralPath $PluginRepository).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
# Kept as a command-name compatibility entry point. Integration uses the committed
# exact contract and source mapping; it never generates an ad-hoc experimental feed.
& (Join-Path $pluginRoot 'tools/Verify-Contract.ps1')
if (-not $?) { throw 'Committed contract verification failed.' }
$dirty = & git -C $pluginRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Plugin source must be a clean committed checkout.' }
$provenance = Get-Content (Join-Path $pluginRoot 'contracts/source-manifest.json') -Raw | ConvertFrom-Json
function Read-GitBlob([string]$Repository, [string]$Path) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach($argument in @('-C', $Repository, 'show', ('HEAD:' + $Path))) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $memory = [IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $errorText = $process.StandardError.ReadToEnd(); $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw $errorText }
        return ,$memory.ToArray()
    } finally { $process.Dispose(); $memory.Dispose() }
}
if ((& git -C $hostRoot status --porcelain -- FolderRewind.Plugin.Abstractions)) { throw 'Host contract has uncommitted changes.' }
foreach($source in $provenance.sources) {
    $bytes = Read-GitBlob $hostRoot $source.path
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bytes)) -ine $source.sha256) {
        throw ('Host contract differs from the frozen package: ' + $source.path)
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
[xml]$projectXml = Get-Content (Join-Path $pluginRoot 'MineRewind/MineRewind.csproj') -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$package = Join-Path $output ("MineRewind-$version.frplugin")
$project = Join-Path $pluginRoot 'MineRewind/MineRewind.csproj'
& dotnet restore $project --configfile (Join-Path $pluginRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed.' }
& dotnet build $project -c Release --no-restore -t:PackFrPlugin "-p:FrPluginOutput=$package"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build or clean-staging packaging failed.' }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $expected = @('MineRewind.dll','MineRewind.World.dll','fNbt.dll','K4os.Compression.LZ4.dll','System.IO.Hashing.dll','preview-dependencies.NOTICE.txt','manifest.json','settings.schema.json','contract-source.json')
    $actual = @($archive.Entries.FullName)
    if ($actual.Count -ne $expected.Count -or (Compare-Object $expected $actual)) { throw 'Unexpected plugin payload inventory.' }
} finally { $archive.Dispose() }
@{ ApiVersion=$provenance.version; ApiSourcesSha256=$provenance.sourceDigestSha256;
   PluginSourceCommit=(& git -C $pluginRoot rev-parse HEAD); HostSourceCommit=(& git -C $hostRoot rev-parse HEAD);
   Package=$package; PackageSha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'build.json')
