[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaseMsi, [Parameter(Mandatory)][string]$UpgradeMsi,
      [Parameter(Mandatory)][string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
$testId = (Get-MsiIdentity $BaseMsi).FOLDERREWIND_TEST_ID
$baseIdentity = Assert-ValidationMsi $BaseMsi $testId
$family = $baseIdentity.UpgradeCode
$installer = New-Object -ComObject WindowsInstaller.Installer
if (@($installer.RelatedProducts($family)).Count -or (Get-Process FolderRewind -ErrorAction SilentlyContinue)) { throw 'An empty isolated context is required.' }
$root = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($path in @($BaseMsi,$UpgradeMsi)) {
    [void](Assert-ValidationMsi $path $testId)
    $db = $installer.OpenDatabase([IO.Path]::GetFullPath($path),0)
    $view = $db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
    $view.Execute(); $found = $view.Fetch().StringData(1); $view.Close()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    if ($found -ne $family) { throw 'Boundary tests refuse production MSI identities.' }
}
function Invoke-Probe([string]$Name,[string]$Msi,[string]$Properties,[int[]]$Expected=@(0,3010),[string]$Action='/i') {
    $log = Join-Path $root ($Name + '.log')
    $path = [IO.Path]::GetFullPath($Msi)
    $process = Start-Process msiexec.exe -ArgumentList ($Action+' "'+$path+'" '+$Properties+' /qn /norestart /l*v "'+$log+'"') -WindowStyle Hidden -Wait -PassThru
    $passed = $process.ExitCode -in $Expected
    $results.Add(@{scenario=$Name;passed=$passed;exitCode=$process.ExitCode;log=$log})
    if (-not $passed) { throw "$Name failed: $($process.ExitCode)" }
}
try {
    Invoke-Probe 'protected-user-path' $BaseMsi 'INSTALLFOLDER="C:\Program Files\FolderRewind MSI Validation"' @(1603)
    Invoke-Probe 'default-user-chinese-install' $BaseMsi 'TRANSFORMS=:zh-CN.mst'
    $products = @($installer.RelatedProducts($family))
    if ($products.Count -ne 1) { throw 'Expected one isolated installation.' }
    $location = $installer.ProductInfo($products[0],'InstallLocation')
    $default = Join-Path $env:LOCALAPPDATA ('Programs\' + $baseIdentity.ProductName)
    if ($location.TrimEnd('\') -ine $default) { throw "Incorrect default user directory: $location" }
    Invoke-Probe 'cross-scope-blocked' $UpgradeMsi 'ALLUSERS=1 MSIINSTALLPERUSER=""' @(1603)
    Invoke-Probe 'chinese-to-english-upgrade' $UpgradeMsi ''
    $products = @($installer.RelatedProducts($family))
    if ($products.Count -ne 1 -or $installer.ProductInfo($products[0],'InstallLocation') -ine $location) { throw 'Reverse language upgrade lost installation identity or path.' }
    if ($installer.ProductInfo($products[0],'Language') -ne '1033') { throw 'Reverse language upgrade was not registered in English.' }
    Invoke-Probe 'boundary-uninstall' $UpgradeMsi '' @(0,3010) '/x'
} finally {
    foreach ($product in @($installer.RelatedProducts($family))) {
        if (Get-Process FolderRewind -ErrorAction SilentlyContinue) { break }
        $log = Join-Path $root 'boundary-cleanup.log'
        $process = Start-Process msiexec.exe -ArgumentList ('/x '+$product+' /qn /norestart /l*v "'+$log+'"') -WindowStyle Hidden -Wait -PassThru
        $results.Add(@{scenario='boundary-cleanup';passed=$process.ExitCode -in @(0,3010);exitCode=$process.ExitCode;log=$log})
    }
    Export-InstallerResults $results (Join-Path $root 'results.json') $BaseMsi
}
if (@($results | Where-Object status -NE 'passed').Count) { throw 'Boundary acceptance failed.' }
