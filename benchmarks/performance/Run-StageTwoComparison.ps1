param(
    [Parameter(Mandatory)][string]$BaselineExe,
    [Parameter(Mandatory)][string]$CandidateExe,
    [Parameter(Mandatory)][string]$SevenZipExe,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(4,10240)][int]$MiB = 64,
    [ValidateRange(1,2)][int]$Rounds = 2,
    [ValidateRange(1,2)][int]$FirstRound = 1,
    [switch]$BackupOnly
)
. (Join-Path $PSScriptRoot '../../scripts/common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($BaselineExe) { $BaselineExe = Resolve-FolderRewindPath -Path $BaselineExe -Root $folderRewindScriptRoot }
if ($CandidateExe) { $CandidateExe = Resolve-FolderRewindPath -Path $CandidateExe -Root $folderRewindScriptRoot }
if ($SevenZipExe) { $SevenZipExe = Resolve-FolderRewindPath -Path $SevenZipExe -Root $folderRewindScriptRoot }
if ($OutputRoot) { $OutputRoot = Resolve-FolderRewindPath -Path $OutputRoot -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$baseline = (Resolve-Path -LiteralPath $BaselineExe).Path
$candidate = (Resolve-Path -LiteralPath $CandidateExe).Path
$sevenZip = (Resolve-Path -LiteralPath $SevenZipExe).Path
$output = [IO.Path]::GetFullPath($OutputRoot)
if ($FirstRound -gt $Rounds) { throw 'FirstRound cannot exceed Rounds.' }
if (Test-Path -LiteralPath $output) { throw 'Comparison requires a new output directory; preserve earlier raw results.' }
[void][IO.Directory]::CreateDirectory($output)
$previousZip = $env:FOLDERREWIND_TEST_7Z; $previousRepeats = $env:FOLDERREWIND_BENCHMARK_REPEATS
$runs = [Collections.Generic.List[object]]::new()
try {
    $env:FOLDERREWIND_TEST_7Z = $sevenZip; $env:FOLDERREWIND_BENCHMARK_REPEATS = '5'
    foreach ($round in $FirstRound..$Rounds) {
        # 第二轮交换先后次序，减轻固定顺序造成的温度/缓存偏差；不宣称缓存受控。
        $cohorts = if ($round -eq 1) { @('baseline','candidate') } else { @('candidate','baseline') }
        foreach ($policy in @('Balanced','Strict')) {
            foreach ($cohort in $cohorts) {
                $name = "$cohort-$($policy.ToLowerInvariant())-r$round"
                $root = Join-Path $output $name
                $executable = if ($cohort -eq 'baseline') { $baseline } else { $candidate }
                $entry = [ordered]@{ cohort=$name; startedAt=[DateTimeOffset]::Now.ToString('O'); completedAt=$null; exitCode=$null }
                $runs.Add($entry)
                $runs.ToArray() | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'run-status.json') -Encoding utf8
                Write-Output "Starting $name (five samples per scenario)"
                $mode = if ($BackupOnly) { 'backup' } else { 'quick' }
                $process = Start-Process -FilePath $executable -ArgumentList @(('"'+$root+'"'),"$MiB",$policy,$mode) -WindowStyle Hidden -Wait -PassThru
                $entry.completedAt=[DateTimeOffset]::Now.ToString('O'); $entry.exitCode=$process.ExitCode
                $runs.ToArray() | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'run-status.json') -Encoding utf8
                Write-Output "Completed $name; exit code $($process.ExitCode)"
                if ($process.ExitCode -notin @(0,2)) {
                    $failure = Join-Path $root 'failure.txt'
                    if (Test-Path -LiteralPath $failure) { Get-Content -LiteralPath $failure }
                    throw "Benchmark process failed: $name"
                }
            }
        }
    }
}
finally {
    $env:FOLDERREWIND_TEST_7Z = $previousZip; $env:FOLDERREWIND_BENCHMARK_REPEATS = $previousRepeats
}
if (@($runs | Where-Object exitCode -ne 0).Count) { throw 'Matrix completed with incorrect outputs. Inspect raw results before comparing performance.' }

} finally { Pop-Location }
