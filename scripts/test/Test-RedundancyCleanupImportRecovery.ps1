[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunRoot)
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$identity = Get-Content "$run/candidate/identity.json" -Raw | ConvertFrom-Json
$summary = Get-Content "$run/data/summary.json" -Raw | ConvertFrom-Json -AsHashtable
if (!$summary.success -or $summary.candidateIdentitySha256 -ne (Get-FileHash "$run/candidate/identity.json").Hash) { throw 'Real-world evidence belongs to a different candidate.' }
$session = Join-Path $run ('data/ir-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $session | Out-Null
$plugin = Join-Path $session 'plugin'
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $run 'candidate/MineRewind.frplugin'), $plugin)
$bin = Join-Path $session 'bin'
$obj = (Join-Path $session 'obj') + '/'
& dotnet.exe build "$repo/tools/RedundancyCleanupPerformanceHost/RedundancyCleanupPerformanceHost.csproj" -c Release `
    "-p:RuntimeAssembly=$($identity.formalDirectory)/FolderRewind.Plugin.Runtime.dll" `
    "-p:AbstractionsAssembly=$($identity.formalDirectory)/FolderRewind.Plugin.Abstractions.dll" `
    "-p:WorldAssembly=$plugin/MineRewind.World.dll" "-p:WorldDependencyDirectory=$plugin" `
    "-p:ApplicationManifest=$repo/src/FolderRewind/app.manifest" `
    "-p:OutputPath=$bin/" "-p:BaseIntermediateOutputPath=$obj" *> "$session/build.log"
if ($LASTEXITCODE -ne 0) { throw 'Import recovery probe failed to build.' }
Copy-Item -LiteralPath "$($identity.formalDirectory)/NuGet.Versioning.dll" -Destination $bin
$runtime = 'FolderRewind.Plugin.Runtime.dll'
if ((Get-FileHash "$bin/$runtime").Hash -ne (Get-FileHash "$($identity.formalDirectory)/$runtime").Hash) { throw 'Recovery probe did not load the frozen Runtime.' }
$probe = Join-Path $bin 'RedundancyCleanupPerformanceHost.exe'
function Probe([string]$Mode, [string]$Workspace, [string]$Format, [string]$Point, [string]$Log) {
    $process = Start-Process -FilePath $probe -ArgumentList @($Mode,('"' + $Workspace + '"'),$Format,$Point) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Log.out.log" -RedirectStandardError "$Log.err.log"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.WaitForExit(1000)) { if ($watch.Elapsed.TotalSeconds -gt 90) { $process.Kill(); throw 'Owned repair probe timed out.' } }
    if ($Mode -eq '--repair-crash') {
        if ($process.ExitCode -eq 0 -or !(Test-Path "$Workspace/fault-witness") -or (Get-Content "$Workspace/fault-witness" -Raw) -ne $Point) { throw 'The process did not crash at the required repair boundary.' }
    } elseif ($process.ExitCode -ne 0) { throw "Repair probe failed: $Mode; see $Log.err.log" }
}
$results = @()
foreach ($format in @('v1','v2')) {
    foreach ($point in @('RepairPrepared:after','RepairOldMoved:after','RepairInstalled:after')) {
        $workspace = Join-Path $session ($format + '-' + $point.Replace(':','-'))
        Probe '--repair-create' $workspace $format $point "$workspace-create"
        Probe '--repair-crash' $workspace $format $point "$workspace-crash"
        foreach ($round in 1,2) {
            Probe '--repair-recover' $workspace $format $point "$workspace-recover-$round"
            $result = Get-Content "$workspace/recovered.json" -Raw | ConvertFrom-Json
            if (!$result.Success -or !$result.ImmutableLedger -or !$result.LogicalAndStorageHashesVerified -or !$result.NoPendingRoots) { throw 'Invalid repair recovery witness.' }
        }
        $results += @{format=$format;point=$point;success=$true;workspace=$workspace;recoveryProcesses=2}
        Write-Host "Passed process import repair: $format $point (two recovery processes)"
    }
}
$evidence = @{success=$true;session=$session;cases=$results;candidateIdentitySha256=(Get-FileHash "$run/candidate/identity.json").Hash;
    probeSha256=(Get-FileHash $probe).Hash;runtimeSha256=(Get-FileHash "$bin/$runtime").Hash;codec='BCL bounded fixture; Runtime transaction is the exact production candidate'}
$evidence | ConvertTo-Json -Depth 10 | Set-Content "$session/results.json" -Encoding utf8
$summary.importRepair = $evidence
$summary | ConvertTo-Json -Depth 30 | Set-Content "$run/data/summary.json" -Encoding utf8
