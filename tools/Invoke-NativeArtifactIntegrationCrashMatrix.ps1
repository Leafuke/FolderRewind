param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$TemplateRequest,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Capture', 'Delta', 'Cloud', 'Migration')][string]$Matrix = 'Capture'
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $Executable).Path
$template = Get-Content -LiteralPath $TemplateRequest -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Crash matrix output must be new.' }
New-Item -ItemType Directory -Path $output | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Invoke-Fixture($request, [string]$file, [bool]$crash) {
    $request | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $file -Encoding utf8
    $process = Start-Process -FilePath $app -ArgumentList '--native-artifact-acceptance', ('"' + $file + '"') -WindowStyle Hidden -PassThru
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($watch.Elapsed.TotalMinutes -ge 3) { $process.Kill(); throw "Owned fixture timed out: $file" }
    }
    $errorFile = Join-Path $request.Workspace ($request.Phase + '-error.txt')
    if (Test-Path -LiteralPath $errorFile) { throw (Get-Content -LiteralPath $errorFile -Raw) }
    if ($crash -and $process.ExitCode -eq 0 -or -not $crash -and $process.ExitCode -ne 0) { throw "Unexpected fixture exit $($process.ExitCode): $file" }
}
$stages = switch ($Matrix) {
    'Capture' { 'Prepared', 'PayloadsSealed', 'ArtifactGraphCommitted', 'HistoryPackCommitted', 'LocalStateApplied', 'Complete' }
    'Delta' { 'ArtifactGraphCommitted', 'HistoryPackCommitted' }
    'Cloud' { 'CloudObject', 'CloudReceipt', 'CloudDescriptor', 'CloudManifest', 'CloudFacts' }
    'Migration' { 'MigrationPrepared', 'MigrationPayload' }
}
foreach ($stage in $stages) {
    foreach ($edge in 'before', 'after') {
        $request = $template | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $request.Workspace = Join-Path $output "$stage-$edge"
        $request.Phase = 'capture'; $request.Iterations = 1
        $request | Add-Member -NotePropertyName ExperimentalDelivery -NotePropertyValue $false -Force
        $rclone = $request.Rclone; $request.PSObject.Properties.Remove('Rclone')
        $request | Add-Member -NotePropertyName CrashAt -NotePropertyValue $null -Force
        $file = Join-Path $output "$stage-$edge.json"
        if ($Matrix -eq 'Migration') { $request.Phase = 'migration-import' }
        elseif ($Matrix -ne 'Capture') {
            Invoke-Fixture $request $file $false
            $request.Phase = if ($Matrix -eq 'Delta') { 'append' } else { 'cloud' }
            if ($Matrix -eq 'Delta') { $request | Add-Member -NotePropertyName PreviousVersions -NotePropertyValue 1 -Force }
            if ($Matrix -eq 'Cloud') { $request | Add-Member -NotePropertyName Rclone -NotePropertyValue $rclone -Force }
        }
        $request.CrashAt = "${stage}:$edge"
        Invoke-Fixture $request $file $true
        $request.Phase = if ($Matrix -eq 'Cloud') { 'cloud-recover' } elseif ($Matrix -eq 'Migration') { 'migration-restart' } else { 'recover' }
        Invoke-Fixture $request $file $false
        Invoke-Fixture $request $file $false
        $reportName = if ($Matrix -eq 'Cloud') { 'cloud-recover-result.json' } elseif ($Matrix -eq 'Migration') { 'migration-restart-result.json' } else { 'p3-recover-result.json' }
        $report = Get-Content -LiteralPath (Join-Path $request.Workspace $reportName) -Raw | ConvertFrom-Json
        if (-not $report.Success) { throw "Recovery failed: ${stage}:$edge" }
        $results.Add($report)
        $results | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
        Write-Output "Passed $Matrix ${stage}:$edge (two recovery processes)"
    }
}
