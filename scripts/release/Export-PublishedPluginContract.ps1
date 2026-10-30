[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidatePackage,
    [string]$PluginRoot = "$PSScriptRoot/../../FolderRewind-Plugin-Minecraft"
)
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$hostRoot = Get-FolderRewindRoot -Start $PSScriptRoot
$CandidatePackage = Resolve-FolderRewindPath -Path $CandidatePackage -Root $hostRoot
$PluginRoot = Resolve-FolderRewindPath -Path $PluginRoot -Root $hostRoot
Push-Location -LiteralPath $hostRoot
try {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $apiRelative = 'src/FolderRewind.Plugin.Abstractions'
    $apiRoot = Join-Path $hostRoot $apiRelative
    $pluginProject = Join-Path $PluginRoot 'MineRewind/MineRewind.csproj'
    if (-not (Test-Path -LiteralPath $pluginProject -PathType Leaf)) { throw 'MineRewind project was not found.' }
    $dirty = git -C $hostRoot status --porcelain --untracked-files=normal -- $apiRelative
    if ($LASTEXITCODE -or $dirty) { throw 'Commit the API sources before freezing a published contract.' }
    $sourceCommit = (git -C $hostRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE) { throw 'Cannot resolve the Host source commit.' }

    function Read-ApiPackage([string]$Path) {
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entries = @($archive.Entries.FullName)
            foreach ($required in @('FolderRewind.Plugin.Abstractions.nuspec', 'README.md',
                'lib/net10.0/FolderRewind.Plugin.Abstractions.dll', 'lib/net10.0/FolderRewind.Plugin.Abstractions.xml')) {
                if ($entries -cnotcontains $required) { throw "API package is missing $required" }
            }
            if (@($entries | Where-Object { $_ -like '*.dll' -and $_ -cne 'lib/net10.0/FolderRewind.Plugin.Abstractions.dll' }).Count) {
                throw 'API package contains an unexpected assembly.'
            }
            $reader = [IO.StreamReader]::new($archive.GetEntry('FolderRewind.Plugin.Abstractions.nuspec').Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.SelectSingleNode('//*[local-name()="metadata"]')
            if ([string]$metadata.id -cne 'FolderRewind.Plugin.Abstractions' -or
                [string]$metadata.version -cnotmatch '^3\.\d+\.\d+$' -or
                $metadata.SelectNodes('.//*[local-name()="dependency"]').Count -ne 0) {
                throw 'Expected a stable, dependency-free Plugin API 3 package.'
            }
            $assembly = $archive.GetEntry('lib/net10.0/FolderRewind.Plugin.Abstractions.dll').Open()
            try { $assemblyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($assembly)).ToLowerInvariant() }
            finally { $assembly.Dispose() }
            [pscustomobject]@{ version = [string]$metadata.version; sourceCommit = [string]$metadata.repository.commit; assemblySha256 = $assemblyHash }
        } finally { $archive.Dispose() }
    }

    $candidate = Read-ApiPackage $CandidatePackage
    [xml]$apiProject = Get-Content -LiteralPath (Join-Path $apiRoot 'FolderRewind.Plugin.Abstractions.csproj') -Raw
    if ($candidate.version -cne [string]$apiProject.Project.PropertyGroup.Version -or $candidate.sourceCommit -cne $sourceCommit) {
        throw 'Candidate package version/source commit differs from this Host checkout.'
    }
    $version = $candidate.version
    $apiVersion = (($version -split '\.')[0..1] -join '.')
    $url = "https://api.nuget.org/v3-flatcontainer/folderrewind.plugin.abstractions/$version/folderrewind.plugin.abstractions.$version.nupkg"
    $downloadRoot = Join-Path $hostRoot "artifacts/published-contracts/$([guid]::NewGuid().ToString('N'))"
    [void][IO.Directory]::CreateDirectory($downloadRoot)
    $fileName = "FolderRewind.Plugin.Abstractions.$version.nupkg"
    $download = Join-Path $downloadRoot $fileName
    Invoke-WebRequest -Uri $url -OutFile $download
    $published = Read-ApiPackage $download
    if ($published.version -cne $version -or $published.sourceCommit -cne $sourceCommit -or
        $published.assemblySha256 -cne $candidate.assemblySha256) {
        throw 'Public package differs from the tested candidate contract.'
    }
    # Registry signing can change the ZIP hash; the contract DLL must remain identical.
    $archive = [IO.Compression.ZipFile]::OpenRead($download)
    try {
        $assemblyPath = Join-Path $downloadRoot 'FolderRewind.Plugin.Abstractions.dll'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('lib/net10.0/FolderRewind.Plugin.Abstractions.dll'), $assemblyPath)
        if ([Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString() -cne '3.0.0.0') { throw 'API 3 assembly identity changed.' }
    } finally { $archive.Dispose() }
    $sources = @(Get-ChildItem -LiteralPath $apiRoot -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.md' } | Sort-Object Name | ForEach-Object {
        [ordered]@{ path = "$apiRelative/$($_.Name)"; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $sourceJson = ConvertTo-Json -InputObject $sources -Depth 5 -Compress
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceJson))).ToLowerInvariant()
    $packageHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
    $feed = Join-Path $PluginRoot 'contracts/feed'
    [void][IO.Directory]::CreateDirectory($feed)
    $destination = Join-Path $feed $fileName
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -ine $packageHash) {
        throw 'A different package already occupies this immutable SDK version.'
    }
    $provenance = [ordered]@{
        packageId = 'FolderRewind.Plugin.Abstractions'; version = $version; apiVersion = $apiVersion; assemblyVersion = '3.0.0.0'
        sourceCommit = $sourceCommit; sourceState = 'committed'; sourceDigestSha256 = $digest
        sourceDigestEncoding = 'SHA256 of UTF-8 compact JSON entries ordered by filename; hashes cover committed source file bytes'
        sources = $sources; packageSource = $url; packageSha256 = $packageHash; assemblySha256 = $published.assemblySha256
    }
    $projectText = [IO.File]::ReadAllText($pluginProject)
    if ([regex]::Matches($projectText, '<PluginApiPackageVersion\b').Count -ne 1) { throw 'Expected one plugin SDK version declaration.' }
    $projectText = [regex]::Replace($projectText, '(<PluginApiPackageVersion[^>]*>)[^<]+', { param($match) $match.Groups[1].Value + $version })
    Copy-Item -LiteralPath $download -Destination $destination -Force
    [IO.File]::WriteAllText((Join-Path $PluginRoot 'contracts/source-manifest.json'), ($provenance | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($pluginProject, $projectText, [Text.UTF8Encoding]::new($false))
    Write-Output "Frozen public API $version; package SHA256 $packageHash; assembly SHA256 $($published.assemblySha256)."
} finally { Pop-Location }
