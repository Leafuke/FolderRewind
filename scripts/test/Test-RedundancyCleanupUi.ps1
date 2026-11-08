param(
    [Parameter(Mandatory)][string]$CandidatePath,
    [Parameter(Mandatory)][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$CandidateDllSha256,
    [Parameter(Mandatory)][string]$PluginPackage,
    [Parameter(Mandatory)][string]$PluginSha256,
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$CaseScript = (Join-Path $PSScriptRoot 'Test-RedundancyCleanupUi.Cases.ps1'),
    [string]$OutputRoot,
    [string[]]$CaseFilter = @(),
    [string]$PythonPath = 'python',
    [string]$RclonePath = 'D:\Programs\Software-Test\rclone\rclone.exe'
)

# The supplied case script is executed in this scope and uses Case, Ui and Shot.
# Every required case must produce a real assertion and a screenshot. Missing
# cases are failures, including when setup or another case aborts the run.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$required = @('discovery','plugin-install-enable-disable-safe-mode',
    'world-core-full','world-native-incremental','history-preview-branch-merge-restore',
    'cancel-close','cloud-profile-webdav','migration-legacy-state',
    'zh-light-dark-high-contrast','en-light-dark-high-contrast')
$requiredAssertions = @{
    'discovery' = @('real-copy-discovered','selection-persisted','ludusavi-result')
    'plugin-install-enable-disable-safe-mode' = @('exact-package-installed','enabled','disabled','safe-mode-blocks','normal-mode-restored')
    'world-core-full' = @('capture-completed','restore-byte-match')
    'world-native-incremental' = @('full-capture-completed','incremental-capture-completed','deleted-content-restored-correctly')
    'history-preview-branch-merge-restore' = @('history-visible','preview-opened','branch-created','merge-committed','restore-byte-match')
    # Controlled coordination and fault injection are verified separately by the host agent.
    'cancel-close' = @('capture-canceled','restore-canceled','restore-cancel-message','close-settled','restart-consistent')
    'cloud-profile-webdav' = @('connection-verified','profile-saved','edit-used-by-webdav','restart-persisted')
    'migration-legacy-state' = @('legacy-imported','unknown-state-preserved','legacy-scope-preserved','workflow-completed','export-roundtrip')
    'zh-light-dark-high-contrast' = @('zh-light','zh-dark','zh-high-contrast')
    'en-light-dark-high-contrast' = @('en-light','en-dark','en-high-contrast')
}
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$workflow = 'redundancy-' + [guid]::NewGuid().ToString('N')
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$oldDataRoot = $env:FOLDERREWIND_TEST_DATA_ROOT
$env:WINAPP_UI_WORKFLOW_ID = $workflow
$app = $null
$webdav = $null
$hwnd = $null
$uiStarted = $false

function Checked([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments 2>&1)
    if ($null -eq $global:LASTEXITCODE -or $global:LASTEXITCODE -ne 0) {
        throw "winapp failed ($global:LASTEXITCODE): $($Arguments -join ' '): $($output -join "`n")"
    }
    $output -join "`n"
}
function Ui([string[]]$Arguments) {
    if (-not $script:hwnd) { throw 'Main window has not been verified.' }
    Checked (@('ui') + $Arguments + @('--on','local','-w',[string]$script:hwnd))
}
function Shot([string]$Name) {
    if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Unsafe screenshot name.' }
    $path = Join-Path $script:evidence ($Name + '.png')
    if (Test-Path -LiteralPath $path) { throw "Evidence already exists: $path" }
    Ui @('screenshot','--output',$path) | Out-Null
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'Screenshot missing or empty.' }
    $screenshots.Add($path)
    $script:caseScreenshots.Add($path)
}
function Case([string]$Name,[scriptblock]$Body) {
    if ($Name -notin $required -or @($results | Where-Object name -eq $Name).Count) { throw "Unknown or duplicate case: $Name" }
    if ($CaseFilter.Count -and $Name -notin $CaseFilter) { return }
    Write-Host "RUN $Name"
    $script:caseScreenshots = [Collections.Generic.List[string]]::new()
    $script:caseAssertions = [Collections.Generic.List[string]]::new()
    try {
        & $Body
        foreach ($requiredAssertion in $requiredAssertions[$Name]) {
            if ($requiredAssertion -notin $script:caseAssertions) { throw "Missing assertion: $requiredAssertion" }
        }
        if (!$script:caseScreenshots.Count) { throw 'No screenshot was captured for this case.' }
        $results.Add(@{name=$Name;status='PASS';assertions=@($script:caseAssertions.ToArray());screenshots=@($script:caseScreenshots.ToArray());visualReview='PENDING'})
        Write-Host "PASS $Name (visual review pending)"
    } catch {
        $failure = "$_"
        try { if ($script:hwnd) { Shot ($Name + '-failure') } } catch { }
        $results.Add(@{name=$Name;status='FAIL';detail=$failure;screenshots=@($script:caseScreenshots.ToArray())})
        Write-Host "FAIL $Name`: $failure"
    }
}
function Assert-Case([bool]$Condition,[string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Description)) { throw 'Assertion description is required.' }
    if (!$Condition) { throw "Assertion failed: $Description" }
    if ($Description -in $script:caseAssertions) { throw "Duplicate assertion: $Description" }
    $script:caseAssertions.Add($Description)
}
function Flatten($nodes) { foreach ($n in $nodes) { if ($null -ne $n) { $n; Flatten $n.children } } }
function Tree {
    $tree = Ui @('inspect','--interactive','--json') | ConvertFrom-Json
    $nodes = @(Flatten $tree.windows.elements)
    if (!$nodes.Count) { throw 'UI inspection returned no elements.' }
    $nodes
}
function Expand-Settings([string]$Id) {
    Ui @('wait-for','SettingsScrollViewer','-t','5000') | Out-Null
    Ui @('scroll','SettingsScrollViewer','--to','top') | Out-Null
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        $part = Ui @('inspect',$Id,'-d','8','--json') | ConvertFrom-Json
        $button = @(Flatten $part.windows.elements | Where-Object { $_.type -eq 'Button' -and $_.expandState -and $_.className -eq 'Microsoft.UI.Xaml.Controls.Expander' } | Select-Object -First 1)
        if ($button.Count -eq 1 -and !$button[0].isOffscreen) {
            if ($button[0].expandState -eq 'collapsed') { Ui @('invoke',$button[0].selector) | Out-Null }
            return
        }
        Ui @('scroll','SettingsScrollViewer','--direction','down') | Out-Null
    }
    throw "Cannot reach settings expander $Id."
}
function Choose-File([string]$Path,[switch]$Folder,[switch]$Save) {
    if (!$Save -and !(Test-Path -LiteralPath $Path -PathType $(if ($Folder) {'Container'} else {'Leaf'}))) { throw "Picker input missing: $Path" }
    if ($Save -and (![IO.Path]::GetFullPath($Path).StartsWith($session + '\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $Path))) { throw 'Save path must be new and inside the isolated session.' }
    $picker = @()
    for ($attempt=0; $attempt -lt 50; $attempt++) {
        $windows = @(Checked @('ui','list-windows','--on','local','--json') | ConvertFrom-Json)
        $picker = @($windows | Where-Object { $_.processName -eq 'PickerHost' -and [string]$_.ownerHwnd -eq $script:hwnd })
        if ($picker.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($picker.Count -ne 1) { throw 'Expected one picker owned by the candidate window.' }
    $pickerHwnd = [string]$picker[0].hwnd
    $inspection = Checked @('ui','inspect','--on','local','-w',$pickerHwnd,'-d','8','--json') | ConvertFrom-Json
    $nodes = @(Flatten $inspection.windows.elements)
    $edit = @($nodes | Where-Object { $_.type -eq 'Edit' -and $_.automationId -in @('1001','1148','1152') })
    $confirm = @($nodes | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq '1' })
    if ($edit.Count -ne 1 -or $confirm.Count -ne 1) { throw 'Picker controls are ambiguous.' }
    Checked @('ui','set-value',$edit[0].selector,$Path,'--on','local','-w',$pickerHwnd) | Out-Null
    Checked @('ui','invoke',$confirm[0].selector,'--on','local','-w',$pickerHwnd) | Out-Null
}
function World-Manifest([string]$WorldPath) {
    $full = [IO.Path]::GetFullPath($WorldPath)
    if (!$full.StartsWith($session + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'World assertion escaped session.' }
    @(Get-ChildItem -LiteralPath $full -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($full,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
function Open-PluginSettings {
    Ui @('invoke','NavHome') | Out-Null
    Ui @('invoke','SettingsItem') | Out-Null
    Expand-Settings 'SettingsPluginsExpander'
    Ui @('wait-for','SettingsManualInstallPlugin','-t','10000') | Out-Null
}
function Install-CandidatePlugin {
    Open-PluginSettings
    Ui @('invoke','SettingsManualInstallPlugin') | Out-Null
    Choose-File $package
    Ui @('wait-for','PrimaryButton','-t','30000') | Out-Null
    Shot 'plugin-install-confirmation'
    Ui @('invoke','PrimaryButton') | Out-Null
    Ui @('wait-for','PrimaryButton','--gone','-t','30000') | Out-Null
    Ui @('wait-for','SettingsPluginEnabled_com.folderrewind.minerewind','-t','30000') | Out-Null
}

try {
    $root = [IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
    if (!$OutputRoot) { $OutputRoot = $root }
    $outputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    if (!$outputRoot.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase) -and $outputRoot -ne $root) { throw 'Output root must be inside run root.' }
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $candidateDll = Join-Path (Split-Path $candidate) 'FolderRewind.dll'
    $package = [IO.Path]::GetFullPath($PluginPackage)
    $casePath = [IO.Path]::GetFullPath($CaseScript)
    $webdavScript = Join-Path $PSScriptRoot 'ui-round2-webdav.py'
    foreach ($file in @($candidate,$candidateDll,$package,$casePath,$RclonePath,$webdavScript,(Join-Path $root 'ui-fixture/world-manifest.json'))) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
    }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $CandidateSha256) { throw 'Candidate SHA-256 mismatch.' }
    if ((Get-FileHash -LiteralPath $candidateDll -Algorithm SHA256).Hash -ne $CandidateDllSha256) { throw 'Candidate DLL SHA-256 mismatch.' }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $PluginSha256) { throw 'Plugin SHA-256 mismatch.' }
    $identityPath = Join-Path $root 'candidate/identity.json'
    $identitySha256 = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $identity = Get-Content -Raw -LiteralPath $identityPath | ConvertFrom-Json
    if (![string]::Equals([IO.Path]::GetFullPath($identity.formalDirectory).TrimEnd('\'), (Split-Path $candidate).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate directory differs from frozen identity.' }
    $formalCount = 0
    foreach ($entry in $identity.files) {
        $frozen = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if (!$frozen.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $frozen -PathType Leaf) -or (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Frozen identity mismatch: $($entry.path)" }
        if ($frozen.StartsWith((Split-Path $candidate).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { $formalCount++ }
    }
    if (!$formalCount -or $formalCount -ne @(Get-ChildItem -LiteralPath (Split-Path $candidate) -File -Recurse -Force).Count) { throw 'Formal candidate file set differs from frozen identity.' }
    foreach ($entry in $identity.msi) { if (!(Test-Path -LiteralPath $entry.path -PathType Leaf) -or (Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Frozen MSI identity mismatch.' } }
    if ((Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash -ne $identitySha256) { throw 'Candidate identity changed during verification.' }
    $fixture = (Get-Content -Raw -LiteralPath (Join-Path $root 'ui-fixture/world-manifest.json')) | ConvertFrom-Json
    foreach ($world in $fixture.worlds) {
        $copy = [IO.Path]::GetFullPath($world.copy)
        if (!$copy.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'World copy escaped run root.' }
        foreach ($file in $world.files) {
            $path = [IO.Path]::GetFullPath((Join-Path $copy $file.path))
            if (!$path.StartsWith($copy + '\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "World fixture mismatch: $($world.name)/$($file.path)" }
        }
        if (@(Get-ChildItem -LiteralPath $copy -Recurse -File).Count -ne @($world.files).Count) { throw 'World fixture contains unexpected files.' }
    }
    # Keep the live data root short enough for SQLite's temporary index names;
    # evidence remains under the requested output directory.
    $runId = [guid]::NewGuid().ToString('N').Substring(0,8)
    $session = Join-Path $root ('u-' + $runId)
    if (Test-Path -LiteralPath $session) { throw 'Session directory already exists.' }
    $script:evidence = Join-Path $outputRoot ('evidence-' + $runId)
    $data = Join-Path $session 'appdata'
    New-Item -ItemType Directory -Path $script:evidence,$data | Out-Null
    # Preserve the verified baseline: cases may mutate only these run copies.
    $script:worlds = @{}
    foreach ($world in $fixture.worlds) {
        $destination = Join-Path $session ('world-' + $world.name)
        Copy-Item -LiteralPath $world.copy -Destination $destination -Recurse
        foreach ($file in $world.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $destination $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Session copy mismatch: $($world.name)/$($file.path)" }
        }
        $script:worlds[$world.name] = $destination
    }
    # Loopback fixture uses a random available port; it must be owned by this run.
    Set-Content -LiteralPath (Join-Path $session 'acceptance-owner') -Value 'redundancy-cleanup-ui' -Encoding utf8
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $python = (Get-Command $PythonPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $script:webdavUrl = "http://127.0.0.1:$port/"
    $webdav = Start-Process -FilePath $python -ArgumentList @('-u',"`"$webdavScript`"",'--root',"`"$session`"",'--port',"$port") -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $session 'webdav.stdout.log') -RedirectStandardError (Join-Path $session 'webdav.stderr.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($webdav.HasExited) { throw 'WebDAV fixture exited before readiness.' }
        try { $client = [Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1',$port); $client.Dispose(); $ready = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'WebDAV fixture did not listen on loopback.' }
    $env:FOLDERREWIND_TEST_DATA_ROOT = $data
    $app = Start-Process -FilePath $candidate -WorkingDirectory (Split-Path $candidate) -PassThru
    $uiStarted = $true
    Checked @('ui','wait-for','NavView','--on','local','-a',"$($app.Id)",'-t','30000') | Out-Null
    $actual = (Get-Process -Id $app.Id).Path
    if (![string]::Equals([IO.Path]::GetFullPath($actual),$candidate,[StringComparison]::OrdinalIgnoreCase)) { throw 'Launched process is not the candidate.' }
    $windows = @(Checked @('ui','list-windows','--on','local','-a',"$($app.Id)",'--json') | ConvertFrom-Json)
    $main = @($windows | Where-Object { $_.hwnd -and $_.className -eq 'WinUIDesktopWin32WindowClass' })
    if ($main.Count -ne 1) { throw 'Expected exactly one main window.' }
    $script:hwnd = [string]$main[0].hwnd
    $tree = Ui @('inspect','--interactive','--json') | ConvertFrom-Json
    if (@(Flatten $tree.windows.elements).Count -eq 0) { throw 'Empty UI tree.' }
    # Explicitly verify the MSI data-root override took effect before any case.
    if (!(Test-Path -LiteralPath (Join-Path $data 'FolderRewind'))) { throw 'Candidate did not create its isolated MSI profile.' }
    . $casePath
} catch {
    $results.Add(@{name='setup-or-execution';status='FAIL';detail="$_"})
} finally {
    if ($app) {
        try {
            $current=Get-Process -Id $app.Id -ErrorAction SilentlyContinue
            if ($current -and [string]::Equals([IO.Path]::GetFullPath($current.Path),$candidate,[StringComparison]::OrdinalIgnoreCase)) {
                [void]$current.CloseMainWindow()
                if ($hwnd) {
                    try { Checked @('ui','wait-for','SecondaryButton','--on','local','-w',[string]$hwnd,'-t','2000') | Out-Null; Checked @('ui','invoke','SecondaryButton','--on','local','-w',[string]$hwnd) | Out-Null } catch { if (!$current.HasExited) { throw } }
                }
                if (!$current.WaitForExit(10000)) { $results.Add(@{name='candidate-cleanup';status='FAIL';detail='Candidate did not close gracefully; process was not forced.'}) }
            }
        } catch { $results.Add(@{name='candidate-cleanup';status='FAIL';detail="$_"}) }
    }
    if ($uiStarted) { try { Checked @('ui','yield','--on','local') | Out-Null } catch { $results.Add(@{name='release-workflow';status='FAIL';detail="$_"}) } }
    if ($webdav -and !$webdav.HasExited) { Stop-Process -Id $webdav.Id -ErrorAction Continue }
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    $env:FOLDERREWIND_TEST_DATA_ROOT = $oldDataRoot
    foreach ($name in $required) { if (!@($results | Where-Object name -eq $name).Count) { $results.Add(@{name=$name;status='NOT_RUN'}) } }
    if ($identitySha256 -and (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash -ne $identitySha256) { $results.Add(@{name='candidate-identity';status='FAIL';detail='Identity changed during UI run.'}) }
    $report = [ordered]@{success=$false;uiOwner='gpt-6.1-terra';target='local';workflowId=$workflow;candidate=$CandidatePath;candidateSha256=$CandidateSha256;candidateDllSha256=$CandidateDllSha256;candidateIdentitySha256=$identitySha256;plugin=$PluginPackage;pluginSha256=$PluginSha256;runRoot=$RunRoot;session=$session;fixtureManifest=(Join-Path $RunRoot 'ui-fixture/world-manifest.json');webdavUrl=$script:webdavUrl;rclone=$RclonePath;appPid=if($app){$app.Id}else{$null};hwnd=$hwnd;results=@($results.ToArray());screenshots=@($screenshots.ToArray());visualReview='PENDING'}
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $(if ($outputRoot) {$outputRoot} else {$RunRoot}) 'ui-results.json') -Encoding utf8
}
if (@($results | Where-Object status -ne 'PASS').Count -or $report.visualReview -ne 'PASS') { exit 1 }
