param([ValidateSet('B0', 'Main', 'Feature', 'Tiny')][string]$Phase = 'B0')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRoot = Join-Path $repoRoot '.tmp/FolderRewindEval'
$source = Join-Path $fixtureRoot 'SourceA'
foreach ($target in @($fixtureRoot, $source, (Join-Path $fixtureRoot 'Backup'))) {
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked fixture path: $target"
        }
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}
# Only these fixture files are managed. Never erase arbitrary source contents.
foreach ($name in @('shared.txt','stable.txt','obsolete.txt','main-only.txt','feature-only.txt','noise.bin')) {
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
$version = switch ($Phase) { 'Main' {'main-version'} 'Feature' {'feature-version'} default {'base-version'} }
[IO.File]::WriteAllText((Join-Path $source 'shared.txt'), $version)
[IO.File]::WriteAllText((Join-Path $source 'stable.txt'), 'stable-data')
if ($Phase -ne 'Feature') { [IO.File]::WriteAllText((Join-Path $source 'obsolete.txt'), 'delete-on-feature') }
if ($Phase -eq 'Main') { [IO.File]::WriteAllText((Join-Path $source 'main-only.txt'), 'main-only') }
if ($Phase -eq 'Feature') { [IO.File]::WriteAllText((Join-Path $source 'feature-only.txt'), 'feature-only') }
if ($Phase -ne 'Tiny') {
    $bytes = New-Object byte[] 32768
    $random = New-Object System.Random 719
    $random.NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $source 'noise.bin'), $bytes)
}
Write-Output "Prepared $Phase in $source. This script does not create backups or change History."
