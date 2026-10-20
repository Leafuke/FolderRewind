param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Resume,
    [switch]$HistoryOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Workspace)
if (!(Test-Path -LiteralPath (Join-Path $root 'acceptance-owner'))) { throw 'An isolated acceptance owner is required.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = [IO.Path]::GetFullPath($OutputDirectory)
$configFile = Join-Path $root 'appdata/FolderRewind/config.json'
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'folderrewind-native-integration'
function Ui([string[]]$Arguments) {
    $text = & winapp ui @Arguments -a $AppPid --on local 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($text -join "`n") }
    return $text -join "`n"
}
function Wait-Condition([scriptblock]$Check) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!(& $Check)) { if ($clock.Elapsed.TotalSeconds -ge 12) { throw 'Fixture state timed out.' }; Start-Sleep -Milliseconds 100 }
}
function Saved { (Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json).BackupConfigs[0] }
function Shot([string]$Name) {
    $path = Join-Path $output ($Name + '.png')
    $mainWindow = @( (Ui @('inspect','--json') | ConvertFrom-Json).windows | Where-Object { $_.elements[0].className -eq 'WinUIDesktopWin32WindowClass' })[0].hwnd
    if (!$mainWindow) { throw 'Missing application main window.' }
    Ui @('screenshot', '--window', [string]$mainWindow, '--output', $path) | Out-Null
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'Screenshot missing.' }
    $screenshots.Add($path)
}
function Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add(@{name=$Name;status='PASS'}); Write-Output "PASS $Name" }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
try {
    if ($Resume) {
        $previous = Get-Content -LiteralPath (Join-Path $output 'test-results.json') -Raw | ConvertFrom-Json
        foreach ($result in $previous.results | Where-Object { $_.status -eq 'PASS' -and $_.name -notlike 'Checkpoint cancellation*' }) { $results.Add($result) }
        foreach ($path in $previous.screenshots) { $screenshots.Add($path) }
    }
    if (!$HistoryOnly) {
    if (!$Resume) {
    Case 'Bundled upgrade preserves previous formal version' {
        $state = Get-Content (Join-Path $root 'appdata/FolderRewind/plugins/com.folderrewind.minerewind/install-state.v1.json') -Raw | ConvertFrom-Json
        $bundle = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../FolderRewind/Assets/Plugins') -Filter 'MineRewind-*.frplugin')
        if ($bundle.Count -ne 1) { throw 'Expected exactly one bundled plugin.' }
        $expectedVersion = $bundle[0].BaseName.Substring('MineRewind-'.Length)
        $expectedHash = ((Get-Content -LiteralPath ($bundle[0].FullName + '.sha256') -Raw) -split '\s+')[0]
        if ($state.currentVersion -ne $expectedVersion -or $state.previousKnownGoodVersion -ne '1.9.8') { throw 'Expected the real bundled upgrade.' }
        if ($state.versions[-1].sha256 -ne $expectedHash) { throw 'Wrong final package.' }
    }
    Case 'Experimental default is off' {
        Ui @('invoke','ConfigSettingsTabBackup') | Out-Null
        Ui @('wait-for','SemanticBackupEnable','--value','Off') | Out-Null
        Shot '01-default-off'
        Ui @('invoke','SemanticBackupEnable','--action','toggle-on') | Out-Null
        Ui @('wait-for','SemanticCheckpointCreate','-p','IsEnabled','--value','True') | Out-Null
    }
    Case 'Saving freezes submission and preserves edits made while awaiting commit' {
        Ui @('invoke','ConfigSettingsTabGeneral') | Out-Null
        Ui @('set-value','ConfigSettingsName','UI submitted snapshot') | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'hold-settings-save') 'hold'
        Ui @('invoke','ProjectSettingsSave') | Out-Null
        Wait-Condition { Test-Path -LiteralPath (Join-Path $root 'settings-save-entered') }
        Ui @('set-value','ConfigSettingsName','UI later edit') | Out-Null
        Ui @('invoke','NavHome') | Out-Null
        Ui @('wait-for','ConfigSettingsName','--value','UI later edit') | Out-Null
        Ui @('wait-for','NavHome','-p','IsSelected','--value','False') | Out-Null
        Remove-Item -LiteralPath (Join-Path $root 'hold-settings-save')
        Ui @('wait-for','ProjectSettingsSave','-p','IsEnabled','--value','True') | Out-Null
        Wait-Condition { (Saved).Name -eq 'UI submitted snapshot' }
        Ui @('wait-for','ConfigSettingsName','--value','UI later edit') | Out-Null
        Shot '02-save-edit-race'
        Ui @('invoke','ProjectSettingsSave') | Out-Null
        Wait-Condition { (Saved).Name -eq 'UI later edit' }
    }
    }
    Case 'Checkpoint cancellation is visible and does not publish another pack' {
        Ui @('invoke','ConfigSettingsTabBackup') | Out-Null
        Ui @('scroll-into-view','SemanticCheckpointCreate') | Out-Null
        $packsBefore = @(Get-ChildItem (Join-Path $root 'appdata/FolderRewind/history') -Recurse -File -Filter '*.frpack').Count
        $entered = Join-Path $root 'checkpoint-entered'
        if (Test-Path -LiteralPath $entered) { Remove-Item -LiteralPath $entered }
        Set-Content -LiteralPath (Join-Path $root 'hold-checkpoint') 'hold'
        Ui @('invoke','SemanticCheckpointCreate') | Out-Null
        Wait-Condition { Test-Path -LiteralPath (Join-Path $root 'checkpoint-entered') }
        Ui @('invoke','SemanticCheckpointCancel') | Out-Null
        Ui @('wait-for','SemanticCheckpointStatus','--contains','--value','取消','-t','10000') | Out-Null
        Remove-Item -LiteralPath (Join-Path $root 'hold-checkpoint')
        $packsAfter = @(Get-ChildItem (Join-Path $root 'appdata/FolderRewind/history') -Recurse -File -Filter '*.frpack').Count
        if ($packsBefore -ne $packsAfter) { throw 'Canceled checkpoint published a pack.' }
        Ui @('scroll-into-view','SemanticCheckpointStatus') | Out-Null
        Shot '03-checkpoint-canceled'
    }
    Case 'Checkpoint command uses the saved policy and completes' {
        Ui @('invoke','SemanticCheckpointCreate') | Out-Null
        Ui @('wait-for','SemanticCheckpointStatus','--contains','--value','已提交','-t','20000') | Out-Null
        Ui @('scroll-into-view','SemanticCheckpointStatus') | Out-Null
        Shot '04-checkpoint-complete'
    }
    Case 'Invalid settings report an error and preserve saved state' {
        Ui @('invoke','ConfigSettingsTabGeneral') | Out-Null
        Ui @('set-value','ConfigSettingsName','') | Out-Null
        Ui @('invoke','ProjectSettingsSave') | Out-Null
        Ui @('wait-for','Message','--contains','--value','请输入唯一的项目名称') | Out-Null
        Ui @('wait-for','ProjectSettingsSave','-p','IsEnabled','--value','True') | Out-Null
        if ((Saved).Name -ne 'UI later edit') { throw 'Invalid settings replaced the saved name.' }
        Shot '05-validation-error'
        Ui @('set-value','ConfigSettingsName','UI later edit') | Out-Null
    }
    }
    Ui @('invoke','NavHistory') | Out-Null
    Ui @('wait-for','HistoryVersionList') | Out-Null
    Ui @('inspect','--interactive','--json') | Set-Content -LiteralPath (Join-Path $output 'history-tree.json') -Encoding utf8
    Shot '06-history'
    Case 'History entry restores exact Native content through the confirmation dialog' {
        $source = [IO.Path]::GetFullPath((Saved).SourceFolders[0].Path)
        if (!$source.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Expected isolated source.' }
        function Tree {
            @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName | ForEach-Object { [IO.Path]::GetRelativePath($source,$_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n"
        }
        $before = Tree
        $extra = Join-Path $source 'ui-restore-extra.bin'
        Set-Content -LiteralPath $extra 'must be removed by clean restore'
        $tree = Ui @('inspect','--interactive','--json') | ConvertFrom-Json
        function Flatten($elements) { foreach($element in $elements) { $element; Flatten $element.children } }
        $restore = @(Flatten $tree.windows.elements | Where-Object automationId -like 'HistoryVersionRestore_*')[0].automationId
        if (!$restore) { throw 'Missing Native history restore entry.' }
        Ui @('invoke',$restore) | Out-Null
        Ui @('wait-for','PrimaryButton','-t','15000') | Out-Null
        Shot '07-restore-confirm'
        Ui @('invoke','PrimaryButton') | Out-Null
        Ui @('wait-for','PrimaryButton','--gone','-t','20000') | Out-Null
        Wait-Condition { !(Test-Path -LiteralPath $extra) }
        if ((Tree) -ne $before) { throw 'UI restore changed captured bytes.' }
        Ui @('wait-for',$restore,'-t','10000') | Out-Null
        Shot '08-restored'
    }
} finally {
    foreach ($point in 'settings-save','checkpoint') { $hold=Join-Path $root ('hold-'+$point); if(Test-Path -LiteralPath $hold) { Remove-Item -LiteralPath $hold } }
    & winapp ui yield --on local | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
    @{target='local';appPid=$AppPid;results=@($results.ToArray());screenshots=@($screenshots.ToArray())} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'test-results.json') -Encoding utf8
}
