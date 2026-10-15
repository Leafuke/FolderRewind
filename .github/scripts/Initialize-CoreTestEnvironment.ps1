[CmdletBinding()]
param([string]$ToolsDirectory = "$PSScriptRoot/../../artifacts/test-tools")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$tools = [IO.Path]::GetFullPath($ToolsDirectory)

$sevenZip = Join-Path $root '7za.exe'
if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) { $sevenZip = (Get-Command 7z.exe -ErrorAction Stop).Source }
& $sevenZip i | Out-Null
if ($LASTEXITCODE) { throw 'Real 7-Zip is required for archive integration tests.' }

# Use the same pinned Windows x64 asset and SHA-256 as the application installer.
$catalog = Get-Content -LiteralPath (Join-Path $root 'FolderRewind/Services/OnboardingToolAssets.cs') -Raw
$assets = [regex]::Matches($catalog, '\("rclone", Architecture\.X64\) => \("(?<version>v[\d.]+)", "rclone/rclone", "(?<file>rclone-[^"]+\.zip)", "(?<hash>[a-f0-9]{64})"\)')
if ($assets.Count -ne 1) { throw 'Expected exactly one pinned Windows x64 rclone asset.' }
$version = $assets[0].Groups['version'].Value
$fileName = $assets[0].Groups['file'].Value
$expectedHash = $assets[0].Groups['hash'].Value
$destination = Join-Path $tools "rclone/$version"
[void][IO.Directory]::CreateDirectory($destination)
$archive = Join-Path $destination $fileName
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Invoke-WebRequest "https://github.com/rclone/rclone/releases/download/$version/$fileName" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedHash) {
    throw 'Pinned rclone archive SHA-256 mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
$rclone = Join-Path $destination ([IO.Path]::GetFileNameWithoutExtension($fileName) + '/rclone.exe')
if (-not (Test-Path -LiteralPath $rclone -PathType Leaf)) { throw 'Pinned rclone executable is missing.' }
$rcloneVersion = @(& $rclone version)
if ($LASTEXITCODE -or $rcloneVersion.Count -eq 0 -or $rcloneVersion[0] -cne "rclone $version") {
    throw 'Pinned rclone executable version mismatch.'
}
$rcloneVersion | Out-Host

# The probe references the Host test project: build serially before any test suite.
dotnet build (Join-Path $root 'tools/RestoreProtocolProbe/RestoreProtocolProbe.csproj') -c Release -m:1 -p:BuildInParallel=false --nologo | Out-Host
if ($LASTEXITCODE) { throw 'Restore protocol probe build failed.' }
$probe = Join-Path $root 'tools/RestoreProtocolProbe/bin/Release/net10.0/RestoreProtocolProbe.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Restore protocol probe is missing.' }

$environment = [ordered]@{
    FOLDERREWIND_TEST_7Z = $sevenZip
    FOLDERREWIND_TEST_RCLONE = $rclone
    FOLDERREWIND_RESTORE_PROTOCOL_PROBE = $probe
}
foreach ($entry in $environment.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    if ($env:GITHUB_ENV) { "$($entry.Key)=$($entry.Value)" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
}
