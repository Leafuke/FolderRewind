[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
$output = [IO.Path]::GetFullPath($ResultDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new control-test directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$native = & "$PSScriptRoot\Build-InstallerNative.ps1" -OutputDirectory (Join-Path $output 'native') -EnableFaultInjection | Select-Object -Last 1
$wix = Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk\7.0.0\tools\net472\x64\wix.exe'
$engine = New-Object -ComObject WindowsInstaller.Installer
$results = [Collections.Generic.List[object]]::new()
# No production identities, application binaries, startup values or shortcuts.
# Each step adds one authoring characteristic; run the full fixture separately.
foreach ($mode in @('per-user-file','dual-file','dual-registry')) {
    $testId = [guid]::NewGuid().ToString('N')
    $family = Get-ValidationGuid "$testId/msi"
    $directory = Join-Path $output $mode
    New-Item -ItemType Directory -Path $directory | Out-Null
    $destination = Join-Path $directory 'installed'
    $packages = @()
    $codes = @()
    foreach ($version in @('1.0.0.0','2.0.0.0')) {
        $code = Get-ValidationGuid "$testId/$version"
        $codes += $code
        $payload = Join-Path $directory "$version.txt"
        "Distinct control payload $version" | Set-Content -LiteralPath $payload
        $scope = if ($mode -eq 'per-user-file') { 'perUser' } else { 'perUserOrMachine' }
        $fileKey = if ($mode -eq 'dual-registry') { 'no' } else { 'yes' }
        $marker = if ($mode -eq 'dual-registry') { '<RegistryValue Root="HKMU" Key="Software\Leafuke\FolderRewind.Msi.Validation.'+$testId+'" Name="control" Type="integer" Value="1" KeyPath="yes" />' } else { '' }
        $source = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
 <Package Name="FolderRewind MSI Validation $testId" Manufacturer="Leafuke" Version="$version" ProductCode="$code" UpgradeCode="$family" Scope="$scope" Language="1033">
  <MajorUpgrade Schedule="afterInstallInitialize" DowngradeErrorMessage="Newer version installed" />
  <MediaTemplate EmbedCab="yes" />
  <Property Id="FOLDERREWIND_TEST_ID" Value="$testId" />
  <Property Id="FOLDERREWIND_TEST_FAIL" Secure="yes" />
  <Property Id="INSTALLFOLDER" Value="$([Security.SecurityElement]::Escape($destination))" Secure="yes" />
  <SetProperty Id="ARPINSTALLLOCATION" Value="[INSTALLFOLDER]" Before="InstallValidate" Sequence="execute" />
  <StandardDirectory Id="LocalAppDataFolder"><Directory Id="INSTALLFOLDER" Name="FolderRewind MSI Validation $testId">
   <Component Id="Control" Guid="$(Get-ValidationGuid "$testId/component")"><File Source="$([Security.SecurityElement]::Escape($payload))" Name="control.txt" KeyPath="$fileKey" />$marker</Component>
  </Directory></StandardDirectory>
  <Feature Id="MainFeature"><ComponentRef Id="Control" /></Feature>
  <Binary Id="Fault" SourceFile="$([Security.SecurityElement]::Escape((Join-Path $native 'FolderRewind.BAFunctions.dll')))" />
  <CustomAction Id="Fail" BinaryRef="Fault" DllEntry="FailForTesting" Execute="deferred" Impersonate="yes" Return="check" />
  <InstallExecuteSequence><Custom Action="Fail" After="PublishProduct" Condition="FOLDERREWIND_TEST_FAIL = 1 AND NOT UPGRADINGPRODUCTCODE" /></InstallExecuteSequence>
 </Package>
</Wix>
"@
        $wxs = Join-Path $directory "$version.wxs"
        $msi = Join-Path $directory "$version.msi"
        $source | Set-Content -LiteralPath $wxs -Encoding utf8
        & $wix build -acceptEula wix7 -arch x64 $wxs -o $msi *> (Join-Path $directory "$version-build.log")
        if ($LASTEXITCODE) { throw "Control build failed: $mode/$version" }
        [void](Assert-ValidationMsi $msi $testId)
        $packages += $msi
    }
    function Invoke-Control([string]$Name, [string]$Package, [string]$Action, [string]$Properties, [int[]]$Expected) {
        [void](Assert-ValidationMsi $Package $testId)
        $log = Join-Path $directory "$Name.log"
        $process = Start-Process msiexec.exe -ArgumentList "$Action `"$Package`" $Properties /qn /norestart /l*v `"$log`"" -WindowStyle Hidden -Wait -PassThru
        $results.Add(@{scenario="$mode/$Name-exit";passed=($process.ExitCode -in $Expected);exitCode=$process.ExitCode;log=$log})
        if ($process.ExitCode -notin $Expected) { throw "Control failed: $Name ($($process.ExitCode))" }
    }
    try {
        foreach ($operation in @('uninstall','upgrade')) {
          try {
            Invoke-Control "$operation-install" $packages[0] '/i' '' @(0)
            $before = Get-PayloadSnapshot $destination
            $package = if ($operation -eq 'uninstall') { $packages[0] } else { $packages[1] }
            $action = if ($operation -eq 'uninstall') { '/x' } else { '/i' }
            Invoke-Control "$operation-fault" $package $action 'FOLDERREWIND_TEST_FAIL=1' @(1603)
            $related = @($engine.RelatedProducts($family))
            $restored = $related.Count -eq 1 -and $related[0] -eq $codes[0] -and $engine.ProductState($codes[0]) -eq 5
            $results.Add(@{scenario="$mode/$operation-registration";passed=$restored;state=$engine.ProductState($codes[0]);related=$related;log=(Join-Path $directory "$operation-fault.log")})
            Assert-PayloadSnapshot $destination $before
            if (-not $restored) { throw 'Control MSI also failed to restore complete registration.' }
            Invoke-Control "$operation-repair" $packages[0] '/i' 'REINSTALL=ALL REINSTALLMODE=amus' @(0)
          } catch { $results.Add(@{scenario="$mode/$operation-control";passed=$false;error=$_.Exception.Message}) }
          finally {
            foreach ($index in @(1,0)) {
                if ($engine.ProductState($codes[$index]) -ne -1) {
                    try { Invoke-Control "$operation-cleanup-$index" $packages[$index] '/x' '' @(0,1605) }
                    catch { $results.Add(@{scenario="$mode/$operation-cleanup";passed=$false;error=$_.Exception.Message}) }
                }
            }
          }
        }
    } catch { $results.Add(@{scenario="$mode/control";passed=$false;error=$_.Exception.Message}) }
    finally {
        # Uninstall by verified package, including an advertised rollback remnant.
        foreach ($index in @(1,0)) {
            if ($engine.ProductState($codes[$index]) -ne -1) {
                try { Invoke-Control "cleanup-$index" $packages[$index] '/x' '' @(0,1605) }
                catch { $results.Add(@{scenario="$mode/cleanup";passed=$false;error=$_.Exception.Message}) }
            }
        }
        Export-InstallerResults $results (Join-Path $output 'results.json') $null
    }
}
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
if (@($results | Where-Object status -NE 'passed').Count) { throw 'Control reproduction found failures; see results.json. This is not a clean-VM result.' }
