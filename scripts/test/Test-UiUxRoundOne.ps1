param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [ValidateSet('Environment', 'Manager', 'Names', 'Scenario', 'FirstBackup', 'Export', 'Template', 'History')][string]$Phase = 'Environment',
    [string]$ExportProjectName = 'UI warning project',
    [string]$TemplateName = 'Round one manual template',
    [string]$BranchName = 'UI round one branch'
)
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$folderRewindScriptRoot = Get-FolderRewindRoot -Start $PSScriptRoot
if ($FixtureRoot) { $FixtureRoot = Resolve-FolderRewindPath -Path $FixtureRoot -Root $folderRewindScriptRoot }
Push-Location -LiteralPath $folderRewindScriptRoot
try {

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'folderrewind-round1'
$results = [Collections.Generic.List[object]]::new()
$evidence = Join-Path $FixtureRoot 'evidence'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Invoke-Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output
}
function Invoke-Ui([string[]]$Arguments) { Invoke-Checked (@('ui') + $Arguments + @('-w', "$script:hwnd")) }
function Get-UiJson([string[]]$Arguments) { (Invoke-Ui ($Arguments + @('--json'))) -join "`n" | ConvertFrom-Json }
function Get-Elements($nodes) { foreach ($node in $nodes) { if ($null -ne $node) { $node; Get-Elements $node.children } } }
function Expand-Settings([string]$Id) {
    Invoke-Ui @('wait-for', $Id, '-t', '5000') | Out-Null
    Invoke-Ui @('scroll', 'SettingsScrollViewer', '--to', 'top') | Out-Null
    $button = @()
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $tree = Get-UiJson @('inspect', $Id, '-i', '-d', '8')
        $button = @(Get-Elements $tree.windows.elements | Where-Object { $_.type -eq 'Button' -and $_.expandState -and $_.className -eq 'Microsoft.UI.Xaml.Controls.Expander' } | Select-Object -First 1)
        if ($button.Count -eq 1 -and -not $button[0].isOffscreen) { break }
        Invoke-Ui @('scroll', 'SettingsScrollViewer', '--direction', 'down') | Out-Null
    }
    if ($button.Count -ne 1 -or $button[0].isOffscreen) { throw "Cannot reach the expand button for $Id." }
    if ($button[0].expandState -eq 'collapsed') { Invoke-Ui @('invoke', $button[0].selector) | Out-Null }
    Invoke-Ui @('scroll', 'SettingsScrollViewer', '--direction', 'down') | Out-Null
}
function Assert-Name([string]$Id) {
    $value = Get-UiJson @('get-property', $Id, '-p', 'Name')
    if ([string]::IsNullOrWhiteSpace($value.properties.Name) -or $value.properties.Name -match 'FolderRewind\.|^InputBox$') { throw "Missing accessible name: $Id." }
}
function Save-Evidence([string]$Name) {
    $path = Join-Path $evidence "$stamp-$Name.png"
    Invoke-Ui @('screenshot', '--output', $path) | Out-Null
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'Screenshot was not delivered.' }
    $script:screenshot = $path
}
function Test-UI([string]$Name, [scriptblock]$Body) {
    $script:screenshot = $null
    try { & $Body; $results.Add(@{ name = $Name; status = 'PASS'; screenshot = $script:screenshot }) }
    catch { $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_"; screenshot = $script:screenshot }); throw }
}
function Complete-Picker([string]$Path, [switch]$Folder) {
    $picker = @()
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $windows = (Invoke-Checked @('ui', 'list-windows', '--json')) -join "`n" | ConvertFrom-Json
        $picker = @($windows | Where-Object { $_.processName -eq 'PickerHost' -and $_.ownerHwnd -eq $script:hwnd })
        if ($picker.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($picker.Count -ne 1) { throw 'Expected one owned file picker.' }
    $tree = (Invoke-Checked @('ui', 'inspect', '-i', '--json', '-d', '7', '-w', "$($picker[0].hwnd)")) -join "`n" | ConvertFrom-Json
    $edit = @(Get-Elements $tree.windows.elements | Where-Object { $_.type -eq 'Edit' -and $_.automationId -in @('1001', '1148', '1152') })
    if ($edit.Count -ne 1) { throw 'Expected one picker file or folder name editor.' }
    Invoke-Checked @('ui', 'set-value', $edit[0].selector, $Path, '-w', "$($picker[0].hwnd)") | Out-Null
    $confirm = @(Get-Elements $tree.windows.elements | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq '1' })
    if ($confirm.Count -ne 1) { throw 'Expected one picker confirmation button.' }
    Invoke-Checked @('ui', 'invoke', $confirm[0].selector, '-w', "$($picker[0].hwnd)") | Out-Null
}

try {
    $windows = (Invoke-Checked @('ui', 'list-windows', '-a', "$AppPid", '--json')) -join "`n" | ConvertFrom-Json
    $main = @($windows | Where-Object { $_.className -eq 'WinUIDesktopWin32WindowClass' })
    if ($main.Count -ne 1) { throw 'Expected one main application window.' }
    $script:hwnd = $main[0].hwnd
    switch ($Phase) {
        'Environment' {
            Test-UI 'Environment report includes every project and the tool path' {
                Invoke-Ui @('invoke', 'SettingsItem') | Out-Null
                $heading = Get-UiJson @('get-property', 'SettingsCoreValidationHeader', '-p', 'Name')
                $expander = Get-UiJson @('get-property', 'SettingsPage_CoreValidationExpander', '-p', 'Name')
                if ($heading.properties.Name -ne $expander.properties.Name) { throw 'The environment check has an outdated accessible name.' }
                Expand-Settings 'SettingsPage_CoreValidationExpander'
                Invoke-Ui @('wait-for', 'SettingsCoreValidationRun', '-t', '5000') | Out-Null
                Invoke-Ui @('invoke', 'SettingsCoreValidationRun') | Out-Null
                Invoke-Ui @('wait-for', 'BackupEnvironmentReportText', '-t', '15000') | Out-Null
                $report = Get-UiJson @('get-value', 'BackupEnvironmentReportText')
                $config = Get-Content -LiteralPath (Join-Path $FixtureRoot 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json
                foreach ($project in $config.BackupConfigs) { if (-not $report.text.Contains($project.Name)) { throw "Project missing from report: $($project.Name)" } }
                if (-not $report.text.Contains('7-Zip') -or $report.text.Length -lt 100) { throw 'Incomplete environment report.' }
                $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "$stamp-report.json") -Encoding utf8
                if ($config.BackupConfigs.Count -gt 2) { Invoke-Ui @('scroll', 'BackupEnvironmentReportScroll', '--to', 'bottom') | Out-Null }
                Save-Evidence 'environment-report'
                Invoke-Ui @('focus', 'PrimaryButton') | Out-Null
                Invoke-Ui @('send-keys', 'tab', '--via', 'send-input') | Out-Null
                $focus = Get-UiJson @('get-focused')
                if (-not $focus.hasFocus -or $focus.element.automationId -ne 'CloseButton') { throw 'Dialog focus did not move to Close.' }
                Invoke-Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
                Invoke-Ui @('wait-for', 'BackupEnvironmentReportText', '--gone', '-t', '3000') | Out-Null
            }
        }
        'Manager' {
            Test-UI 'Applied minimum window keeps backup actions and comments visible' {
                Invoke-Ui @('invoke', 'SettingsItem') | Out-Null
                Expand-Settings 'SettingsPage_PersonalizationExpander'
                Invoke-Ui @('wait-for', 'SettingsStartupWidth', '-t', '5000') | Out-Null
                Invoke-Ui @('set-value', 'SettingsStartupWidth', '1200') | Out-Null
                Invoke-Ui @('set-value', 'SettingsStartupHeight', '600') | Out-Null
                Invoke-Ui @('invoke', 'SettingsStartupSizeApply') | Out-Null
                Invoke-Ui @('invoke', 'NavManager') | Out-Null
                Invoke-Ui @('wait-for', 'FolderManagerBackupConfig', '-t', '3000') | Out-Null
                foreach ($id in @('FolderManagerConfigSelector', 'FolderManagerAddFolder', 'FolderManagerBackupConfig', 'FolderManagerBackupComment')) {
                    $properties = Get-UiJson @('get-property', $id, '-p', 'IsOffscreen')
                    if ($properties.properties.IsOffscreen -ne $false) { throw "Offscreen command: $id" }
                }
                Save-Evidence 'manager-minimum'
            }
        }
        'Names' {
            Test-UI 'Destination and template buttons have accessible names' {
                Invoke-Ui @('invoke', 'NavManager') | Out-Null
                Invoke-Ui @('invoke', 'FolderManagerConfigSettings') | Out-Null
                foreach ($id in @('ConfigSettingsDialog_BrowseButton','ConfigSettingsDialog_OpenDestination','ConfigSettingsSaveTemplate')) { Assert-Name $id }
                Invoke-Ui @('invoke', 'ProjectSettingsCancel') | Out-Null
            }
            Test-UI 'Schedule time editors and delete actions include schedule context' {
                Invoke-Ui @('invoke', 'FolderManagerConfigSettings') | Out-Null
                Invoke-Ui @('invoke', 'ConfigSettingsTabAutomation') | Out-Null
                Invoke-Ui @('invoke', 'ConfigSettingsDialog_AutoBackup') | Out-Null
                Invoke-Ui @('invoke', 'ConfigSettingsDialog_ScheduledMode') | Out-Null
                Invoke-Ui @('invoke', 'AddScheduleButton') | Out-Null
                foreach ($id in @('ScheduleHourBox', 'ScheduleMinuteBox', 'DeleteScheduleEntryButton')) {
                    Assert-Name $id
                    if ($id -ne 'DeleteScheduleEntryButton') {
                        $editor = Get-UiJson @('get-property', 'InputBox', '--root', $id, '-p', 'Name')
                        if ([string]::IsNullOrWhiteSpace($editor.properties.Name) -or $editor.properties.Name -eq 'InputBox') { throw 'Unnamed inner time editor.' }
                    }
                }
                Invoke-Ui @('invoke', 'ProjectSettingsCancel') | Out-Null
                Invoke-Ui @('wait-for', 'SecondaryButton', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'SecondaryButton') | Out-Null
            }
        }
        'Scenario' {
            Test-UI 'All scenario cards are named and keyboard accessible' {
                Invoke-Ui @('invoke', 'NavHome') | Out-Null
                Invoke-Ui @('wait-for', 'HomeNewConfig', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'HomeNewConfig') | Out-Null
                Invoke-Ui @('wait-for', 'ScenarioFolder', '-t', '3000') | Out-Null
                foreach ($id in @('ScenarioFolder', 'ScenarioMinecraft', 'ScenarioOtherGame')) { Assert-Name $id }
                Invoke-Ui @('focus', 'ScenarioFolder') | Out-Null
                Invoke-Ui @('send-keys', 'tab', '--via', 'send-input') | Out-Null
                $focus = Get-UiJson @('get-focused')
                if (-not $focus.hasFocus -or $focus.element.automationId -ne 'ScenarioMinecraft') { throw 'Tab did not move to the next scenario card.' }
                $focus | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence "$stamp-scenario-focus.json") -Encoding utf8
                Save-Evidence 'scenario-names'
            }
        }
        'FirstBackup' {
            Test-UI 'First backup keeps its small-file warning and full diagnostic details' {
                $source = Join-Path $FixtureRoot "setup-source-$stamp"
                New-Item -ItemType Directory -Path $source | Out-Null
                [IO.File]::WriteAllText((Join-Path $source 'hello.txt'), 'first backup warning fixture')
                Invoke-Ui @('invoke', 'NavHome') | Out-Null
                Invoke-Ui @('wait-for', 'HomeNewConfig', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'HomeNewConfig') | Out-Null
                Invoke-Ui @('wait-for', 'ScenarioFolder', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'ScenarioFolder') | Out-Null
                Invoke-Ui @('invoke', 'SetupAddFolder') | Out-Null
                Complete-Picker $source -Folder
                Invoke-Ui @('wait-for', 'SetupNextContent', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'SetupNextContent') | Out-Null
                Invoke-Ui @('wait-for', 'SetupName', '-t', '3000') | Out-Null
                Invoke-Ui @('set-value', 'SetupName', "UI warning $stamp") | Out-Null
                Invoke-Ui @('set-value', 'SetupDestination', (Join-Path $FixtureRoot "backups/setup-$stamp")) | Out-Null
                Invoke-Ui @('invoke', 'SetupNextLocation') | Out-Null
                Invoke-Ui @('wait-for', 'SetupCreateBackup', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'SetupCreateBackup') | Out-Null
                Invoke-Ui @('wait-for', 'SetupResultDetails', '-t', '20000') | Out-Null
                Save-Evidence 'first-backup-warning'
                Invoke-Ui @('invoke', 'SetupResultDetails') | Out-Null
                Invoke-Ui @('wait-for', 'SetupResultDetailsText', '-t', '3000') | Out-Null
                $details = Get-UiJson @('get-value', 'SetupResultDetailsText')
                if (-not $details.text.Contains('backup.file_too_small')) { throw 'The complete warning diagnostic is missing.' }
                $details | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "$stamp-backup-details.json") -Encoding utf8
                Invoke-Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
            }
        }
        'Template' {
            Test-UI 'A saved settings-only template opens directly at manual source selection' {
                $config = Get-Content -LiteralPath (Join-Path $FixtureRoot 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json
                $template = @($config.Templates | Where-Object Name -eq $TemplateName)
                if ($template.Count -ne 1 -or $template[0].PathRules.Count -ne 0 -or $template[0].DiscoverySources.Count -ne 0) { throw 'Expected one saved settings-only template fixture.' }
                Invoke-Ui @('invoke', 'NavHome') | Out-Null
                Invoke-Ui @('wait-for', 'HomeNewConfig', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'HomeNewConfig', '--action', 'expand') | Out-Null
                Invoke-Ui @('wait-for', 'HomeAddFromTemplate', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'HomeAddFromTemplate') | Out-Null
                Invoke-Ui @('wait-for', 'HomeTemplatePicker', '-t', '3000') | Out-Null
                $selected = Get-UiJson @('get-value', 'HomeTemplatePicker')
                if ($selected.text -ne $TemplateName) { throw 'Select the expected fixture template before running this phase.' }
                Invoke-Ui @('set-value', 'HomeTemplateConfigName', "UI template $stamp") | Out-Null
                Invoke-Ui @('invoke', 'PrimaryButton') | Out-Null
                Invoke-Ui @('wait-for', 'SetupAddFolder', '-t', '5000') | Out-Null
                Save-Evidence 'template-manual-sources'
                Invoke-Ui @('invoke', 'SetupCancel') | Out-Null
            }
        }
        'Export' {
            Test-UI 'Export results and the written manifest contain only the selected project' {
                Invoke-Ui @('invoke', 'SettingsItem') | Out-Null
                Expand-Settings 'SettingsPage_DataMigrationExpander'
                Invoke-Ui @('scroll', 'SettingsScrollViewer', '--to', 'bottom') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationExportData', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'MigrationExportData') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardPrimary', '-t', '3000') | Out-Null
                $config = Get-Content -LiteralPath (Join-Path $FixtureRoot 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json
                if (@($config.BackupConfigs | Where-Object Name -eq $ExportProjectName).Count -ne 1) { throw 'Export fixture project was not found.' }
                foreach ($project in $config.BackupConfigs | Where-Object Name -ne $ExportProjectName) {
                    Invoke-Ui @('invoke', ('MigrationExportProject_' + $project.Id.Replace('-', ''))) | Out-Null
                }
                Invoke-Ui @('invoke', 'MigrationWizardPrimary') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardArchiveMode', '-t', '3000') | Out-Null
                $tree = Get-UiJson @('inspect', 'MigrationWizardArchiveMode', '-i', '-d', '6')
                $choices = @(Get-Elements $tree.windows.elements | Where-Object type -eq 'RadioButton')
                if ($choices.Count -ne 2) { throw 'Expected both archive inclusion options.' }
                Invoke-Ui @('invoke', $choices[1].selector) | Out-Null
                Invoke-Ui @('invoke', 'MigrationWizardPrimary') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardLocation', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'MigrationWizardPrimary') | Out-Null
                $path = Join-Path $FixtureRoot "selected-$stamp.frhistory"
                Complete-Picker $path
                Invoke-Ui @('wait-for', 'MigrationWizardResults', '-t', '15000') | Out-Null
                $tree = Get-UiJson @('inspect', 'MigrationWizardResults', '-d', '8')
                $rows = @(Get-Elements $tree.windows.elements | Where-Object type -eq 'ListItem')
                if ($rows.Count -ne 1 -or $rows[0].name -ne $ExportProjectName) { throw 'The result includes the wrong projects.' }
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $zip = [IO.Compression.ZipFile]::OpenRead($path)
                try {
                    $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
                    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                } finally { $zip.Dispose() }
                if ($manifest.projects.Count -ne 1 -or $manifest.projects[0].name -ne $ExportProjectName -or -not $manifest.includesPayloads) { throw 'The written package differs from the result.' }
                Save-Evidence 'selected-export'
                Invoke-Ui @('invoke', 'MigrationWizardPrimary') | Out-Null
                Invoke-Ui @('wait-for', 'SettingsPage_DataMigrationExpander', '-t', '3000') | Out-Null
                Expand-Settings 'SettingsPage_DataMigrationExpander'
                Invoke-Ui @('scroll', 'SettingsScrollViewer', '--to', 'bottom') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationImportData', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'MigrationImportData') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardChoosePackage', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'MigrationWizardChoosePackage') | Out-Null
                Complete-Picker $path
                Invoke-Ui @('wait-for', 'MigrationWizardPackagePath', '-t', '3000') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardPrimary', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                Invoke-Ui @('invoke', 'MigrationWizardPrimary') | Out-Null
                Invoke-Ui @('wait-for', 'MigrationWizardImportProjects', '-t', '5000') | Out-Null
                $preview = Get-UiJson @('inspect', 'MigrationWizardImportProjects', '-i', '-d', '8')
                $selections = @(Get-Elements $preview.windows.elements | Where-Object { $_.type -eq 'CheckBox' -and $_.automationId -like 'MigrationImportProject_*' })
                if ($selections.Count -ne 1 -or $selections[0].name -ne $ExportProjectName) { throw 'Import preview differs from the export receipt.' }
                Invoke-Ui @('invoke', 'MigrationWizardLeave') | Out-Null
            }
        }
        'History' {
            # The fixture has one backed-up source and a second branch at that version.
            Test-UI 'Restore offers clean and overwrite modes with their file effects' {
                $config = Get-Content -LiteralPath (Join-Path $FixtureRoot 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json
                $project = @($config.BackupConfigs | Where-Object Name -eq $ExportProjectName)
                $script:historyProject = $project
                if ($project.Count -ne 1 -or $project[0].SourceFolders.Count -ne 1) { throw 'Expected one source in the history fixture project.' }
                Invoke-Ui @('invoke', 'NavHome') | Out-Null
                Invoke-Ui @('wait-for', ('HomeConfigCard_' + $project[0].Id), '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', ('HomeConfigCard_' + $project[0].Id)) | Out-Null
                Invoke-Ui @('wait-for', ('FolderManagerFolder_' + $project[0].SourceFolders[0].Id), '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', ('FolderManagerFolder_' + $project[0].SourceFolders[0].Id)) | Out-Null
                Invoke-Ui @('wait-for', 'FolderManagerOpenHistory', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'FolderManagerOpenHistory') | Out-Null
                Invoke-Ui @('wait-for', 'HistoryVersionList', '-t', '5000') | Out-Null
                $tree = Get-UiJson @('inspect', 'HistoryVersionList', '-i', '-d', '6')
                $restore = @(Get-Elements $tree.windows.elements | Where-Object { $_.automationId -like 'HistoryVersionRestore_*' } | Select-Object -First 1)
                if ($restore.Count -ne 1) { throw 'No restorable fixture version.' }
                Invoke-Ui @('invoke', $restore[0].automationId) | Out-Null
                Invoke-Ui @('wait-for', 'PrimaryButton', '-t', '3000') | Out-Null
                $clean = Get-UiJson @('get-property', 'PrimaryButton', '-p', 'Name')
                $overwrite = Get-UiJson @('get-property', 'SecondaryButton', '-p', 'Name')
                if ($clean.properties.Name -notin @('清理还原', 'Clean restore') -or $overwrite.properties.Name -notin @('覆盖还原', 'Overwrite restore')) { throw 'Ambiguous restore mode labels.' }
                Save-Evidence 'restore-modes'
                Invoke-Ui @('invoke', 'CloseButton') | Out-Null
            }
            Test-UI 'Checkout identifies the project, target branch, version, and affected source' {
                Invoke-Ui @('invoke', 'AdvancedHistoryItem') | Out-Null
                Invoke-Ui @('wait-for', 'HistoryBranchFilter', '-t', '3000') | Out-Null
                Invoke-Ui @('invoke', 'HistoryBranchFilter', '--action', 'expand') | Out-Null
                $tree = Get-UiJson @('inspect', 'HistoryBranchFilter', '-i', '-d', '6')
                $branch = @(Get-Elements $tree.windows.elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $BranchName } | Select-Object -First 1)
                if ($branch.Count -ne 1) { throw 'The checkout fixture branch was not found.' }
                Invoke-Ui @('invoke', $branch[0].selector, '--action', 'select') | Out-Null
                Invoke-Ui @('wait-for', 'HistoryBranchCheckout', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                Invoke-Ui @('invoke', 'HistoryBranchCheckout') | Out-Null
                Invoke-Ui @('wait-for', 'PrimaryButton', '-t', '5000') | Out-Null
                $tree = Get-UiJson @('inspect', '-d', '12')
                $text = (Get-Elements $tree.windows.elements | Where-Object type -eq 'Text' | ForEach-Object name) -join "`n"
                foreach ($expected in @($ExportProjectName, $BranchName, $script:historyProject[0].SourceFolders[0].Path)) {
                    if (-not $text.Contains($expected)) { throw "Checkout identity or scope missing: $expected" }
                }
                $primary = Get-UiJson @('get-property', 'PrimaryButton', '-p', 'Name')
                if ($primary.properties.Name -notin @('备份并切换','Back up and switch','切换分支','Switch branch')) { throw 'Checkout action contradicts the plan.' }
                Save-Evidence 'checkout-confirmation'
                Invoke-Ui @('invoke', 'CloseButton') | Out-Null
            }
        }
    }
} catch {
    if ($results.Count -eq 0) { $results.Add(@{name = 'UI setup'; status = 'FAIL'; detail = "$_"}) }
} finally {
    if ($script:hwnd) { try { Invoke-Checked @('ui', 'yield') | Out-Null } catch { $results.Add(@{name = 'Release UI workflow'; status = 'FAIL'; detail = "$_"}) } }
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    $reportPath = Join-Path $evidence "$stamp-$Phase-results.json"
    @{ target = 'local'; appPid = $AppPid; phase = $Phase; results = @($results.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Results: $reportPath"
}
if ($results.Count -eq 0 -or @($results | Where-Object status -eq 'FAIL').Count -gt 0) { exit 1 }

} finally { Pop-Location }
