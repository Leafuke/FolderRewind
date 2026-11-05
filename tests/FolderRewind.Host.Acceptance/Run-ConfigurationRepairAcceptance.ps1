param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$RunDirectory,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($Executable)
$run = [IO.Path]::GetFullPath($RunDirectory)
if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or (Test-Path -LiteralPath $run)) {
    throw 'Executable missing or isolated run directory already exists.'
}
New-Item -ItemType Directory -Path $run | Out-Null
Set-Content -LiteralPath (Join-Path $run 'acceptance-owner') -Value 'FolderRewind isolated configuration repair acceptance'
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'executable-sha256.json')
$fixtureEnvironment = @{
    FOLDERREWIND_TEST_DATA_ROOT = (Join-Path $run 'appdata')
    FOLDERREWIND_CONFIGURATION_REPAIR = '1'
    FOLDERREWIND_PERFORMANCE_UI = '0'; FOLDERREWIND_MANAGER_UI = '0'; FOLDERREWIND_SPATIAL_UI = '0'
    FOLDERREWIND_UI_PROFILING = '0'; FOLDERREWIND_UI_POST_GC = '0'
}
$previousEnvironment = @{}
foreach ($name in $fixtureEnvironment.Keys) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($name in $fixtureEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $fixtureEnvironment[$name], 'Process') }
    $process = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
} finally {
    foreach ($name in $fixtureEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
Set-Content -LiteralPath (Join-Path $run 'pid.txt') -Value $process.Id
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$resultPath = Join-Path $run 'configuration-repair-results.json'
$confirmed = @{}
try {
    while (!(Test-Path -LiteralPath $resultPath)) {
        if ($process.HasExited) { throw "Isolated Host exited before completing configuration acceptance: $($process.ExitCode)" }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Configuration repair acceptance timed out.' }
        foreach ($ordinal in 1..2) {
            $ready = Join-Path $run "history-confirm-$ordinal.ready.json"
            if ($confirmed.ContainsKey($ordinal) -or !(Test-Path -LiteralPath $ready)) { continue }
            $windows = @(winapp ui list-windows -a "$($process.Id)" --json | ConvertFrom-Json)
            if ($LASTEXITCODE -ne 0 -or $windows.Count -ne 1) { throw 'Expected one isolated Host window.' }
            $handle = [string]$windows[0].hwnd
            winapp ui wait-for PrimaryButton -w $handle -t 15000 --json | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Real History boundary confirmation $ordinal did not appear." }
            winapp ui screenshot -w $handle -o (Join-Path $run "history-confirm-$ordinal.png") --json | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not capture History confirmation evidence.' }
            winapp ui invoke PrimaryButton -w $handle --json | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Real History boundary confirmation $ordinal failed." }
            $confirmed[$ordinal] = $true
        }
        Start-Sleep -Milliseconds 100
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -ne 'PASS') { throw $result.error }
    if ($confirmed.Count -ne 2 -or $result.groups -ne 7) { throw 'The full seven-group Host acceptance did not run.' }
    winapp ui screenshot -w $handle -o (Join-Path $run 'configuration-repair-final.png') --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture final Host evidence.' }
    $result | ConvertTo-Json -Depth 12
} finally {
    try { winapp ui yield --on local | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    Write-Host "Review the result and close isolated PID $($process.Id) normally."
}
