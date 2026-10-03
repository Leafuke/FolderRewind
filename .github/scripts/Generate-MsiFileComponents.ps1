param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$OutputPath, [string]$Platform='x64', [string]$RegistryKey='Software\Leafuke\FolderRewind')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\') + '\'
function Get-Identifier([string]$value) { 'p' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))).Substring(0,30) }
function Escape-Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
function Get-ComponentGuid([string]$value) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("FolderRewind.Msi/$RegistryKey/$Platform/$value"))
    '{' + [guid]::new([byte[]]$bytes[0..15]).ToString().ToUpperInvariant() + '}'
}
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>')
$directories = @{''='INSTALLFOLDER'}
foreach ($dir in Get-ChildItem -LiteralPath $root -Directory -Recurse | Sort-Object FullName) {
    $relative = $dir.FullName.Substring($root.Length)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $id = Get-Identifier $relative
    $directories[$relative] = $id
    $lines.Add('<DirectoryRef Id="' + $directories[$parent] + '"><Directory Id="' + $id + '" Name="' + (Escape-Xml $dir.Name) + '" /></DirectoryRef>')
}
$components = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($root.Length)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $id = Get-Identifier $relative
    $components.Add($id)
    $language = if ([Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).FileVersion) { ' DefaultLanguage="1033"' } else { '' }
    $lines.Add('<DirectoryRef Id="' + $directories[$parent] + '"><Component Id="' + $id + '" Guid="' + (Get-ComponentGuid $id) + '"><File Id="f' + $id + '" Source="' + (Escape-Xml $file.FullName) + '"' + $language + ' KeyPath="no" /><RegistryValue Root="HKMU" Key="Software\Leafuke\FolderRewind\Components" Name="' + $id + '" Type="integer" Value="1" KeyPath="yes" /></Component></DirectoryRef>')
}
foreach ($relative in $directories.Keys | Sort-Object) {
    $id = 'remove' + (Get-Identifier ($relative + '\'))
    $components.Add($id)
    $lines.Add('<DirectoryRef Id="' + $directories[$relative] + '"><Component Id="' + $id + '" Guid="*"><RemoveFolder Id="' + $id + '" On="uninstall" /><RegistryValue Root="HKMU" Key="Software\Leafuke\FolderRewind\Components" Name="' + $id + '" Type="integer" Value="1" KeyPath="yes" /></Component></DirectoryRef>')
}
$lines.Add('<ComponentGroup Id="ApplicationFiles">')
foreach ($id in $components) { $lines.Add('<ComponentRef Id="' + $id + '" />') }
$lines.Add('</ComponentGroup></Fragment></Wix>')
($lines -join [Environment]::NewLine).Replace('Software\Leafuke\FolderRewind\Components',(Escape-Xml ($RegistryKey+'\Components'))) | Set-Content -LiteralPath $OutputPath -Encoding utf8
