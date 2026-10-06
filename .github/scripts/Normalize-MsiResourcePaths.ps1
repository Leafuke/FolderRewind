param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
$makepri = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Filter makepri.exe -File -Recurse |
    Where-Object FullName -Match '[\\/]x64[\\/]makepri\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
$dump = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.xml')
try {
    & $makepri.FullName dump /if (Join-Path $root 'FolderRewind.pri') /of $dump /o | Out-Null
    if ($LASTEXITCODE) { throw 'Cannot normalize unreadable PRI.' }
    [xml]$xml = Get-Content -LiteralPath $dump -Raw
    $files = @{}
    Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object { $files[$_.FullName.Substring($root.Length)] = $_ }
    foreach ($candidate in $xml.SelectNodes('//ResourceMap[@name="FolderRewind"]//Candidate[@type="Path"]')) {
        $relative = $candidate.SelectSingleNode('Value').InnerText.Replace('/', '\')
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "PRI path escapes root: $relative" }
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $actual = $files[$relative]
            $name = [IO.Path]::GetFileName($path)
            if ($actual.Name -cne $name) {
                # Some SDK payloads use a differently cased filename from their PRI.
                # Normalize only the staged payload, retaining the exact referenced name.
                $temporary = $name + '.case-' + [guid]::NewGuid().ToString('N')
                Rename-Item -LiteralPath $actual.FullName -NewName $temporary
                Rename-Item -LiteralPath (Join-Path $actual.DirectoryName $temporary) -NewName $name
                Write-Host "Normalized SDK resource filename: $relative"
                $files[$relative] = Get-Item -LiteralPath $path
            }
        }
    }
} finally { Remove-Item -LiteralPath $dump -Force -ErrorAction SilentlyContinue }
