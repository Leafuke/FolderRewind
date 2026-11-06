param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$PluginPackage,
    [Parameter(Mandatory)][string]$SevenZip,
    [switch]$DecisionRegression
)
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($Executable)
$run = [IO.Path]::GetFullPath($RunDirectory)
$source = [IO.Path]::GetFullPath($Source)
$package = [IO.Path]::GetFullPath($PluginPackage)
$sevenZip = [IO.Path]::GetFullPath($SevenZip)
foreach ($file in @($exe,$package,$sevenZip)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" } }
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Missing source directory: $source" }
if (Test-Path -LiteralPath $run) { throw "Run directory must be fresh: $run" }
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts'))
if (!$run.StartsWith(($artifactsRoot + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run directory must be a fresh child of artifacts.'
}
$parent = Split-Path -Parent $run
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$requestPath = Join-Path $parent ((Split-Path -Leaf $run) + '-request.json')
if (Test-Path -LiteralPath $requestPath) { throw "Request already exists: $requestPath" }
$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
[ordered]@{ Workspace=$run; Phase='capture'; Package=$package; PackageSha256=$sha; SevenZip=$sevenZip;
    Source=$source; SourceId=[guid]::NewGuid().ToString(); Encrypted=$false; MergeUiSeed=$true } |
    ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
$seed = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -ArgumentList @('--native-artifact-acceptance', ('"' + $requestPath + '"')) -WindowStyle Hidden -PassThru -Wait
if ($seed.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $run 'merge-ui-seed.json'))) { throw "Isolated seed failed (exit $($seed.ExitCode)); inspect $run." }

$environment = @{
    FOLDERREWIND_TEST_DATA_ROOT=(Join-Path $run 'appdata'); FOLDERREWIND_MERGE_UI='1'; FOLDERREWIND_SPATIAL_UI='0'
    FOLDERREWIND_PERFORMANCE_UI='0'; FOLDERREWIND_UI_POST_GC='0'; FOLDERREWIND_UI_PROFILING='0'; FOLDERREWIND_MANAGER_UI='0'
    FOLDERREWIND_MERGE_DECISION_UI=$(if ($DecisionRegression) { '1' } else { '0' })
}
$old = @{}
foreach ($name in $environment.Keys) { $old[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    $process = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
} finally { foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $old[$name], 'Process') } }
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'FolderRewind-merge-ui-' + [guid]::NewGuid().ToString('N')
function Invoke-Checked([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ') $($output -join ' ')" }
    $output
}
function Wait-File([string]$Name, [int]$Seconds = 120) {
    $path = Join-Path $run $Name; $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (!(Test-Path -LiteralPath $path)) {
        $errorFile = Join-Path $run 'merge-ui-error.json'
        if (Test-Path -LiteralPath $errorFile) { throw (Get-Content -LiteralPath $errorFile -Raw) }
        if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw "Fixture exited or timed out waiting for $Name." }
        Start-Sleep -Milliseconds 100
    }
    if ($Name.EndsWith('.json')) { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
}
function Ui([string[]]$Arguments) { Invoke-Checked (@('ui') + $Arguments + @('-w', "$hwnd", '--on', 'local', '--json')) | Out-Null }
function Nodes($items) { foreach ($item in $items) { if ($null -ne $item) { $item; if ($item.children) { Nodes $item.children } } } }
function Capture([string]$Name) {
    $path = Join-Path $run $Name
    Ui @('screenshot','-o',$path)
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing screenshot $path" }
}
function DecisionSnapshot([string]$Name) {
    Set-Content -LiteralPath (Join-Path $run "decision-$Name.request") -Value 'capture'
    Wait-File "decision-$Name.json" 45
}
try {
    $ready = Wait-File 'merge-ui-ready.json'
    if ($ready.status -ne 'READY' -or !$ready.CanAnalyze) { throw 'Merge setup was not ready.' }
    $windows = @(Invoke-Checked @('ui','list-windows','-a',"$($process.Id)",'--on','local','--json') | ConvertFrom-Json)
    $main = @($windows | Where-Object { $_.hwnd -and !$_.ownerHwnd })
    if ($main.Count -ne 1) { throw 'Cannot identify a unique fixture main window.' }
    $hwnd = $main[0].hwnd
    Capture '01-merge-setup.png'
    Set-Content -LiteralPath (Join-Path $run 'hold-merge-history') -Value 'hold'
    Ui @('invoke','MergeWorkspace_Analyze')
    Wait-File 'merge-history-entered' 25
    Ui @('wait-for','MergeWorkspace_SetupStop','-t','10000')
    Capture '02-preparing-history.png'
    Ui @('invoke','MergeWorkspace_SetupStop')
    Ui @('wait-for','MergeWorkspace_Analyze','-p','IsEnabled','--value','True','-t','30000')
    Remove-Item -LiteralPath (Join-Path $run 'hold-merge-history')
    Ui @('invoke','MergeWorkspace_Analyze')
    Ui @('wait-for','SpatialWorkspace_OpenMerge','-t','180000')
    Capture '03-merge-result.png'
    Set-Content -LiteralPath (Join-Path $run 'hold-merge-spatial-index') -Value 'hold'
    Ui @('invoke','SpatialWorkspace_OpenMerge')
    Wait-File 'merge-spatial-index-entered' 25
    $null = Wait-File 'merge-spatial-open-1.json' 20
    Ui @('wait-for','SpatialWorkspace_Back','-t','10000')
    Capture '04-spatial-initializing.png'
    Ui @('invoke','SpatialWorkspace_Back')
    $closedDuringInitialization = Wait-File 'merge-spatial-release-1.json' 30
    if ($closedDuringInitialization.published -or $closedDuringInitialization.rootExists -or
        !$closedDuringInitialization.indexDisposed -or !$closedDuringInitialization.overlayDrained) {
        throw 'Closing during initialization published partial data or did not release owned state.'
    }
    Remove-Item -LiteralPath (Join-Path $run 'hold-merge-spatial-index')
    for ($visit=2; $visit -le 3; $visit++) {
        Ui @('invoke','SpatialWorkspace_OpenMerge')
        $spatial = Wait-File "merge-spatial-ready-$visit.json" 180
        if ($spatial.status -ne 'READY' -or !$spatial.IsMerge) { throw "Spatial visit $visit was not ready in Merge mode." }
        Ui @('wait-for','SpatialWorkspace_LeftMap','-t','10000')
        Capture ("05-spatial-$visit.png")
        if ($DecisionRegression -and $visit -eq 2) {
            Ui @('invoke','SpatialWorkspace_List')
            Ui @('wait-for','Filter','-t','10000')
            Ui @('invoke','Filter','--action','expand')
            $owned = @(Invoke-Checked @('ui','list-windows','-a',"$($process.Id)",'--on','local','--json') | ConvertFrom-Json | Where-Object { [string]$_.ownerHwnd -eq [string]$hwnd })
            $options = @(
                foreach ($popup in $owned) {
                    $tree = Invoke-Checked @('ui','inspect','-w',"$($popup.hwnd)",'--interactive','--on','local','--json') | ConvertFrom-Json
                    Nodes $tree.windows.elements | Where-Object { $_.name -in @('全部变化','All changes') -and $_.type -in @('ListItem','ComboBoxItem') -and !$_.isOffscreen } |
                        ForEach-Object { [pscustomobject]@{ selector=$_.selector; hwnd=$popup.hwnd } }
                }
            )
            if ($options.Count -ne 1) { throw 'Expected one visible All changes filter option in an owned popup.' }
            Invoke-Checked @('ui','invoke',$options[0].selector,'-w',"$($options[0].hwnd)",'--action','select','--on','local','--json') | Out-Null
            $before = DecisionSnapshot 'before'
            $candidate = @($before.Rows | Where-Object { $_.Id -eq $before.target -and $_.Status -eq 'Automatic' -and $_.CanChooseOurs })
            if ($candidate.Count -ne 1 -or !$before.sameIndex -or !$before.sameBuild) { throw 'No unique overridable automatic row.' }
            $checkbox = $candidate[0].AutomationId
            Ui @('invoke',$checkbox,'--action','toggle-on')
            $selected = DecisionSnapshot 'selected'
            if ($selected.Counts -eq $before.Counts) { throw 'Selection count did not update.' }
            Ui @('invoke','SpatialWorkspace_UseOurs')
            Ui @('wait-for','PrimaryButton','-t','10000')
            Capture '06-confirm-overwrite.png'
            Ui @('invoke','PrimaryButton')
            $decided = DecisionSnapshot 'decided'
            if ($decided.Summary -eq $before.Summary -or $decided.Counts -ne $before.Counts) { throw 'Decision counts or selection did not update.' }
            Capture '07-decision-applied.png'
            Ui @('invoke',$checkbox,'--action','toggle-on')
            $reselected = DecisionSnapshot 'reselected'
            Ui @('invoke','MoreButton')
            Ui @('invoke','SpatialWorkspace_ResetDecision')
            $cleared = DecisionSnapshot 'cleared'
            if ($cleared.Summary -ne $before.Summary -or $cleared.Counts -ne $before.Counts) { throw 'Clear did not restore automatic counts and empty selection.' }
            foreach ($item in @($selected,$decided,$reselected,$cleared)) {
                if (!$item.sameIndex -or !$item.sameBuild -or $item.Directory -cne $before.Directory -or
                    $item.Session -cne $before.Session -or $item.Plan -cne $before.Plan) { throw 'A single decision replaced the published index or identity.' }
            }
            if ($decided.Revision -ne ($before.Revision + 1) -or $cleared.Revision -ne ($before.Revision + 2)) { throw 'Decision revisions did not advance exactly once.' }
            if ($selected.Generation -ne $before.Generation -or $decided.Generation -ne ($before.Generation + 1) -or
                $reselected.Generation -ne $decided.Generation -or $cleared.Generation -ne ($before.Generation + 2)) {
                throw 'Unexpected extra index synchronization or rebuild was observed.'
            }
            Capture '08-decision-cleared.png'
        }
        if ($DecisionRegression -and $visit -eq 3) {
            if ($null -eq $spatial.reopened -or $spatial.reopened.sameIndex -or $spatial.reopened.sameBuild -or
                $spatial.reopened.Directory -ceq $before.Directory -or $spatial.reopened.Revision -ne $cleared.Revision -or
                $spatial.reopened.IndexedRevision -ne $cleared.Revision -or @($spatial.reopened.Selected).Count -ne 0) {
                throw 'Reentry did not initialize a new index at the cleared revision.'
            }
        }
        Ui @('invoke','SpatialWorkspace_Back')
        Ui @('wait-for','SpatialWorkspace_OpenMerge','-t','30000')
    }
    Set-Content -LiteralPath (Join-Path $run 'merge-ui-finish.request') -Value 'finish'
    $observation = Wait-File 'merge-ui-observation.json' 60
    if (@($observation.events | Where-Object { $_.kind -eq 'canceled' -and !$_.IsBusy }).Count -eq 0) {
        throw 'The UI stop did not produce a drained canceled operation.'
    }
    if (@($observation.releases).Count -ne 3 -or @($observation.releases | Where-Object { !$_.indexDisposed -or !$_.overlayDrained -or $_.rootExists }).Count) {
        throw 'Spatial page disposal did not drain for all visits.'
    }
    $stages = @($observation.events | Where-Object kind -eq 'progress' | Select-Object -ExpandProperty stage -Unique)
    $uiStages = @($observation.events | Where-Object kind -eq 'merge-ui' | Select-Object -ExpandProperty stage -Unique)
    $missing = @(@('PreparingHistory','AnalyzingWorld','OrganizingResults') | Where-Object { $_ -notin $stages -or $_ -notin $uiStages })
    $initializing = @($observation.events | Where-Object { $_.kind -eq 'spatial-ui' -and ($_.Status -like '*可视化*' -or $_.Status -like '*visualization*') }).Count -gt 0
    $result = [ordered]@{ status = $(if ($missing.Count -eq 0 -and $initializing) {'PASS'} else {'INCOMPLETE'});
        pluginSha256=$sha; observedStages=$stages; observedUiStages=$uiStages; missingStages=$missing; initializingVisualizationObserved=$initializing;
        cancellation='UI stop invoked while acceptance gate held PreparingHistory'; closeDuringInitialization=$closedDuringInitialization;
        spatialVisits=3; decisionRegression=$DecisionRegression.IsPresent; releases=$observation.releases }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'merge-ui-results.json') -Encoding utf8
    Set-Content -LiteralPath (Join-Path $run 'merge-ui-exit.request') -Value 'exit'
    if (!$process.WaitForExit(60000)) { throw 'Isolated application did not finish shutdown within 60 seconds.' }
    if ($result.status -ne 'PASS') { throw 'Some actual progress stages were not observed; see merge-ui-results.json.' }
} finally {
    try { Invoke-Checked @('ui','yield','--on','local','--json') | Out-Null } catch { Write-Warning "Could not release UI workflow: $_" }
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
}
