param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$RunDirectory,
    [switch]$EnablePostGc,
    [ValidateSet('Candidate','Baseline')][string]$Role='Candidate',
    [ValidateRange(0.001,1000000)][double]$P95LimitMs=50
)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($Executable)
$run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
if (Test-Path -LiteralPath $run) { throw "Run directory already exists: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
Set-Content -LiteralPath (Join-Path $run 'acceptance-owner') -Value 'FolderRewind isolated performance fixture'
$fixtureEnvironment=@{
    FOLDERREWIND_TEST_DATA_ROOT=(Join-Path $run 'appdata'); FOLDERREWIND_PERFORMANCE_UI='1'
    FOLDERREWIND_UI_POST_GC=$(if ($EnablePostGc) { '1' } else { '0' })
    FOLDERREWIND_UI_PROFILING='0'; FOLDERREWIND_MANAGER_UI='0'; FOLDERREWIND_SPATIAL_UI='0'
}
$previousEnvironment=@{}
foreach($name in $fixtureEnvironment.Keys) { $previousEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
$previousWorkflow=[Environment]::GetEnvironmentVariable('WINAPP_UI_WORKFLOW_ID','Process')
$collection='FAIL'; $functionality='FAIL'; $performance='NOT_EVALUATED'; $lifecycle='FAIL'
$summary=$null; $result=$null; $post=$null; $process=$null; $driverError=$null; $uiUsed=$false
try {
    try {
        foreach($name in $fixtureEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$fixtureEnvironment[$name],'Process') }
        $process=Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
    } finally {
        foreach($name in $fixtureEnvironment.Keys) {
            if($null -eq $previousEnvironment[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name,$previousEnvironment[$name],'Process') }
        }
    }
    [Environment]::SetEnvironmentVariable('WINAPP_UI_WORKFLOW_ID',('FolderRewind-performance-'+[guid]::NewGuid().ToString('N')),'Process')
    Set-Content -LiteralPath (Join-Path $run 'pid.txt') -Value $process.Id
    $deadline=[DateTime]::UtcNow.AddMinutes(12)
    $screenshotTaken=$false
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $run 'exercise-error.txt')) { throw (Get-Content -Raw -LiteralPath (Join-Path $run 'exercise-error.txt')) }
        if ($process.HasExited) { throw "Fixture process exited before results: $($process.ExitCode)" }
        if (!$screenshotTaken -and (Test-Path -LiteralPath (Join-Path $run 'samples.csv'))) {
            $sample=Import-Csv -LiteralPath (Join-Path $run 'samples.csv') | Select-Object -Last 1
            $elapsed=0.0
            if ($sample -and [double]::TryParse([string]$sample.elapsed_s, [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$elapsed) -and $elapsed -ge 20) {
                $uiUsed=$true
                $global:LASTEXITCODE=$null
                $rawWindows=@(& winapp ui list-windows -a "$($process.Id)" --json)
                if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) { throw 'winapp list-windows failed' }
                $windows=@($rawWindows | ConvertFrom-Json)
                $preview=$windows | Where-Object title -eq 'Performance preview fixture' | Select-Object -First 1
                if ($preview -and $preview.hwnd) {
                    $global:LASTEXITCODE=$null
                    & winapp ui screenshot -w "$($preview.hwnd)" -o (Join-Path $run 'map.png') --json |
                        Set-Content -LiteralPath (Join-Path $run 'screenshot-result.json')
                    if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0 -or
                        !(Test-Path -LiteralPath (Join-Path $run 'map.png') -PathType Leaf) -or
                        (Get-Item -LiteralPath (Join-Path $run 'map.png')).Length -eq 0) { throw 'Screenshot failed' }
                    $screenshotTaken=$true
                }
            }
        }
        if (Test-Path -LiteralPath (Join-Path $run 'results.json')) {
            # File.WriteAllTextAsync can expose a path before its final bytes arrive.
            try { $result=Get-Content -Raw -LiteralPath (Join-Path $run 'results.json') | ConvertFrom-Json } catch { $result=$null }
            if ($null -ne $result) { break }
        }
        Start-Sleep -Seconds 2
    }
    if ($null -eq $result) { throw 'Timed results missing; process left running for diagnosis.' }
    if (!$screenshotTaken) { throw 'No preview screenshot was collected.' }
    $summary=(& (Join-Path $PSScriptRoot 'Summarize-UiPerformance.ps1') -RunDirectory $run | ConvertFrom-Json)
    if ($null -eq $summary -or $summary.status -cne 'OBSERVED') { throw 'Performance sample observation is missing.' }
    $collection='PASS'
    $currentnessPath=Join-Path $run 'preview-currentness.txt'
    if ($null -eq $result.durationSeconds -or $result.durationSeconds -lt 600 -or
        $null -eq $result.failures -or @($result.failures).Count -ne 0 -or
        $result.cycles -lt 1 -or $result.mapMoves -lt 1 -or $result.taskTransitions -lt 1 -or
        $result.managerSwitches -lt 1 -or !(Test-Path -LiteralPath $currentnessPath -PathType Leaf) -or
        (Get-Content -Raw -LiteralPath $currentnessPath).Trim() -cne 'PASS') {
        throw 'Timed workload functional evidence is incomplete or failed.'
    }
    $functionality='PASS'
    if ($Role -eq 'Candidate') {
        $performance=if ($summary.dispatchMs.p95 -le $P95LimitMs) { 'PASS' } else { 'FAIL' }
    }
    if (!$EnablePostGc) { throw 'Post-GC lifecycle evidence is required; use an opt-in build and -EnablePostGc.' }
    $post=(& (Join-Path $PSScriptRoot 'Invoke-UiPostRunGc.ps1') -RunDirectory $run | ConvertFrom-Json)
    if ($null -eq $post -or $post.status -cne 'PASS') { throw 'Post-GC lifecycle acceptance is missing or failed.' }
    $lifecycle='PASS'
} catch {
    $driverError=$_.ToString()
} finally {
    if ($uiUsed) {
        try {
            $global:LASTEXITCODE=$null
            & winapp ui yield --json | Out-Null
            if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) { throw 'winapp UI workflow yield failed.' }
        } catch { if ($null -eq $driverError) { $driverError=$_.ToString() } }
    }
    if ($null -eq $previousWorkflow) { Remove-Item -LiteralPath 'Env:WINAPP_UI_WORKFLOW_ID' -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable('WINAPP_UI_WORKFLOW_ID',$previousWorkflow,'Process') }
}
$status=if ($driverError -or $collection -ne 'PASS' -or $functionality -ne 'PASS' -or $lifecycle -ne 'PASS' -or $performance -eq 'FAIL') {
    'FAIL'
} elseif ($Role -eq 'Baseline') { 'NOT_EVALUATED' } else { 'PASS' }
$report=[ordered]@{
    status=$status; role=$Role; error=$driverError
    collection=[ordered]@{status=$collection;observation='summary.json'}
    functionality=[ordered]@{status=$functionality;observation='results.json'}
    performance=[ordered]@{status=$performance;p95Ms=$(if($summary){$summary.dispatchMs.p95}else{$null});limitMs=$(if($Role -eq 'Candidate'){$P95LimitMs}else{$null})}
    lifecycle=[ordered]@{status=$lifecycle;observation='post-gc-observation.json';acceptance='post-gc-results.json'}
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'ui-acceptance-results.json') -Encoding utf8
$report | ConvertTo-Json -Depth 8
if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'NOT_EVALUATED') { exit 2 }
Write-Host "UI fixture completed. PID $($process.Id) remains running; close normally after visual review."
exit 0
