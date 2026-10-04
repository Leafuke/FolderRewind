[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -Recurse -File)
if ($files.Count -ne 3) { throw 'Expected exactly three complete core test reports.' }
foreach ($suite in @('abstractions','runtime','host')) {
    $match = @($files | Where-Object { $_.Directory.Name -ceq $suite })
    if ($match.Count -ne 1) { throw "Missing or duplicate test suite: $suite" }
    [xml]$trx = Get-Content -LiteralPath $match[0].FullName -Raw
    $count = $trx.TestRun.ResultSummary.Counters
    if ($trx.TestRun.ResultSummary.outcome -cne 'Completed' -or [int]$count.total -le 0 -or [int]$count.passed -ne [int]$count.total) {
        throw "Failed, skipped or incomplete tests: $suite"
    }
    [pscustomobject]@{suite=$suite;total=[int]$count.total;passed=[int]$count.passed;status='passed'}
}
