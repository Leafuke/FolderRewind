[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SevenZip = '7z.exe'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing fixtures are never replaced.' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind182Producer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$files = @(
    'FolderRewind/Models/BackupMetadata.cs',
    'FolderRewind/Services/BackupService.Metadata.cs',
    'FolderRewind/Services/BackupMetadataStoreService.cs',
    'FolderRewind/Services/BackupMetadataDeletionPolicy.cs',
    'FolderRewind/Services/BackupStoragePathService.cs'
)
$revision = (& git -C $repo rev-parse 'v1.8.2^{commit}').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Fetch the official v1.8.2 tag before generating fixtures.' }
$provenance = foreach ($file in $files) {
    $content = & git -C $repo show "v1.8.2:$file"
    if ($LASTEXITCODE -ne 0) { throw "Missing released source: $file" }
    $content | Set-Content -LiteralPath (Join-Path $scratch ([IO.Path]::GetFileName($file))) -Encoding utf8
    [ordered]@{ path = $file; gitBlob = (& git -C $repo rev-parse "v1.8.2:$file").Trim() }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Legacy182FixtureProducer.cs') -Destination (Join-Path $scratch 'Program.cs')
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $scratch 'Producer.csproj') -Encoding utf8
$executable = (Get-Command $SevenZip -ErrorAction Stop).Source
& dotnet run --project (Join-Path $scratch 'Producer.csproj') -c Release -- $output $executable
if ($LASTEXITCODE -ne 0) { throw "Released-source producer failed. Scratch retained at $scratch" }
[ordered]@{
    origin = 'v1.8.2 tagged metadata writer and change detection, invoked by a console harness; not an installed-app UI acceptance run'
    commit = $revision
    sources = @($provenance)
    sevenZip = (& $executable i | Select-Object -First 4) -join "`n"
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    installerAcceptance = 'pending manual verification'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
Write-Output $output
