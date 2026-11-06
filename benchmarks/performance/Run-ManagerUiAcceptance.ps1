param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($Executable); $run=[IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or (Test-Path -LiteralPath $run)) { throw 'Executable missing or fixture directory already exists.' }
New-Item -ItemType Directory -Path $run | Out-Null
Set-Content -LiteralPath (Join-Path $run 'acceptance-owner') -Value 'FolderRewind isolated manager UI acceptance'
$fixtureEnvironment=@{
    FOLDERREWIND_TEST_DATA_ROOT=(Join-Path $run 'appdata'); FOLDERREWIND_MANAGER_UI='1'
    FOLDERREWIND_PERFORMANCE_UI='0'; FOLDERREWIND_UI_PROFILING='0'; FOLDERREWIND_UI_POST_GC='0'; FOLDERREWIND_SPATIAL_UI='0'
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
Set-Content -LiteralPath (Join-Path $run 'pid.txt') -Value $process.Id
$previousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID=[guid]::NewGuid().ToString('N')
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    do { if ($process.HasExited) { throw 'Application exited during startup.' }; Start-Sleep -Milliseconds 500 } until ((Test-Path (Join-Path $run 'ready.txt')) -or [DateTime]::UtcNow -gt $deadline)
    if (!(Test-Path (Join-Path $run 'ready.txt'))) { throw 'Fixture was not ready.' }
    $windows=@(winapp ui list-windows -a "$($process.Id)" --json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $windows.Count -ne 1) { throw 'Expected one app window.' }
    $handle=[string]$windows[0].hwnd
    winapp ui invoke NavManager -w $handle --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Manager navigation failed.' }
    winapp ui wait-for FolderManagerFolderList -w $handle -t 10000 --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Folder list not available.' }
    winapp ui scroll FolderManagerFolderList -w $handle --wheel -15 --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Scrolling failed.' }
    Start-Sleep -Milliseconds 750
    $tree=winapp ui inspect -w $handle --interactive --depth 15 --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Inspect failed.' }
    function Nodes($items) { foreach($item in $items) { if($null -ne $item) { $item; if($item.children) { Nodes $item.children } } } }
    $folder=@(Nodes @($tree.windows | ForEach-Object { $_.elements })) | Where-Object { $_.automationId -like 'FolderManagerFolder_*' -and -not $_.isOffscreen } | Select-Object -First 1
    if (!$folder) { throw 'No visible folder after scrolling.' }
    winapp ui invoke "$($folder.automationId)" -w $handle --action select --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Folder selection failed.' }
    winapp ui screenshot -w $handle -o (Join-Path $run 'before.png') --json | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path (Join-Path $run 'before.png'))) { throw 'Before screenshot failed.' }
    Set-Content -LiteralPath (Join-Path $run 'reset.request') -Value 'reset'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 200 } until ((Test-Path (Join-Path $run 'reset-result.json')) -or (Test-Path (Join-Path $run 'manager-results.json')) -or [DateTime]::UtcNow -gt $deadline)
    if (Test-Path (Join-Path $run 'manager-results.json')) { throw (Get-Content -Raw (Join-Path $run 'manager-results.json')) }
    if (!(Test-Path (Join-Path $run 'reset-result.json'))) { throw 'Reset fixture timed out.' }
    winapp ui screenshot -w $handle -o (Join-Path $run 'after.png') --json | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path (Join-Path $run 'after.png'))) { throw 'After screenshot failed.' }
    Set-Content -LiteralPath (Join-Path $run 'replace.request') -Value 'replace all source models with same IDs'
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 200 } until ((Test-Path (Join-Path $run 'replacement-result.json')) -or (Test-Path (Join-Path $run 'manager-results.json')) -or [DateTime]::UtcNow -gt $deadline)
    if (Test-Path (Join-Path $run 'manager-results.json')) { throw (Get-Content -Raw (Join-Path $run 'manager-results.json')) }
    if (!(Test-Path (Join-Path $run 'replacement-result.json'))) { throw 'Model replacement timed out.' }
    winapp ui screenshot -w $handle -o (Join-Path $run 'replacement.png') --json | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path (Join-Path $run 'replacement.png'))) { throw 'Model replacement screenshot failed.' }
    Set-Content -LiteralPath (Join-Path $run 'switch.request') -Value 'switch'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 200 } until ((Test-Path (Join-Path $run 'manager-results.json')) -or [DateTime]::UtcNow -gt $deadline)
    if (!(Test-Path (Join-Path $run 'manager-results.json'))) { throw 'Switch fixture timed out.' }
    $result=Get-Content -Raw (Join-Path $run 'manager-results.json') | ConvertFrom-Json
    if ($result.status -ne 'PASS') { throw $result.error }
    $result | ConvertTo-Json -Depth 5
} finally {
    try { winapp ui yield --on local | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'UI workflow release failed.' } }
    finally { $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
    Write-Host "Close isolated PID $($process.Id) normally after reviewing results."
}
