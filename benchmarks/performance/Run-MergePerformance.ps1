[CmdletBinding()]
param(
    [ValidateSet('Plugin', 'Host', 'All')][string]$Suite = 'All',
    [string]$PluginBaselineDll,
    [string]$PluginCandidateDll,
    [string]$HostBaselineDll,
    [string]$HostCandidateDll,
    [Parameter(Mandatory)][string]$CorpusRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(1, 9)][int]$Rounds = 3,
    [ValidateSet('modern154', 'plugin-small-normal', 'plugin-small-embedded')]
    [string[]]$PluginCorpora = @('modern154', 'plugin-small-normal', 'plugin-small-embedded'),
    [ValidateSet('conservative', 'terrain-first')][string[]]$Policies = @('conservative', 'terrain-first'),
    [ValidateRange(1, 9)][int[]]$RoundNumbers,
    [ValidateRange(10, 1800)][int]$TimeoutSeconds = 600,
    [ValidateRange(10, 300)][int]$StressSeconds = 60
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$records = [Collections.Generic.List[object]]::new()
if ([IO.File]::Exists((Join-Path $outputPath 'runs.json'))) {
    foreach ($previous in (Get-Content -LiteralPath (Join-Path $outputPath 'runs.json') -Raw | ConvertFrom-Json)) { $records.Add($previous) }
}

function Invoke-Probe([string]$Label, [string]$Dll, [string[]]$ProbeArguments,
    [int]$Timeout, [string]$Transcript, [int]$CancelMs = 0) {
    if (-not [IO.File]::Exists($Dll)) { throw "Missing exact probe binary: $Dll" }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add([IO.Path]::GetFullPath($Dll))
    foreach ($argument in $ProbeArguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('MERGE_PROBE_CANCEL_MS') | Out-Null
    $start.Environment.Remove('MERGE_PROBE_TRANSCRIPT') | Out-Null
    if ($Transcript) { $start.Environment['MERGE_PROBE_TRANSCRIPT'] = $Transcript }
    if ($CancelMs) { $start.Environment['MERGE_PROBE_CANCEL_MS'] = "$CancelMs" }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit($Timeout * 1000)
    if (-not $completed) { $process.Kill($true); $process.WaitForExit() }
    $watch.Stop()
    [IO.File]::WriteAllText((Join-Path $outputPath "$Label.stdout.json"), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $outputPath "$Label.stderr.log"), $stderr.GetAwaiter().GetResult())
    $record = [ordered]@{ Label = $Label; Binary = [IO.Path]::GetFullPath($Dll)
        Arguments = $ProbeArguments; TimedOut = -not $completed; ExitCode = $process.ExitCode
        ProcessWallSeconds = $watch.Elapsed.TotalSeconds; TimeoutSeconds = $Timeout }
    $records.Add($record)
    [IO.File]::WriteAllText((Join-Path $outputPath 'runs.json'), (ConvertTo-Json -InputObject $records.ToArray() -Depth 8))
    $process.Dispose()
    Write-Output ("MERGE_MEASUREMENT " + (ConvertTo-Json -InputObject $record -Compress -Depth 5))
    if ($completed -and $record.ExitCode -ne 0) { throw "Probe failed: $Label; inspect stderr" }
}

if ($Suite -in 'Plugin', 'All') {
    $selectedRounds = if ($RoundNumbers) { @($RoundNumbers | Select-Object -Unique) } else { @(1..$Rounds) }
    foreach ($corpus in $PluginCorpora) {
        foreach ($policy in $Policies) {
            foreach ($round in $selectedRounds) {
                foreach ($variant in 'baseline', 'candidate') {
                    $label = "plugin-$corpus-$policy-r$round-$variant"
                    $dll = if ($variant -eq 'baseline') { $PluginBaselineDll } else { $PluginCandidateDll }
                    $probeArgs = @('base', 'ours', 'theirs') | ForEach-Object { Join-Path $CorpusRoot "$corpus\$_" }
                    Invoke-Probe $label $dll (@($probeArgs) + @('verified', $policy)) $TimeoutSeconds (Join-Path $outputPath "$label.transcript.json")
                }
            }
        }
    }
    $probeArgs = @('base', 'ours', 'theirs') | ForEach-Object { Join-Path $CorpusRoot "modern154\$_" }
    Invoke-Probe 'plugin-candidate-cancel' $PluginCandidateDll (@($probeArgs) + @('verified', 'terrain-first')) 10 '' 250
}
if ($Suite -in 'Host', 'All') {
    foreach ($count in 1000, 10000, 20000) {
        for ($round = 1; $round -le $Rounds; $round++) {
            foreach ($variant in 'baseline', 'candidate') {
                $label = "host-n$count-r$round-$variant"
                $dll = if ($variant -eq 'baseline') { $HostBaselineDll } else { $HostCandidateDll }
                Invoke-Probe $label $dll @('--counts', "$count", '--output', (Join-Path $outputPath $label)) $TimeoutSeconds ''
            }
        }
    }
    foreach ($variant in 'baseline', 'candidate') {
        $label = "host-n50000-stress-$variant"
        $dll = if ($variant -eq 'baseline') { $HostBaselineDll } else { $HostCandidateDll }
        Invoke-Probe $label $dll @('--counts', '50000', '--output', (Join-Path $outputPath $label)) $StressSeconds ''
    }
    $label = 'host-n20000-candidate-cancel'
    Invoke-Probe $label $HostCandidateDll @('--counts', '20000', '--cancel-import-ms', '25', '--output', (Join-Path $outputPath $label)) 30 ''
}
