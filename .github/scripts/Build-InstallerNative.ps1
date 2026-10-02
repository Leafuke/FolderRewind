[CmdletBinding()]
param([string]$OutputDirectory = "$PSScriptRoot\..\..\artifacts\installer-native", [switch]$EnableFaultInjection)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$api = & "$PSScriptRoot\Restore-InstallerNativeApi.ps1"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'The native installer requires the installed MSVC x86/x64 build tools.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild "$PSScriptRoot\..\..\Installer\Native\FolderRewind.BAFunctions.vcxproj" /t:Build /p:Configuration=Release /p:Platform=Win32 "/p:EnableFaultInjection=$($EnableFaultInjection.IsPresent.ToString().ToLowerInvariant())" "/p:NativeApiRoot=$api" "/p:OutDir=$output\" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "BAFunctions build failed: $LASTEXITCODE" }
Write-Output $output
