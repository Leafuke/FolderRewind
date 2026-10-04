[CmdletBinding()]
param(
    [ValidateRange(0, 20)]
    [int]$WarmupRuns = 1,

    [ValidateRange(1, 50)]
    [int]$MeasuredRuns = 5,

    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 30,

    [switch]$SkipBuild,
    [switch]$HistoricalSdkMatrix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory "..\.."))
$projectPath = Join-Path $repositoryRoot "FolderRewind\FolderRewind.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts\performance"
$publishRoot = Join-Path $artifactRoot "publish"
$resultRoot = Join-Path $artifactRoot "results"
$logPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    ("FolderRewind\logs\app-{0:yyyy-MM-dd}.log" -f [DateTime]::Now)

$allOptionalChanges = "DeferContextFlyoutInit,IconNoGridOptimization,DefaultStyleOptimizations,OptimizeApplyStyles"
$variants = @(
    [pscustomobject]@{
        Name = "sdk-2.2.0"
        SdkVersion = "2.2.0"
        OptionalChanges = ""
    },
    [pscustomobject]@{
        Name = "sdk-2.3.1-default"
        SdkVersion = "2.3.1"
        OptionalChanges = ""
    },
    [pscustomobject]@{
        Name = "defer-context-flyout"
        SdkVersion = "2.3.1"
        OptionalChanges = "DeferContextFlyoutInit"
    },
    [pscustomobject]@{
        Name = "icon-no-grid"
        SdkVersion = "2.3.1"
        OptionalChanges = "IconNoGridOptimization"
    },
    [pscustomobject]@{
        Name = "default-style-optimizations"
        SdkVersion = "2.3.1"
        OptionalChanges = "DefaultStyleOptimizations"
    },
    [pscustomobject]@{
        Name = "optimize-apply-styles"
        SdkVersion = "2.3.1"
        OptionalChanges = "OptimizeApplyStyles"
    },
    [pscustomobject]@{
        Name = "all-optional-changes"
        SdkVersion = "2.3.1"
        OptionalChanges = $allOptionalChanges
    }
)

# Normal measurement follows the current project. Legacy SDK comparisons remain explicit opt-in.
if (-not $HistoricalSdkMatrix) {
    [xml]$projectDocument = Get-Content -LiteralPath $projectPath -Raw
    $currentSdk = $projectDocument.SelectSingleNode('//FolderRewindWindowsAppSdkVersion').InnerText
    $variants = @([pscustomobject]@{ Name = 'current'; SdkVersion = $currentSdk; OptionalChanges = $allOptionalChanges })
}

function Assert-NoRunningFolderRewind {
    $running = Get-Process -Name "FolderRewind" -ErrorAction SilentlyContinue
    if ($running) {
        $ids = ($running.Id -join ", ")
        throw "FolderRewind is already running (PID: $ids). Exit it before starting the benchmark."
    }
}

function Assert-ArtifactChildPath {
    param([string]$Path)

    $root = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not $candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the performance artifact directory: $candidate"
    }
}

function Convert-ToMsBuildList {
    param([string]$Value)

    # MSBuild treats commas in -p arguments as property separators. URL-escape
    # them so XamlCompiler receives one comma-delimited property value.
    return $Value.Replace(",", "%2c")
}

function Publish-Variant {
    param([pscustomobject]$Variant)

    $publishDirectory = Join-Path $publishRoot $Variant.Name

    Assert-ArtifactChildPath $publishDirectory

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    $commonArguments = @(
        $projectPath,
        "-c", "Release",
        "--nologo",
        "-v:q",
        "-p:Platform=x64",
        "-p:FolderRewindDistributionChannel=Msi",
        "-p:FolderRewindWindowsAppSdkVersion=$($Variant.SdkVersion)",
        "-p:FolderRewindEnableXaml231Optimizations=false",
        "-p:GenerateAppxPackageOnBuild=false"
    )

    if (-not [string]::IsNullOrWhiteSpace($Variant.OptionalChanges)) {
        $encodedChanges = Convert-ToMsBuildList $Variant.OptionalChanges
        $commonArguments += "-p:EnabledXamlOptionalChanges=$encodedChanges"
    }

    Write-Host "Cleaning $($Variant.Name)..."
    & dotnet clean @commonArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean failed for $($Variant.Name) with exit code $LASTEXITCODE."
    }

    $arguments = @("publish") + $commonArguments + @("--force", "-o", $publishDirectory)

    Write-Host "Publishing $($Variant.Name)..."
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($Variant.Name) with exit code $LASTEXITCODE."
    }

    $executablePath = Join-Path $publishDirectory "FolderRewind.exe"
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "Published executable was not found: $executablePath"
    }
}

function Read-AppendedLogText {
    param(
        [string]$Path,
        [long]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($Offset -gt $stream.Length) {
            $Offset = 0
        }
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-StartupRun {
    param(
        [pscustomobject]$Variant,
        [int]$RunNumber,
        [bool]$IsWarmup
    )

    Assert-NoRunningFolderRewind

    $executablePath = Join-Path (Join-Path $publishRoot $Variant.Name) "FolderRewind.exe"
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "Published executable was not found: $executablePath. Run without -SkipBuild first."
    }

    $logOffset = 0L
    if (Test-Path -LiteralPath $logPath) {
        $logOffset = (Get-Item -LiteralPath $logPath).Length
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executablePath -WorkingDirectory (Split-Path -Parent $executablePath) -PassThru
    $windowDetectedMs = $null
    $windowActivatedMs = $null
    $appReadyMs = $null
    $historyWarmupMs = $null
    $peakWorkingSetBytes = 0L
    $capturedLog = ""

    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                throw "FolderRewind exited before startup completed for $($Variant.Name)."
            }

            $process.Refresh()
            $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $process.PeakWorkingSet64)

            if ($null -eq $windowDetectedMs -and $process.MainWindowHandle -ne [IntPtr]::Zero -and $process.Responding) {
                $windowDetectedMs = $stopwatch.ElapsedMilliseconds
            }

            $capturedLog = Read-AppendedLogText -Path $logPath -Offset $logOffset
            if ($null -eq $windowActivatedMs -and
                $capturedLog -match "\[Startup\] Window activated: (?<value>\d+)ms") {
                $windowActivatedMs = [long]$Matches.value
            }
            if ($null -eq $appReadyMs -and
                $capturedLog -match "\[Startup\] Home loaded: (?<value>\d+)ms") {
                $appReadyMs = [long]$Matches.value
            }

            if ($null -eq $historyWarmupMs -and $capturedLog -match "\[Startup\] History warmup completed: (?<value>\d+)ms") {
                $historyWarmupMs = [long]$Matches.value
            }
            if ($null -ne $windowDetectedMs -and $null -ne $windowActivatedMs -and $null -ne $appReadyMs -and $null -ne $historyWarmupMs) {
                break
            }

            Start-Sleep -Milliseconds 50
        }

        if ($null -eq $windowDetectedMs -or $null -eq $windowActivatedMs -or $null -eq $appReadyMs) {
            $missing = @()
            if ($null -eq $windowDetectedMs) { $missing += "responsive window" }
            if ($null -eq $windowActivatedMs) { $missing += "Window activated log marker" }
            if ($null -eq $appReadyMs) { $missing += "Home loaded log marker" }
            throw "Startup timed out for $($Variant.Name). Missing: $($missing -join ', '). Ensure file logging is enabled."
        }

        $process.Refresh()
        $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $process.PeakWorkingSet64)

        return [pscustomobject]@{
            Variant = $Variant.Name
            SdkVersion = $Variant.SdkVersion
            OptionalChanges = $Variant.OptionalChanges
            Run = $RunNumber
            Warmup = $IsWarmup
            WindowDetectedMs = $windowDetectedMs
            WindowActivatedMs = $windowActivatedMs
            HomeLoadedMs = $appReadyMs
            AppReadyMs = $appReadyMs
            HistoryWarmupMs = $historyWarmupMs
            PeakWorkingSetMB = [Math]::Round($peakWorkingSetBytes / 1MB, 2)
        }
    }
    finally {
        $stopwatch.Stop()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
        Start-Sleep -Milliseconds 300
    }
}

function Get-Median {
    param([double[]]$Values)

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        return [double]::NaN
    }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2
}

function Write-MarkdownSummary {
    param(
        [object[]]$Summary,
        [string]$Path
    )

    $lines = @(
        "# Local startup measurements",
        "",
        "- Generated: $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz'))",
        "- Machine: $([Environment]::MachineName)",
        "- OS: $([Environment]::OSVersion.VersionString)",
        "- Warm-up runs per variant: $WarmupRuns",
        "- Measured runs per variant: $MeasuredRuns",
        "",
        "| Variant | SDK | Optional changes | Direct baseline | Window activated (ms) | Home loaded (ms) | History warmup (ms) | Home loaded delta | Peak working set median (MB) | Working set delta |",
        "|---|---:|---|---|---:|---:|---:|---:|---:|---:|"
    )

    foreach ($row in $Summary) {
        $changes = if ([string]::IsNullOrWhiteSpace($row.OptionalChanges)) { "(none)" } else { $row.OptionalChanges }
        $baseline = if ([string]::IsNullOrWhiteSpace($row.BaselineVariant)) { "(baseline)" } else { $row.BaselineVariant }
        $appReadyDelta = if ($null -eq $row.AppReadyDeltaPercent) { "-" } else { "$($row.AppReadyDeltaPercent)%" }
        $workingSetDelta = if ($null -eq $row.PeakWorkingSetDeltaPercent) { "-" } else { "$($row.PeakWorkingSetDeltaPercent)%" }
        $lines += "| $($row.Variant) | $($row.SdkVersion) | $changes | $baseline | $($row.WindowActivatedMedianMs) | $($row.HomeLoadedMedianMs) | $($row.HistoryWarmupMedianMs) | $appReadyDelta | $($row.PeakWorkingSetMedianMB) | $workingSetDelta |"
    }

    $lines += @(
        "",
        "Measurements are informational only. Compare repeated runs with the same fixture and machine; no performance threshold is enforced. Blank warmup values mean warmup was not observed before the timeout."
    )

    Set-Content -LiteralPath $Path -Value $lines -Encoding utf8
}

Assert-NoRunningFolderRewind
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "FolderRewind project was not found: $projectPath"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null

if (-not $SkipBuild) {
    foreach ($variant in $variants) {
        Publish-Variant $variant
    }
}

$results = [Collections.Generic.List[object]]::new()
foreach ($variant in $variants) {
    Write-Host "Measuring $($variant.Name)..."

    for ($run = 1; $run -le $WarmupRuns; $run++) {
        $warmupResult = Invoke-StartupRun -Variant $variant -RunNumber $run -IsWarmup $true
        $results.Add($warmupResult)
        Write-Host "  warm-up $run completed"
    }

    for ($run = 1; $run -le $MeasuredRuns; $run++) {
        $result = Invoke-StartupRun -Variant $variant -RunNumber $run -IsWarmup $false
        $results.Add($result)
        Write-Host "  run $run/$MeasuredRuns`: app ready $($result.AppReadyMs) ms, peak $($result.PeakWorkingSetMB) MB"
    }
}

$timestamp = [DateTime]::Now.ToString("yyyyMMdd-HHmmss")
$resultPrefix = if ($HistoricalSdkMatrix) { "windowsappsdk-2.3.1" } else { "current-startup" }
$rawCsvPath = Join-Path $resultRoot "$resultPrefix-$timestamp.csv"
$summaryCsvPath = Join-Path $resultRoot "$resultPrefix-$timestamp-summary.csv"
$summaryMarkdownPath = Join-Path $resultRoot "$resultPrefix-$timestamp-summary.md"

$results | Export-Csv -LiteralPath $rawCsvPath -NoTypeInformation -Encoding utf8

$summary = foreach ($variant in $variants) {
    $measured = @($results | Where-Object { $_.Variant -eq $variant.Name -and -not $_.Warmup })
    [pscustomobject]@{
        Variant = $variant.Name
        SdkVersion = $variant.SdkVersion
        OptionalChanges = $variant.OptionalChanges
        WindowDetectedMedianMs = [Math]::Round((Get-Median @($measured.WindowDetectedMs)), 2)
        WindowActivatedMedianMs = [Math]::Round((Get-Median @($measured.WindowActivatedMs)), 2)
        HomeLoadedMedianMs = [Math]::Round((Get-Median @($measured.HomeLoadedMs)), 2)
        AppReadyMedianMs = [Math]::Round((Get-Median @($measured.AppReadyMs)), 2)
        HistoryWarmupMedianMs = if (@($measured | Where-Object { $null -ne $_.HistoryWarmupMs }).Count -gt 0) {
            [Math]::Round((Get-Median @($measured | Where-Object { $null -ne $_.HistoryWarmupMs } | ForEach-Object { $_.HistoryWarmupMs })), 2)
        } else { $null }
        PeakWorkingSetMedianMB = [Math]::Round((Get-Median @($measured.PeakWorkingSetMB)), 2)
    }
}

$summaryByVariant = @{}
foreach ($row in $summary) {
    $summaryByVariant[$row.Variant] = $row
}

foreach ($row in $summary) {
    $baselineVariant = if ($row.Variant -in @("current", "sdk-2.2.0")) {
        ""
    }
    elseif ($row.Variant -eq "sdk-2.3.1-default") {
        "sdk-2.2.0"
    }
    else {
        "sdk-2.3.1-default"
    }

    $appReadyDelta = $null
    $workingSetDelta = $null
    if (-not [string]::IsNullOrWhiteSpace($baselineVariant)) {
        $baseline = $summaryByVariant[$baselineVariant]
        $appReadyDelta = [Math]::Round((($row.AppReadyMedianMs / $baseline.AppReadyMedianMs) - 1) * 100, 2)
        $workingSetDelta = [Math]::Round((($row.PeakWorkingSetMedianMB / $baseline.PeakWorkingSetMedianMB) - 1) * 100, 2)
    }

    Add-Member -InputObject $row -NotePropertyName BaselineVariant -NotePropertyValue $baselineVariant
    Add-Member -InputObject $row -NotePropertyName AppReadyDeltaPercent -NotePropertyValue $appReadyDelta
    Add-Member -InputObject $row -NotePropertyName PeakWorkingSetDeltaPercent -NotePropertyValue $workingSetDelta
}

$summary | Export-Csv -LiteralPath $summaryCsvPath -NoTypeInformation -Encoding utf8
Write-MarkdownSummary -Summary $summary -Path $summaryMarkdownPath

Write-Host ""
Write-Host "Benchmark completed."
Write-Host "Raw results: $rawCsvPath"
Write-Host "CSV summary: $summaryCsvPath"
Write-Host "Markdown summary: $summaryMarkdownPath"
