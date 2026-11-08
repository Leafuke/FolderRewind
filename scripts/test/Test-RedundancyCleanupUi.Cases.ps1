# Invoked by Test-RedundancyCleanupUi.ps1 in its scoped, isolated session.
# These cases exercise production UI; no acceptance bootstrap is compiled in.
function Wait-Until([scriptblock]$Condition,[int]$Seconds=30) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while (!(& $Condition)) {
        if ($clock.Elapsed.TotalSeconds -ge $Seconds) { throw 'Timed out waiting for fixture state.' }
        Start-Sleep -Milliseconds 150
    }
}
function Current-Config {
    $path=Join-Path $data 'FolderRewind/config.json'
    if (!(Test-Path -LiteralPath $path)) { throw 'Isolated configuration missing.' }
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try { return (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json) }
        catch [IO.IOException] { if ($attempt -eq 29) { throw }; Start-Sleep -Milliseconds 100 }
    }
}
function Start-Scenario([string]$Scenario) {
    Ui @('invoke','NavHome') | Out-Null
    $choices=@(Tree | Where-Object { $_.automationId -eq $Scenario -and !$_.isOffscreen })
    if (!$choices.Count) {
        Ui @('wait-for','HomeNewConfig','-t','10000') | Out-Null
        Ui @('invoke','HomeNewConfig') | Out-Null
    }
    Ui @('wait-for',$Scenario,'-t','10000') | Out-Null
    Ui @('invoke',$Scenario) | Out-Null
}
function Select-OpenPopupItem([string]$Name) {
    $found=@()
    $windows=@(Checked @('ui','list-windows','--on','local','-a',"$($app.Id)",'--json') | ConvertFrom-Json)
    foreach ($window in $windows) {
        if (!$window.hwnd) { continue }
        $tree=Checked @('ui','inspect','--on','local','-w',[string]$window.hwnd,'-d','16','--json') | ConvertFrom-Json
        foreach ($item in @(Flatten $tree.windows.elements | Where-Object { $_.name -eq $Name -and $_.type -in @('ListItem','MenuItem') -and !$_.isOffscreen })) {
            $found+=@{window=[string]$window.hwnd;selector=$item.selector}
        }
    }
    if ($found.Count -ne 1) { throw "Popup item '$Name' not uniquely visible." }
    Checked @('ui','invoke',$found[0].selector,'--action','select','--on','local','-w',$found[0].window) | Out-Null
}
function Select-Branch([int]$Index) {
    Ui @('focus','HistoryBranchFilter') | Out-Null
    Ui @('invoke','HistoryBranchFilter','--action','expand') | Out-Null
    Ui @('send-keys','home','--via','send-input') | Out-Null
    for ($i=0; $i -lt $Index; $i++) { Ui @('send-keys','down','--via','send-input') | Out-Null }
    Ui @('send-keys','enter','--via','send-input') | Out-Null
}
function Select-Project([string]$Name) {
    $project=@((Current-Config).BackupConfigs | Where-Object Name -eq $Name)
    if ($project.Count -ne 1) { throw "Expected one project named $Name." }
    Ui @('invoke','NavHome') | Out-Null
    Ui @('wait-for',('HomeConfigCard_'+$project[0].Id),'-t','10000') | Out-Null
    Ui @('invoke',('HomeConfigCard_'+$project[0].Id)) | Out-Null
    Ui @('wait-for','FolderManagerBackupConfig','-t','10000') | Out-Null
    $project[0]
}
function Open-ProjectHistory([string]$Name) {
    $project=Select-Project $Name
    if (@($project.SourceFolders).Count -ne 1) { throw 'Expected one project source.' }
    $sourceId='FolderManagerFolder_' + $project.SourceFolders[0].Id
    Ui @('wait-for',$sourceId,'-t','10000') | Out-Null
    Ui @('invoke',$sourceId) | Out-Null
    Ui @('wait-for','FolderManagerOpenHistory','-t','10000') | Out-Null
    Ui @('invoke','FolderManagerOpenHistory') | Out-Null
    Ui @('wait-for','HistoryVersionList','-t','30000') | Out-Null
}
function Backup-ChangedProject([string]$Name) {
    $logDir=Join-Path $data 'FolderRewind/logs'
    $log=Get-ChildItem -LiteralPath $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$log) { throw 'Backup log is unavailable.' }
    $pattern='备份结果: ' + [regex]::Escape($Name)
    $before=[regex]::Matches((Get-Content -Raw -LiteralPath $log.FullName),$pattern).Count
    Ui @('invoke','FolderManagerBackupConfig') | Out-Null
    Wait-Until {
        $script:backupLog=Get-Content -Raw -LiteralPath $log.FullName
        [regex]::Matches($script:backupLog,$pattern).Count -gt $before
    } 120
    $last=$script:backupLog.LastIndexOf('备份结果: ' + $Name,[StringComparison]::Ordinal)
    $terminal=$script:backupLog.Substring($last)
    if ($terminal -notmatch '新备份\s+1\s+·\s+无变化\s+0') { throw 'Branch backup did not capture a new version.' }
    Ui @('wait-for','FolderManagerBackupConfig','-p','IsEnabled','--value','True','-t','120000') | Out-Null
}
function Bind-RestartedCandidate([switch]$Safe) {
    $oldPid=$app.Id
    Wait-Until { !(Get-Process -Id $oldPid -ErrorAction SilentlyContinue) } 30
    $script:restartedProcesses=@()
    Wait-Until {
        $script:restartedProcesses=@(Get-CimInstance Win32_Process -Filter "Name='FolderRewind.exe'" | Where-Object {
            $_.ProcessId -ne $oldPid -and $_.ExecutablePath -and
            [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),$candidate,[StringComparison]::OrdinalIgnoreCase) -and
            ((($_.CommandLine -match '(?:^|\s)--safe-mode(?:\s|$)')) -eq $Safe.IsPresent)
        })
        $script:restartedProcesses.Count -eq 1
    } 30
    $script:app=Get-Process -Id $script:restartedProcesses[0].ProcessId
    Checked @('ui','wait-for','NavView','--on','local','-a',"$($app.Id)",'-t','30000') | Out-Null
    $windows=@(Checked @('ui','list-windows','--on','local','-a',"$($app.Id)",'--json') | ConvertFrom-Json)
    $main=@($windows | Where-Object {$_.hwnd -and $_.className -eq 'WinUIDesktopWin32WindowClass'})
    if ($main.Count -ne 1) { throw 'Restarted candidate window ambiguous.' }
    $script:hwnd=[string]$main[0].hwnd
    if (@(Tree).Count -eq 0) { throw 'Restarted candidate UI tree empty.' }
}
function Restart-Normal {
    # Close through the app's normal close prompt and select Exit, never tray.
    [void]$app.CloseMainWindow()
    try { Ui @('wait-for','SecondaryButton','-t','2000') | Out-Null; Ui @('invoke','SecondaryButton') | Out-Null } catch { if (!$app.HasExited) { throw } }
    $previous=$app.Id
    Wait-Until { !(Get-Process -Id $previous -ErrorAction SilentlyContinue) } 30
    $script:app=Start-Process -FilePath $candidate -WorkingDirectory (Split-Path $candidate) -PassThru
    Checked @('ui','wait-for','NavView','--on','local','-a',"$($app.Id)",'-t','30000') | Out-Null
    $windows=@(Checked @('ui','list-windows','--on','local','-a',"$($app.Id)",'--json') | ConvertFrom-Json)
    $main=@($windows | Where-Object {$_.hwnd -and $_.className -eq 'WinUIDesktopWin32WindowClass'})
    if ($main.Count -ne 1) { throw 'Normal restart window ambiguous.' }
    $script:hwnd=[string]$main[0].hwnd
    if (@(Tree).Count -eq 0) { throw 'Normal restart UI tree empty.' }
}
Case 'plugin-install-enable-disable-safe-mode' {
    Install-CandidatePlugin
    $toggle='SettingsPluginEnabled_com.folderrewind.minerewind'
    Ui @('wait-for',$toggle,'--value','On','-t','30000') | Out-Null
    $statePath=Join-Path $data 'FolderRewind/plugins/com.folderrewind.minerewind/install-state.v1.json'
    Wait-Until { Test-Path -LiteralPath $statePath -PathType Leaf } 30
    $state=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    $current=@($state.versions | Where-Object { $_.version -eq $state.currentVersion })
    Assert-Case ($current.Count -eq 1 -and $current[0].sha256 -eq $PluginSha256) 'exact-package-installed'
    Assert-Case $true 'enabled'
    Ui @('invoke',$toggle,'--action','toggle-off') | Out-Null
    Ui @('wait-for',$toggle,'--value','Off','-t','30000') | Out-Null
    Assert-Case $true 'disabled'
    Ui @('invoke',$toggle,'--action','toggle-on') | Out-Null
    Ui @('wait-for',$toggle,'--value','On','-t','30000') | Out-Null
    Ui @('invoke','SettingsRestartSafeMode') | Out-Null
    Ui @('wait-for','PrimaryButton','-t','5000') | Out-Null
    Shot 'safe-mode-confirmation'
    Ui @('invoke','PrimaryButton') | Out-Null
    Bind-RestartedCandidate -Safe
    Open-PluginSettings
    Ui @('invoke','SettingsManualInstallPlugin') | Out-Null
    Start-Sleep -Milliseconds 750
    $pickers=@(Checked @('ui','list-windows','--on','local','--json') | ConvertFrom-Json | Where-Object { $_.processName -eq 'PickerHost' -and [string]$_.ownerHwnd -eq $script:hwnd })
    Assert-Case ($pickers.Count -eq 0) 'safe-mode-blocks'
    Ui @('scroll-into-view','SettingsManualInstallPlugin') | Out-Null
    Shot 'safe-mode-active'
    Restart-Normal
    Open-PluginSettings
    Ui @('wait-for',$toggle,'--value','On','-t','30000') | Out-Null
    Assert-Case $true 'normal-mode-restored'
    Ui @('scroll-into-view',$toggle) | Out-Null
    Shot 'plugin-normal-restored'
}

Case 'world-core-full' {
    $world=$worlds['fabric']
    $expected=World-Manifest $world | ConvertTo-Json -Compress
    Start-Scenario 'ScenarioFolder'
    Ui @('wait-for','SetupAddFolder','-t','5000') | Out-Null
    Ui @('invoke','SetupAddFolder') | Out-Null
    Choose-File $world -Folder
    Ui @('wait-for','SetupNextContent','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    Ui @('invoke','SetupNextContent') | Out-Null
    Ui @('wait-for','SetupName','-t','5000') | Out-Null
    Ui @('set-value','SetupName','Redundancy Core Full') | Out-Null
    Ui @('set-value','SetupDestination',(Join-Path $session 'core-backup')) | Out-Null
    Ui @('invoke','SetupNextLocation') | Out-Null
    Ui @('wait-for','SetupCreateBackup','-t','5000') | Out-Null
    Ui @('invoke','SetupCreateBackup') | Out-Null
    Ui @('wait-for','SetupViewVersions','-t','120000') | Out-Null
    $project=@((Current-Config).BackupConfigs | Where-Object Name -eq 'Redundancy Core Full')
    Assert-Case ($project.Count -eq 1 -and $project[0].SourceFolders[0].Path -eq $world) 'capture-completed'
    Shot 'core-first-backup'
    $extra=Join-Path $world 'redundancy-ui-extra.txt'
    [IO.File]::WriteAllText($extra,'restore must remove this file')
    Ui @('invoke','SetupViewVersions') | Out-Null
    Ui @('wait-for','HistoryVersionList','-t','30000') | Out-Null
    $restore=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionRestore_*' -and !$_.isOffscreen } | Select-Object -First 1)
    if ($restore.Count -ne 1) { throw 'No visible version restore action.' }
    Ui @('invoke',$restore[0].automationId) | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Shot 'core-restore-confirmation'
    Ui @('invoke','PrimaryButton') | Out-Null
    Wait-Until { !(Test-Path -LiteralPath $extra) } 120
    Ui @('wait-for','PrimaryButton','--gone','-t','30000') | Out-Null
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','120000') | Out-Null
    $actual=World-Manifest $world | ConvertTo-Json -Compress
    Assert-Case ($actual -ceq $expected) 'restore-byte-match'
    Shot 'core-restored'
}

Case 'world-native-incremental' {
    $world=$worlds['neoforge']
    Start-Scenario 'ScenarioFolder'
    Ui @('invoke','SetupAddFolder') | Out-Null
    Choose-File $world -Folder
    Ui @('invoke','SetupNextContent') | Out-Null
    Ui @('wait-for','SetupName','-t','5000') | Out-Null
    Ui @('set-value','SetupName','Redundancy Native') | Out-Null
    Ui @('set-value','SetupDestination',(Join-Path $session 'native-backup')) | Out-Null
    Ui @('focus','SetupKind') | Out-Null
    Ui @('invoke','SetupKind','--action','expand') | Out-Null
    Ui @('send-keys','home','--via','send-input') | Out-Null
    Ui @('send-keys','down','--via','send-input') | Out-Null
    Ui @('send-keys','down','--via','send-input') | Out-Null
    Ui @('send-keys','enter','--via','send-input') | Out-Null
    $kind=(Ui @('get-value','SetupKind','--json') | ConvertFrom-Json).text
    if ($kind -notin @('Minecraft 存档','Minecraft Saves')) { throw "Unexpected project kind: $kind" }
    Ui @('invoke','SetupNextLocation') | Out-Null
    Ui @('wait-for','SetupCreateOnly','-t','5000') | Out-Null
    Ui @('invoke','SetupCreateOnly') | Out-Null
    Ui @('wait-for','SetupViewProject','-t','30000') | Out-Null
    Ui @('invoke','SetupViewProject') | Out-Null
    Ui @('wait-for','FolderManagerConfigSettings','-t','10000') | Out-Null
    Ui @('invoke','FolderManagerConfigSettings') | Out-Null
    Ui @('invoke','ConfigSettingsTabBackup') | Out-Null
    Ui @('wait-for','SemanticBackupEnable','-p','IsEnabled','--value','True','-t','30000') | Out-Null
    Ui @('invoke','SemanticBackupEnable','--action','toggle-on') | Out-Null
    Ui @('wait-for','SemanticBackupFallback','-t','5000') | Out-Null
    Ui @('invoke','SemanticBackupFallback','--action','toggle-off') | Out-Null
    Ui @('invoke','ProjectSettingsSave') | Out-Null
    Wait-Until { @((Current-Config).BackupConfigs | Where-Object { $_.Name -eq 'Redundancy Native' -and $_.ArtifactTransformPolicy.ExperimentalEnabled }).Count -eq 1 } 30
    $null=Select-Project 'Redundancy Native'
    $history=Join-Path $data 'FolderRewind/history'
    $before=@(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack' -ErrorAction SilentlyContinue).Count
    Ui @('invoke','FolderManagerBackupConfig') | Out-Null
    Wait-Until { @(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack' -ErrorAction SilentlyContinue).Count -gt $before } 120
    Ui @('wait-for','FolderManagerBackupConfig','-p','IsEnabled','--value','True','-t','120000') | Out-Null
    Assert-Case $true 'full-capture-completed'
    $region=Get-ChildItem -LiteralPath $world -Recurse -File -Filter '*.mca' | Select-Object -First 1
    if (!$region) { throw 'The copied world contains no region file.' }
    Remove-Item -LiteralPath $region.FullName
    $expected=World-Manifest $world | ConvertTo-Json -Compress
    $before=@(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack').Count
    Ui @('invoke','FolderManagerBackupConfig') | Out-Null
    Wait-Until { @(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack' -ErrorAction SilentlyContinue).Count -gt $before } 120
    Ui @('wait-for','FolderManagerBackupConfig','-p','IsEnabled','--value','True','-t','120000') | Out-Null
    Assert-Case $true 'incremental-capture-completed'
    Shot 'native-incremental-captured'
    [IO.File]::WriteAllText($region.FullName,'restore must remove this file')
    Open-ProjectHistory 'Redundancy Native'
    $restore=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionRestore_*' -and !$_.isOffscreen } | Select-Object -First 1)
    if ($restore.Count -ne 1) { throw 'No visible Native version restore action.' }
    Ui @('invoke',$restore[0].automationId) | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Wait-Until { !(Test-Path -LiteralPath $region.FullName) } 120
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','120000') | Out-Null
    $actual=World-Manifest $world | ConvertTo-Json -Compress
    Assert-Case ($actual -ceq $expected) 'deleted-content-restored-correctly'
    Shot 'native-deletion-restored'
}

Case 'history-preview-branch-merge-restore' {
    Open-ProjectHistory 'Redundancy Native'
    $versions=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionMore_*' -and !$_.isOffscreen })
    Assert-Case ($versions.Count -gt 0) 'history-visible'
    Ui @('invoke',$versions[0].automationId) | Out-Null
    $preview=@(Tree | Where-Object { $_.automationId -like 'HistoryPreviewWorld_*' -and $_.isEnabled })
    if ($preview.Count -ne 1) { throw 'World preview menu action unavailable.' }
    Ui @('invoke',$preview[0].automationId) | Out-Null
    Ui @('wait-for','PreviewRefresh','-t','120000') | Out-Null
    Ui @('wait-for','PreviewLayers','-p','IsEnabled','--value','True','-t','120000') | Out-Null
    Ui @('wait-for','PreviewLoading','--gone','-t','120000') | Out-Null
    Assert-Case (@(Tree | Where-Object { $_.automationId -eq 'PreviewLayers' -and $_.isEnabled }).Count -eq 1) 'preview-opened'
    Shot 'history-world-preview'
    Ui @('invoke','NavHistory') | Out-Null
    Ui @('wait-for','AdvancedHistoryItem','-t','10000') | Out-Null
    Ui @('invoke','AdvancedHistoryItem') | Out-Null
    Ui @('wait-for','HistoryBranchFilter','-t','10000') | Out-Null
    $mainBranch=(Ui @('get-value','HistoryBranchFilter','--json') | ConvertFrom-Json).text
    if ([string]::IsNullOrWhiteSpace($mainBranch)) { throw 'Current branch name is missing.' }
    Ui @('wait-for','HistoryVersionList','-t','10000') | Out-Null
    $versions=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionMore_*' -and !$_.isOffscreen })
    if (!$versions.Count) { throw 'No visible source version for branching.' }
    Ui @('invoke',$versions[0].automationId) | Out-Null
    $branchAction=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionCreateBranch_*' -and $_.isEnabled })
    if ($branchAction.Count -ne 1) { throw 'Branch creation action unavailable.' }
    Ui @('invoke',$branchAction[0].automationId) | Out-Null
    Ui @('wait-for','AppDialogTextInput','-t','5000') | Out-Null
    Ui @('set-value','AppDialogTextInput','redundancy-feature') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Ui @('wait-for','HistoryBranchFilter','-t','10000') | Out-Null
    Select-Branch 1
    Ui @('wait-for','HistoryBranchFilter','--value','redundancy-feature','-t','10000') | Out-Null
    Assert-Case $true 'branch-created'
    Shot 'history-branch-created'
    Ui @('wait-for','HistoryBranchCheckout','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    Ui @('invoke','HistoryBranchCheckout') | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Ui @('wait-for','PrimaryButton','--gone','-t','60000') | Out-Null
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','120000') | Out-Null
    Wait-Until { $v=(Ui @('get-value','HistoryBranchFilter','--json') | ConvertFrom-Json).text; $v -match 'redundancy-feature' -and $v -match '当前|current' } 120
    $world=$worlds['neoforge']
    [IO.File]::WriteAllText((Join-Path $world 'data/redundancy-feature.dat'),'feature branch')
    $history=Join-Path $data 'FolderRewind/history'
    $null=Select-Project 'Redundancy Native'
    Backup-ChangedProject 'Redundancy Native'
    Ui @('invoke','NavHistory') | Out-Null
    Ui @('wait-for','AdvancedHistoryItem','-t','10000') | Out-Null
    Ui @('invoke','AdvancedHistoryItem') | Out-Null
    # The current branch is listed first; main is the other entry here.
    Select-Branch 1
    Ui @('wait-for','HistoryBranchCheckout','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    Ui @('invoke','HistoryBranchCheckout') | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Ui @('wait-for','PrimaryButton','--gone','-t','60000') | Out-Null
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','120000') | Out-Null
    Wait-Until { $v=(Ui @('get-value','HistoryBranchFilter','--json') | ConvertFrom-Json).text; $v -match 'main' -and $v -match '当前|current' } 120
    [IO.File]::WriteAllText((Join-Path $world 'data/redundancy-main.dat'),'main branch')
    $null=Select-Project 'Redundancy Native'
    Backup-ChangedProject 'Redundancy Native'
    Ui @('invoke','NavHistory') | Out-Null
    Ui @('wait-for','AdvancedHistoryItem','-t','10000') | Out-Null
    Ui @('invoke','AdvancedHistoryItem') | Out-Null
    Select-Branch 1
    Ui @('invoke','HistoryBranchMoreActions') | Out-Null
    Ui @('invoke','HistoryBranchMerge') | Out-Null
    Ui @('wait-for','MergeWorkspace_Analyze','-p','IsEnabled','--value','True','-t','15000') | Out-Null
    Ui @('invoke','MergeWorkspace_Analyze') | Out-Null
    Ui @('wait-for','MergeWorkspace_Generate','-p','IsEnabled','--value','True','-t','30000') | Out-Null
    Shot 'history-merge-analyzed'
    Ui @('invoke','MergeWorkspace_Generate') | Out-Null
    Ui @('wait-for','MergeWorkspace_ApplyReviewed','-p','IsEnabled','--value','True','-t','120000') | Out-Null
    Ui @('invoke','MergeWorkspace_ApplyReviewed') | Out-Null
    Ui @('wait-for','MergeWorkspace_ReturnHistory','-t','120000') | Out-Null
    Assert-Case ((Test-Path -LiteralPath (Join-Path $world 'data/redundancy-feature.dat')) -and (Test-Path -LiteralPath (Join-Path $world 'data/redundancy-main.dat'))) 'merge-committed'
    $expected=World-Manifest $world | ConvertTo-Json -Compress
    Set-Content -LiteralPath (Join-Path $session 'merge-expected-manifest.json') -Value $expected -Encoding utf8
    Shot 'history-merge-result'
    Ui @('invoke','MergeWorkspace_ReturnHistory') | Out-Null
    Ui @('wait-for','HistoryVersionList','-t','30000') | Out-Null
    Ui @('wait-for','HistoryBranchFilter','-t','10000') | Out-Null
    Select-Branch 0
    Ui @('wait-for','HistoryBranchFilter','--contains','--value','main','-t','10000') | Out-Null
    $restore=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionRestore_*' -and !$_.isOffscreen } | Select-Object -First 1)
    if ($restore.Count -ne 1) { throw 'Merged version restore action missing.' }
    $extra=Join-Path $world 'redundancy-merge-extra.txt'
    [IO.File]::WriteAllText($extra,'remove after restore')
    Ui @('invoke',$restore[0].automationId) | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Wait-Until { !(Test-Path -LiteralPath $extra) } 120
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','120000') | Out-Null
    $actual=World-Manifest $world | ConvertTo-Json -Compress
    Set-Content -LiteralPath (Join-Path $session 'merge-actual-manifest.json') -Value $actual -Encoding utf8
    Assert-Case ($actual -ceq $expected) 'restore-byte-match'
    Shot 'history-merge-restored'
}

Case 'discovery' {
    $discoveryWorld=Join-Path $session 'discovery-world'
    Copy-Item -LiteralPath (Join-Path $root 'ui-fixture/neoforge') -Destination $discoveryWorld -Recurse
    Start-Scenario 'ScenarioMinecraft'
    $pick=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('选择目录扫描','Choose a folder to scan') })
    if ($pick.Count -ne 1) { throw 'Minecraft discovery root button missing or ambiguous.' }
    Ui @('invoke',$pick[0].selector) | Out-Null
    Choose-File $discoveryWorld -Folder
    Wait-Until { @((Tree) | Where-Object { $_.type -eq 'CheckBox' -and $_.name -and $_.isEnabled }).Count -gt 0 } 30
    $nodes=Tree
    Assert-Case (@($nodes | Where-Object { $_.name -and $_.name.Contains('discovery-world') }).Count -gt 0) 'real-copy-discovered'
    $choice=@($nodes | Where-Object { $_.type -eq 'CheckBox' -and $_.name -and $_.isEnabled } | Select-Object -First 1)
    Ui @('invoke',$choice[0].selector,'--action','toggle-on') | Out-Null
    $continue=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('继续创建备份项目','Continue creating backup project') -and $_.isEnabled })
    if ($continue.Count -ne 1) { throw 'Discovery continue action missing.' }
    Ui @('invoke',$continue[0].selector) | Out-Null
    Ui @('wait-for','SetupName','-t','10000') | Out-Null
    Shot 'minecraft-discovery-selection'
    Ui @('set-value','SetupName','Redundancy Discovered') | Out-Null
    Ui @('set-value','SetupDestination',(Join-Path $session 'discovery-backup')) | Out-Null
    Ui @('invoke','SetupNextLocation') | Out-Null
    Ui @('wait-for','SetupCreateOnly','-t','5000') | Out-Null
    Ui @('invoke','SetupCreateOnly') | Out-Null
    Ui @('wait-for','SetupViewProject','-t','30000') | Out-Null
    Restart-Normal
    $saved=@((Current-Config).BackupConfigs | Where-Object Name -eq 'Redundancy Discovered')
    Assert-Case ($saved.Count -eq 1 -and @($saved[0].SourceFolders | Where-Object Path -eq $discoveryWorld).Count -eq 1) 'selection-persisted'
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','HomeNewConfig','--action','expand') | Out-Null
    Ui @('invoke','HomeAutoDiscoverGames') | Out-Null
    Ui @('wait-for','DiscoveryScan','-t','10000') | Out-Null
    $steamRoot=Join-Path $session 'steam-fixture'
    $steamApps=Join-Path $steamRoot 'steamapps'
    $fixtureDirectory=Join-Path $steamApps 'common/CleanupFixture/Saves'
    New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
    $fixtureSave=Join-Path $fixtureDirectory 'fixture.sav'
    [IO.File]::WriteAllText($fixtureSave,'deterministic discovery fixture')
    [IO.File]::WriteAllText((Join-Path $steamApps 'appmanifest_42424242.acf'),"`"AppState`"`n{`n `"appid`" `"42424242`"`n `"name`" `"FolderRewind Cleanup Fixture`"`n `"installdir`" `"CleanupFixture`"`n}`n")
    $manifest=Join-Path $session 'ludusavi-fixture.yaml'
    [IO.File]::WriteAllText($manifest,"FolderRewind Cleanup Fixture:`n  steam:`n    id: 42424242`n  files:`n    '<base>/Saves/*.sav': {}`n")
    Ui @('invoke','DiscoveryRules') | Out-Null
    $roots=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('游戏平台根目录','Game store roots') -and $_.expandState })
    if ($roots.Count -ne 1) { throw 'Game store roots expander missing.' }
    if ($roots[0].expandState -eq 'collapsed') { Ui @('invoke',$roots[0].selector) | Out-Null }
    $addRoot=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('添加 Steam 根目录','Add Steam root') })
    if ($addRoot.Count -ne 1) { throw 'Add Steam root action missing.' }
    Ui @('invoke',$addRoot[0].selector) | Out-Null
    Choose-File $steamRoot -Folder
    $saveRoots=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('保存发现设置','Save discovery settings') })
    if ($saveRoots.Count -ne 1) { throw 'Save discovery settings action missing.' }
    Ui @('invoke',$saveRoots[0].selector) | Out-Null
    $import=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('导入本地清单','Import local manifest') })
    if ($import.Count -ne 1) { throw 'Ludusavi manifest import button missing or ambiguous.' }
    Ui @('invoke',$import[0].selector) | Out-Null
    Choose-File $manifest
    Wait-Until {
        $games=Ui @('inspect','DiscoveryGames','-d','16','--json') | ConvertFrom-Json
        @(Flatten $games.windows.elements | Where-Object { $_.name -eq 'FolderRewind Cleanup Fixture' }).Count -gt 0
    } 60
    Assert-Case $true 'ludusavi-result'
    Shot 'ludusavi-discovery'
}

Case 'cloud-profile-webdav' {
    $null=Select-Project 'Redundancy Core Full'
    Ui @('invoke','FolderManagerConfigSettings') | Out-Null
    Ui @('invoke','ConfigSettingsTabCloud') | Out-Null
    Ui @('invoke','ProjectSettingsCloudSetup') | Out-Null
    Ui @('wait-for','CloudProjectPageScroll','-t','10000') | Out-Null
    $change=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('添加或更换云连接','Add or change cloud connection') })
    if ($change.Count -ne 1) { throw 'Cloud connection action missing or ambiguous.' }
    Ui @('invoke',$change[0].selector) | Out-Null
    $webdavChoice=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -in @('连接已有 WebDAV 服务','Connect an existing WebDAV service') })
    if ($webdavChoice.Count -ne 1) { throw 'WebDAV method button missing or ambiguous.' }
    Ui @('invoke',$webdavChoice[0].selector) | Out-Null
    Ui @('wait-for','CloudSetupExecutable','-t','5000') | Out-Null
    Ui @('set-value','CloudSetupExecutable',$RclonePath) | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    Ui @('wait-for','CloudSetupUrl','-t','30000') | Out-Null
    Ui @('set-value','CloudSetupUrl',$script:webdavUrl) | Out-Null
    Ui @('set-value','CloudSetupUser','fixture') | Out-Null
    Ui @('send-keys','fixture-password','--target','CloudSetupPassword','--via','send-input','--verbatim') | Out-Null
    $insecure=@(Tree | Where-Object { $_.type -eq 'CheckBox' -and $_.name -match 'HTTP' })
    if ($insecure.Count -ne 1) { throw 'HTTP loopback consent control missing or ambiguous.' }
    Ui @('invoke',$insecure[0].selector,'--action','toggle-on') | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    Ui @('wait-for','CloudSetupRemoteRoot','-t','30000') | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    Wait-Until { @(Tree | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq 'NextButton' -and $_.isEnabled }).Count -eq 1 } 60
    $log=Join-Path $session 'webdav.log'
    Assert-Case ((Test-Path -LiteralPath $log) -and (Get-Content -Raw -LiteralPath $log) -match '"(?:PUT|MKCOL|PROPFIND) [^"]+" 2\d\d') 'connection-verified'
    Shot 'cloud-write-verified'
    Ui @('invoke','NextButton') | Out-Null
    $hasConfirm=$false
    try { Ui @('wait-for','PrimaryButton','-t','3000') | Out-Null; $hasConfirm=$true } catch { }
    if ($hasConfirm) { Ui @('invoke','PrimaryButton') | Out-Null }
    Wait-Until { @((Current-Config).CloudConnections).Count -gt 0 } 60
    Assert-Case (@((Current-Config).CloudConnections).Count -gt 0) 'profile-saved'
    Shot 'cloud-profile-saved'
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','SettingsItem') | Out-Null
    Ui @('wait-for','SettingsCloudConnections','-t','10000') | Out-Null
    Ui @('invoke','SettingsCloudConnections') | Out-Null
    Ui @('wait-for','CloudConnectionCatalog','-t','10000') | Out-Null
    $catalog=Ui @('inspect','CloudConnectionCatalog','-d','12','--json') | ConvertFrom-Json
    $profiles=@(Flatten $catalog.windows.elements | Where-Object { $_.type -eq 'ListItem' -and !$_.isOffscreen })
    if ($profiles.Count -ne 1) { throw 'Expected one created cloud profile.' }
    Ui @('invoke',$profiles[0].selector,'--action','select') | Out-Null
    Ui @('invoke','CloudEditConnection') | Out-Null
    Ui @('wait-for','BackButton','-t','10000') | Out-Null
    Ui @('invoke','BackButton') | Out-Null
    Ui @('invoke','BackButton') | Out-Null
    Ui @('invoke','BackButton') | Out-Null
    Ui @('wait-for','CloudSetupExecutable','-t','10000') | Out-Null
    $alternate=Join-Path $session 'rclone-edited.exe'
    Copy-Item -LiteralPath $RclonePath -Destination $alternate
    Ui @('set-value','CloudSetupExecutable',$alternate) | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    Ui @('wait-for','CloudSetupConfigPath','-t','30000') | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    Ui @('wait-for','CloudSetupRemoteRoot','-t','30000') | Out-Null
    $logLength=(Get-Item -LiteralPath $log).Length
    Ui @('invoke','NextButton') | Out-Null
    Wait-Until { (Get-Item -LiteralPath $log).Length -gt $logLength } 60
    Ui @('wait-for','NextButton','-p','IsEnabled','--value','True','-t','60000') | Out-Null
    Ui @('invoke','NextButton') | Out-Null
    $hasConfirm=$false
    try { Ui @('wait-for','PrimaryButton','-t','3000') | Out-Null; $hasConfirm=$true } catch { }
    if ($hasConfirm) { Ui @('invoke','PrimaryButton') | Out-Null }
    Wait-Until { @((Current-Config).CloudConnections | Where-Object ExecutablePath -eq $alternate).Count -eq 1 } 60
    Assert-Case (@((Current-Config).CloudConnections | Where-Object ExecutablePath -eq $alternate).Count -eq 1) 'edit-used-by-webdav'
    Shot 'cloud-profile-edited'
    Restart-Normal
    Assert-Case (@((Current-Config).CloudConnections | Where-Object ExecutablePath -eq $alternate).Count -eq 1) 'restart-persisted'
    Ui @('invoke','SettingsItem') | Out-Null
    Ui @('wait-for','SettingsCloudConnections','-t','10000') | Out-Null
    Shot 'cloud-profile-restarted'
}

Case 'cancel-close' {
    $world=$worlds['neoforge']
    $large=Join-Path $world 'redundancy-cancel-input.bin'
    $stream=[IO.File]::Create($large)
    try { $stream.SetLength(536870912) } finally { $stream.Dispose() }
    $null=Select-Project 'Redundancy Native'
    Ui @('invoke','FolderManagerConfigSettings') | Out-Null
    Ui @('invoke','ConfigSettingsTabBackup') | Out-Null
    Ui @('wait-for','SemanticCheckpointCreate','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    Ui @('invoke','SemanticCheckpointCreate') | Out-Null
    Ui @('wait-for','SemanticCheckpointCancel','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    Ui @('invoke','SemanticCheckpointCancel') | Out-Null
    $status=Ui @('get-value','SemanticCheckpointStatus','--json') | ConvertFrom-Json
    Wait-Until {
        $script:cancelStatus=(Ui @('get-value','SemanticCheckpointStatus','--json') | ConvertFrom-Json).text
        $script:cancelStatus -match '取消|cancel'
    } 60
    Assert-Case ($script:cancelStatus -match '取消|cancel') 'capture-canceled'
    Shot 'capture-canceled'
    Ui @('invoke','ProjectSettingsCancel') | Out-Null
    # Explicitly dismiss the capture toast: it can persist until closed and
    # must not be mistaken for the restore cancellation notification.
    try { Ui @('invoke','NotificationToastClose') | Out-Null } catch { }
    Open-ProjectHistory 'Redundancy Native'
    $restore=@(Tree | Where-Object { $_.automationId -like 'HistoryVersionRestore_*' -and !$_.isOffscreen } | Select-Object -First 1)
    if ($restore.Count -ne 1) { throw 'No restore action available for cancellation.' }
    Ui @('invoke',$restore[0].automationId) | Out-Null
    Ui @('wait-for','PrimaryButton','-t','10000') | Out-Null
    Ui @('invoke','PrimaryButton') | Out-Null
    Ui @('wait-for','HistoryCancelOperation','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    Ui @('invoke','HistoryCancelOperation') | Out-Null
    Ui @('wait-for','HistoryCancelOperation','--gone','-t','60000') | Out-Null
    Assert-Case (Test-Path -LiteralPath $large) 'restore-canceled'
    Wait-Until { $script:cancelLog=@(Get-ChildItem (Join-Path $session 'appdata\FolderRewind\logs') -Filter 'app-*.log' | Select-String -Pattern 'Restore canceled:'); $script:cancelLog.Count -gt 0 } 30
    $match=[regex]::Match($script:cancelLog[-1].Line,'\[operation:([^\]]+)\]')
    if (!$match.Success) { throw 'Restore cancellation log has no operation identifier.' }
    $operation=$match.Groups[1].Value
    $notification=@(Get-ChildItem (Join-Path $session 'appdata\FolderRewind\logs') -Filter 'app-*.log' | Select-String -SimpleMatch "[operation:$operation] 已取消: 已取消")
    Assert-Case ($notification.Count -eq 1) 'restore-cancel-message'
    Ui @('invoke','NavLogs') | Out-Null
    Ui @('wait-for','LogSearch','-t','10000') | Out-Null
    Ui @('set-value','LogSearch','Restore canceled:') | Out-Null
    Wait-Until { $t=Ui @('inspect','-d','16','--json') | ConvertFrom-Json; @(Flatten $t.windows.elements | Where-Object { $_.name -match 'Restore canceled:' -and !$_.isOffscreen }).Count -gt 0 } 20
    try { Ui @('invoke','NotificationToastClose') | Out-Null } catch { }
    Shot 'restore-canceled'
    $previous=$app.Id
    [void]$app.CloseMainWindow()
    try { Ui @('wait-for','SecondaryButton','-t','2000') | Out-Null; Ui @('invoke','SecondaryButton') | Out-Null } catch { if (!$app.HasExited) { throw } }
    Wait-Until { !(Get-Process -Id $previous -ErrorAction SilentlyContinue) } 30
    Assert-Case $true 'close-settled'
    $script:app=Start-Process -FilePath $candidate -WorkingDirectory (Split-Path $candidate) -PassThru
    Checked @('ui','wait-for','NavView','--on','local','-a',"$($app.Id)",'-t','30000') | Out-Null
    $windows=@(Checked @('ui','list-windows','--on','local','-a',"$($app.Id)",'--json') | ConvertFrom-Json)
    $main=@($windows | Where-Object {$_.hwnd -and $_.className -eq 'WinUIDesktopWin32WindowClass'})
    if ($main.Count -ne 1) { throw 'Post-cancel restart window ambiguous.' }
    $script:hwnd=[string]$main[0].hwnd
    $null=Select-Project 'Redundancy Native'
    Assert-Case (Test-Path -LiteralPath $large) 'restart-consistent'
    Shot 'post-cancel-restart'
}

Case 'migration-legacy-state' {
    $legacyPath=Join-Path $root 'ui-fixture/legacy-config.json'
    if (!(Test-Path -LiteralPath $legacyPath -PathType Leaf)) { throw 'Legacy migration fixture is missing.' }
    $legacy=Get-Content -Raw -LiteralPath $legacyPath | ConvertFrom-Json
    $project=@($legacy.BackupConfigs | Where-Object Name -eq 'Batch6 Legacy Fixture')
    if ($project.Count -ne 1) { throw 'Legacy fixture project is missing or ambiguous.' }
    $project[0].SourceFolders[0].Path=$worlds['neoforge']
    $project[0].DestinationPath=Join-Path $session 'migration-backups'
    New-Item -ItemType Directory -Path $project[0].DestinationPath | Out-Null
    $importPath=Join-Path $session 'legacy-config.json'
    $legacy | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $importPath -Encoding utf8
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','SettingsItem') | Out-Null
    Expand-Settings 'SettingsPage_DataMigrationExpander'
    Ui @('invoke','MigrationAdvanced') | Out-Null
    Ui @('wait-for','SettingsImportConfig','-t','5000') | Out-Null
    Ui @('invoke','SettingsImportConfig') | Out-Null
    Ui @('wait-for','MigrationWizardChoosePackage','-t','10000') | Out-Null
    Ui @('invoke','MigrationWizardChoosePackage') | Out-Null
    Choose-File $importPath
    Ui @('wait-for','MigrationWizardPrimary','-p','IsEnabled','--value','True','-t','30000') | Out-Null
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Ui @('wait-for','MigrationWizardImportProjects','-t','10000') | Out-Null
    $selection=@(Tree | Where-Object { $_.type -eq 'CheckBox' -and $_.name -eq 'Batch6 Legacy Fixture' })
    if ($selection.Count -ne 1) { throw 'Expected imported legacy project selection.' }
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Ui @('wait-for','MigrationWizardProjectProgress','-t','30000') | Out-Null
    Ui @('wait-for','MigrationWizardPrimary','-p','IsEnabled','--value','True','-t','30000') | Out-Null
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Bind-RestartedCandidate
    Wait-Until { @((Current-Config).BackupConfigs | Where-Object Name -eq 'Batch6 Legacy Fixture').Count -eq 1 } 60
    $saved=@((Current-Config).BackupConfigs | Where-Object Name -eq 'Batch6 Legacy Fixture')[0]
    Assert-Case ($saved.SourceFolders[0].Path -eq $worlds['neoforge'] -and $saved.DestinationPath -eq (Join-Path $session 'migration-backups')) 'legacy-imported'
    $future=$saved.ProviderStates.'com.example.cleanup.future'
    $mine=$saved.ProviderStates.'com.folderrewind.minerewind'
    Assert-Case ($future.SchemaVersion -eq 7 -and $future.Data.marker -eq 'preserve-unknown' -and $future.Data.nested.fact -eq 42 -and $mine.SchemaVersion -eq 1 -and $mine.Data.marker -eq 'existing-state-preserved' -and $saved.Batch6UnknownFact.marker -eq 'preserve-project') 'unknown-state-preserved'
    Assert-Case ($saved.BackupScope.Parameters.selectedRegions -eq '0,0' -and $saved.BackupScope.Parameters.dimensions -eq 'overworld' -and $saved.Cloud.Enabled -eq $false -and $saved.Cloud.LegacyConversionRequired -eq $true) 'legacy-scope-preserved'
    # The post-restart recovery page can require an explicit recheck after
    # local state is initialized. Drive that user-visible action once.
    $recheck=@(Tree | Where-Object { $_.automationId -eq 'MigrationWizardPrimary' -and $_.isEnabled -and !$_.isOffscreen })
    if ($recheck.Count -eq 1) { Ui @('invoke','MigrationWizardPrimary') | Out-Null }
    Wait-Until { @(Tree | Where-Object { $_.automationId -eq 'MigrationWizardPrimary' -and $_.isEnabled -and !$_.isOffscreen -and $_.name -match '完成.*已就绪|finish.*ready' }).Count -eq 1 } 60
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Ui @('wait-for','MigrationWizardResults','-t','60000') | Out-Null
    Assert-Case (@(Tree | Where-Object { $_.name -match '这一步未完成|This step.*not complete' -and !$_.isOffscreen }).Count -eq 0) 'workflow-completed'
    Shot 'legacy-imported'
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','SettingsItem') | Out-Null
    Expand-Settings 'SettingsPage_DataMigrationExpander'
    Ui @('invoke','MigrationAdvanced') | Out-Null
    Ui @('wait-for','SettingsExportConfig','-t','5000') | Out-Null
    Ui @('invoke','SettingsExportConfig') | Out-Null
    Ui @('wait-for','MigrationWizardExportProjects','-t','10000') | Out-Null
    foreach ($other in @((Current-Config).BackupConfigs | Where-Object Name -ne 'Batch6 Legacy Fixture')) {
        Ui @('invoke',('MigrationExportProject_'+$other.Id.Replace('-',''))) | Out-Null
    }
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Ui @('wait-for','MigrationWizardPrimary','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    Ui @('wait-for','MigrationWizardLocation','-t','10000') | Out-Null
    Ui @('invoke','MigrationWizardPrimary') | Out-Null
    $export=Join-Path $session 'legacy-export.json'
    Choose-File $export -Save
    Ui @('wait-for','MigrationWizardResults','-t','30000') | Out-Null
    $exported=Get-Content -Raw -LiteralPath $export | ConvertFrom-Json
    $roundtrip=@($exported.BackupConfigs | Where-Object Name -eq 'Batch6 Legacy Fixture')
    $roundFuture=if($roundtrip.Count -eq 1){$roundtrip[0].ProviderStates.'com.example.cleanup.future'}else{$null}
    Assert-Case ($roundtrip.Count -eq 1 -and $roundFuture.Data.marker -eq 'preserve-unknown' -and $roundFuture.Data.nested.fact -eq 42 -and $roundtrip[0].BackupScope.Parameters.selectedRegions -eq '0,0' -and $roundtrip[0].Cloud.ArgumentsTemplate -notmatch 'legacy-custom-cleanup-fixture') 'export-roundtrip'
    Shot 'legacy-exported'
}

function Select-ComboIndex([string]$Id,[int]$Index) {
    Ui @('scroll-into-view',$Id) | Out-Null
    Ui @('focus',$Id) | Out-Null
    Ui @('invoke',$Id) | Out-Null
    Ui @('send-keys','home','--via','send-input') | Out-Null
    for ($i=0; $i -lt $Index; $i++) { Ui @('send-keys','down','--via','send-input') | Out-Null }
    Ui @('send-keys','enter','--via','send-input') | Out-Null
}
function Open-Appearance {
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','SettingsItem') | Out-Null
    Expand-Settings 'SettingsPage_PersonalizationExpander'
    Ui @('wait-for','SettingsTheme','-t','10000') | Out-Null
}
if (-not ('FolderRewindUiContrast' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FolderRewindUiContrast {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct State {
    public int cbSize; public uint flags; public IntPtr scheme;
  }
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool SystemParametersInfo(uint action, uint size, ref State state, uint flags);
}
'@
}
function Get-Contrast {
    $state=[FolderRewindUiContrast+State]::new()
    $state.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($state)
    if (![FolderRewindUiContrast]::SystemParametersInfo(0x42,[uint32]$state.cbSize,[ref]$state,0)) { throw 'Cannot read Windows high contrast state.' }
    @{flags=$state.flags;scheme=if($state.scheme -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::PtrToStringUni($state.scheme)}else{$null}}
}
function Set-Contrast([uint32]$Flags,[string]$Scheme) {
    $state=[FolderRewindUiContrast+State]::new()
    $state.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($state)
    $state.flags=$Flags
    $memory=if($Scheme){[Runtime.InteropServices.Marshal]::StringToHGlobalUni($Scheme)}else{[IntPtr]::Zero}
    try {
        $state.scheme=$memory
        if (![FolderRewindUiContrast]::SystemParametersInfo(0x43,[uint32]$state.cbSize,[ref]$state,3)) { throw 'Cannot set Windows high contrast state.' }
    } finally { if($memory -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::FreeHGlobal($memory)} }
}
function Appearance-Case([string]$Language,[int]$LanguageIndex) {
    $prefix=if($Language -eq 'zh-CN'){'zh'}else{'en'}
    $original=Get-Contrast
    try {
    if (($original.flags -band 1) -ne 0) { Set-Contrast ($original.flags -band (-bnot 1)) $original.scheme }
    Open-Appearance
    Select-ComboIndex 'SettingsLanguage' $LanguageIndex
    Ui @('invoke','NavHome') | Out-Null
    Wait-Until { (Current-Config).GlobalSettings.Language -eq $Language } 30
    Restart-Normal
    Open-Appearance
    $resPath=Join-Path $root "../../../src/FolderRewind/Strings/$Language/Resources.resw"
    [xml]$resources=Get-Content -Raw -LiteralPath $resPath
    $expectedHistory=($resources.root.data | Where-Object name -eq 'ShellPage_NavHistory.Content').value
    $actualHistory=(Ui @('get-property','NavHistory','-p','Name','--json') | ConvertFrom-Json).properties.Name
    if (!$expectedHistory -or $actualHistory -ne $expectedHistory) { throw "UI language did not switch to $Language." }
    foreach ($entry in @(@{index=1;name='light'},@{index=0;name='dark'})) {
        Select-ComboIndex 'SettingsTheme' $entry.index
        Ui @('invoke','NavHome') | Out-Null
        Wait-Until { (Current-Config).GlobalSettings.ThemeIndex -eq $entry.index } 30
        Open-Appearance
        Assert-Case $true "$prefix-$($entry.name)"
        Shot "$prefix-$($entry.name)-settings"
        Ui @('invoke','NavHistory') | Out-Null
        Ui @('wait-for','HistoryVersionList','-t','10000') | Out-Null
        Shot "$prefix-$($entry.name)-history"
        Open-Appearance
    }
        Select-ComboIndex 'SettingsTheme' 2
        Set-Contrast ($original.flags -bor 1) $original.scheme
        Wait-Until { ((Get-Contrast).flags -band 1) -ne 0 } 15
        Assert-Case $true "$prefix-high-contrast"
        Shot "$prefix-high-contrast-settings"
        Ui @('invoke','NavHistory') | Out-Null
        Ui @('wait-for','HistoryVersionList','-t','10000') | Out-Null
        Shot "$prefix-high-contrast-history"
    } finally { Set-Contrast $original.flags $original.scheme }
}
Case 'zh-light-dark-high-contrast' { Appearance-Case 'zh-CN' 2 }
Case 'en-light-dark-high-contrast' { Appearance-Case 'en-US' 1 }

# Missing cases are NOT_RUN; they cannot contribute to a passing report.
