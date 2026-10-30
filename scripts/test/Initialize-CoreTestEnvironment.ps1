[CmdletBinding()]
param([string]$ToolsDirectory = "$PSScriptRoot/../../artifacts/test-tools")
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($ToolsDirectory) { $ToolsDirectory = Resolve-FolderRewindPath -Path $ToolsDirectory -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$tools = [IO.Path]::GetFullPath($ToolsDirectory)

$sevenZip = Join-Path $root '7za.exe'
if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) { $sevenZip = (Get-Command 7z.exe -ErrorAction Stop).Source }
& $sevenZip i | Out-Null
if ($LASTEXITCODE) { throw 'Real 7-Zip is required for archive integration tests.' }

# Use the same pinned Windows x64 asset and SHA-256 as the application installer.
$catalog = Get-Content -LiteralPath (Join-Path $root 'src/FolderRewind/Services/Onboarding/OnboardingToolAssets.cs') -Raw
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

# The OpenList integration test initializes only an isolated temporary instance.
$initializer = Get-Content -LiteralPath (Join-Path $root 'src/FolderRewind/Services/Cloud/OpenListInitializer.cs') -Raw
$openListVersionMatch = [regex]::Match($initializer, 'SupportedVersion\s*=\s*"(?<version>[\d.]+)"')
$openListAsset = [regex]::Match($catalog, '\("openlist", Architecture\.X64\) => \("v" \+ OpenListInitializer\.SupportedVersion, "(?<repository>[^"]+)", "(?<file>[^"]+)", "(?<hash>[a-f0-9]{64})"\)')
if (-not $openListVersionMatch.Success -or -not $openListAsset.Success) { throw 'Pinned OpenList test asset was not found.' }
$openListVersion = 'v' + $openListVersionMatch.Groups['version'].Value
$openListDirectory = Join-Path $tools "openlist/$openListVersion"
[void][IO.Directory]::CreateDirectory($openListDirectory)
$openListArchive = Join-Path $openListDirectory $openListAsset.Groups['file'].Value
if (-not (Test-Path -LiteralPath $openListArchive -PathType Leaf)) {
    Invoke-WebRequest "https://github.com/$($openListAsset.Groups['repository'].Value)/releases/download/$openListVersion/$($openListAsset.Groups['file'].Value)" -OutFile $openListArchive
}
if ((Get-FileHash -LiteralPath $openListArchive -Algorithm SHA256).Hash -ine $openListAsset.Groups['hash'].Value) { throw 'Pinned OpenList archive SHA-256 mismatch.' }
Expand-Archive -LiteralPath $openListArchive -DestinationPath $openListDirectory -Force
$openListExecutables = @(Get-ChildItem -LiteralPath $openListDirectory -Filter openlist.exe -File -Recurse)
if ($openListExecutables.Count -ne 1) { throw 'Expected one pinned OpenList executable.' }
$openList = $openListExecutables[0].FullName

# Fault hooks and their complete dependency graph have isolated build outputs.
dotnet build (Join-Path $root 'tools/RestoreProtocolProbe/RestoreProtocolProbe.csproj') -c Release -p:FolderRewindFaultInjection=true -m:1 -p:BuildInParallel=false --nologo | Out-Host
if ($LASTEXITCODE) { throw 'Restore protocol probe build failed.' }
$probe = Join-Path $root 'artifacts/build/fault-injection/RestoreProtocolProbe/bin/Release/net10.0/RestoreProtocolProbe.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Restore protocol probe is missing.' }

$environment = [ordered]@{
    FOLDERREWIND_TEST_7Z = $sevenZip
    FOLDERREWIND_TEST_RCLONE = $rclone
    FOLDERREWIND_TEST_OPENLIST = $openList
    FOLDERREWIND_RESTORE_PROTOCOL_PROBE = $probe
}
foreach ($entry in $environment.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    if ($env:GITHUB_ENV) { "$($entry.Key)=$($entry.Value)" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
}

} finally { Pop-Location }
