[CmdletBinding()]
param([string]$RepositoryRoot = "$PSScriptRoot/../..")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'FolderRewind/Package.appxmanifest') -Raw
[xml]$project = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'FolderRewind/FolderRewind.csproj') -Raw
$version = [string]$manifest.Package.Identity.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Manifest version must use major.minor.build.0.' }
$number = [version]$version
if ($number.Major -gt 255 -or $number.Minor -gt 255 -or $number.Build -gt 65535) { throw 'Version exceeds MSI bounds (255.255.65535.0).' }
$projectVersions = @($project.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object InnerText)
if ($projectVersions.Count -ne 1 -or $projectVersions[0] -cne $version) { throw 'Application project version differs from Package.appxmanifest.' }
$shortVersion = "$($number.Major).$($number.Minor).$($number.Build)"
$notes = ''
foreach ($relative in @("docs/release/FolderRewind-$shortVersion-notes.md", ".github/release-notes/v$shortVersion.md")) {
    if (Test-Path -LiteralPath (Join-Path $RepositoryRoot $relative) -PathType Leaf) { $notes = $relative; break }
}
[pscustomobject]@{ version=$version; tag="v$shortVersion"; title="FolderRewind $shortVersion"; notesPath=$notes }
