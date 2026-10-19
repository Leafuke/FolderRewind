param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$Output,
    [switch]$BackupOnly,
    [switch]$RestoreComponents
)
$ErrorActionPreference = 'Stop'
if ($BackupOnly -and $RestoreComponents) { throw 'Choose either backup-only or restore components.' }
# 读取隔离 Host 工具的 JSON；不触及用户配置或世界。每组必须有五个正确样本才能参与比较。
function Get-Statistics($Values) {
    $samples = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    if (!$samples.Count) { return $null }
    $sorted = @($samples | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    [ordered]@{ n = $sorted.Count; median = $median; min = $sorted[0]; max = $sorted[-1]; samples = $samples }
}
$groups = [Collections.Generic.List[object]]::new()
foreach ($directory in Get-ChildItem -LiteralPath $InputRoot -Directory | Sort-Object Name) {
    if ($directory.Name -notmatch '^(baseline|candidate)-(balanced|strict)-r(\d+)$') { continue }
    $cohort = $Matches[1]; $policy = $Matches[2]; $round = [int]$Matches[3]
    foreach ($kind in $(if ($RestoreComponents) { @('full-restore-components') } elseif ($BackupOnly) { @('backup') } else { @('backup', 'quick') })) {
        $fileName = if ($RestoreComponents) { "$kind.json" } else { "host-$kind.json" }
        $path = Join-Path $directory.FullName $fileName
        if (!(Test-Path -LiteralPath $path)) { throw "Missing benchmark result: $path" }
        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $buckets = @($document.results | Group-Object -Property {
            if ($RestoreComponents) { "$($_.scenario)/$($_.mode)" }
            elseif ($kind -eq 'backup') { "$($_.mode)/layer-$($_.layer)" }
            else { "ordinary-$($_.ordinary)/quick-$($_.requestedQuick)/changed-$($_.changed)" }
        })
        foreach ($bucket in $buckets) {
            $counterRows = @(); $phaseRows = @()
            foreach ($row in $bucket.Group) {
                $diagnostics = if ($kind -eq 'quick') { @($row.diagnostics) } else { @($row.diagnostic) }
                $counters = @{}; $phases = @{}
                foreach ($diagnostic in $diagnostics) {
                    if ($null -eq $diagnostic) { continue }
                    foreach ($property in $diagnostic.Counters.PSObject.Properties) {
                        # Host 的 OS I/O 是进程级 inclusive 数据，只取包住整个操作的 scope。
                        if ($property.Name -like 'host-os-*' -and $diagnostic.Operation -ne $(if ($RestoreComponents) { 'B1-B5-full-restore' } elseif ($kind -eq 'backup') { 'backup' } else { 'host-quick' })) { continue }
                        if ($property.Name -like '*peak*') { $counters[$property.Name] = [Math]::Max($counters[$property.Name], $property.Value) }
                        else { $counters[$property.Name] += $property.Value }
                    }
                    foreach ($property in $diagnostic.InclusivePhases.PSObject.Properties) {
                        $key = "$($diagnostic.Operation)/$($property.Name)"
                        $phases[$key] += [TimeSpan]::Parse($property.Value).TotalMilliseconds
                    }
                }
                $counterRows += ,$counters; $phaseRows += ,$phases
            }
            $counterStats = [ordered]@{}; $phaseStats = [ordered]@{}
            foreach ($key in @($counterRows.Keys | Sort-Object -Unique)) { $counterStats[$key] = Get-Statistics @($counterRows | ForEach-Object { if ($_.ContainsKey($key)) { $_[$key] } else { 0 } }) }
            foreach ($key in @($phaseRows.Keys | Sort-Object -Unique)) { $phaseStats[$key] = Get-Statistics @($phaseRows | ForEach-Object { if ($_.ContainsKey($key)) { $_[$key] } else { 0 } }) }
            $valid = $bucket.Count -eq 5 -and @($bucket.Group | Where-Object { $_.correct -ne $true -or $null -eq $_.fileReadyMs -or $null -eq $_.totalMs }).Count -eq 0
            $groups.Add([pscustomobject][ordered]@{
                cohort = $cohort; policy = $policy; round = $round; benchmark = $kind; scenario = $bucket.Name
                n = $bucket.Count; valid = $valid; correctCount = @($bucket.Group | Where-Object correct -eq $true).Count
                fileReadyMs = Get-Statistics @($bucket.Group.fileReadyMs)
                totalMs = Get-Statistics @($bucket.Group.totalMs)
                logBytes = Get-Statistics @($bucket.Group.logBytes)
                logDrainMs = $(if ($RestoreComponents) { $null } else { Get-Statistics @($bucket.Group | ForEach-Object { if ($null -ne $_.logDrainMs) { $_.logDrainMs } else { 0 } }) })
                cleanupDrainMs = Get-Statistics @($bucket.Group | ForEach-Object { if ($null -ne $_.cleanupDrainMs) { $_.cleanupDrainMs } else { 0 } })
                counters = $counterStats; inclusivePhaseMs = $phaseStats
                raw = "$($directory.Name)/$fileName"; rawSha256 = (Get-FileHash -LiteralPath $path).Hash
                diagnostics = @($bucket.Group | Where-Object correct -ne $true | ForEach-Object { $_.Diagnostic })
            })
        }
    }
}
if (!$groups.Count) { throw 'No stage-two benchmark cohorts found.' }
$comparisons = [Collections.Generic.List[object]]::new()
foreach ($candidate in $groups | Where-Object cohort -eq candidate) {
    $baseline = @($groups | Where-Object { $_.cohort -eq 'baseline' -and $_.policy -eq $candidate.policy -and $_.round -eq $candidate.round -and $_.benchmark -eq $candidate.benchmark -and $_.scenario -eq $candidate.scenario })
    if ($baseline.Count -ne 1) { throw "Missing or duplicate baseline for $($candidate.policy)/$($candidate.scenario)/$($candidate.round)" }
    foreach ($metric in @('fileReadyMs', 'totalMs')) {
        $before = $baseline[0].$metric.median; $after = $candidate.$metric.median
        $valid = $baseline[0].valid -and $candidate.valid -and $before -gt 0
        $comparisons.Add([pscustomobject][ordered]@{
            policy = $candidate.policy; round = $candidate.round; benchmark = $candidate.benchmark; scenario = $candidate.scenario
            metric = $metric; valid = $valid; baseline = $before; candidate = $after
            deltaMs = $after - $before; deltaPercent = $(if ($before -gt 0) { ($after / $before - 1) * 100 } else { $null })
            regression = $valid -and ($after - $before) -gt 20 -and $after -gt ($before * 1.1)
        })
    }
}
$persistent = [Collections.Generic.List[object]]::new()
foreach ($bucket in $comparisons | Group-Object policy, benchmark, scenario, metric) {
    $previous = $null
    foreach ($row in $bucket.Group | Sort-Object round) {
        if ($row.regression -and $null -ne $previous -and $previous.regression -and $row.round -eq ($previous.round + 1)) { $persistent.Add($row) }
        $previous = $row
    }
}
$report = [ordered]@{
    schema = 1; generatedAt = [DateTimeOffset]::UtcNow.ToString('O'); inputRoot = (Resolve-Path -LiteralPath $InputRoot).Path
    samplesPerGroup = 5; cache = 'uncontrolled; no cache purge; immutable-payload checks outside operation timing'
    ioMeaning = 'GetProcessIoCounters logical process bytes, including cache; not physical disk bytes. Host inclusive counters are not summed across nested scopes.'
    totalMeaning = 'API result + deferred cleanup drain + log drain; fileReady is measured at API completion.'
    baselineCorrectnessFailures = @($groups | Where-Object { $_.cohort -eq 'baseline' -and !$_.valid }).Count
    candidateCorrectnessFailures = @($groups | Where-Object { $_.cohort -eq 'candidate' -and !$_.valid }).Count
    groups = @($groups); comparisons = @($comparisons); persistentRegressions = @($persistent)
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($Output)); [void][IO.Directory]::CreateDirectory($parent)
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Output -Encoding utf8
[pscustomobject]@{ groups = $groups.Count; candidateInvalid = $report.candidateCorrectnessFailures; baselineInvalid = $report.baselineCorrectnessFailures; persistentRegressions = $persistent.Count; output = [IO.Path]::GetFullPath($Output) }
