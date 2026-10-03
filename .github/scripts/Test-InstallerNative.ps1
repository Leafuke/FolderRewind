[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC build tools are required.' }
& (Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe') "$PSScriptRoot\..\..\Installer\Native\Tests\RegistryValueTests.vcxproj" /p:Configuration=Release /p:Platform=Win32 "/p:OutDir=$output\" /nologo /verbosity:minimal *> (Join-Path $output 'build.log')
if ($LASTEXITCODE) { throw 'Native registry tests did not build.' }
& (Join-Path $output 'RegistryValueTests.exe') | Set-Content (Join-Path $output 'results.json')
if ($LASTEXITCODE) { throw 'Native registry tests failed.' }
Get-Content (Join-Path $output 'results.json')
