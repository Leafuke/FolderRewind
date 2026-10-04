[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/core-tests')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sevenZip = (Get-Command 7z.exe -ErrorAction Stop).Source
& $sevenZip i | Out-Null
if ($LASTEXITCODE) { throw 'Real 7-Zip is required for archive integration tests.' }
$env:FOLDERREWIND_TEST_7Z = $sevenZip
$projects = [ordered]@{
    abstractions='FolderRewind.Plugin.Abstractions.Tests/FolderRewind.Plugin.Abstractions.Tests.csproj'
    runtime='FolderRewind.Plugin.Runtime.Tests/FolderRewind.Plugin.Runtime.Tests.csproj'
    host='FolderRewind.Tests/FolderRewind.Tests.csproj'
}
$timings = @()
foreach ($suite in $projects.Keys) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    dotnet test $projects[$suite] -c Release --logger trx --results-directory (Join-Path $ResultsDirectory $suite) --blame-hang-timeout 2m --blame-hang-dump-type none
    if ($LASTEXITCODE) { throw "Core test suite failed: $suite" }
    $timings += @{suite=$suite;seconds=[math]::Round($timer.Elapsed.TotalSeconds,2)}
}
& "$PSScriptRoot/Get-SetupTestResults.ps1" -Directory $ResultsDirectory | Out-Host
$timings | ConvertTo-Json | Set-Content (Join-Path $ResultsDirectory 'timings.json') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) {
    "### Core tests`n`n| Suite | Seconds |`n| --- | ---: |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    foreach ($row in $timings) { "| $($row.suite) | $($row.seconds) |" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
