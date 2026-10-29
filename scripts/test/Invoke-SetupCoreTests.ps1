[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/core-tests')
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($ResultsDirectory) { $ResultsDirectory = Resolve-FolderRewindPath -Path $ResultsDirectory -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")

# Localization contract tests inspect WinUI metadata from the application restore.
& "$PSScriptRoot/Test-RepositoryLayout.ps1" | Out-Host
dotnet restore (Join-Path $root 'FolderRewind.slnx') -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Restore Host solution failed.' }
$assetsPath = Join-Path $root 'src/FolderRewind/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "Missing application restore metadata: $assetsPath"
}

& "$PSScriptRoot/Initialize-CoreTestEnvironment.ps1"
$projects = [ordered]@{
    abstractions='tests/FolderRewind.Plugin.Abstractions.Tests/FolderRewind.Plugin.Abstractions.Tests.csproj'
    runtime='tests/FolderRewind.Plugin.Runtime.Tests/FolderRewind.Plugin.Runtime.Tests.csproj'
    host='tests/FolderRewind.Tests/FolderRewind.Tests.csproj'
    recovery='tests/FolderRewind.Core.Recovery.Tests/FolderRewind.Core.Recovery.Tests.csproj'
}
$timings = @()
foreach ($suite in $projects.Keys) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $filter = if ($suite -eq 'host') { @('--filter', 'TestCategory!=PerformanceBenchmark') } else { @() }
    $variant = @(if ($suite -eq 'recovery') { '-p:FolderRewindFaultInjection=true' })
    dotnet test $projects[$suite] -c Release @filter @variant --logger trx --results-directory (Join-Path $ResultsDirectory $suite) --blame-hang-timeout 2m --blame-hang-dump-type none
    if ($LASTEXITCODE) { throw "Core test suite failed: $suite" }
    $timings += @{suite=$suite;seconds=[math]::Round($timer.Elapsed.TotalSeconds,2)}
}
& "$PSScriptRoot/Get-SetupTestResults.ps1" -Directory $ResultsDirectory | Out-Host
$timings | ConvertTo-Json | Set-Content (Join-Path $ResultsDirectory 'timings.json') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) {
    "### Core tests`n`n| Suite | Seconds |`n| --- | ---: |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    foreach ($row in $timings) { "| $($row.suite) | $($row.seconds) |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}

} finally { Pop-Location }
