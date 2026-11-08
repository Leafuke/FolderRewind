[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunRoot)
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$identity = Get-Content "$run/candidate/identity.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$run/data/summary.json" -Raw | ConvertFrom-Json -AsHashtable
if (!$summary.success) { throw 'The real-world baseline stage must pass first.' }
if ($summary.candidateIdentitySha256 -ne (Get-FileHash "$run/candidate/identity.json").Hash) { throw 'Real-world evidence belongs to a different candidate.' }
# Verify checkpoint publication as well as the byte witnesses produced by the fixture.
foreach ($case in $summary.cases | Where-Object name -Like '*-deletions-checkpoint') {
    $report = Get-Content -LiteralPath $case.result -Raw | ConvertFrom-Json
    if (@($report.Checks).Count -ne 3 -or @($report.Checks.VersionId | Select-Object -Unique).Count -ne 3 -or $report.DeletedSlot -lt 0) { throw 'A deletion/checkpoint was not committed.' }
    $before = Get-Content -LiteralPath $report.Checks[0].Manifest -Raw | ConvertFrom-Json -AsHashtable
    $after = Get-Content -LiteralPath $report.Checks[1].Manifest -Raw | ConvertFrom-Json -AsHashtable
    $region = $report.ChangedRegion.Replace('\','/')
    if (!$before.Files.ContainsKey('redundancy-delete.fixture') -or $after.Files.ContainsKey('redundancy-delete.fixture') -or
        $before.Files[$region].Digest -eq $after.Files[$region].Digest -or $report.Checks[1].Digest -ne $report.Checks[2].Digest) { throw 'Deletion or checkpoint byte witnesses disagree.' }
}
$catalog = Get-Content "$repo/src/FolderRewind/Services/Onboarding/OnboardingToolAssets.cs" -Raw
$asset = [regex]::Match($catalog, '\("rclone", Architecture\.X64\) => \("(?<version>v[\d.]+)", "rclone/rclone", "(?<file>rclone-[^"]+\.zip)"')
if (!$asset.Success) { throw 'Pinned rclone identity missing.' }
$rclone = Join-Path $repo ("artifacts/test-tools/rclone/" + $asset.Groups['version'].Value + '/' + [IO.Path]::GetFileNameWithoutExtension($asset.Groups['file'].Value) + '/rclone.exe')
if (!(Test-Path -LiteralPath $rclone)) { throw 'Run the functional prerequisites to stage pinned rclone.' }
$workspace = Join-Path $run ('data/wdav-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$exe = Join-Path $identity.faultDirectory 'FolderRewind.exe'
$package = Join-Path $run 'candidate/MineRewind.frplugin'
$request = @{Workspace=$workspace;Phase='capture';Source=$summary.worlds[-1].copy;SourceId=[guid]::NewGuid().ToString('D');
    Package=$package;PackageSha256=(Get-FileHash -LiteralPath $package).Hash;SevenZip="$repo/7za.exe";Incremental=$true;Iterations=1;Encrypted=$false}
function Invoke-WebDavFixture([string]$ResultName) {
    $path = "$workspace-request.json"
    $request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8
    $process = Start-Process -FilePath $exe -ArgumentList '--native-artifact-acceptance', ('"' + $path + '"') -WindowStyle Hidden -PassThru
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.WaitForExit(1000)) { if ($watch.Elapsed.TotalMinutes -gt 8) { $process.Kill(); throw 'Owned WebDAV fixture timed out.' } }
    $errorFile = Join-Path $workspace ($request.Phase + '-error.txt')
    if (Test-Path -LiteralPath $errorFile) { throw (Get-Content -LiteralPath $errorFile -Raw) }
    $resultPath = Join-Path $workspace $ResultName
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath)) { throw "WebDAV fixture failed: $($request.Phase)" }
    $report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (!$report.Success) { throw 'WebDAV fixture did not pass.' }
    $report
}
$server = $null
try {
    $null = Invoke-WebDavFixture 'p3-capture-result.json'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $server = Start-Process -FilePath $rclone -ArgumentList @('serve','webdav',('"' + $workspace + '"'),"--addr=127.0.0.1:$port",'--dir-cache-time=1ms','--poll-interval=0',
        '--vfs-cache-mode=off',('--log-file="' + $workspace + '/webdav-server.log"'),'-vv') -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt=0; $attempt -lt 50; $attempt++) {
        if ($server.HasExited) { throw 'Owned WebDAV server exited.' }
        try { $client=[Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1',$port); $client.Dispose(); $ready=$true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'Owned WebDAV server did not become ready.' }
    Set-Content -LiteralPath "$workspace/cloud-local.conf" -Value "[acceptance]`ntype = webdav`nurl = http://127.0.0.1:$port/`nvendor = other`n" -Encoding utf8
    $request.Phase='cloud'; $request.Rclone=$rclone
    $result = Invoke-WebDavFixture 'cloud-result.json'
    $summary.webdav = @{success=$true;workspace=$workspace;result=$result;serverPid=$server.Id;loopback=$true;
        rclone=$rclone;rcloneSha256=(Get-FileHash -LiteralPath $rclone).Hash;candidateIdentitySha256=(Get-FileHash "$run/candidate/identity.json").Hash}
    $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$run/data/summary.json" -Encoding utf8
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
}
