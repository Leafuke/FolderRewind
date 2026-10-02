param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$path = [IO.Path]::GetFullPath($PackagePath)
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($path, 1)
try {
    # Deploy the complete multilingual application as one neutral payload. The
    # SDK's language list exceeds MSI's 20-character column, and its newer MUI
    # LANGIDs are absent from the MSI validator's legacy language enumeration.
    $view = $database.OpenView('SELECT `File`, `Version`, `Language` FROM `File`')
    $view.Execute()
    $ids = @()
    while ($row = $view.Fetch()) { if ($row.StringData(2) -and $row.StringData(3) -ne '0') { $ids += $row.StringData(1) } }
    $view.Close()
    foreach ($id in $ids) {
        $update = $database.OpenView('UPDATE `File` SET `Language` = ? WHERE `File` = ?')
        $record = $installer.CreateRecord(2)
        $record.StringData(1) = '0'; $record.StringData(2) = $id
        $update.Execute($record); $update.Close()
    }
    $database.Commit()
    $view = $database.OpenView('SELECT `Action`, `Type` FROM `CustomAction`')
    $view.Execute()
    while ($row = $view.Fetch()) {
        if ([int]$row.IntegerData(2) -band 2048) {
            $action = $row.StringData(1)
            if ($action -notin @('RollbackStartupMachine','CleanupStartupMachine','CommitStartupMachine')) { throw "Unexpected elevated action: $action" }
            $guard = $database.OpenView('SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action` = ?')
            $parameter = $installer.CreateRecord(1); $parameter.StringData(1) = $action
            $guard.Execute($parameter); $condition = $guard.Fetch(); $guard.Close()
            if (-not $condition -or ($condition.StringData(1) -replace '\s','') -ne 'REMOVE="ALL"ANDNOTUPGRADINGPRODUCTCODEANDALLUSERS=1') {
                throw "Elevated action is not restricted to machine uninstall: $action"
            }
        }
    }
    $view.Close()
} finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
$wix = Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk\7.0.0\tools\net472\x64\wix.exe'
& $wix msi validate -acceptEula wix7 -sice ICE57 -sice ICE105 $path
if ($LASTEXITCODE) { throw "Final MSI validation failed: $LASTEXITCODE" }
