param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath (Join-Path $run 'acceptance-owner') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $run 'results.json') -PathType Leaf)) {
    throw 'A marked fixture with completed timed results is required.'
}
$result=Get-Content -Raw -LiteralPath (Join-Path $run 'results.json') | ConvertFrom-Json
if ($null -eq $result.durationSeconds -or $result.durationSeconds -lt 600 -or
    $null -eq $result.failures -or @($result.failures).Count -ne 0) { throw 'Timed workload did not pass.' }
$output=Join-Path $run 'post-gc-results.json'
if (Test-Path -LiteralPath $output) { throw 'Post-GC result already exists.' }
Set-Content -LiteralPath (Join-Path $run 'post-gc.request') -Value 'Run only after timed measurement'
$deadline=[DateTime]::UtcNow.AddSeconds(30)
do { Start-Sleep -Milliseconds 250 } until ((Test-Path -LiteralPath $output) -or [DateTime]::UtcNow -gt $deadline)
if (!(Test-Path -LiteralPath $output)) { throw 'Post-GC diagnostic timed out; verify the opt-in build and running fixture.' }
$report=Get-Content -Raw -LiteralPath $output | ConvertFrom-Json
if ($report.status -cne 'PASS' -or $report.collection -cne 'OBSERVED') {
    throw "Post-GC lifecycle acceptance failed: $($report | ConvertTo-Json -Compress -Depth 8)"
}
$observationPath=Join-Path $run 'post-gc-observation.json'
if (!(Test-Path -LiteralPath $observationPath -PathType Leaf)) { throw 'Post-GC observation is missing.' }
$observation=Get-Content -Raw -LiteralPath $observationPath | ConvertFrom-Json
if ($observation.status -cne 'OBSERVED' -or $observation.homeReached -ne $true -or
    $null -eq $observation.trackedReferences -or $observation.trackedReferences -le 0 -or
    $null -eq $observation.provider.Generations -or $observation.provider.Generations -ne 0 -or
    $null -eq $observation.rounds -or @($observation.rounds).Count -ne 3) {
    throw 'Post-GC collection is incomplete.'
}
for ($i=0; $i -lt 3; $i++) {
    $round=$observation.rounds[$i]
    if ($round.Round -ne ($i+1) -or $null -eq $round.Managers -or $null -eq $round.Canvases -or
        $null -eq $round.Total -or $round.Managers -ne 0 -or $round.Canvases -ne 0 -or $round.Total -ne 0) {
        throw "Post-GC round $($i+1) is incomplete or retains targets."
    }
}
$report | ConvertTo-Json -Depth 6
