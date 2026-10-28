param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [ValidateSet('Cloud','Tool','Migration','Settings','CloudTransfer','Recovery','RecoveryCancel')][string]$Phase='Cloud',
    [ValidateSet('zh-CN','en-US')][string]$Language='zh-CN'
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='folderrewind-round2'
$evidence=Join-Path $FixtureRoot 'evidence'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments) {
    $result=@(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' '): $($result -join ' ')" }
    $result
}
function Ui([string[]]$Arguments) { Checked (@('ui')+$Arguments+@('-w',"$script:hwnd")) }
function Json([string[]]$Arguments) { (Ui ($Arguments+@('--json'))) -join "`n" | ConvertFrom-Json }
function Elements($nodes) { foreach($node in $nodes) { if($null -ne $node) { $node; Elements $node.children } } }
function Tree {
    $tree=Json @('inspect','-d','12')
    $seen=[Collections.Generic.HashSet[string]]::new()
    @(Elements $tree.windows.elements | Where-Object { !$_.selector -or $seen.Add($_.selector) })
}
function Invoke-Name([string]$Name) { Ui @('invoke',$Name) | Out-Null }
function Save-Evidence([string]$Name) {
    $path=Join-Path $evidence "$Language-$Phase-$Name-$(Get-Date -Format 'HHmmss').png"
    Ui @('screenshot','--output',$path) | Out-Null
    if(!(Test-Path -LiteralPath $path)) { throw 'Screenshot missing.' }
    $script:lastScreenshot=$path
}
function Test-UI([string]$Name,[scriptblock]$Body) {
    $script:lastScreenshot=$null
    try { & $Body; $results.Add(@{name=$Name;status='PASS';screenshot=$script:lastScreenshot}) }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Expand-Settings([string]$Id) {
    Ui @('scroll','SettingsScrollViewer','--to','top') | Out-Null
    for($attempt=0;$attempt -lt 40;$attempt++) {
        $part=Json @('inspect',$Id,'-d','8')
        $button=@(Elements $part.windows.elements | Where-Object {$_.type -eq 'Button' -and $_.expandState -and $_.className -eq 'Microsoft.UI.Xaml.Controls.Expander'} | Select-Object -First 1)
        if($button.Count -eq 1 -and !$button[0].isOffscreen) {
            if($button[0].expandState -eq 'collapsed') { Invoke-Name $button[0].selector }; return
        }
        Ui @('scroll','SettingsScrollViewer','--direction','down') | Out-Null
    }
    throw "Cannot reach $Id."
}
function Choose-Package([string]$Path) {
    $picker=@()
    for($attempt=0;$attempt -lt 30;$attempt++) {
        $windows=(Checked @('ui','list-windows','--json')) -join "`n" | ConvertFrom-Json
        $picker=@($windows | Where-Object {$_.processName -eq 'PickerHost' -and $_.ownerHwnd -eq $script:hwnd})
        if($picker.Count -eq 1) {break}; Start-Sleep -Milliseconds 100
    }
    if($picker.Count -ne 1) {throw 'Expected one owned file picker.'}
    $tree=(Checked @('ui','inspect','-w',"$($picker[0].hwnd)",'-d','8','--json')) -join "`n" | ConvertFrom-Json
    $edit=@(Elements $tree.windows.elements | Where-Object {$_.type -eq 'Edit' -and $_.automationId -in @('1001','1148','1152')})
    if($edit.Count -ne 1) {throw 'Picker name field missing.'}
    $confirm=@(Elements $tree.windows.elements | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'})
    if($confirm.Count -ne 1) {throw 'Picker open button missing.'}
    Checked @('ui','set-value',$edit[0].selector,$Path,'-w',"$($picker[0].hwnd)") | Out-Null
    Checked @('ui','invoke',$confirm[0].selector,'-w',"$($picker[0].hwnd)") | Out-Null
}
function Resource([string]$Key) {
    [xml]$res=Get-Content -LiteralPath (Join-Path $PSScriptRoot "../../FolderRewind/Strings/$Language/Resources.resw") -Raw
    ($res.root.data | Where-Object {$_.name -eq $Key}).value
}
try {
    Checked @('ui','wait-for','NavView','-a',"$AppPid",'-t','15000') | Out-Null
    $windows=(Checked @('ui','list-windows','-a',"$AppPid",'--json')) -join "`n" | ConvertFrom-Json
    $main=@($windows | Where-Object {$_.className -eq 'WinUIDesktopWin32WindowClass'})
    if($main.Count -ne 1) { throw 'Expected exactly one fixture window.' }
    $script:hwnd=$main[0].hwnd
    # The x64 output must be the artifact built for this acceptance run.
    $process=Get-Process -Id $AppPid
    if($process.Path -notmatch '\\bin\\x64\\Debug\\') { throw "Unexpected app artifact: $($process.Path)" }
    switch($Phase) {
        'RecoveryCancel' {
            Test-UI 'Cloud discovery can be canceled without leaving loading rows' {
                Ui @('wait-for','CloudRecoveryCancelOperation','--gone','-t','10000') | Out-Null
                Set-Content -LiteralPath (Join-Path $FixtureRoot 'webdav-display-delay') -Value 'delay' -Encoding utf8
                try {
                    Invoke-Name 'CloudDiscoverProjects'
                    Ui @('wait-for','CloudRecoveryCancelOperation','-t','3000') | Out-Null
                    Start-Sleep -Milliseconds 1200
                    Invoke-Name 'CloudRecoveryCancelOperation'
                    Ui @('wait-for','CloudRecoveryCancelOperation','--gone','-t','5000') | Out-Null
                    $combo=@(Tree | Where-Object {$_.type -eq 'ComboBox'}); Invoke-Name $combo[0].selector
                    $reading=@(Tree | Where-Object {$_.type -eq 'ListItem' -and $_.name -match 'Reading project information|正在读取项目信息'})
                    if($reading.Count) {throw 'Canceled discovery still has loading rows.'}
                    Save-Evidence 'canceled-discovery'
                    $dismiss=@(Tree | Where-Object {$_.automationId -eq 'Light Dismiss'})
                    if($dismiss.Count) {Invoke-Name $dismiss[0].selector}
                } finally { Remove-Item -LiteralPath (Join-Path $FixtureRoot 'webdav-display-delay') -ErrorAction SilentlyContinue }
                Invoke-Name 'CloudDiscoverProjects'
                Ui @('wait-for','CloudRecoveryCancelOperation','-t','3000') | Out-Null
                Ui @('wait-for','CloudRecoveryCancelOperation','--gone','-t','10000') | Out-Null
            }
        }
        'Recovery' {
            Test-UI 'Independent recovery lists the project name and identity before analysis' {
                Invoke-Name 'NavHome'
                Ui @('invoke','HomeNewConfig','--action','expand') | Out-Null
                Invoke-Name 'HomeCloudRecoveryMenu'
                Invoke-Name (Resource 'CloudWizard_ChooseRclone.Content')
                $config=Get-Content -LiteralPath (Join-Path $FixtureRoot 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json
                $profile=$config.CloudConnections[0]
                Ui @('set-value','CloudSetupConfigPath',$profile.CredentialReference) | Out-Null
                Ui @('focus','CloudSetupConfigPath') | Out-Null
                Ui @('send-keys','tab','--via','send-input') | Out-Null
                Invoke-Name (Resource 'CloudSetup_ListRemotes.Content')
                Ui @('wait-for','NextButton','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                $combo=@(Tree | Where-Object {$_.type -eq 'ComboBox'})
                Invoke-Name $combo[0].selector
                $remote=@(Tree | Where-Object {$_.type -eq 'ListItem' -and $_.name -like "$($profile.RemoteName)*"})
                if($remote.Count -ne 1) {throw 'Expected the fixture remote.'}; Invoke-Name $remote[0].selector
                Invoke-Name 'NextButton'
                Ui @('wait-for','CloudSetupRemoteRoot','-t','5000') | Out-Null
                Invoke-Name 'NextButton'
                Ui @('wait-for','NextButton','--value',(Resource 'CloudWizard_Confirm'),'-t','10000') | Out-Null
                Invoke-Name 'NextButton'
                Ui @('wait-for','CloudDiscoverProjects','-t','5000') | Out-Null
                Invoke-Name 'CloudDiscoverProjects'
                Ui @('wait-for','CloudDiscoverProjects','-p','IsEnabled','--value','true','-t','10000') | Out-Null
                $combo=@(Tree | Where-Object {$_.type -eq 'ComboBox'})
                Invoke-Name $combo[0].selector
                $project=@(Tree | Where-Object {$_.type -eq 'ListItem' -and $_.name -like "$($config.BackupConfigs[0].Name)*"})
                if($project.Count -ne 1 -or $project[0].name -notmatch 'ID ') {throw 'Named cloud project or short ID missing.'}
                Save-Evidence 'named-project'
                Invoke-Name $project[0].selector
                Invoke-Name 'CloudAnalyzeProject'
            }
        }
        'CloudTransfer' {
            Test-UI 'New cloud failure supersedes success and retry finishes the same task' {
                Invoke-Name 'CloudLoadVersions'
                function Select-Version([int]$Index) {
                    $combo=@(Tree | Where-Object {$_.type -eq 'ComboBox'})
                    if($combo.Count -ne 1) { throw 'Expected one version selector.' }
                    Invoke-Name $combo[0].selector
                    $options=@(Tree | Where-Object {$_.type -eq 'ListItem' -and $_.name -match '^source ·'})
                    if($options.Count -le $Index) { throw "Missing version $Index in the selector." }
                    Invoke-Name $options[$Index].selector
                }
                Select-Version 0
                Invoke-Name 'CloudUploadVersion'; Invoke-Name 'PrimaryButton'
                $succeeded=if($Language -eq 'zh-CN') {'云备份完整完成'}else{'Cloud backup complete'}
                Ui @('wait-for','CloudLatestJobSummary','--contains','--value',$succeeded,'-t','45000') | Out-Null
                Set-Content -LiteralPath (Join-Path $FixtureRoot 'webdav-offline') -Value 'offline' -Encoding utf8
                try {
                    Select-Version 1
                    Invoke-Name 'CloudUploadVersion'; Invoke-Name 'PrimaryButton'
                    $failed=if($Language -eq 'zh-CN') {'需要处理'}else{'Needs attention'}
                    Ui @('wait-for','CloudLatestJobSummary','--contains','--value',$failed,'-t','45000') | Out-Null
                    Ui @('wait-for','Title','--root','GlobalInfoBar','--contains','--value',$failed,'-t','10000') | Out-Null
                    $notifications=Json @('inspect','GlobalInfoBar','-d','5')
                    $message=(Elements $notifications.windows.elements | ForEach-Object {$_.name}) -join "`n"
                    if($message -notmatch '需要处理|Needs attention') { throw "The cloud failure notification is missing: $message" }
                    Ui @('scroll','CloudProjectPageScroll','--to','top') | Out-Null
                    Save-Evidence 'offline-task'
                    Invoke-Name 'ShellNotificationAction'
                    Ui @('wait-for','CloudProjectJobs','-t','3000') | Out-Null
                } finally { Remove-Item -LiteralPath (Join-Path $FixtureRoot 'webdav-offline') -ErrorAction SilentlyContinue }
                Invoke-Name 'CloudRetryJobs'
                Ui @('wait-for','CloudLatestJobSummary','--contains','--value',$succeeded,'-t','45000') | Out-Null
                Ui @('scroll','CloudProjectPageScroll','--to','top') | Out-Null
                Save-Evidence 'retry-complete'
            }
        }
        'Cloud' {
            Test-UI 'Independent cloud facts survive navigation and fit the minimum window' {
                Invoke-Name 'NavHome'; Invoke-Name 'NavManager'; Invoke-Name 'FolderManagerConfigSettings'; Invoke-Name 'ConfigSettingsTabCloud'; Invoke-Name 'ProjectSettingsCloudSetup'
                Ui @('wait-for','CloudCopySummary','-t','8000') | Out-Null
                foreach($id in @('CloudConnectionCheckSummary','CloudCopySummary','CloudLatestJobSummary','CloudPendingJobSummary')) {
                    $value=Json @('get-value',$id)
                    if([string]::IsNullOrWhiteSpace($value.text)) { throw "Empty cloud fact: $id" }
                }
                $check=(Json @('get-value','CloudConnectionCheckSummary')).text
                if($check -match '尚未检查|not been checked') { throw 'A completed connection verification was forgotten.' }
                Save-Evidence 'facts'
                $nodes=Tree
                $visible=@($nodes | Where-Object {$_.automationId -match '^Cloud(ConnectionCheck|Copy|LatestJob|PendingJob)Summary$'})
                if($visible.Count -ne 4) { throw 'Cloud facts are absent from the accessibility tree.' }
            }
        }
        'Tool' {
            Test-UI 'Missing rclone identifies the tool and offers a repair entry' {
                $nodes=Tree
                $edit=@($nodes | Where-Object {$_.automationId -eq 'CloudSetupExecutable'})
                if($edit.Count -ne 1) { throw 'Open the connection environment step before this phase.' }
                Ui @('set-value','CloudSetupExecutable',(Join-Path $FixtureRoot 'missing-rclone.exe')) | Out-Null
                Ui @('focus','CloudSetupExecutable') | Out-Null
                Ui @('send-keys','tab','--via','send-input') | Out-Null
                $next=@(Tree | Where-Object {$_.automationId -eq 'NextButton'})
                if($next.Count -ne 1) { throw 'Next button missing.' }
                Invoke-Name $next[0].selector
                Ui @('wait-for','CloudRepairAction','-t','3000') | Out-Null
                $status=Json @('inspect','CloudSetupStatus','-d','5')
                $message=(Elements $status.windows.elements | Where-Object {$_.automationId -eq 'Message'} | ForEach-Object {$_.name}) -join "`n"
                if($message -notmatch 'rclone') { throw "Unexpected missing-tool feedback: $message" }
                Save-Evidence 'missing-tool'
            }
        }
        'Settings' {
            Test-UI 'Consecutive project saves preserve edits and finish without conflict' {
                Invoke-Name 'NavHome'; Invoke-Name 'NavManager'; Invoke-Name 'FolderManagerConfigSettings'
                $before=(Json @('get-value','ConfigSettingsName')).text
                Ui @('set-value','ConfigSettingsName',($before+' UI')) | Out-Null
                Invoke-Name 'ProjectSettingsSave'
                Ui @('wait-for','ProjectSettingsSave','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                Ui @('set-value','ConfigSettingsName',$before) | Out-Null
                Invoke-Name 'ProjectSettingsSave'
                Ui @('wait-for','ProjectSettingsSave','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                $nodes=Tree
                if(@($nodes | Where-Object {$_.automationId -eq 'ProjectSettingsConflict' -and !$_.isOffscreen}).Count) { throw 'Unexpected settings conflict.' }
                Save-Evidence 'saved-twice'
            }
        }
        'Migration' {
            Test-UI 'Invalid package has an actionable localized summary' {
                if(@(Tree | Where-Object {$_.automationId -eq 'MigrationWizardLeave'}).Count) { Invoke-Name 'MigrationWizardLeave' }
                else { Invoke-Name 'NavHome'; Invoke-Name 'SettingsItem' }
                Ui @('wait-for','SettingsScrollViewer','-t','5000') | Out-Null
                Expand-Settings 'SettingsPage_DataMigrationExpander'
                Invoke-Name 'MigrationImportData'
                Ui @('wait-for','MigrationWizardChoosePackage','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                Invoke-Name 'MigrationWizardChoosePackage'
                Choose-Package (Join-Path $FixtureRoot 'invalid.frhistory')
                Ui @('wait-for','MigrationWizardActionDetails','-t','5000') | Out-Null
                $nodes=Tree
                $text=($nodes | Where-Object {$_.type -eq 'Text'} | ForEach-Object {$_.name}) -join "`n"
                if($text -notmatch '迁移包损坏|damaged or incomplete') { throw 'Expected invalid-package summary is not visible.' }
                $primary=Json @('get-property','MigrationWizardPrimary','-p','IsEnabled')
                if($primary.properties.IsEnabled -ne $false) { throw 'Invalid package can still be submitted.' }
                Save-Evidence 'invalid-package'
            }
            Test-UI 'Reselecting a valid migration package clears the failure' {
                Ui @('wait-for','MigrationWizardChoosePackage','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                Invoke-Name 'MigrationWizardChoosePackage'; Choose-Package (Join-Path $FixtureRoot 'valid.json')
                Ui @('wait-for','MigrationWizardPrimary','-p','IsEnabled','--value','true','-t','5000') | Out-Null
                Save-Evidence 'valid-package'
            }
        }
    }
} finally {
    try { Checked @('ui','yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID=$previous }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence "$Language-$Phase-results.json") -Encoding utf8
}
