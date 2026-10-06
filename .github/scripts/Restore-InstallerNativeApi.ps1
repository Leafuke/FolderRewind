[CmdletBinding()]
param([string]$OutputDirectory = "$PSScriptRoot\..\..\artifacts\installer-native-api\7.0.0")
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$hashes = @{
    'WixToolset.BootstrapperApplicationApi' = 'exMTJnplE3VsXvoJfoOf/6QaG3rsfXxc3BDj1eOiymdGVtxlh0q4R7ceZnn7Xo8A/EYZgmQJKygaGkMKwlqFQQ=='
    'WixToolset.WixStandardBootstrapperApplicationFunctionApi' = 'boM/Sj2v/Q3JCUBct+JeKNmCVxjmKBJhCThk+rsnMcPl8EVX7NVUIYW8Msxs9gPgDmewjVDtlW634VzcLLficQ=='
    'WixToolset.DUtil' = 'eBBrfNIbOsc8oI4Cy6f9lc26ndbyjWzNl7MTFx0CQao/BKMoXs1y+VKFCHVFaMoURwlZ2op6tugvRMPu5bte2A=='
}
foreach ($id in @('WixToolset.BootstrapperApplicationApi','WixToolset.WixStandardBootstrapperApplicationFunctionApi','WixToolset.DUtil')) {
    $destination = Join-Path $root $id
    if ((Test-Path -LiteralPath (Join-Path $destination 'lib\native\include')) -or (Test-Path -LiteralPath (Join-Path $destination 'build\native\include'))) { continue }
    $name = $id.ToLowerInvariant()
    $uri = "https://api.nuget.org/v3-flatcontainer/$name/7.0.0/$name.7.0.0.nupkg"
    $archivePath = Join-Path $root "$name.zip"
    Invoke-WebRequest -Uri $uri -OutFile $archivePath
    $expected = $hashes[$id]
    $actual = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($archivePath)))
    if ($actual -cne $expected) { throw "Native API package checksum mismatch: $id" }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid native package entry: $($entry.FullName)" }
            if ($entry.FullName.EndsWith('/')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $archive.Dispose() }
}
Write-Output $root
