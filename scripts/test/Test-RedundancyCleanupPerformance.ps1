[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunRoot)
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$identity = Get-Content (Join-Path $run 'candidate/identity.json') -Raw | ConvertFrom-Json
$perf = Join-Path $run 'performance'
New-Item -ItemType Directory -Path $perf -Force | Out-Null
$session = Join-Path $perf ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session | Out-Null
$plugin = Join-Path $session 'candidate-plugin'
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $run 'candidate/MineRewind.frplugin'), $plugin)
function Invoke-PerformanceDotnet([string]$Name, [string[]]$Arguments) {
    & dotnet.exe @Arguments *> "$session/$Name.log"
    if ($LASTEXITCODE -ne 0) { throw "Performance command failed: $Name" }
}
$region = Get-ChildItem "$run/ui-fixture/neoforge" -Filter 'r.*.*.mca' -File -Recurse | Sort-Object Length -Descending | Select-Object -First 1
if (!$region) { throw 'A pinned real-world copy is required for the performance input.' }
$inputHash = (Get-FileHash -LiteralPath $region.FullName).Hash
foreach ($variant in @('baseline','candidate')) {
    $runtime = if ($variant -eq 'baseline') { "$run/baseline/host-tests/FolderRewind.Plugin.Runtime.dll" } else { (Join-Path $identity.formalDirectory "FolderRewind.Plugin.Runtime.dll") }
    $abstractions = if ($variant -eq 'baseline') { "$run/baseline/host-tests/FolderRewind.Plugin.Abstractions.dll" } else { (Join-Path $identity.formalDirectory "FolderRewind.Plugin.Abstractions.dll") }
    $world = if ($variant -eq 'baseline') { "$run/baseline/world/MineRewind.World.dll" } else { "$plugin/MineRewind.World.dll" }
    $bin = "$session/$variant-bin"; $obj = "$session/$variant-obj/"
    Invoke-PerformanceDotnet "$variant-build" @('build',"$repo/tools/RedundancyCleanupPerformanceHost/RedundancyCleanupPerformanceHost.csproj",'-c','Release',
        "-p:RuntimeAssembly=$runtime","-p:AbstractionsAssembly=$abstractions","-p:WorldAssembly=$world",
        "-p:WorldDependencyDirectory=$plugin","-p:OutputPath=$bin/","-p:BaseIntermediateOutputPath=$obj")
    $runtimeDirectory = Split-Path $runtime -Parent
    if (Test-Path "$runtimeDirectory/NuGet.Versioning.dll") { Copy-Item "$runtimeDirectory/NuGet.Versioning.dll" $bin }
    Invoke-PerformanceDotnet "$variant-micro" @("$bin/RedundancyCleanupPerformanceHost.dll","$session/$variant-micro",$region.FullName)
}
Invoke-PerformanceDotnet 'history-build' @('build',"$repo/tools/HistoryMetadataPerformanceHost/HistoryMetadataPerformanceHost.csproj",'-c','Release','--no-restore')
$history = "$session/history-probe"
Copy-Item "$repo/tools/HistoryMetadataPerformanceHost/bin/Release/net10.0" $history -Recurse
foreach ($assembly in @('FolderRewind.Core.dll','FolderRewind.Plugin.Runtime.dll','FolderRewind.Plugin.Abstractions.dll')) {
    Copy-Item -LiteralPath (Join-Path $identity.formalDirectory $assembly) -Destination $history -Force
}
Invoke-PerformanceDotnet 'history-warmup' @("$history/HistoryMetadataPerformanceHost.dll","$session/history-warmup",'10')
for ($round = 1; $round -le 3; $round++) {
    Invoke-PerformanceDotnet "history-$round" @("$history/HistoryMetadataPerformanceHost.dll","$session/history-$round",'500,2000')
}
if ((Get-FileHash -LiteralPath $region.FullName).Hash -ne $inputHash) { throw 'Performance input changed.' }
$probeIdentity = @{candidateIdentitySha256=(Get-FileHash "$run/candidate/identity.json").Hash;region=$region.FullName;regionSha256=$inputHash;
    pluginSha256=(Get-FileHash "$run/candidate/MineRewind.frplugin").Hash;
    files=@(Get-ChildItem "$session/baseline-bin","$session/candidate-bin",$history -Filter '*.dll' -File | Sort-Object FullName | ForEach-Object {
        @{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })}
$probeIdentity | ConvertTo-Json -Depth 10 | Set-Content "$session/probe-identity.json" -Encoding utf8
& python "$repo/benchmarks/performance/Summarize-RedundancyCleanup.py" $run $session
if ($LASTEXITCODE -ne 0) { throw 'Performance comparison failed.' }
Get-Content "$perf/summary.json" -Raw
