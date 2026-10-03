param(
    [string]$ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'folderrewind-stage-a-cancellation'),
    [ValidateRange(1, 100)] [int]$Iterations = 20,
    [string]$HangTimeout = '2m'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repositoryRoot 'FolderRewind.Tests/FolderRewind.Tests.csproj'
$resultsRoot = [IO.Path]::GetFullPath($ResultsDirectory)

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $reportName = "cloud-cancellation-$iteration.trx"
    dotnet test $project -c Release --no-build --no-restore `
        --filter 'FullyQualifiedName~CancellationDuringUploadDoesNotLockOutTheNextTask' `
        --logger "trx;LogFileName=$reportName" --results-directory $resultsRoot --verbosity quiet `
        --blame-hang-timeout $HangTimeout --blame-hang-dump-type none
    if ($LASTEXITCODE -ne 0) { throw "Upload cancellation round $iteration failed." }
    [xml]$result = Get-Content -LiteralPath (Join-Path $resultsRoot $reportName)
    $counters = $result.TestRun.ResultSummary.Counters
    if ([int]$counters.total -eq 0 -or [int]$counters.passed -ne [int]$counters.total) {
        throw "All enumerated cancellation boundaries must pass without skipping in round $iteration."
    }
}
Write-Output "Passed $Iterations rounds at all enumerated cancellation boundaries. Results: $resultsRoot"
