[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureMsi, [Parameter(Mandatory)][string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$output = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installer = New-Object -ComObject WindowsInstaller.Installer
$msi = [IO.Path]::GetFullPath($FixtureMsi)
$db = $installer.OpenDatabase($msi,0)
function Read-Value([string]$Query) {
    $view = $db.OpenView($Query); [void]$view.Execute()
    try { return $view.Fetch().StringData(1) }
    finally { [void]$view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}
$family = Read-Value 'SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode'''
if ($family -ne '{39B92584-E7D5-43F9-AD6F-476D08F854AC}') { throw "Bundle tests refuse non-isolated MSI identities: $family" }
if (@($installer.RelatedProducts($family)).Count -or (Get-Process FolderRewind -ErrorAction SilentlyContinue)) { throw 'An empty isolated context is required.' }
$product = Read-Value 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''
$key = Read-Value 'SELECT `Key` FROM `RegLocator` WHERE `Signature_` = ''RememberedUserPath'''
if (-not $key.StartsWith('Software\Leafuke\FolderRewind.Msi.Validation.')) { throw 'Unexpected fixture registry identity.' }
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
$native = & "$PSScriptRoot\Build-InstallerNative.ps1" | Select-Object -Last 1
dotnet build "$root\Installer\Bootstrapper\FolderRewind.Bootstrapper.wixproj" -t:Rebuild -c Release `
    -p:ProductVersion=99.99.99.0 -p:PayloadArchitecture=x64 "-p:MsiPath=$msi" "-p:ProductCode=$product" `
    "-p:NativeOutputDirectory=$native" "-p:OutputPath=$output\" "-p:MsiRegistryKey=$key" "-p:MsiUpgradeCode=$family" `
    '-p:BundleDisplayName=FolderRewind MSI Validation' '-p:BundleUpgradeCode={90DDD6A1-C52C-421A-B46B-89F41059F578}' /warnaserror
if ($LASTEXITCODE) { throw 'Isolated bundle build failed.' }
$exe = Join-Path $output 'FolderRewind_99.99.99.0_Setup_x64.exe'
$destination = Join-Path ([IO.Path]::GetTempPath()) ('FolderRewind-bundle-validation-' + [guid]::NewGuid().ToString('N'))
$results = [Collections.Generic.List[object]]::new()
$installed = $false
$ownedCommand = '"' + (Join-Path $destination 'FolderRewind.exe') + '" --startup'
$startupProbe = $false
try {
    $log = Join-Path $output 'install.log'
    $process = Start-Process $exe -ArgumentList ('/quiet /norestart InstallFolder="'+$destination+'" /log "'+$log+'"') -WindowStyle Hidden -Wait -PassThru
    $installed = $process.ExitCode -in @(0,3010)
    $results.Add(@{scenario='bundle-install';passed=$installed;exitCode=$process.ExitCode})
    if (-not $installed) { throw 'Bundle installation failed.' }
    & "$PSScriptRoot\Test-MsiPayload.ps1" -Directory $destination -ResultPath (Join-Path $output 'installed-resources.json')
    $results.Add(@{scenario='bundle-main-feature-and-resources';passed=$true})
    if (Get-Process FolderRewind -ErrorAction SilentlyContinue) { throw 'Quiet setup launched the application.' }
    $results.Add(@{scenario='quiet-no-launch';passed=$true})
    $process = Start-Process $exe -ArgumentList ('/quiet /norestart /log "'+$output+'\same-version.log"') -WindowStyle Hidden -Wait -PassThru
    $single = @($installer.RelatedProducts($family)).Count -eq 1
    $results.Add(@{scenario='bundle-same-version';passed=$process.ExitCode -in @(0,3010) -and $single;exitCode=$process.ExitCode})
    if (-not $single -or $process.ExitCode -notin @(0,3010)) { throw 'Same-version bundle maintenance failed.' }
} catch {
    $results.Add(@{scenario='bundle-validation-failure';passed=$false;error=$_.Exception.Message})
    throw
} finally {
    if ($installed -and -not (Get-Process FolderRewind -ErrorAction SilentlyContinue)) {
        $run = $null; $approved = $null; $marker = $null
        try {
        $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
        $approved = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')
        if ($null -eq $run.GetValue('FolderRewind') -and $null -eq $approved.GetValue('FolderRewind')) {
            $marker = [byte[]]::new(12); $marker[0] = 3
            # Enable recovery before either write, so a partial probe is removed.
            $startupProbe = $true
            $run.SetValue('FolderRewind',$ownedCommand,[Microsoft.Win32.RegistryValueKind]::String)
            $approved.SetValue('FolderRewind',$marker,[Microsoft.Win32.RegistryValueKind]::Binary)
        } else { $results.Add(@{scenario='owned-startup-cleanup';status='not-run';reason='Existing startup preferences were preserved.'}) }
        } catch { $results.Add(@{scenario='startup-probe-preparation';passed=$false;error=$_.Exception.Message}) }
        try {
        $process = Start-Process $exe -ArgumentList ('/uninstall /quiet /norestart /log "'+$output+'\uninstall.log"') -WindowStyle Hidden -Wait -PassThru
        $removed = @($installer.RelatedProducts($family)).Count -eq 0
        $results.Add(@{scenario='bundle-uninstall';passed=$process.ExitCode -in @(0,3010) -and $removed;exitCode=$process.ExitCode})
        if ($startupProbe) {
            $runAfter = $run.GetValue('FolderRewind'); $approvalAfter = $approved.GetValue('FolderRewind')
            $results.Add(@{scenario='owned-startup-cleanup';passed=$null -eq $runAfter -and $null -eq $approvalAfter;runAfter=$runAfter;approvalAfter=$approvalAfter})
        }
        } catch { $results.Add(@{scenario='bundle-uninstall';passed=$false;error=$_.Exception.Message}) }
        finally {
        try {
        if ($startupProbe) {
            # Restore only values created by this isolated probe if uninstall failed.
            if ($run -and $run.GetValue('FolderRewind') -ceq $ownedCommand) { $run.DeleteValue('FolderRewind',$false) }
            $remaining = $null
            if ($approved) { $remaining = $approved.GetValue('FolderRewind') }
            if ($remaining -is [byte[]] -and [Convert]::ToBase64String($remaining) -eq [Convert]::ToBase64String($marker)) { $approved.DeleteValue('FolderRewind',$false) }
        }
        } catch { $results.Add(@{scenario='startup-probe-recovery';passed=$false;error=$_.Exception.Message}) }
        finally { if ($run) { $run.Dispose() }; if ($approved) { $approved.Dispose() } }
        }
    } elseif ($installed) {
        $results.Add(@{scenario='bundle-uninstall';passed=$false;error='A running application blocked safe cleanup; installation preserved.'})
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'results.json') -Encoding utf8
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
if (@($results | Where-Object { $_.ContainsKey('passed') -and -not $_.passed }).Count) { throw 'Bundle acceptance failed; inspect results.json.' }
