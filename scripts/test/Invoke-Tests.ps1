<#
.SYNOPSIS
Runs a named test layer from any working directory. All is the release acceptance gate.
.EXAMPLE
./scripts/test/Invoke-Tests.ps1 -Suite ExternalTool
.EXAMPLE
./scripts/test/Invoke-Tests.ps1 -Suite Host -Filter 'FullyQualifiedName~Notification'
#>
[CmdletBinding()]
param(
    [ValidateSet('All','Contracts','Runtime','Host','ExternalTool','WindowsIntegration','Recovery')]
    [string]$Suite = 'All',
    [string]$Filter,
    [string]$ResultsDirectory = 'artifacts/focused-tests'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$root = Get-FolderRewindRoot -Start $PSScriptRoot
if ($Suite -eq 'All') {
    if ($Filter) { throw 'All requires complete acceptance; use a named suite for filtered runs.' }
    & "$PSScriptRoot/Invoke-SetupCoreTests.ps1" -ResultsDirectory $ResultsDirectory
    return
}
Push-Location -LiteralPath $root
try {
    $projects = @{
        Contracts = 'FolderRewind.Plugin.Abstractions.Tests'
        Runtime = 'FolderRewind.Plugin.Runtime.Tests'
        Host = 'FolderRewind.Tests'
        ExternalTool = 'FolderRewind.Tests'
        WindowsIntegration = 'FolderRewind.Tests'
        Recovery = 'FolderRewind.Core.Recovery.Tests'
    }
    if ($Suite -in @('Host','ExternalTool','Recovery')) {
        & "$PSScriptRoot/Initialize-CoreTestEnvironment.ps1"
    }
    if ($Suite -eq 'Host') {
        # XAML resource checks inspect the restored application's control metadata.
        dotnet restore FolderRewind.slnx -p:Platform=x64
        if ($LASTEXITCODE) { throw 'Application metadata restore failed.' }
    }
    $selection = 'TestCategory!=PerformanceBenchmark'
    if ($Suite -in @('ExternalTool','WindowsIntegration')) { $selection += "&TestCategory=$Suite" }
    if ($Filter) { $selection += "&($Filter)" }
    $variant = @(if ($Suite -eq 'Recovery') { '-p:FolderRewindFaultInjection=true' })
    $project = $projects[$Suite]
    $output = Join-Path (Resolve-FolderRewindPath -Path $ResultsDirectory -Root $root) $Suite
    dotnet test "tests/$project/$project.csproj" -c Release @variant --filter $selection --logger 'trx;LogFileName=focused.trx' --results-directory $output --blame-hang-timeout 2m --blame-hang-dump-type none
    if ($LASTEXITCODE) { throw "Test suite failed: $Suite" }
    [xml]$report = Get-Content -LiteralPath (Join-Path $output 'focused.trx') -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.total -le 0 -or [int]$counts.passed -ne [int]$counts.total) {
        throw 'Selection matched no tests, or contains skipped/incomplete tests.'
    }
} finally { Pop-Location }
