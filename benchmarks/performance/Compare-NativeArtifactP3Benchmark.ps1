param(
    [Parameter(Mandatory)][string]$CoreResult,
    [Parameter(Mandatory)][string]$SemanticResult,
    [Parameter(Mandatory)][string]$OutputFile
)
. (Join-Path $PSScriptRoot '../../scripts/common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($CoreResult) { $CoreResult = Resolve-FolderRewindPath -Path $CoreResult -Root $folderRewindScriptRoot }
if ($SemanticResult) { $SemanticResult = Resolve-FolderRewindPath -Path $SemanticResult -Root $folderRewindScriptRoot }
if ($OutputFile) { $OutputFile = Resolve-FolderRewindPath -Path $OutputFile -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$p3Core = Get-Content -LiteralPath $CoreResult -Raw | ConvertFrom-Json
$p3Semantic = Get-Content -LiteralPath $SemanticResult -Raw | ConvertFrom-Json
function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Empty metric series.' }
    return $sorted[[int][math]::Floor($sorted.Count / 2)]
}
function Maximum($rows, $property) { return ($rows | Measure-Object -Property $property -Maximum).Maximum }
$p3Checks = @()
foreach ($p3Scenario in @('unchanged','sparse','dense','deletion','checkpoint')) {
    $p3Control = @($p3Core.Rows | Where-Object { $_.Scenario -eq $p3Scenario -and !$_.Warmup })
    $p3Candidate = @($p3Semantic.Rows | Where-Object { $_.Scenario -eq $p3Scenario -and !$_.Warmup })
    $p3Count = if ($p3Scenario -eq 'checkpoint') { 1 } else { 3 }
    if ($p3Control.Count -ne $p3Count -or $p3Candidate.Count -ne $p3Count) { throw "Incomplete matrix: $p3Scenario" }
    $p3Factor = if ($p3Scenario -eq 'unchanged') { 1.10 } elseif ($p3Scenario -eq 'sparse') { 1.20 } else { 1.25 }
    $p3Allowance = if ($p3Scenario -eq 'unchanged') { 250 } elseif ($p3Scenario -eq 'sparse') { 500 } else { 1000 }
    $p3BackupLimit = (Maximum $p3Control BackupMilliseconds) * $p3Factor + $p3Allowance
    $p3RestoreLimit = (Maximum $p3Control RestoreMilliseconds) * 1.50 + 1000
    $p3SameBytes = $true
    foreach ($p3Row in $p3Candidate) {
        $p3Corresponding = @($p3Control | Where-Object Iteration -eq $p3Row.Iteration)
        if ($p3Corresponding.Count -ne 1 -or !$p3Row.ByteExact -or $p3Row.StateDigest -ne $p3Corresponding[0].StateDigest) { $p3SameBytes = $false }
    }
    $p3StorageFactor = if ($p3Scenario -eq 'sparse') { 0.50 } elseif ($p3Scenario -in @('dense','checkpoint')) { 1.25 } else { $null }
    $p3StoragePassed = if ($null -eq $p3StorageFactor) { $true } else {
        (Maximum $p3Candidate AddedStorageBytes) -le (Maximum $p3Control AddedStorageBytes) * $p3StorageFactor -and
        (Maximum $p3Candidate UploadedBytes) -le (Maximum $p3Control UploadedBytes) * $p3StorageFactor
    }
    $p3Checks += [pscustomobject]@{
        Scenario=$p3Scenario; Runs=$p3Count; SameInputAndByteExact=$p3SameBytes
        CoreBackupMedianMs=(Median $p3Control.BackupMilliseconds); SemanticBackupMedianMs=(Median $p3Candidate.BackupMilliseconds)
        CoreBackupMaxMs=(Maximum $p3Control BackupMilliseconds); SemanticBackupMaxMs=(Maximum $p3Candidate BackupMilliseconds)
        BackupLimitMs=$p3BackupLimit; BackupPassed=((Maximum $p3Candidate BackupMilliseconds) -le $p3BackupLimit)
        CoreRestoreMaxMs=(Maximum $p3Control RestoreMilliseconds); SemanticRestoreMaxMs=(Maximum $p3Candidate RestoreMilliseconds)
        RestoreLimitMs=$p3RestoreLimit; RestorePassed=((Maximum $p3Candidate RestoreMilliseconds) -le $p3RestoreLimit)
        CoreAddedMaxBytes=(Maximum $p3Control AddedStorageBytes); SemanticAddedMaxBytes=(Maximum $p3Candidate AddedStorageBytes)
        CoreUploadMaxBytes=(Maximum $p3Control UploadedBytes); SemanticUploadMaxBytes=(Maximum $p3Candidate UploadedBytes)
        StoragePassed=$p3StoragePassed
    }
}
$p3Report = [pscustomobject]@{
    PerformanceGatePassed=(@($p3Checks | Where-Object { !$_.SameInputAndByteExact -or !$_.BackupPassed -or !$_.RestorePassed -or !$_.StoragePassed }).Count -eq 0)
    ColdCacheControlled=$false; Comparison='one warmup + three samples; maxima, not p95; checkpoint one sample'
    CoreResult=[IO.Path]::GetFullPath($CoreResult); SemanticResult=[IO.Path]::GetFullPath($SemanticResult)
    HostPeakWorkingSetBytes=(Maximum $p3Semantic.Rows HostPeakWorkingSet)
    ChildProcessIoIncluded=$false; UploadScope='copied ciphertext payload; excludes descriptor/receipt and protocol overhead'
    ChildProcessMetrics='HostReadBytes/HostWriteBytes exclude children; instrumented 7z counters and 25ms sampled concurrent working sets are in per-row BackupStages/RestoreStages when present'
    Checks=$p3Checks
}
$p3Output = [IO.Path]::GetFullPath($OutputFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p3Output)) | Out-Null
$p3Report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p3Output
$p3Checks | Format-Table Scenario,SameInputAndByteExact,BackupPassed,RestorePassed,StoragePassed
Write-Output "PerformanceGatePassed=$($p3Report.PerformanceGatePassed)"

} finally { Pop-Location }
