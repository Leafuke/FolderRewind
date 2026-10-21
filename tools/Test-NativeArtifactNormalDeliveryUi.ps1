param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Workspace)
if (!(Test-Path -LiteralPath (Join-Path $root 'acceptance-owner'))) { throw 'An isolated fixture is required.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = [IO.Path]::GetFullPath($OutputDirectory)
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'folderrewind-normal-delivery'
function Ui([string[]]$Arguments) {
    $text = & winapp ui @Arguments -a $AppPid --on local 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($text -join "`n") }
    $text -join "`n"
}
function Wait-Condition([scriptblock]$Check) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!(& $Check)) { if ($timer.Elapsed.TotalSeconds -ge 20) { throw 'Fixture state timed out.' }; Start-Sleep -Milliseconds 100 }
}
function Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add(@{name=$Name;status='PASS'}); Write-Output "PASS $Name" }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Shot([string]$Name) {
    $tree = Ui @('inspect','--json') | ConvertFrom-Json
    $window = @($tree.windows | Where-Object { $_.elements[0].className -eq 'WinUIDesktopWin32WindowClass' })[0].hwnd
    if (!$window) { throw 'Expected the main app window.' }
    $path = Join-Path $output ($Name + '.png')
    Ui @('screenshot','--window',[string]$window,'--output',$path) | Out-Null
    if (!(Test-Path -LiteralPath $path)) { throw 'Screenshot not delivered.' }
    $screenshots.Add($path)
}
function Saved { (Get-Content (Join-Path $root 'appdata/FolderRewind/config.json') -Raw | ConvertFrom-Json).BackupConfigs[0] }
try {
    Case 'Normal build activates final plugin and reads persisted settings' {
        Ui @('invoke','NavManager') | Out-Null
        Ui @('wait-for','FolderManagerConfigSettings') | Out-Null
        Ui @('invoke','FolderManagerConfigSettings') | Out-Null
        Ui @('wait-for','ConfigSettingsName','--value','UI later edit') | Out-Null
        Ui @('invoke','ConfigSettingsTabBackup') | Out-Null
        Ui @('wait-for','SemanticBackupEnable','--value','On') | Out-Null
        Ui @('invoke','SemanticBackupFallback','--action','toggle-off') | Out-Null
        Ui @('invoke','ProjectSettingsSave') | Out-Null
        Wait-Condition { (Saved).ArtifactTransformPolicy.FailureBehavior -eq 1 }
        Ui @('wait-for','SemanticCheckpointCreate','-p','IsEnabled','--value','True') | Out-Null
        Shot '01-normal-activation'
        Ui @('invoke','NavManager') | Out-Null
        Ui @('wait-for','FolderManagerBackupConfig') | Out-Null
    }
    $source = [IO.Path]::GetFullPath((Saved).SourceFolders[0].Path)
    if (!$source.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source escaped fixture.' }
    function Tree {
        $files = @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName | ForEach-Object { [IO.Path]::GetRelativePath($source,$_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash })
        $directories = @(Get-ChildItem -LiteralPath $source -Recurse -Directory | Sort-Object FullName | ForEach-Object { [IO.Path]::GetRelativePath($source,$_.FullName) + '/' })
        ($files + $directories) -join "`n"
    }
    Case 'Normal build creates a Native version with fallback forbidden' {
        $history = Join-Path $root 'appdata/FolderRewind/history'
        $before = @(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack').Count
        Set-Content -LiteralPath (Join-Path $source 'normal-delivery-capture.bin') 'final normal build Native capture'
        Ui @('invoke','FolderManagerBackupConfig') | Out-Null
        Wait-Condition { @(Get-ChildItem -LiteralPath $history -Recurse -File -Filter '*.frpack').Count -gt $before }
        Ui @('wait-for','FolderManagerBackupConfig','-p','IsEnabled','--value','True','-t','15000') | Out-Null
    }
    Case 'Normal build safely restores final Native content and directories' {
        $expected = Tree
        $extra = Join-Path $source 'normal-delivery-extra.bin'
        Set-Content -LiteralPath $extra 'remove this extra file'
        Ui @('invoke','NavHistory') | Out-Null
        Ui @('wait-for','HistoryVersionList') | Out-Null
        $tree = Ui @('inspect','--interactive','--json') | ConvertFrom-Json
        function Flatten($elements) { foreach($element in $elements) { $element; Flatten $element.children } }
        $restore = @(Flatten $tree.windows.elements | Where-Object automationId -like 'HistoryVersionRestore_*')[0].automationId
        if (!$restore) { throw 'Native history version not visible.' }
        Ui @('invoke',$restore) | Out-Null
        Ui @('wait-for','PrimaryButton','-t','15000') | Out-Null
        Shot '02-normal-restore-confirm'
        Ui @('invoke','PrimaryButton') | Out-Null
        Ui @('wait-for','PrimaryButton','--gone','-t','20000') | Out-Null
        Wait-Condition { !(Test-Path -LiteralPath $extra) }
        if ((Tree) -ne $expected) { throw 'Normal delivery restore differs in bytes or directories.' }
        Ui @('wait-for',$restore,'-t','10000') | Out-Null
        Shot '03-normal-restored'
    }
} finally {
    & winapp ui yield --on local | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
    @{target='local';appPid=$AppPid;normalBuild=$true;results=@($results.ToArray());screenshots=@($screenshots.ToArray())} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'test-results.json') -Encoding utf8
}
