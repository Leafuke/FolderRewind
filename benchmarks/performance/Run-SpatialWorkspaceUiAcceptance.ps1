param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$RunDirectory,
    [switch]$LifetimeProbe, [ValidateScript({ $_ -in @(2,5,20) })][int]$LifetimePages=2,
    [ValidateScript({ $_ -in @(10000,50000,100000) })][int]$RowCount=100000)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($Executable)
$run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
if (Test-Path -LiteralPath $run) { throw "Run directory already exists: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
Set-Content -LiteralPath (Join-Path $run 'acceptance-owner') -Value 'FolderRewind isolated spatial UI fixture'
$fixtureEnvironment=@{
    FOLDERREWIND_TEST_DATA_ROOT=(Join-Path $run 'appdata'); FOLDERREWIND_SPATIAL_UI='1'
    FOLDERREWIND_PERFORMANCE_UI='0'; FOLDERREWIND_UI_POST_GC='0'; FOLDERREWIND_UI_PROFILING='0'; FOLDERREWIND_MANAGER_UI='0'
    FOLDERREWIND_SPATIAL_LIFETIME_ONLY=$(if($LifetimeProbe) { '1' } else { '0' })
    FOLDERREWIND_SPATIAL_LIFETIME_PAGES="$LifetimePages"
    FOLDERREWIND_SPATIAL_ROWS="$RowCount"
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
function Invoke-Checked([string[]]$Arguments) {
    $global:LASTEXITCODE=$null
    $output=@(& winapp @Arguments)
    if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ') $($output -join ' ')" }
    $output
}
function Wait-File([string]$Name, [int]$Seconds=30) {
    $path=Join-Path $run $Name
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    while (!(Test-Path -LiteralPath $path) -and [DateTime]::UtcNow -lt $deadline) {
        $final=Join-Path $run 'spatial-results.json'
        if ($Name -ne 'spatial-results.json' -and (Test-Path -LiteralPath $final)) {
            $status=Get-Content -Raw -LiteralPath $final | ConvertFrom-Json
            if ($status.status -eq 'FAIL') { throw $status.error }
        }
        if ($process.HasExited) { throw "Fixture process exited: $($process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path -LiteralPath $path)) { throw "Timed out: $Name" }
    Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}
function Request([string]$Name) { Set-Content -LiteralPath (Join-Path $run $Name) -Value 'UI driver observed previous stage.' }
function Require-Status($Result, [string]$Expected, [string]$Name) {
    if ($null -eq $Result -or $Result.status -cne $Expected) { throw "$Name did not produce $Expected evidence." }
}
function Capture([string]$Name) {
    $path=Join-Path $run $Name
    Invoke-Checked @('ui','screenshot','-w',"$mainHwnd",'-o',$path,'--json') | Set-Content -LiteralPath ($path+'.json')
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Screenshot missing: $path" }
}
function Nodes($items) { foreach($item in $items) { if ($null -ne $item) { $item; Nodes $item.children } } }
function Focus-Home([string]$EvidenceName) {
    $output=@(& winapp ui focus NavHome -w "$mainHwnd" --json)
    $focusCode=$LASTEXITCODE
    $path=Join-Path $run $EvidenceName
    if($focusCode -eq 0) { $output | Set-Content -LiteralPath $path; return }
    $failure=$output -join "`n"
    $failure | Set-Content -LiteralPath ($path+'.failed-activation.json')
    try { $reason=$failure | ConvertFrom-Json } catch { throw 'Home focus failed without a structured reason.' }
    if($reason.error.code -cne 'foreground_not_target') { throw "Home focus failed: $failure" }
    # Input readiness is outside scan timing. Do not send input to a different foreground window.
    $name=[IO.Path]::GetFileNameWithoutExtension($EvidenceName)
    $ready=Join-Path $run ($name+'.foreground-restored.request')
    [ordered]@{status='NEEDS_FOREGROUND';pid=$process.Id;hwnd=$mainHwnd;executable=$exe;resumeFile=$ready} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run ($name+'.foreground-required.json'))
    $deadline=[DateTime]::UtcNow.AddMinutes(5)
    while(!(Test-Path -LiteralPath $ready)) {
        if($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'The verified fixture window was not restored to the foreground.' }
        Start-Sleep -Milliseconds 100
    }
    $windows=@(Invoke-Checked @('ui','list-windows','-a',"$($process.Id)",'--json') | ConvertFrom-Json)
    if(@($windows | Where-Object { [string]$_.hwnd -eq [string]$mainHwnd -and $_.isForeground }).Count -ne 1) {
        throw 'Foreground restoration did not match the recorded fixture window.'
    }
    Invoke-Checked @('ui','focus','NavHome','-w',"$mainHwnd",'--json') | Set-Content -LiteralPath $path
}
$env:WINAPP_UI_WORKFLOW_ID='FolderRewind-spatial-ui-'+[guid]::NewGuid().ToString('N')
$driverError=$null
$result=$null
try {
    if($LifetimeProbe) { Require-Status (Wait-File 'diagnostic-ready.json') 'READY' 'Diagnostic setup' }
    else {
        $full=Wait-File 'full-ready.json' 1020
        Require-Status $full 'OBSERVED' 'Full scan'
        if ($full.overlayDrained -ne $true) { throw 'The final overlay was not drained.' }
        $metrics=Wait-File 'spatial-index-observation.json'
        Require-Status $metrics 'OBSERVED' 'Spatial index metrics'
        if ($metrics.rowCount -ne $RowCount -or $null -eq $metrics.scanMs -or $metrics.scanMs -le 0 -or
            $null -eq $metrics.operations -or $null -eq $metrics.ui -or @($metrics.ui).Count -eq 0) {
            throw 'Spatial index metrics are incomplete.'
        }
        if ([double]::IsNaN([double]$metrics.scanMs) -or [double]::IsInfinity([double]$metrics.scanMs)) {
            throw 'Spatial scan time is invalid.'
        }
        if ($metrics.peakOperations -lt 1 -or $metrics.stopwatchFrequency -le 0) { throw 'Spatial concurrency evidence is missing.' }
        foreach ($kind in @('put','presentation','overlay')) {
            if (@($metrics.operations | Where-Object { $_.Kind -ceq $kind }).Count -eq 0) {
                throw "Spatial index metrics are missing $kind samples."
            }
            if (@($metrics.operations | Where-Object { $_.Kind -ceq $kind -and $null -ne $_.Phases -and @($_.Phases).Count -gt 0 }).Count -eq 0) {
                throw "Spatial index metrics are missing $kind phase samples."
            }
        }
        $expectedPuts=[int]($RowCount / 500)
        if (@($metrics.operations | Where-Object { $_.Kind -ceq 'put' }).Count -ne $expectedPuts) {
            throw "Expected $expectedPuts recorded Put operations."
        }
        if (@($metrics.operations | Group-Object Id | Where-Object Count -gt 1).Count -ne 0) { throw 'Duplicate operation identifiers.' }
        foreach ($sample in $metrics.operations) {
            foreach ($name in @('Id','Kind','WaitMilliseconds','HoldMilliseconds','Commands','BeginVersion','EndVersion','Canceled','Phases','Context')) {
                if ($null -eq $sample.$name) { throw "Spatial operation sample is missing $name." }
            }
            if ($sample.Kind -cin @('presentation','overlay')) {
                $fields=if ($sample.Kind -ceq 'presentation') { @('query','offset','merge') } else { @('query','bounds','scale','grid') }
                foreach ($name in $fields) {
                    if ([string]::IsNullOrWhiteSpace([string]$sample.Context.$name)) { throw "Spatial $($sample.Kind) sample is missing context $name." }
                }
                foreach ($name in $(if ($sample.Kind -ceq 'presentation') { @('query') } else { @('query','bounds') })) {
                    try { $parsed=$sample.Context.$name | ConvertFrom-Json } catch { throw "Spatial $($sample.Kind) context $name is not JSON." }
                    if ($null -eq $parsed) { throw "Spatial $($sample.Kind) context $name is null." }
                }
            }
            foreach ($name in @('WaitMilliseconds','HoldMilliseconds')) {
                $value=[double]$sample.$name
                if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { throw "Invalid operation $name." }
            }
            if ($sample.Id -le 0 -or $sample.Commands -lt 0 -or $sample.BeginVersion -lt 0 -or $sample.EndVersion -lt $sample.BeginVersion) {
                throw 'Invalid spatial operation identity, commands or versions.'
            }
            foreach ($phase in $sample.Phases) {
                foreach ($name in @('Name','Milliseconds','Calls','Commands')) {
                    if ($null -eq $phase.$name) { throw "Spatial phase sample is missing $name." }
                }
                $value=[double]$phase.Milliseconds
                if ([string]::IsNullOrWhiteSpace($phase.Name) -or [double]::IsNaN($value) -or
                    [double]::IsInfinity($value) -or $value -lt 0 -or $phase.Calls -lt 1 -or $phase.Commands -lt 0) {
                    throw 'Invalid spatial phase sample.'
                }
            }
        }
        foreach ($sample in $metrics.ui) {
            foreach ($name in @('Kind','Generation','IndexVersion','ApplyMilliseconds','Published')) {
                if ($null -eq $sample.$name) { throw "Spatial UI sample is missing $name." }
            }
            if ($sample.Kind -cnotin @('rows','overlay') -or
                $null -eq $sample.PSObject.Properties['QueueMilliseconds'] -or
                ($sample.Kind -ceq 'rows' -and $null -eq $sample.QueueMilliseconds)) {
                throw 'Spatial UI sample kind or rows dispatch timing is missing.'
            }
            foreach ($name in @('QueueMilliseconds','ApplyMilliseconds')) {
                if ($null -eq $sample.$name) { continue } # Overlay dispatch timing is not measured.
                $value=[double]$sample.$name
                if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { throw "Invalid UI $name." }
            }
            if ($sample.Generation -lt 0 -or $sample.IndexVersion -lt 0) { throw 'Invalid spatial UI generation or index version.' }
        }
        if (@($metrics.ui | Where-Object { $_.Kind -ceq 'rows' -and $_.Published -eq $true -and $_.IndexVersion -ge $expectedPuts }).Count -eq 0) {
            throw 'No rows publication includes the final index generation.'
        }
        if (@($metrics.ui | Where-Object { $_.Kind -ceq 'overlay' -and $_.Published -eq $true }).Count -eq 0) {
            throw 'No overlay application was observed.'
        }
        $snapshot=Wait-File 'spatial-index-snapshot.json' 120
        Require-Status $snapshot 'OBSERVED' 'Spatial database snapshot'
        if ($snapshot.file -cne 'full-spatial.db' -or $snapshot.rowCount -ne $RowCount -or
            $snapshot.bytes -le 0 -or $snapshot.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Spatial database snapshot metadata is incomplete.'
        }
        $snapshotPath=Join-Path $run 'full-spatial.db'
        if (!(Test-Path -LiteralPath $snapshotPath -PathType Leaf) -or (Get-Item -LiteralPath $snapshotPath).Length -ne $snapshot.bytes -or
            (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash -cne $snapshot.sha256) {
            throw 'Spatial database snapshot is missing or does not match its size and SHA-256.'
        }
        $replay=Wait-File 'spatial-query-replay.json' 120
        Require-Status $replay 'OBSERVED' 'Warm query replay'
        if ($replay.rowCount -ne $RowCount -or @($replay.operations).Count -ne 3 -or
            @($replay.operations | Where-Object { $_.Kind -cne 'presentation' -or $_.Canceled }).Count -ne 0) {
            throw 'Three complete warm presentation reads were not recorded.'
        }
    }
    $windows=@(Invoke-Checked @('ui','list-windows','-a',"$($process.Id)",'--json') | ConvertFrom-Json)
    $main=$windows | Where-Object { $_.hwnd -and $_.title -ne 'PopupHost' } | Select-Object -First 1
    if (!$main) { throw 'Main spatial fixture window missing.' }
    $mainHwnd=$main.hwnd
    $tree=Invoke-Checked @('ui','inspect','-w',"$mainHwnd",'-d','8','--json') | ConvertFrom-Json
    $tree | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $run 'initial-tree.json')
    $search=Nodes @($tree.windows | ForEach-Object elements) | Where-Object automationId -eq 'SpatialWorkspace_Search' | Select-Object -First 1
    if (!$search -or $search.isOffscreen) { Invoke-Checked @('ui','invoke','SpatialWorkspace_List','-w',"$mainHwnd",'--json') | Out-Null }
    Invoke-Checked @('ui','wait-for','SpatialWorkspace_Search','-w',"$mainHwnd",'-t','3000') | Out-Null
    Capture '01-full-page.png'
    if(!$LifetimeProbe) {
    Request 'rapid.request'
    Require-Status (Wait-File 'rapid-results.json') 'OBSERVED' 'Rapid filter'
    Invoke-Checked @('ui','wait-for','SpatialWorkspace_Search','--value','Cell 00234','-w',"$mainHwnd",'-t','3000') | Out-Null
    Capture '02-filtered-page.png'
    }
    Request 'prepare-cancel.request'
    Require-Status (Wait-File 'cancel-ready.json') 'READY' 'Stop setup'
    Invoke-Checked @('ui','wait-for','SpatialWorkspace2_Stop','-p','IsEnabled','--value','True','-w',"$mainHwnd",'-t','3000') | Out-Null
    Invoke-Checked @('ui','invoke','SpatialWorkspace2_Stop','-w',"$mainHwnd",'--json') | Out-Null
    Request 'cancel-invoked.request'
    Require-Status (Wait-File 'cancel-results.json') 'OBSERVED' 'Stop'
    Capture '03-canceled-page.png'
    Request 'prepare-close.request'
    Require-Status (Wait-File 'close-ready.json' 180) 'READY' 'Back setup'
    Invoke-Checked @('ui','invoke','SpatialWorkspace_Back','-w',"$mainHwnd",'--json') | Out-Null
    Request 'close-invoked.request'
    Require-Status (Wait-File 'gc-ready.json') 'READY' 'Back drain'
    Focus-Home 'home-focus.json'
    Invoke-Checked @('ui','yield','--json') | Set-Content -LiteralPath (Join-Path $run 'pre-gc-yield.json')
    Request 'gc.request'
    $weakResult=Wait-File 'weak-lifetime.json'
    Require-Status $weakResult 'OBSERVED' 'Back lifetime'
    Require-Status (Wait-File 'navigation-reset-ready.json') 'READY' 'Normal-navigation setup'
    Invoke-Checked @('ui','invoke','NavTasks','-w',"$mainHwnd",'--json') | Set-Content -LiteralPath (Join-Path $run 'navigate-tasks.json')
    Request 'tasks-visited.request'
    Require-Status (Wait-File 'tasks-observation.json') 'OBSERVED' 'Tasks frame'
    Invoke-Checked @('ui','invoke','NavHome','-w',"$mainHwnd",'--json') | Set-Content -LiteralPath (Join-Path $run 'navigate-home.json')
    Focus-Home 'navigation-home-focus.json'
    Invoke-Checked @('ui','yield','--json') | Set-Content -LiteralPath (Join-Path $run 'navigation-yield.json')
    Request 'navigation-gc.request'
    Require-Status (Wait-File 'navigation-lifetime.json') 'OBSERVED' 'Normal-navigation lifetime'
    Require-Status (Wait-File 'spatial-observation.json') 'OBSERVED' 'Spatial collection'
    $result=Wait-File 'spatial-results.json'
    $expectedStatus=if($LifetimeProbe) { 'PASS' } else { 'NOT_EVALUATED' }
    Require-Status $result $expectedStatus 'Spatial acceptance'
    Require-Status $result.collection 'PASS' 'Collection acceptance'
    Require-Status $result.functionality 'PASS' 'Functional acceptance'
    Require-Status $result.lifecycle 'PASS' 'Lifecycle acceptance'
    Capture '04-navigated-away.png'
} catch {
    $driverError=$_
} finally {
    try { Invoke-Checked @('ui','yield','--json') | Out-Null } catch { if ($null -eq $driverError) { $driverError=$_ } }
    if ($null -eq $previousWorkflow) { Remove-Item -LiteralPath 'Env:WINAPP_UI_WORKFLOW_ID' -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable('WINAPP_UI_WORKFLOW_ID',$previousWorkflow,'Process') }
}
if ($null -ne $driverError) {
    [ordered]@{status='FAIL';collection='FAIL';error=$driverError.ToString()} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $run 'spatial-driver-results.json') -Encoding utf8
    throw $driverError
}
[ordered]@{status=$result.status;collection='PASS';functionality=$result.functionality;performance=$result.performance;lifecycle=$result.lifecycle} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'spatial-driver-results.json') -Encoding utf8
$result | ConvertTo-Json -Depth 8
Write-Host "UI fixture completed. PID $($process.Id) remains running; close normally after visual review."
if ($result.status -ceq 'NOT_EVALUATED') { exit 2 }
exit 0
