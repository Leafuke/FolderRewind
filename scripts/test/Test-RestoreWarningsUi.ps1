param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('zh-CN','en-US')][string]$Language = 'zh-CN'
)
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($OutputDirectory) { $OutputDirectory = Resolve-FolderRewindPath -Path $OutputDirectory -Root $folderRewindScriptRoot }
if ($Workspace) { $Workspace = Resolve-FolderRewindPath -Path $Workspace -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [IO.Path]::GetFullPath($Workspace)
if (!(Test-Path -LiteralPath (Join-Path $root 'acceptance-owner'))) { throw 'An isolated acceptance owner is required.' }
$settings = Get-Content -LiteralPath (Join-Path $root 'FolderRewind/config.json') -Raw | ConvertFrom-Json
$config = @($settings.BackupConfigs | Where-Object Name -eq '协调回归 Full')[0]
if (!$config -or !$config.SourceFolders.Count) { throw 'Missing synthetic Full configuration.' }
$world = [IO.Path]::GetFullPath($config.SourceFolders[0].Path)
if (!$world.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source escaped the isolated workspace.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = [IO.Path]::GetFullPath($OutputDirectory)
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'folderrewind-restore-warning-' + [guid]::NewGuid().ToString('N')
$held = $null
function Ui([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $targetArgs = if ($Arguments[0] -eq 'yield') { @('--on','local') } else { @('-a',[string]$AppPid,'--on','local') }
    $value = @(& winapp ui @Arguments @targetArgs 2>&1)
    if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) { throw ($value -join "`n") }
    $value -join "`n"
}
function Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add(@{name=$Name;status='PASS'}); Write-Output "PASS $Name" }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Shot([string]$Name) {
    $tree = Ui @('inspect','--json') | ConvertFrom-Json
    $window = @($tree.windows | Where-Object { $_.elements[0].className -eq 'WinUIDesktopWin32WindowClass' })[0].hwnd
    if (!$window) { throw 'Missing main window.' }
    $path = Join-Path $output ($Name + '.png')
    Ui @('screenshot','--window',[string]$window,'--output',$path) | Out-Null
    if (!(Test-Path -LiteralPath $path)) { throw 'Screenshot was not delivered.' }
    $screenshots.Add($path)
}
try {
    Ui @('wait-for','NavHome','-t','20000') | Out-Null
    $lockPath = Join-Path $world 'session.lock'
    [IO.File]::WriteAllBytes($lockPath, [byte[]]@(0))
    $held = [IO.FileStream]::new($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Case 'Real backup exposes a persistent task warning' {
        Ui @('invoke','NavHome') | Out-Null
        Ui @('wait-for',('HomeConfigCard_' + $config.Id),'-t','10000') | Out-Null
        Ui @('click',('HomeConfigCard_' + $config.Id),'--right') | Out-Null
        Ui @('wait-for',('HomeConfigBackupAll_' + $config.Id),'-t','10000') | Out-Null
        Ui @('invoke',('HomeConfigBackupAll_' + $config.Id)) | Out-Null
        Ui @('invoke','NavTasks') | Out-Null
        Ui @('wait-for','BackupTaskDetails','-t','20000') | Out-Null
        $expected = if ($Language -eq 'zh-CN') { '任务包含警告' } else { 'This task has warnings' }
        Ui @('wait-for','BackupTaskWarningSummary','--contains','--value',$expected) | Out-Null
        Shot '01-warning-card'
    }
    Case 'Warning details retain readable text and diagnostic identity' {
        Ui @('invoke','BackupTaskDetails') | Out-Null
        Ui @('wait-for','BackupTaskDetailsText','-t','5000') | Out-Null
        $script:details = (Ui @('get-value','BackupTaskDetailsText','--json') | ConvertFrom-Json).text
        if ($script:details -notlike '*minerewind.consistency_raw_source*' -or $script:details -notlike '*com.folderrewind.minerewind*') { throw 'Diagnostic identity is missing.' }
        $expected = if ($Language -eq 'zh-CN') { '未能协调游戏保存' } else { 'a game save could not be coordinated' }
        if ($script:details -notlike ('*' + $expected + '*')) { throw 'Localized explanation is missing.' }
        Shot '02-warning-details'
    }
    Case 'Copy retains the complete diagnostic' {
        Ui @('invoke','PrimaryButton') | Out-Null
        $copied = Get-Clipboard -Raw
        if ($copied -notlike '*minerewind.consistency_raw_source*' -or $copied -notlike '*com.folderrewind.minerewind*') { throw 'Copied warning is incomplete.' }
        Set-Content -LiteralPath (Join-Path $output 'copied-details.txt') -Value $copied -Encoding utf8
        Ui @('invoke','CloseButton') | Out-Null
    }
    Case 'Warning severity and keyword filters retain the diagnostic' {
        Ui @('invoke','NavLogs') | Out-Null
        Ui @('wait-for','LogLevelFilter') | Out-Null
        Ui @('focus','LogLevelFilter') | Out-Null
        Ui @('send-keys','home down down tab','--target','LogLevelFilter','--via','send-input') | Out-Null
        $levelName = if ($Language -eq 'zh-CN') { '警告' } else { 'Warning' }
        Ui @('wait-for','LogLevelFilter','--value',$levelName) | Out-Null
        Ui @('set-value','LogSearch','minerewind.consistency_raw_source') | Out-Null
        $inspection = Ui @('inspect','--depth','16','--json')
        Set-Content -LiteralPath (Join-Path $output 'filtered-tree.json') -Value $inspection -Encoding utf8
        if ($inspection -notlike '*code=minerewind.consistency_raw_source*') { throw 'Warning was not found after filtering.' }
        Shot '03-filtered-warning-log'
    }
}
catch {
    if (@($results.ToArray() | Where-Object status -eq 'FAIL').Count -eq 0) { $results.Add(@{name='Fixture setup / execution';status='FAIL';detail="$_"}) }
    try { Shot 'failure' } catch { }
    Write-Output $_
}
finally {
    if ($null -ne $held) { $held.Dispose() }
    try { Ui @('yield') | Out-Null } catch { $results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"}) }
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
    $failed = @($results.ToArray() | Where-Object status -eq 'FAIL').Count
    @{language=$Language;appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray())} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'test-results.json') -Encoding utf8
}
if (@($results | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }

} finally { Pop-Location }
