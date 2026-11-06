param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($Executable)
$run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
if (Test-Path -LiteralPath $run) { throw "Run directory already exists: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
Set-Content -LiteralPath (Join-Path $run 'acceptance-owner') -Value 'FolderRewind isolated UI profiling fixture'
$fixtureEnvironment=@{
    FOLDERREWIND_TEST_DATA_ROOT=(Join-Path $run 'appdata'); FOLDERREWIND_PERFORMANCE_UI='1'
    FOLDERREWIND_UI_PROFILING='1'; FOLDERREWIND_UI_POST_GC='0'; FOLDERREWIND_MANAGER_UI='0'; FOLDERREWIND_SPATIAL_UI='0'
}
$previousEnvironment=@{}
foreach($name in $fixtureEnvironment.Keys) { $previousEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
try {
    foreach($name in $fixtureEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$fixtureEnvironment[$name],'Process') }
    $process=Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
} finally {
    foreach($name in $fixtureEnvironment.Keys) {
        if($null -eq $previousEnvironment[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$previousEnvironment[$name],'Process') }
    }
}
$previousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
Set-Content -LiteralPath (Join-Path $run 'pid.txt') -Value $process.Id
function Invoke-WinAppChecked([string[]]$Arguments) {
    $global:LASTEXITCODE=$null
    $output=@(& winapp @Arguments)
    if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ') $($output -join ' ')" }
    $output
}
function Get-Nodes($items) { foreach($item in $items) { if ($null -ne $item) { $item; Get-Nodes $item.children } } }
$screenshotTaken=$false
$env:WINAPP_UI_WORKFLOW_ID='FolderRewind-ui-profile-'+[guid]::NewGuid().ToString('N')
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(100)
    $resultPath=Join-Path $run 'profile-results.json'
    while (!(Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) {
        $exerciseError=Join-Path $run 'exercise-error.txt'
        if (Test-Path -LiteralPath $exerciseError) { throw (Get-Content -Raw -LiteralPath $exerciseError) }
        if ($process.HasExited) { throw "Profiling process exited early: $($process.ExitCode)" }
        if (!$screenshotTaken -and (Test-Path -LiteralPath (Join-Path $run 'samples.csv'))) {
            $last=Get-Content -LiteralPath (Join-Path $run 'samples.csv') -Tail 1
            if ($last -match '^([0-9.]+),' -and [double]::Parse($matches[1], [Globalization.CultureInfo]::InvariantCulture) -ge 20) {
                $windows=@(Invoke-WinAppChecked @('ui','list-windows','-a',"$($process.Id)",'--json') | ConvertFrom-Json)
                $preview=$windows | Where-Object title -eq 'Performance preview fixture' | Select-Object -First 1
                if ($preview) {
                    Invoke-WinAppChecked @('ui','screenshot','-w',"$($preview.hwnd)",'-o',(Join-Path $run 'map.png'),'--json') | Set-Content -LiteralPath (Join-Path $run 'map-screenshot.json')
                    if (!(Test-Path -LiteralPath (Join-Path $run 'map.png') -PathType Leaf)) { throw 'Profiling screenshot missing.' }
                    $screenshotTaken=$true
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (!(Test-Path -LiteralPath $resultPath)) { throw 'Profiling did not write its 60-second result; process left running for diagnosis.' }
    $result=Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($result.status -ne 'OBSERVED' -or $result.samples -lt 1 -or $result.seconds -lt 60) { throw "Profiling failed: $(Get-Content -Raw -LiteralPath $resultPath)" }
    foreach ($name in @('ui-profile.csv','ui-profile-navigation.csv','ui-profile-window-state.csv')) {
        if (!(Test-Path -LiteralPath (Join-Path $run $name) -PathType Leaf)) { throw "Profiling observation missing: $name" }
    }
    if ($result.windowSamples -lt 1 -or $result.navigationSignals -lt 1) { throw 'Profiling did not record window or navigation observations.' }
    $drainPath=Join-Path $run 'profile-drained.json'
    $drainDeadline=[DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath $drainPath) -and [DateTime]::UtcNow -lt $drainDeadline) {
        $exerciseError=Join-Path $run 'exercise-error.txt'
        if (Test-Path -LiteralPath $exerciseError) { throw (Get-Content -Raw -LiteralPath $exerciseError) }
        if ($process.HasExited) { throw 'Profiling process exited before exercise drain.' }
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path -LiteralPath $drainPath)) { throw 'Profiling exercise did not drain before normal exit.' }
    $drain=Get-Content -Raw -LiteralPath $drainPath | ConvertFrom-Json
    if ($drain.status -ne 'OBSERVED' -or $drain.generations -ne 0) { throw 'Profiling provider did not drain.' }
    $samplesResultPath=Join-Path $run 'results.json'
    if (!(Test-Path -LiteralPath $samplesResultPath)) { throw 'Profiling sampler did not finish before exercise drain.' }
    $samplesResult=Get-Content -Raw -LiteralPath $samplesResultPath | ConvertFrom-Json
    if($samplesResult.failures.Count -ne 0) { throw "Profiling sampler failed: $($samplesResult.failures -join '; ')" }
    $windows=@(Invoke-WinAppChecked @('ui','list-windows','-a',"$($process.Id)",'--json') | ConvertFrom-Json)
    $main=$windows | Where-Object { $_.title -ne 'Performance preview fixture' -and $_.title -ne 'PopupHost' -and $_.hwnd } | Select-Object -First 1
    if (!$main) { throw 'Profiling main window missing.' }
    $mainHwnd=$main.hwnd
    $tree=Invoke-WinAppChecked @('ui','inspect','-w',"$mainHwnd",'--interactive','--json') | ConvertFrom-Json
    $tree | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $run 'close-tree.json')
    $close=Get-Nodes @($tree.windows | ForEach-Object elements) | Where-Object { $_.automationId -eq 'Close' -and $_.name -eq 'Close' -and !$_.isOffscreen } | Select-Object -First 1
    if (!$close) { throw 'Actual caption Close command missing.' }
    Invoke-WinAppChecked @('ui','invoke',$close.selector,'-w',"$mainHwnd",'--json') | Set-Content -LiteralPath (Join-Path $run 'close-invoke.json')
    $exit=Invoke-WinAppChecked @('ui','wait-for','SecondaryButton','-w',"$mainHwnd",'-p','IsOffscreen','--value','False','-t','5000','--json') | ConvertFrom-Json
    if (!$exit.found -or !$exit.element.selector) { throw 'Graceful exit dialog missing.' }
    $exit | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'exit-dialog.json')
    Invoke-WinAppChecked @('ui','invoke',$exit.element.selector,'-w',"$mainHwnd",'--json') | Set-Content -LiteralPath (Join-Path $run 'exit-invoke.json')
    if (!$process.WaitForExit(30000)) { throw 'Profiling app did not exit normally within 30 seconds.' }
    if (Test-Path -LiteralPath (Join-Path $run 'exercise-error.txt')) { throw 'Exercise failed during diagnostic shutdown.' }
    [pscustomobject]@{ status='OBSERVED'; seconds=$result.seconds; samples=$result.samples; cycles=$result.cycles; exerciseDrained=$true; exitedNormally=$true; exitCode=$process.ExitCode } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'driver-results.json')
    $result | ConvertTo-Json -Depth 6
    Write-Host '60-second diagnosis completed and app exited normally. This is not a ten-minute performance comparison.'
} catch {
    [pscustomobject]@{ status='ERROR'; error=$_.ToString(); pid=$process.Id } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'driver-results.json')
    throw
} finally {
    try { Invoke-WinAppChecked @('ui','yield','--json') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
}
