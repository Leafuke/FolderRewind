[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/update-p0/ui')
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($ResultsDirectory) { $ResultsDirectory = Resolve-FolderRewindPath -Path $ResultsDirectory -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {


$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resultsPath = [IO.Path]::GetFullPath((Join-Path $repository $ResultsDirectory))
$reportPath = Join-Path $resultsPath 'test-results.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a new results directory to preserve existing evidence.' }
New-Item -ItemType Directory -Force -Path $resultsPath | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$appPid = 0

function Invoke-WinAppChecked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE) { throw "winapp failed: $($Arguments -join ' '): $($output -join "`n")" }
    $output
}

function Invoke-Ui([string[]]$Arguments) {
    Invoke-WinAppChecked (@('ui') + $Arguments + @('--on', 'local', '-a', "$script:appPid"))
}

try {
    foreach ($locale in @('zh-CN', 'en-US')) {
        [xml]$resources = Get-Content -LiteralPath (Join-Path $repository "src/FolderRewind/Strings/$locale/Resources.resw") -Raw
        $strings = @{}
        foreach ($entry in $resources.root.data) { $strings[$entry.name] = [string]$entry.value }
        foreach ($scenario in @('setup', 'manual', 'store', 'unsafe-link', 'missing-asset', 'unsupported')) {
            $appPid = 0
            try {
                $launch = Invoke-WinAppChecked @('run', (Join-Path $repository 'tools/AppUpdateUiProbe/AppUpdateUiProbe.csproj'),
                    '-c', 'Release', '--arch', 'x64', '--no-build', '--detach', '--json', '--on', 'local',
                    '--', '--locale', $locale, '--scenario', $scenario) | ConvertFrom-Json
                $appPid = [int]$launch.ProcessId
                if ($appPid -le 0) { throw 'No local fixture PID returned.' }
                Invoke-Ui @('wait-for', 'ViewUpdate', '-t', '5000', '--json') | Out-Null
                Invoke-Ui @('invoke', 'ViewUpdate', '--json') | Out-Null

                $hint = switch ($scenario) {
                    'setup' { 'Update_Dialog_SetupExitHint' }
                    'manual' { 'Update_Dialog_ManualMigrationHint' }
                    'store' { 'Update_Dialog_StoreDelayHint' }
                    'unsafe-link' { 'Update_Dialog_UnsafeMetadataHint' }
                    'missing-asset' { 'Update_Dialog_PackageUnavailableHint' }
                    'unsupported' { 'Update_Dialog_UnsupportedArchitectureHint' }
                }
                $button = switch ($scenario) {
                    'setup' { 'Update_Dialog_DownloadSetup' }
                    'store' { 'Update_Dialog_OpenStore' }
                    default { 'Update_Dialog_OpenRelease' }
                }
                $target = switch ($scenario) {
                    'setup' { 'https://github.com/Leafuke/FolderRewind/releases/download/v1.9.6/FolderRewind_1.9.6.0_Setup_x64.exe' }
                    'manual' { 'https://github.com/Leafuke/FolderRewind/releases/tag/v1.9.6' }
                    'store' { 'https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct' }
                    default { 'https://github.com/Leafuke/FolderRewind/releases' }
                }
                Invoke-Ui @('wait-for', 'UpdateContent', '--value', $strings[$hint], '--contains', '-t', '3000', '--json') | Out-Null
                Invoke-Ui @('wait-for', 'UpdateContent', '--value', $strings['Update_Dialog_MirrorMetadataHint'], '--contains', '-t', '3000', '--json') | Out-Null
                Invoke-Ui @('wait-for', 'PrimaryButton', '--value', $strings[$button], '-t', '3000', '--json') | Out-Null
                $inspection = Invoke-Ui @('inspect', '-d', '10', '--json')
                $inspection | Set-Content -LiteralPath (Join-Path $resultsPath "$locale-$scenario-tree.json") -Encoding utf8
                $window = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json) |
                    Where-Object title -EQ 'FolderRewind P0 update fixture' | Select-Object -First 1
                if (-not $window.hwnd) { throw 'Fixture main window not found.' }
                $screenshot = Join-Path $resultsPath "$locale-$scenario.png"
                Invoke-WinAppChecked @('ui', 'screenshot', '--on', 'local', '-w', "$($window.hwnd)", '--output', $screenshot, '--json') | Out-Null
                if (-not (Test-Path -LiteralPath $screenshot) -or (Get-Item -LiteralPath $screenshot).Length -eq 0) {
                    throw 'Screenshot was not delivered.'
                }
                $screenshots.Add($screenshot)

                Invoke-Ui @('invoke', 'CloseButton', '--json') | Out-Null
                Invoke-Ui @('wait-for', 'ActionTarget', '--value', 'Deferred', '-t', '3000', '--json') | Out-Null
                Invoke-Ui @('invoke', 'ViewUpdate', '--json') | Out-Null
                Invoke-Ui @('wait-for', 'PrimaryButton', '-t', '3000', '--json') | Out-Null
                Invoke-Ui @('invoke', 'PrimaryButton', '--json') | Out-Null
                Invoke-Ui @('wait-for', 'ActionTarget', '--value', $target, '-t', '3000', '--json') | Out-Null
                Invoke-Ui @('invoke', 'CloseProbe', '--json') | Out-Null
                $results.Add(@{locale=$locale;scenario=$scenario;status='PASS';target=$target;screenshot=$screenshot})
            }
            catch {
                $results.Add(@{locale=$locale;scenario=$scenario;status='FAIL';detail="$_"})
            }
            finally {
                # Only this script's data-free probe may be stopped; never a FolderRewind process.
                if ($appPid -gt 0) {
                    $probe = Get-Process -Id $appPid -ErrorAction SilentlyContinue
                    if ($probe -and $probe.ProcessName -eq 'AppUpdateUiProbe') { $probe | Stop-Process }
                }
            }
        }
    }
}
finally {
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
}

$failed = @($results | Where-Object status -EQ 'FAIL').Count
@{target='local';passed=$results.Count-$failed;failed=$failed;launchIntercepted=$true;
    visualReview='pending';results=@($results.ToArray());screenshots=@($screenshots.ToArray())} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Passed: $($results.Count-$failed) | Failed: $failed | Report: $reportPath"
if ($failed) { exit 1 }

} finally { Pop-Location }
