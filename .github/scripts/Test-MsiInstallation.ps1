[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath, [Parameter(Mandatory)][string]$PublishDirectory,
      [Parameter(Mandatory)][string]$ResultDirectory, [string]$BaseFixtureMsi, [switch]$SkipRollbackProbes)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$msi = [IO.Path]::GetFullPath($MsiPath)
$resultRoot = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$installer = New-Object -ComObject WindowsInstaller.Installer
$family = '{39B92584-E7D5-43F9-AD6F-476D08F854AC}'
if (@($installer.RelatedProducts($family)).Count) { throw 'MSI validation requires an empty installation context. Existing FolderRewind records will not be replaced.' }
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
& "$PSScriptRoot\Prepare-MsiPackage.ps1" -ProjectPath "$PSScriptRoot\..\..\Installer\FolderRewind.Installer.wixproj" `
    -PublishDirectory $PublishDirectory -Version $version -Platform x64 -OutputDirectory $baseAssets -SkipBundle -TestIdentity -EnableFaultInjection -TestIdentitySuffix $fixtureSuffix
}
$fixtureRegistry = "HKCU:\Software\Leafuke\FolderRewind.Msi.Validation.$fixtureSuffix"
$msi = Join-Path $baseAssets "FolderRewind_${version}_x64.msi"
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
function Invoke-Msi([string]$name,[string]$arguments,[int[]]$Expected=@(0,3010)) {
    if ($arguments -match '^/[ix]\s+"([^\"]+\.msi)"') {
        $candidate = $installer.OpenDatabase($Matches[1], 0)
        $identity = $candidate.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
        $identity.Execute(); $found = $identity.Fetch().StringData(1); $identity.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($identity) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($candidate) | Out-Null
        if ($found -ne $family) { throw "Refusing non-isolated MSI in validation: $name ($found)" }
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
    & "$PSScriptRoot\Prepare-MsiPackage.ps1" -ProjectPath "$PSScriptRoot\..\..\Installer\FolderRewind.Installer.wixproj" `
        -PublishDirectory $PublishDirectory -Version '99.99.99.0' -Platform x64 -OutputDirectory $upgradeAssets -SkipBundle -TestIdentity -EnableFaultInjection -TestIdentitySuffix $fixtureSuffix
    $upgrade = Join-Path $upgradeAssets 'FolderRewind_99.99.99.0_x64.msi'
    $beforeUpgradeHash = (Get-FileHash (Join-Path $destination 'FolderRewind.exe') -Algorithm SHA256).Hash
    if ($SkipRollbackProbes) {
        $results.Add(@{scenario='upgrade-rollback';status='not-run';reason='Explicitly skipped; prior failed rollback evidence is retained separately.'})
    } else {
    Invoke-Msi 'upgrade-rollback' ('/i "' + $upgrade + '" TRANSFORMS=:zh-CN.mst FOLDERREWIND_TEST_FAIL=1') @(1603)
    if (@($installer.RelatedProducts($family)).Count -ne 1 -or
        (Get-FileHash (Join-Path $destination 'FolderRewind.exe') -Algorithm SHA256).Hash -ne $beforeUpgradeHash) { throw 'Failed upgrade did not restore the previous installation.' }
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
        Invoke-Msi 'uninstall-rollback' ('/x "' + $upgrade + '" FOLDERREWIND_TEST_FAIL=1') @(1603)
        if ((Get-ItemPropertyValue -LiteralPath $runPath -Name FolderRewind) -cne $command) { throw 'Uninstall rollback did not restore the owned startup entry.' }
    } elseif ($SkipRollbackProbes) { $results.Add(@{scenario='uninstall-rollback';status='not-run';reason='Explicitly skipped; rollback acceptance remains open.'}) }
    else { $results.Add(@{scenario='startup-cleanup-probe';status='blocked';reason='A pre-existing Run entry was preserved.'}) }
    Invoke-Msi 'uninstall' ('/x "' + $upgrade + '"')
    $installed = $false
    if (Test-Path -LiteralPath $destination) { throw 'Uninstall left the application directory behind.' }
    if ((Get-FileHash $configPath -Algorithm SHA256).Hash -ne $before) { throw 'Uninstall modified application configuration.' }
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
        foreach ($product in @($installer.RelatedProducts($family))) {
            try { Invoke-Msi 'cleanup-uninstall' ('/x ' + $product) } catch { $results.Add(@{scenario='cleanup';passed=$false;error=$_.Exception.Message}) }
        }
    }
    @{scenarios=$results;isolatedProfile=$profile;installDirectory=$destination;visualReview='Screenshots captured; human visual review required.'} |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $resultRoot 'results.json') -Encoding utf8
}
