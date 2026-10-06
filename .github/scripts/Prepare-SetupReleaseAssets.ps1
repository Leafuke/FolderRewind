[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$name = "FolderRewind_${Version}_Setup_$($Platform.ToLowerInvariant()).exe"
$source = Join-Path ([IO.Path]::GetFullPath($SourceDirectory)) $name
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Setup EXE not found: $source" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
# A stale MSI/MSIX/archive must never survive into the public staging directory.
foreach ($file in Get-ChildItem -LiteralPath $output -Force) {
    if ($file.PSIsContainer -or $file.Name -notin @($name,"$name.sha256")) { throw "Unexpected file in public staging: $($file.Name)" }
}
$destination = Join-Path $output $name
if ($source -ine $destination) { Copy-Item -LiteralPath $source -Destination $destination -Force }
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$name" | Set-Content -LiteralPath ($destination + '.sha256') -Encoding ascii
& "$PSScriptRoot\Get-SetupReleaseAssets.ps1" -Directory $output -Version $Version -Architectures $Platform.ToLowerInvariant() | Out-Null
Write-Host "Prepared public Setup EXE and SHA-256 in $output"
