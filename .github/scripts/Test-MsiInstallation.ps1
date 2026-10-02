[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath, [Parameter(Mandatory)][string]$PublishDirectory,
      [Parameter(Mandatory)][string]$ResultDirectory, [string]$BaseFixtureMsi, [switch]$SkipRollbackProbes)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
$msi = [IO.Path]::GetFullPath($MsiPath)
$resultRoot = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$installer = New-Object -ComObject WindowsInstaller.Installer
if (Get-Process FolderRewind -ErrorAction SilentlyContinue) { throw 'MSI validation requires no running FolderRewind instance.' }
$db = $installer.OpenDatabase($msi, 0)
$view = $db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
$view.Execute(); $version = $view.Fetch().StringData(1); $view.Close()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
$baseAssets = Join-Path $resultRoot 'base-fixture'
$fixtureSuffix = [guid]::NewGuid().ToString('N').Substring(0,16)
if ($BaseFixtureMsi) {
    $baseAssets = Split-Path ([IO.Path]::GetFullPath($BaseFixtureMsi))
    $fixtureDatabase = $installer.OpenDatabase([IO.Path]::GetFullPath($BaseFixtureMsi), 0)
    $fixtureQuery = $fixtureDatabase.OpenView('SELECT `Key` FROM `RegLocator` WHERE `Signature_` = ''RememberedUserPath''')
    $fixtureQuery.Execute(); $key = $fixtureQuery.Fetch().StringData(1); $fixtureQuery.Close()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($fixtureQuery) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($fixtureDatabase) | Out-Null
    $fixtureSuffix = $key.Split('.')[-1]
} else {
$basePayload = Join-Path $resultRoot 'base-payload'
if (Test-Path -LiteralPath $basePayload) { throw 'Use a fresh result directory.' }
New-Item -ItemType Directory -Path $basePayload | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $basePayload -Recurse
'old content' | Set-Content (Join-Path $basePayload 'rollback-modified.txt')
'removed in upgrade' | Set-Content (Join-Path $basePayload 'rollback-removed.txt')
& "$PSScriptRoot\Prepare-MsiPackage.ps1" -ProjectPath "$PSScriptRoot\..\..\Installer\FolderRewind.Installer.wixproj" `
    -PublishDirectory $basePayload -Version $version -Platform x64 -OutputDirectory $baseAssets -SkipBundle -TestIdentity -EnableFaultInjection -TestIdentitySuffix $fixtureSuffix
}
$fixtureRegistry = "HKCU:\Software\Leafuke\FolderRewind.Msi.Validation.$fixtureSuffix"
$msi = Join-Path $baseAssets "FolderRewind_${version}_x64.msi"
$baseIdentity = Assert-ValidationMsi $msi $fixtureSuffix
$family = $baseIdentity.UpgradeCode
if (@($installer.RelatedProducts($family)).Count) { throw 'This test family is already installed.' }
$profile = Join-Path $resultRoot 'isolated-profile'
$configDirectory = Join-Path $profile 'FolderRewind'
$destination = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind-msi-validation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
$configPath = Join-Path $configDirectory 'config.json'
'{"GlobalSettings":{"Language":"zh_CN","CloseBehavior":2,"RememberCloseBehavior":true,"HasShownFirstLaunchGuide":true}}' |
    Set-Content -LiteralPath $configPath -Encoding utf8
$results = [Collections.Generic.List[object]]::new()
$installed = $false
$app = $null
$command = $null
$existingRun = $null
function Invoke-Msi([string]$name,[string]$arguments,[int[]]$Expected=@(0,3010)) {
    if ($arguments -match '^/[ix]\s+"([^\"]+\.msi)"') {
        [void](Assert-ValidationMsi $Matches[1] $fixtureSuffix)
    } elseif ($arguments -match '^/x\s+(\{[0-9A-Fa-f-]+\})') {
        if ($Matches[1] -notin @($installer.RelatedProducts($family))) { throw "Refusing non-isolated product uninstall: $name" }
    } else {
        throw "Unrecognized MSI invocation in validation: $name"
    }
    $log = Join-Path $resultRoot "$name.log"
    $process = Start-Process msiexec.exe -ArgumentList ($arguments + ' /qn /norestart /l*v "' + $log + '"') -WindowStyle Hidden -Wait -PassThru
    $pass = $process.ExitCode -in $Expected
    $results.Add(@{scenario=$name;passed=$pass;exitCode=$process.ExitCode;log=$log})
    if (-not $pass) { throw "$name failed: $($process.ExitCode)" }
}
function Assert-Installed($Identity) {
    $products = @($installer.RelatedProducts($family))
    if ($products.Count -ne 1 -or $products[0] -ne $Identity.ProductCode -or $installer.ProductState($products[0]) -ne 5) {
        throw 'Expected exactly the fully installed original ProductCode.'
    }
    foreach ($property in @{VersionString=$Identity.ProductVersion;InstallLocation=($destination+'\');AssignmentType='0'}.GetEnumerator()) {
        if ($installer.ProductInfo($products[0],$property.Key) -ine $property.Value) { throw "Registration mismatch: $($property.Key)" }
    }
    foreach ($feature in @('MainFeature','DesktopShortcutFeature')) {
        if ($installer.FeatureState($products[0],$feature) -ne 3) { throw "Feature not restored: $feature" }
    }
    $cache = $installer.ProductInfo($products[0],'LocalPackage')
    if (-not (Test-Path -LiteralPath $cache) -or (Get-MsiIdentity $cache).ProductCode -ne $Identity.ProductCode) { throw 'Cached MSI missing or incorrect.' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $contexts = @($installer.ProductsEx($products[0],$sid,2))
    if ($contexts.Count -ne 1 -or $contexts[0].Context -ne 2 -or $contexts[0].UserSid -ne $sid) { throw 'User registration context was not restored.' }
}
try {
    Invoke-Msi 'install-user' ('/i "' + $msi + '" INSTALLFOLDER="' + $destination + '"')
    $installed = $true
    & "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $destination -ResultPath (Join-Path $resultRoot 'installed-resources.json')
    $registeredPath = (Get-ItemProperty -LiteralPath $fixtureRegistry -Name InstallFolder).InstallFolder
    if ($registeredPath.TrimEnd('\') -ine $destination) { throw 'Per-user install path was not registered correctly.' }
    $products = @($installer.RelatedProducts($family))
    if ($products.Count -ne 1) { throw 'Initial install did not produce exactly one MSI product.' }
    Invoke-Msi 'same-version-maintenance' ('/i "' + $msi + '"')
    if (@($installer.RelatedProducts($family)).Count -ne 1) { throw 'Same-version maintenance created duplicate records.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $destination 'FolderRewind.exe'))
    $start.UseShellExecute = $false; $start.WorkingDirectory = $destination
    $start.Environment['FOLDERREWIND_TEST_DATA_ROOT'] = $profile
    $app = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 200; $app.Refresh()
        if ($app.HasExited) { throw "Installed application exited at startup: $($app.ExitCode)" }
    } while ($app.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed application did not show a main window.' }
    if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) { throw 'WinApp CLI is required for UI evidence; a window handle alone is insufficient.' }
    winapp ui inspect -a $app.Id --json | Set-Content (Join-Path $resultRoot 'app-tree.json')
    winapp ui screenshot -a $app.Id --output (Join-Path $resultRoot 'installed-app.png') --json |
        Set-Content (Join-Path $resultRoot 'app-screenshot.json')
    if ($LASTEXITCODE) { throw 'UI screenshot capture failed.' }
    $start.Arguments = '--startup'
    $duplicate = [Diagnostics.Process]::Start($start)
    if (-not $duplicate.WaitForExit(15000) -or $duplicate.ExitCode) { throw 'Second startup did not exit after notifying the primary.' }
    Invoke-Msi 'running-repair-blocked' ('/i "' + $msi + '" REINSTALL=ALL REINSTALLMODE=amus') @(1603)
    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(45000)) { throw 'Application could not exit gracefully; validation will preserve the installation.' }
    $app = $null
    $configuration = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($configuration.GlobalSettings.Language -ne 'zh-CN') { throw 'Legacy application language was not migrated.' }
    $icon = Join-Path $destination 'Assets\MsiApp.ico'
    Remove-Item -LiteralPath $icon -Force
    Invoke-Msi 'repair-file' ('/i "' + $msi + '" REINSTALL=ALL REINSTALLMODE=amus')
    if (-not (Test-Path -LiteralPath $icon)) { throw 'Repair did not restore the selected icon component.' }
    Invoke-Msi 'remove-desktop-feature' ('/i "' + $msi + '" REMOVE=DesktopShortcutFeature')
    if (Get-ItemProperty -LiteralPath $fixtureRegistry -Name DesktopShortcut -ErrorAction SilentlyContinue) { throw 'Desktop shortcut feature was not removed.' }
    Invoke-Msi 'add-desktop-feature' ('/i "' + $msi + '" ADDLOCAL=MainFeature,DesktopShortcutFeature')
    $upgradeAssets = Join-Path $resultRoot 'upgrade-fixture'
    $upgradePayload = Join-Path $resultRoot 'upgrade-payload'
    if (Test-Path -LiteralPath $upgradePayload) { throw 'Use a fresh upgrade payload directory.' }
    New-Item -ItemType Directory -Path $upgradePayload | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $upgradePayload -Recurse
    'new content' | Set-Content (Join-Path $upgradePayload 'rollback-modified.txt')
    'added in upgrade' | Set-Content (Join-Path $upgradePayload 'rollback-added.txt')
    if (-not (Test-Path (Join-Path $destination 'rollback-removed.txt')) -or (Test-Path (Join-Path $upgradePayload 'rollback-removed.txt'))) { throw 'Base fixture must include the old-only file.' }
    & "$PSScriptRoot\Prepare-MsiPackage.ps1" -ProjectPath "$PSScriptRoot\..\..\Installer\FolderRewind.Installer.wixproj" `
        -PublishDirectory $upgradePayload -Version '99.99.99.0' -Platform x64 -OutputDirectory $upgradeAssets -SkipBundle -TestIdentity -EnableFaultInjection -TestIdentitySuffix $fixtureSuffix
    $upgrade = Join-Path $upgradeAssets 'FolderRewind_99.99.99.0_x64.msi'
    $upgradeIdentity = Assert-ValidationMsi $upgrade $fixtureSuffix
    $beforeUpgrade = Get-PayloadSnapshot $destination
    $beforeConfig = (Get-FileHash $configPath).Hash
    if ($SkipRollbackProbes) {
        $results.Add(@{scenario='upgrade-rollback';status='not-run';reason='Explicitly skipped; prior failed rollback evidence is retained separately.'})
    } else {
    foreach ($point in @('files','registration')) {
        Invoke-Msi "upgrade-rollback-$point" ('/i "' + $upgrade + '" TRANSFORMS=:zh-CN.mst FOLDERREWIND_TEST_FAIL='+$point) @(1603)
        Assert-Installed $baseIdentity
        Assert-PayloadSnapshot $destination $beforeUpgrade
        if ((Get-FileHash $configPath).Hash -ne $beforeConfig) { throw 'Rollback changed application configuration.' }
        Invoke-Msi "repair-after-rollback-$point" ('/i "'+$msi+'" REINSTALL=ALL REINSTALLMODE=amus')
        Assert-Installed $baseIdentity
        $start.Arguments = ''
        $restoredApp = [Diagnostics.Process]::Start($start)
        if ($restoredApp.WaitForExit(5000)) { throw 'Restored application did not remain running.' }
        if (-not $restoredApp.CloseMainWindow() -or -not $restoredApp.WaitForExit(45000)) { throw 'Restored application did not exit normally.' }
        $results.Add(@{scenario="upgrade-rollback-$point-state";passed=$true})
    }
    }
    Invoke-Msi 'english-to-chinese-upgrade' ('/i "' + $upgrade + '" TRANSFORMS=:zh-CN.mst')
    if ((Get-ItemProperty -LiteralPath $fixtureRegistry).InstallFolder.TrimEnd('\') -ine $destination) { throw 'Upgrade lost the selected install path.' }
    Invoke-Msi 'downgrade-blocked' ('/i "' + $msi + '"') @(1603)
    $before = (Get-FileHash $configPath -Algorithm SHA256).Hash
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { $existingRun = if ($runKey) { $runKey.GetValue('FolderRewind') } else { $null } }
    finally { if ($runKey) { $runKey.Dispose() } }
    if ($null -eq $existingRun -and -not $SkipRollbackProbes) {
        $command = '"' + (Join-Path $destination 'FolderRewind.exe') + '" --startup'
        New-Item -Path $runPath -Force | Out-Null
        Set-ItemProperty -LiteralPath $runPath -Name FolderRewind -Value $command
        $beforeUninstall = Get-PayloadSnapshot $destination
        foreach ($point in @('cleanup','files','registration')) {
            Invoke-Msi "uninstall-rollback-$point" ('/x "' + $upgrade + '" FOLDERREWIND_TEST_FAIL='+$point) @(1603)
            $logText = Get-Content (Join-Path $resultRoot "uninstall-rollback-$point.log") -Raw
            if ($logText -notmatch 'startup-delete-verified' -or $logText -notmatch 'startup-restore-verified') { throw 'Run value did not demonstrably pass through deletion and restoration.' }
            if ((Get-ItemPropertyValue -LiteralPath $runPath -Name FolderRewind) -cne $command) { throw 'Uninstall rollback did not restore the owned startup entry.' }
            Assert-Installed $upgradeIdentity
            Assert-PayloadSnapshot $destination $beforeUninstall
            Invoke-Msi "repair-after-uninstall-rollback-$point" ('/i "'+$upgrade+'" REINSTALL=ALL REINSTALLMODE=amus')
            $results.Add(@{scenario="uninstall-rollback-$point-state";passed=$true})
        }
    } elseif ($SkipRollbackProbes) { $results.Add(@{scenario='uninstall-rollback';status='not-run';reason='Explicitly skipped; rollback acceptance remains open.'}) }
    else { $results.Add(@{scenario='startup-cleanup-probe';status='blocked';reason='A pre-existing Run entry was preserved.'}) }
    Invoke-Msi 'uninstall' ('/x "' + $upgrade + '"')
    $installed = $false
    if (Test-Path -LiteralPath $destination) { throw 'Uninstall left the application directory behind.' }
    if ((Get-FileHash $configPath -Algorithm SHA256).Hash -ne $before) { throw 'Uninstall modified application configuration.' }
    if ($null -eq $existingRun -and -not $SkipRollbackProbes -and (Get-ItemProperty -LiteralPath $runPath -Name FolderRewind -ErrorAction SilentlyContinue)) { throw 'Successful uninstall left the owned startup command.' }
    $results.Add(@{scenario='configuration-preserved';passed=$true})
} catch {
    $results.Add(@{scenario='validation-failure';passed=$false;error=$_.Exception.Message})
    throw
} finally {
    if ($app -and -not $app.HasExited) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(45000)) { $results.Add(@{scenario='cleanup';passed=$false;error='Application did not exit normally; installation preserved.'}) }
    }
    if ($installed -and -not (Get-Process FolderRewind -ErrorAction SilentlyContinue)) {
        # RelatedProducts can omit advertised records after failed rollback.
        # Use only this run's verified packages, never broad family deletion.
        $cleanupPackages = @($msi)
        if (Get-Variable upgrade -ErrorAction SilentlyContinue) { $cleanupPackages = @($upgrade,$msi) }
        foreach ($package in $cleanupPackages) {
            try {
                $identity = Assert-ValidationMsi $package $fixtureSuffix
                if ($installer.ProductState($identity.ProductCode) -ne -1) {
                    Invoke-Msi ('cleanup-'+$identity.ProductVersion) ('/x "'+$package+'"')
                }
            } catch { $results.Add(@{scenario='cleanup';passed=$false;error=$_.Exception.Message}) }
        }
    }
    if ($command -and $null -eq $existingRun) {
        $probeKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
        try { if ($probeKey -and $probeKey.GetValue('FolderRewind') -ceq $command) { $probeKey.DeleteValue('FolderRewind',$false) } }
        finally { if ($probeKey) { $probeKey.Dispose() } }
    }
    Export-InstallerResults $results (Join-Path $resultRoot 'results.json') $msi
}
if (@($results | Where-Object status -NE 'passed').Count) { throw 'Required lifecycle scenarios were failed, blocked or not run.' }
