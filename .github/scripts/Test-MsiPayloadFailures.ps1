[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind-resource-validation-' + [guid]::NewGuid().ToString('N'))
$source = [IO.Path]::GetFullPath($Directory)
$results = [Collections.Generic.List[object]]::new()
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $temporary -Recurse
    foreach ($relative in @('Assets\qq_group_light.jpg','Assets\StoreLogo.png')) {
        $path = Join-Path $temporary $relative
        Move-Item -LiteralPath $path -Destination ($path + '.held')
        try {
            $rejected = $false
            try { & "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $temporary }
            catch { $rejected = $_.Exception.Message.Contains($relative); $results.Add(@{resource=$relative;rejected=$rejected;message=$_.Exception.Message}) }
            if (-not $rejected) { throw "The packaging gate did not identify the missing resource: $relative" }
        } finally { Move-Item -LiteralPath ($path + '.held') -Destination $path }
    }
    & "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $temporary
} finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultPath -Encoding utf8
    $resolved = [IO.Path]::GetFullPath($temporary)
    $base = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'FolderRewind-resource-validation-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
