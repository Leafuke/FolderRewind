param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$ci=[Globalization.CultureInfo]::InvariantCulture
$rows=@(Import-Csv -LiteralPath (Join-Path $RunDirectory 'samples.csv') | Where-Object phase -eq 'measure')
if ($rows.Count -lt 100) { throw 'Insufficient measured samples.' }
function N($x) { [double]::Parse($x,$ci) }
$previousElapsed=-1.0
foreach ($row in $rows) {
    foreach ($name in @('elapsed_s','dispatch_ms','allocated_bytes','gc0','gc1','gc2','private_bytes','working_set_bytes','managed_heap_bytes','tracked_pages_alive','preview_renders','preview_closes')) {
        if ([string]::IsNullOrWhiteSpace($row.$name)) { throw "Sample is missing $name." }
        $value=N $row.$name
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { throw "Sample contains invalid $name." }
    }
    $elapsed=N $row.elapsed_s
    if ($elapsed -lt 120 -or $elapsed -le $previousElapsed) { throw 'Measured sample times are incomplete or not strictly increasing.' }
    $previousElapsed=$elapsed
}
$latency=@($rows | ForEach-Object { N $_.dispatch_ms } | Sort-Object)
function Percentile([double]$p) { $latency[[Math]::Min($latency.Count-1,[int][Math]::Ceiling($p*$latency.Count)-1)] }
$first=$rows[0]; $last=$rows[-1]
$duration=(N $last.elapsed_s)-(N $first.elapsed_s)
if ($duration -lt 475) { throw 'The steady-state sampling interval is incomplete.' }
$summary=[ordered]@{
    status='OBSERVED'; measuredSeconds=$duration; samples=$rows.Count
    dispatchMs=[ordered]@{p50=Percentile 0.5;p95=Percentile 0.95;p99=Percentile 0.99;max=$latency[-1]}
    allocatedBytesDelta=(N $last.allocated_bytes)-(N $first.allocated_bytes)
    collections=@(((N $last.gc0)-(N $first.gc0)), ((N $last.gc1)-(N $first.gc1)), ((N $last.gc2)-(N $first.gc2)))
    privateBytesFirst=N $first.private_bytes; privateBytesLast=N $last.private_bytes
    privateBytesPeak=($rows | ForEach-Object {N $_.private_bytes} | Measure-Object -Maximum).Maximum
    workingSetFirst=N $first.working_set_bytes; workingSetLast=N $last.working_set_bytes
    workingSetPeak=($rows | ForEach-Object {N $_.working_set_bytes} | Measure-Object -Maximum).Maximum
    managedHeapFirst=N $first.managed_heap_bytes; managedHeapLast=N $last.managed_heap_bytes
    trackedWeakTargetsFirst=N $first.tracked_pages_alive; trackedWeakTargetsLast=N $last.tracked_pages_alive
    rendersDelta=(N $last.preview_renders)-(N $first.preview_renders)
    closesDelta=(N $last.preview_closes)-(N $first.preview_closes)
    note='Dispatcher delay, not FPS. Managed heap is GC.GetTotalMemory(false), not an object count. Weak targets are tracked fixture pages/canvases without forced GC.'
}
if (Test-Path -LiteralPath (Join-Path $RunDirectory 'summary.json')) { throw 'Summary already exists; choose a new evidence directory.' }
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunDirectory 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 5
