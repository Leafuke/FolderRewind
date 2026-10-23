param(
    [Parameter(Mandatory)][string]$HostExe,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SevenZipExe = (Join-Path $PSScriptRoot '../../7za.exe')
)
$ErrorActionPreference = 'Stop'
$hostPath = (Resolve-Path -LiteralPath $HostExe).Path
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $outputPath) { throw 'OutputRoot must not exist.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$saved7z = $env:FOLDERREWIND_TEST_7Z
$savedRepeats = $env:FOLDERREWIND_BENCHMARK_REPEATS
$savedProbe = $env:FOLDERREWIND_ANALYSIS_PROBE
$runs = @()
try {
    $env:FOLDERREWIND_TEST_7Z = (Resolve-Path -LiteralPath $SevenZipExe).Path
    $env:FOLDERREWIND_BENCHMARK_REPEATS = '5'
    foreach ($case in @('balanced-64', 'strict-64', 'many-files-1000', 'many-files-20000', 'history-growth', 'large-file')) {
        $env:FOLDERREWIND_ANALYSIS_PROBE = if ($case.EndsWith('-64')) { $null } else { $case }
        $mib = if ($case.EndsWith('-64')) { 64 } else { 4 }
        $policy = if ($case -eq 'strict-64') { 'Strict' } else { 'Balanced' }
        $started = [DateTimeOffset]::UtcNow
        & $hostPath (Join-Path $outputPath $case) $mib $policy
        $code = $LASTEXITCODE
        $runs += [pscustomobject]@{ case = $case; exitCode = $code; started = $started; ended = [DateTimeOffset]::UtcNow }
        Write-Output "$case exit=$code"
        if ($code -ne 0) { throw "Benchmark failed: $case, exit $code" }
    }
}
finally {
    [ordered]@{
        host = $hostPath
        hashes = @(Get-FileHash -LiteralPath $hostPath,
            (Join-Path (Split-Path $hostPath) ([IO.Path]::GetFileNameWithoutExtension($hostPath) + '.dll')),
            (Join-Path (Split-Path $hostPath) 'FolderRewind.dll'), $env:FOLDERREWIND_TEST_7Z | Select-Object Path, Hash)
        runs = $runs
        cache = 'uncontrolled'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'run.json') -Encoding utf8
    $env:FOLDERREWIND_TEST_7Z = $saved7z
    $env:FOLDERREWIND_BENCHMARK_REPEATS = $savedRepeats
    $env:FOLDERREWIND_ANALYSIS_PROBE = $savedProbe
}
