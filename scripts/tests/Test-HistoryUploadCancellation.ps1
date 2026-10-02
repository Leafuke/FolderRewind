param(
    [string]$ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'folderrewind-stage-a-cancellation'),
    [ValidateRange(1, 100)] [int]$Iterations = 20
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repositoryRoot 'FolderRewind.Tests/FolderRewind.Tests.csproj'
$resultsRoot = [IO.Path]::GetFullPath($ResultsDirectory)

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $reportName = "cloud-cancellation-$iteration.trx"
    dotnet test $project -c Release --no-build --no-restore `
        --filter 'FullyQualifiedName~CancellationDuringUploadDoesNotLockOutTheNextTask' `
        --logger "trx;LogFileName=$reportName" --results-directory $resultsRoot --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Upload cancellation round $iteration failed." }
    [xml]$result = Get-Content -LiteralPath (Join-Path $resultsRoot $reportName)
    $counters = $result.TestRun.ResultSummary.Counters
    if ([int]$counters.total -ne 3 -or [int]$counters.passed -ne 3) {
        throw "All three cancellation boundaries must pass without skipping in round $iteration."
    }
}
Write-Output "Passed $Iterations rounds at all three cancellation boundaries. Results: $resultsRoot"
