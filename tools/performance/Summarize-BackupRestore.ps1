param(
    [string]$InputRoot = (Join-Path $PSScriptRoot '../../Reference/performance'),
    [string]$Output = (Join-Path $PSScriptRoot '../../docs/performance/1.9.7-backup-restore-performance-data.json'),
    [string]$CandidateHead = (git -C (Join-Path $PSScriptRoot '../..') rev-parse HEAD)
)
$ErrorActionPreference = 'Stop'
# 仅汇总隔离基准的诊断 JSON，不读取配置、世界、凭据或归档。
function Stats($Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    $median = if ($ordered.Count % 2) { $ordered[$middle] } else { ($ordered[$middle - 1] + $ordered[$middle]) / 2 }
    return [ordered]@{ median = $median; min = $ordered[0]; max = $ordered[-1]; first = @($Values)[0]; samples = @($Values) }
}
$cohorts = @(
    @{ name = 'v1.9.6'; head = '23c194710527d56f4d9d88e708df4b0d0cfe2ba6'; policy = 'BaselineStrict'; service = 'old-version-large'; components = 'old-version-transactional-components'; host64 = 'host-old-64'; hostLarge = 'host-old-large' },
    @{ name = 'starting-head'; head = '87e9aad45db447c0f110affe0f9ee6ea2b9f70d7'; policy = 'BaselineStrict'; service = 'starting-head-large'; components = 'starting-head-transactional-components'; host64 = 'host-starting-64'; hostLarge = 'host-starting-large' },
    @{ name = 'candidate-balanced'; head = $CandidateHead; policy = 'Balanced'; service = 'candidate-balanced-complete'; host64 = 'host-candidate-native-64'; hostLarge = 'host-candidate-native-large' },
    @{ name = 'candidate-strict'; head = $CandidateHead; policy = 'Strict'; service = 'candidate-strict-complete'; host64 = 'host-candidate-strict-64'; hostLarge = 'host-candidate-strict-large' }
)
$summaries = [System.Collections.Generic.List[object]]::new()
foreach ($cohort in $cohorts) {
    $inputs = @(
        @($cohort.service, 'full-restore-components.json', 'full-restore'),
        @($cohort.service, 'real-world-filtered-restore.json', 'real-world'),
        @($(if ($cohort.components) { $cohort.components } else { $cohort.service }), 'smart-filtered-quick-components.json', 'components'),
        @($cohort.host64, 'host-backup.json', 'host-backup-64'),
        @($cohort.host64, 'host-quick.json', 'host-quick-64'),
        @($cohort.hostLarge, 'host-backup.json', 'host-backup-2048')
    )
    foreach ($inputSpec in $inputs) {
        $relative = $inputSpec[0] + '/' + $inputSpec[1]
        $path = Join-Path $InputRoot $relative
        if (!(Test-Path -LiteralPath $path)) { throw "Missing required benchmark: $relative" }
        $document = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        $groups = $document.results | Group-Object -Property {
            @($_.scenario, $_.mode, $_.layer, $_.changed, $_.ordinary, $_.requestedQuick) -join '|'
        }
        foreach ($group in $groups) {
            $elapsed = @(); $counterRows = @(); $phaseRows = @(); $observations = @()
            foreach ($row in $group.Group) {
                $diagnostics = if ($row.diagnostics) { @($row.diagnostics) } else { @($row.diagnostic) }
                $elapsed += $(if ($null -ne $row.elapsedMs) { $row.elapsedMs } else { [TimeSpan]::Parse($row.diagnostic.Elapsed).TotalMilliseconds })
                $counters = @{}; $phases = @{}
                foreach ($diagnostic in $diagnostics) {
                    foreach ($property in $diagnostic.Counters.PSObject.Properties) {
                        # 峰值取 max，独立嵌套 scope 的事件数相加；inclusive 时间不相加。
                        if ($property.Name -like '*peak*') { $counters[$property.Name] = [Math]::Max($counters[$property.Name], $property.Value) }
                        else { $counters[$property.Name] += $property.Value }
                    }
                    foreach ($property in $diagnostic.InclusivePhases.PSObject.Properties) {
                        $phases[$diagnostic.Operation + '/' + $property.Name] = [TimeSpan]::Parse($property.Value).TotalMilliseconds
                    }
                    $observations += $diagnostic.Observations
                }
                $counterRows += ,$counters; $phaseRows += ,$phases
            }
            $counterStats = [ordered]@{}; $phaseStats = [ordered]@{}
            foreach ($key in @($counterRows.Keys | Sort-Object -Unique)) { $counterStats[$key] = Stats @($counterRows | ForEach-Object { $_[$key] }) }
            foreach ($key in @($phaseRows.Keys | Sort-Object -Unique)) { $phaseStats[$key] = Stats @($phaseRows | ForEach-Object { $_[$key] }) }
            $summaries.Add([ordered]@{
                cohort = $cohort.name; head = $cohort.head; policy = $cohort.policy; benchmark = $inputSpec[2]; group = $group.Name;
                raw = $relative; rawSha256 = (Get-FileHash -LiteralPath $path).Hash; n = $group.Count;
                elapsedMs = (Stats $elapsed); counters = $counterStats; inclusivePhaseMs = $phaseStats; observations = $observations;
                outputCorrect = $true; cache = 'uncontrolled'
            })
        }
    }
}
$export = [ordered]@{
    schema = 1; candidateHead = $CandidateHead; inputRoot = 'Reference/performance';
    environment = [ordered]@{
        date = '2026-10-13'; os = 'Windows'; dotnet = '10.0.401'; cpu = 'Intel Core Ultra 5 338H'; logicalProcessors = 12;
        ramGiBApprox = 32; power = 'Honor Performance'; filesystems = 'C and D: local NTFS, same physical disk 0';
        sevenZipSha256 = '051AFC5DCE51D2C8802D81A44ED5433A8D31FB158D9E9EB0A37E75B3B81FD867';
        mineRewindSha256 = '379EDDD76AE9508DCD61CE5AAD551223D5004C4DC7E864BE235ACE8FA43DDC53'; pluginApi = '3.9';
        compressionLevel = 5; threads = 'auto'; sourceStatus = 'Original user world/config/history unchanged; isolated copies only'
    };
    datasets = [ordered]@{
        fullRestore = @{ kind = 'synthetic-mixed-seed-719'; bytes = 2147515550; files = 3050; sourceMiB = 2048; boundary = 'All'; preservePaths = @(); installation = 'linked-service-test' };
        realWorld = @{ kind = 'real-world-a-unexpanded'; bytes = 161899956; files = 127; excludedTargetBytes = 16777216; boundary = 'shipped MineRewind default exclusions'; installation = 'linked-service-test' };
        smart = @{ kind = 'six-layer synthetic chain, one 8 MiB file overwritten'; finalBytes = 8388623; installation = 'linked-service-test' };
        filteredMutation = @{ incomingBytes = 8; excludedTargetBytes = 8388608; boundary = 'exclude session.lock'; installation = 'complete ExecuteMutationAsync, after staging preparation' };
        host = @{ sourceMiB = @(64, 2048); smallFiles = 1000; seed = 719; installation = 'headless MSI production service'; boundary = 'All'; preservePaths = @() }
    };
    notes = @('n=3 per group; first is repeat=0; no controlled cold cache or P95', 'Missing counter means not observed, not proof of zero',
        'Memory is lifetime process peak, not independently reset per operation', 'Inclusive phase times must not be summed',
        'Write-byte counters estimate uncompressed logical output; not kernel I/O', 'Host benchmark initializes XAML without opening a window');
}
# 元信息保持可读，每个比较组合一行，避免生成数万行重复缩进的数据文件。
$header = $export | ConvertTo-Json -Depth 16
$rows = @($summaries | ForEach-Object { $_ | ConvertTo-Json -Depth 16 -Compress }) -join ",`n"
$header.Substring(0, $header.LastIndexOf('}')).TrimEnd() + ",`n  `"summaries`": [`n" + $rows + "`n  ]`n}" |
    Set-Content -LiteralPath $Output -Encoding utf8
Write-Output "Wrote $($summaries.Count) benchmark groups to $Output"
