param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$TemplateRequest,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Delta,
    [switch]$Transport
)
$ErrorActionPreference = 'Stop'
$p3Executable = (Resolve-Path -LiteralPath $Executable).Path
$p3Template = Get-Content -LiteralPath $TemplateRequest -Raw | ConvertFrom-Json
$p3Output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $p3Output -Force | Out-Null
$p3Results = @()
$p3Phases = if ($Delta) { @('ArtifactGraphCommitted','HistoryPackCommitted') } else { @('Prepared','PayloadsSealed','ArtifactGraphCommitted','HistoryPackCommitted','LocalStateApplied','Complete') }
# Direct stable views have no Core transport to clean. Retain explicit coverage for legacy transport builds.
if ($Transport) { $p3Phases += 'PrimaryCleanup' }
foreach ($phase in $p3Phases) {
    foreach ($edge in @('before','after')) {
        $p3Request = $p3Template | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $p3Request.Workspace = Join-Path $p3Output "$phase-$edge"
        if (Test-Path -LiteralPath $p3Request.Workspace) { throw "Refusing to reuse an existing crash workspace: $($p3Request.Workspace)" }
        $p3Request.Phase = 'capture'
        $p3Request.Iterations = 1
        $p3Request.Encrypted = $false
        $p3Request | Add-Member -NotePropertyName CrashAt -NotePropertyValue "${phase}:$edge" -Force
        $p3Request.PSObject.Properties.Remove('Rclone')
        $p3Path = Join-Path $p3Output "$phase-$edge.json"
        if ($Delta) {
            $p3Request.CrashAt = $null
            $p3Request | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $p3Path
            $p3Process = Start-Process -FilePath $p3Executable -ArgumentList '--native-artifact-acceptance', ('"' + $p3Path + '"') -WindowStyle Hidden -PassThru
            if (!$p3Process.WaitForExit(60000)) { $p3Process.Kill(); throw 'Baseline capture timed out.' }
            if ($p3Process.ExitCode -ne 0) { throw 'Baseline capture failed.' }
            $p3Request.Phase = 'append'
            $p3Request.CrashAt = "${phase}:$edge"
            $p3Request | Add-Member -NotePropertyName PreviousVersions -NotePropertyValue 1 -Force
        }
        $p3Request | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $p3Path
        $p3Process = Start-Process -FilePath $p3Executable -ArgumentList '--native-artifact-acceptance', ('"' + $p3Path + '"') -WindowStyle Hidden -PassThru
        if (!$p3Process.WaitForExit(60000)) { $p3Process.Kill(); throw "Crash injection timed out: ${phase}:$edge" }
        if (Test-Path -LiteralPath (Join-Path $p3Request.Workspace 'capture-error.txt')) { throw (Get-Content -LiteralPath (Join-Path $p3Request.Workspace 'capture-error.txt') -Raw) }
        $p3Request.Phase = 'recover'
        $p3Request | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $p3Path
        $p3Process = Start-Process -FilePath $p3Executable -ArgumentList '--native-artifact-acceptance', ('"' + $p3Path + '"') -WindowStyle Hidden -PassThru
        if (!$p3Process.WaitForExit(60000)) { $p3Process.Kill(); throw "Recovery timed out: ${phase}:$edge" }
        $p3Result = Join-Path $p3Request.Workspace 'p3-recover-result.json'
        if ($p3Process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $p3Result)) {
            throw "Recovery failed: ${phase}:$edge; see $($p3Request.Workspace)"
        }
        $p3Results += Get-Content -LiteralPath $p3Result -Raw | ConvertFrom-Json
        $p3Results | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $p3Output 'results.json')
        Write-Output "Passed ${phase}:$edge"
    }
}
