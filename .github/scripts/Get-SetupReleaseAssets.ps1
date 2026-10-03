[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version,
    [ValidateNotNullOrEmpty()][ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expected = @($Architectures | ForEach-Object {
    $name = "FolderRewind_${Version}_Setup_$($_.ToLowerInvariant()).exe"
    $name; "$name.sha256"
})
$files = @(Get-ChildItem -LiteralPath $Directory -Force)
$names = @($files | Select-Object -ExpandProperty Name)
foreach ($file in $files) {
    if ($file.PSIsContainer -or $file.Name -notin $expected) { throw "Unexpected public release asset: $($file.Name). Only Setup EXE and its SHA-256 are allowed." }
}
foreach ($name in $expected) {
    if ($name -notin $names) { throw "Missing public release asset: $name" }
}
foreach ($architecture in $Architectures) {
    $name = "FolderRewind_${Version}_Setup_$($architecture.ToLowerInvariant()).exe"
    $exe = Join-Path $Directory $name
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = (Get-Content -LiteralPath ($exe + '.sha256') -Raw).Trim()
    if ($checksum -ine "$hash *$name") { throw "Invalid public release checksum: $name.sha256" }
}
$files | Sort-Object Name
