[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [string]$ResultPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
foreach ($relative in @('FolderRewind.exe','FolderRewind.dll','FolderRewind.deps.json','FolderRewind.runtimeconfig.json',
    'FolderRewind.pri','Microsoft.UI.Xaml.dll','Microsoft.UI.Xaml.Controls.pri','Microsoft.WindowsAppRuntime.dll',
    '7za.exe','Assets\MsiApp.ico','Assets\StoreLogo.png','Assets\qq_group_light.jpg')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Missing MSI payload: $relative" }
}
if (Get-ChildItem -LiteralPath $root -Filter '*.pdb' -File -Recurse) { throw 'MSI payload contains PDB files.' }
$contract = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $root 'FolderRewind.Plugin.Abstractions.dll'))
if ($contract.Version -ne [version]'3.0.0.0') { throw "Public plugin contract identity changed: $($contract.FullName)" }
$sdk = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
$makepri = Get-ChildItem -LiteralPath $sdk -Filter makepri.exe -Recurse -File |
    Where-Object FullName -Match '[\\/]x64[\\/]makepri\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makepri) { throw 'makepri.exe is required for MSI payload validation.' }
$dump = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind-resource-' + [guid]::NewGuid().ToString('N') + '.xml')
try {
    & $makepri.FullName dump /if (Join-Path $root 'FolderRewind.pri') /of $dump /o | Out-Null
    if ($LASTEXITCODE) { throw "makepri failed: $LASTEXITCODE" }
    [xml]$xml = Get-Content -LiteralPath $dump -Raw
    if (-not $xml.SelectSingleNode('//ResourceMapSubtree[@name="Microsoft.UI.Xaml"]//NamedResource[@name="themeresources.xbf"]')) {
        throw 'The MSI PRI is missing dependency-merged XAML theme resources.'
    }
    $files = @{}
    Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object { $files[$_.FullName.Substring($root.Length)] = $_.FullName.Substring($root.Length) }
    $verified = @()
    foreach ($candidate in $xml.SelectNodes('//ResourceMap[@name="FolderRewind"]//Candidate[@type="Path"]')) {
        $name = $candidate.ParentNode.GetAttribute('uri')
        $relative = $candidate.SelectSingleNode('Value').InnerText.Replace('/', '\')
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if ([IO.Path]::IsPathRooted($relative) -or -not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "PRI path escapes payload: $name -> $relative" }
        if (-not $files.ContainsKey($relative)) { throw "PRI file missing: $name -> $relative" }
        if ($files[$relative] -cne $relative) { throw "PRI casing mismatch: $name -> $relative (actual $($files[$relative]))" }
        $verified += @{resource=$name; path=$relative}
    }
    if ($ResultPath) { @{directory=$root; passed=$true; resources=$verified} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8 }
    Write-Host "Verified $($verified.Count) PRI file references and the self-contained payload."
} finally { Remove-Item -LiteralPath $dump -Force -ErrorAction SilentlyContinue }
