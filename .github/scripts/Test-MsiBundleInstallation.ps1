[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureMsi, [Parameter(Mandatory)][string]$ResultDirectory,
      [ValidateSet('Owned','Foreign')][string]$StartupProbeMode = 'Owned', [switch]$IncludeFailureProbe,
      [switch]$ClearSettings)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$output = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installer = New-Object -ComObject WindowsInstaller.Installer
$msi = [IO.Path]::GetFullPath($FixtureMsi)
$testId = (Get-MsiIdentity $msi).FOLDERREWIND_TEST_ID
$identity = Assert-ValidationMsi $msi $testId
$bundleFamily = Get-ValidationGuid "$testId/bundle"
$db = $installer.OpenDatabase($msi,0)
function Read-Value([string]$Query) {
    $view = $db.OpenView($Query); [void]$view.Execute()
    try { return $view.Fetch().StringData(1) }
    finally { [void]$view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}
$family = Read-Value 'SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode'''
if ($family -ne $identity.UpgradeCode) { throw 'Bundle identity mismatch.' }
if (@($installer.RelatedProducts($family)).Count) { throw 'An empty isolated context is required.' }
$product = Read-Value 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''
$key = Read-Value 'SELECT `Key` FROM `RegLocator` WHERE `Signature_` = ''RememberedUserPath'''
if (-not $key.StartsWith('Software\Leafuke\FolderRewind.Msi.Validation.')) { throw 'Unexpected fixture registry identity.' }
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
$native = & "$PSScriptRoot\Build-InstallerNative.ps1" | Select-Object -Last 1
dotnet build "$root\Installer\Bootstrapper\FolderRewind.Bootstrapper.wixproj" -t:Rebuild -c Release -p:Platform=x86 `
    -p:ProductVersion=99.99.99.0 -p:PayloadArchitecture=x64 "-p:MsiPath=$msi" "-p:ProductCode=$product" `
    "-p:NativeOutputDirectory=$native" "-p:OutputPath=$output\" "-p:MsiRegistryKey=$key" "-p:MsiUpgradeCode=$family" `
    "-p:BundleDisplayName=$($identity.ProductName)" "-p:BundleUpgradeCode=$bundleFamily" -p:EnableFaultInjection=1 /warnaserror
if ($LASTEXITCODE) { throw 'Isolated bundle build failed.' }
$exe = Join-Path $output 'FolderRewind_99.99.99.0_Setup_x64.exe'
$destination = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind-bundle-validation-' + [guid]::NewGuid().ToString('N'))
function Get-FixtureProcess { Get-Process FolderRewind -ErrorAction SilentlyContinue | Where-Object Path -IEQ (Join-Path $destination 'FolderRewind.exe') }
$results = [Collections.Generic.List[object]]::new()
$installed = $false
$ownedCommand = '"' + (Join-Path $destination 'FolderRewind.exe') + '" --startup'
$probeCommand = if ($StartupProbeMode -eq 'Owned') { $ownedCommand } else { '"C:\FolderRewind-foreign-'+$testId+'\FolderRewind.exe" --startup' }
$probeScenario = if ($StartupProbeMode -eq 'Owned') { 'owned-startup-cleanup' } else { 'foreign-startup-preserved' }
$startupProbe = $false
$settingsBase = Join-Path $output ('settings-' + [guid]::NewGuid().ToString('N'))
$settingsRoot = Join-Path $settingsBase 'FolderRewind'
New-Item -ItemType Directory -Path (Join-Path $settingsRoot 'backups') -Force | Out-Null
$settingsFiles = @('config.json','config.json.bak','config.json.recovery.test.json')
$preservedFiles = @('history.json','backups\config.json','plugins.json')
foreach ($file in ($settingsFiles + $preservedFiles)) { [IO.File]::WriteAllText((Join-Path $settingsRoot $file),'fixture-' + $file) }
function Get-BundleRegistrations {
    foreach ($registryView in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$registryView)
        $uninstall = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
        try {
            if ($uninstall) { foreach ($name in $uninstall.GetSubKeyNames()) {
                $entry = $uninstall.OpenSubKey($name)
                try { if (@($entry.GetValue('BundleUpgradeCode')) -contains $bundleFamily) {
                    [pscustomobject]@{code=$name;location=$entry.GetValue('InstallLocation');view="$registryView"}
                } } finally { $entry.Dispose() }
            } }
        } finally { if ($uninstall) { $uninstall.Dispose() }; $base.Dispose() }
    }
}
function Read-StartupProbe {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
    $runRead = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    $approvalRead = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')
    try {
        $runValue = $null; $approvalValue = $null
        if ($runRead) { $runValue = $runRead.GetValue('FolderRewind') }
        if ($approvalRead) { $approvalValue = $approvalRead.GetValue('FolderRewind') }
        # A pipeline/subexpression would enumerate byte[] into object[], making
        # the raw-type assertion fail even when the registry bytes are intact.
        return @{run=$runValue;approval=$approvalValue}
    }
    finally { if ($runRead) { $runRead.Dispose() }; if ($approvalRead) { $approvalRead.Dispose() }; $base.Dispose() }
}
try {
    $log = Join-Path $output 'install.log'
    $process = Start-Process $exe -ArgumentList ('/quiet /norestart InstallFolder="'+$destination+'" /log "'+$log+'"') -WindowStyle Hidden -Wait -PassThru
    $installed = $process.ExitCode -in @(0,3010)
    $results.Add(@{scenario='bundle-install';passed=$installed;exitCode=$process.ExitCode})
    if (-not $installed) { throw 'Bundle installation failed.' }
    & "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $destination -ResultPath (Join-Path $output 'installed-resources.json')
    $results.Add(@{scenario='bundle-main-feature-and-resources';passed=$true})
    $shell = New-Object -ComObject WScript.Shell
    try {
        $linkPath = Join-Path ([Environment]::GetFolderPath('Programs')) ($identity.ProductName + '\' + $identity.ProductName + '.lnk')
        if (-not (Test-Path -LiteralPath $linkPath)) { throw 'Installed Start menu shortcut is missing.' }
        $link = $shell.CreateShortcut($linkPath)
        try {
            if ($link.TargetPath -ine (Join-Path $destination 'FolderRewind.exe') -or $link.IconLocation -ine ((Join-Path $destination 'FolderRewind.exe') + ',0')) { throw 'Installed shortcut does not use the stable executable icon.' }
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $results.Add(@{scenario='installed-shortcut-stable-icon';passed=$true})
    if (Get-FixtureProcess) { throw 'Quiet setup launched the application.' }
    $results.Add(@{scenario='quiet-no-launch';passed=$true})
    $registrations = @(Get-BundleRegistrations | Sort-Object code -Unique)
    if ($registrations.Count -ne 1 -or $registrations[0].location.TrimEnd('\') -ine $destination) { throw 'Bundle registration or InstallLocation is incorrect.' }
    $results.Add(@{scenario='bundle-location';passed=$true})
    $process = Start-Process $exe -ArgumentList ('/quiet /norestart /log "'+$output+'\same-version.log"') -WindowStyle Hidden -Wait -PassThru
    $single = @($installer.RelatedProducts($family)).Count -eq 1 -and @(Get-BundleRegistrations | Sort-Object code -Unique).Count -eq 1
    $results.Add(@{scenario='bundle-same-version';passed=$process.ExitCode -in @(0,3010) -and $single;exitCode=$process.ExitCode})
    if (-not $single -or $process.ExitCode -notin @(0,3010)) { throw 'Same-version bundle maintenance failed.' }
    $rebuilt = Join-Path $output 'rebuilt'
    dotnet build "$root\Installer\Bootstrapper\FolderRewind.Bootstrapper.wixproj" -t:Rebuild -c Release -p:Platform=x86 `
        -p:ProductVersion=99.99.99.0 -p:PayloadArchitecture=x64 "-p:MsiPath=$msi" "-p:ProductCode=$product" `
        "-p:NativeOutputDirectory=$native" "-p:OutputPath=$rebuilt\" "-p:MsiRegistryKey=$key" "-p:MsiUpgradeCode=$family" `
        "-p:BundleDisplayName=$($identity.ProductName)" "-p:BundleUpgradeCode=$bundleFamily" -p:EnableFaultInjection=1 /warnaserror
    if ($LASTEXITCODE) { throw 'Rebuilt bundle failed.' }
    $process = Start-Process (Join-Path $rebuilt (Split-Path $exe -Leaf)) -ArgumentList ('/quiet /norestart /log "'+$output+'\different-bundle.log"') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -in @(0,3010) -or @(Get-BundleRegistrations | Sort-Object code -Unique).Count -ne 1) { throw 'Same-version different BundleCode was not rejected.' }
    if ((Get-Content (Join-Path $output 'different-bundle.log') -Raw) -notmatch 'same version') { throw 'Rejection did not identify the duplicate version.' }
    $results.Add(@{scenario='same-version-different-bundle-rejected';passed=$true})
} catch {
    $results.Add(@{scenario='bundle-validation-failure';passed=$false;error=$_.Exception.Message})
    throw
} finally {
    if ($installed -and -not (Get-FixtureProcess)) {
        $run = $null; $approved = $null; $marker = $null
        try {
        $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
        $approved = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')
        if ($null -eq $run.GetValue('FolderRewind') -and $null -eq $approved.GetValue('FolderRewind')) {
            $marker = [byte[]]::new(12); $marker[0] = 3
            # Enable recovery before either write, so a partial probe is removed.
            $startupProbe = $true
            $run.SetValue('FolderRewind',$probeCommand,[Microsoft.Win32.RegistryValueKind]::String)
            $approved.SetValue('FolderRewind',$marker,[Microsoft.Win32.RegistryValueKind]::Binary)
        } else { $results.Add(@{scenario=$probeScenario;status='not-run';reason='Existing startup preferences were preserved.'}) }
        } catch { $results.Add(@{scenario='startup-probe-preparation';passed=$false;error=$_.Exception.Message}) }
        finally { if ($run) { $run.Dispose(); $run = $null }; if ($approved) { $approved.Dispose(); $approved = $null } }
        try {
        if ($startupProbe -and $IncludeFailureProbe) {
            $faultLog = Join-Path $output 'uninstall-fault.log'
            $fault = Start-Process $exe -ArgumentList ('/uninstall /quiet /norestart FolderRewindTestFail=cleanup /log "'+$faultLog+'"') -WindowStyle Hidden -Wait -PassThru
            $afterFault = Read-StartupProbe
            $childLog = Get-Content (Join-Path $output 'uninstall-fault_000_FolderRewindMsi.log') -Raw
            $preserved = $fault.ExitCode -eq 1603 -and $afterFault.run -ceq $probeCommand -and
                $afterFault.approval -is [byte[]] -and [Convert]::ToBase64String($afterFault.approval) -eq [Convert]::ToBase64String($marker) -and
                $installer.ProductState($product) -eq 5 -and $childLog.Contains('isolated fault injection reached')
            if ($childLog.Contains('startup-delete-verified') -and -not $childLog.Contains('startup-restore-verified')) { $preserved = $false }
            $results.Add(@{scenario='failed-uninstall-preserves-startup-and-registration';passed=$preserved;exitCode=$fault.ExitCode;log=$faultLog})
        }
        $settingsOption = if ($ClearSettings) { ' ClearSettingsChosen=1' } else { '' }
        $process = Start-Process $exe -ArgumentList ('/uninstall /quiet /norestart FolderRewindSettingsTestRoot="'+$settingsBase+'"'+$settingsOption+' /log "'+$output+'\uninstall.log"') -WindowStyle Hidden -Wait -PassThru
        $removed = @($installer.RelatedProducts($family)).Count -eq 0 -and $installer.ProductState($product) -eq -1 -and @(Get-BundleRegistrations).Count -eq 0
        $results.Add(@{scenario='bundle-uninstall';passed=$process.ExitCode -in @(0,3010) -and $removed;exitCode=$process.ExitCode})
        $settingsPassed = $true
        foreach ($file in $settingsFiles) {
            $path = Join-Path $settingsRoot $file
            if ($ClearSettings) { if (Test-Path -LiteralPath $path) { $settingsPassed = $false } }
            elseif (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne ('fixture-' + $file)) { $settingsPassed = $false }
        }
        foreach ($file in $preservedFiles) {
            $path = Join-Path $settingsRoot $file
            if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne ('fixture-' + $file)) { $settingsPassed = $false }
        }
        $results.Add(@{scenario=$(if ($ClearSettings) { 'settings-opt-in-backups-preserved' } else { 'settings-default-preserved' });passed=$settingsPassed})
        if ($startupProbe) {
            $fresh = Read-StartupProbe
            $runAfter = $fresh.run; $approvalAfter = $fresh.approval
            $probePassed = if ($StartupProbeMode -eq 'Owned') { $null -eq $runAfter -and $null -eq $approvalAfter } else {
                $runAfter -ceq $probeCommand -and $approvalAfter -is [byte[]] -and [Convert]::ToBase64String($approvalAfter) -eq [Convert]::ToBase64String($marker)
            }
            $results.Add(@{scenario=$probeScenario;passed=$probePassed;runAfter=$runAfter;approvalAfter=$approvalAfter})
        }
        } catch { $results.Add(@{scenario='bundle-uninstall';passed=$false;error=$_.Exception.Message}) }
        finally {
        try {
        if ($startupProbe) {
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            $approved = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run',$true)
            # Restore only values created by this isolated probe if uninstall failed.
            $currentRun = if ($run) { $run.GetValue('FolderRewind') } else { $null }
            if ($run -and $currentRun -ceq $probeCommand) { $run.DeleteValue('FolderRewind',$false) }
            $remaining = $null
            if ($approved) { $remaining = $approved.GetValue('FolderRewind') }
            if (($null -eq $currentRun -or $currentRun -ceq $probeCommand) -and $remaining -is [byte[]] -and [Convert]::ToBase64String($remaining) -eq [Convert]::ToBase64String($marker)) { $approved.DeleteValue('FolderRewind',$false) }
        }
        } catch { $results.Add(@{scenario='startup-probe-recovery';passed=$false;error=$_.Exception.Message}) }
        finally { if ($run) { $run.Dispose() }; if ($approved) { $approved.Dispose() } }
        }
    } elseif ($installed) {
        $results.Add(@{scenario='bundle-uninstall';passed=$false;error='A running application blocked safe cleanup; installation preserved.'})
    }
    if (-not (Get-FixtureProcess) -and $installer.ProductState($product) -ne -1) {
        [void](Assert-ValidationMsi $msi $testId)
        $cleanupLog = Join-Path $output 'fixture-registration-cleanup.log'
        $cleanup = Start-Process msiexec.exe -ArgumentList ('/x "'+$msi+'" /qn /norestart /l*v "'+$cleanupLog+'"') -WindowStyle Hidden -Wait -PassThru
        $results.Add(@{scenario='fixture-registration-cleanup';passed=($cleanup.ExitCode -eq 0 -and $installer.ProductState($product) -eq -1);log=$cleanupLog;exitCode=$cleanup.ExitCode})
    }
    Export-InstallerResults $results (Join-Path $output 'results.json') $exe
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
if (@($results | Where-Object status -NE 'passed').Count) { throw 'Bundle acceptance failed or incomplete; inspect results.json.' }
