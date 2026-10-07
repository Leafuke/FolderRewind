param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SevenZipPath,
    [Parameter(Mandatory)][string]$RclonePath,
    [Parameter(Mandatory)][string]$SamplesJson,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$CrashMatrix
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Use a new output directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
$app = (Resolve-Path -LiteralPath $AppPath).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$samples = Get-Content -LiteralPath $SamplesJson -Raw | ConvertFrom-Json
$reports = [Collections.Generic.List[object]]::new()
function Invoke-Acceptance([hashtable]$request, [string]$name, [bool]$expectCrash = $false) {
    $requestFile = Join-Path $root ($name + '.json')
    $request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestFile -Encoding utf8
    $process = Start-Process -FilePath $app -ArgumentList '--native-artifact-acceptance', ('"' + $requestFile + '"') -WindowStyle Hidden -PassThru
    $peakDisk = 0L
    while (-not $process.WaitForExit(1000)) {
        $disk = 0L
        if (Test-Path -LiteralPath $request.Workspace) {
            foreach ($file in Get-ChildItem -LiteralPath $request.Workspace -File -Recurse -ErrorAction SilentlyContinue) { $disk += $file.Length }
        }
        $peakDisk = [Math]::Max($peakDisk, $disk)
    }
    $process.Refresh()
    if ($expectCrash) {
        if ($process.ExitCode -eq 0) { throw "Fault did not interrupt $name" }
        return
    }
    $result = Join-Path $request.Workspace ($request.Phase + '-result.json')
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) {
        throw "Acceptance failed: $name; inspect $($request.Workspace)"
    }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    $report | Add-Member WorkspaceDiskPeakSampledBytes $peakDisk
    $reports.Add($report)
    $reports | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
}
for ($i = 0; $i -lt $samples.Count; $i++) {
    foreach ($encrypted in @($false, $true)) {
        $name = "sample-$i-$encrypted"
        $request = @{ Workspace = Join-Path $root $name; Phase = 'capture'; Package = $package; PackageSha256 = $hash
            SevenZip = (Resolve-Path -LiteralPath $SevenZipPath).Path; Source = $samples[$i].Path; SourceId = $samples[$i].SourceId
            Encrypted = $encrypted; Rclone = (Resolve-Path -LiteralPath $RclonePath).Path }
        Invoke-Acceptance $request "$name-capture"
        $request.Phase = 'restart'
        Invoke-Acceptance $request "$name-restart"
        if ($i -eq 0 -and -not $encrypted) {
            $request.Phase = 'faults'
            Invoke-Acceptance $request "$name-faults"
            $request.Phase = 'workflow'
            Invoke-Acceptance $request "$name-workflow"
        }
    }
}
if ($CrashMatrix) {
    foreach ($phase in 'Prepared','PayloadsSealed','ArtifactGraphCommitted','HistoryPackCommitted','LocalStateApplied','Complete') {
        foreach ($edge in 'before','after') {
            $name = "crash-$phase-$edge"
            $request = @{ Workspace = Join-Path $root $name; Phase = 'capture'; Package = $package; PackageSha256 = $hash
                SevenZip = (Resolve-Path -LiteralPath $SevenZipPath).Path; Source = $samples[0].Path; SourceId = $samples[0].SourceId
                Encrypted = $false; CrashAt = "${phase}:$edge" }
            Invoke-Acceptance $request "$name-capture" $true
            $request.Phase = 'recover'
            Invoke-Acceptance $request "$name-recover"
        }
    }
}
Write-Output (Join-Path $root 'results.json')
