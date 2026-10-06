[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
      [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version,
      [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
      [switch]$IdentityOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\InstallerValidation.ps1"
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$repo = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot establish source revision.' }
$dirty = [bool](git -C $repo status --porcelain --untracked-files=normal)
$sources = [Collections.Generic.List[object]]::new()
foreach ($relative in @(git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard) | Sort-Object -Unique) {
    $path = Join-Path $repo $relative
    if (Test-Path -LiteralPath $path -PathType Leaf) { $sources.Add([ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}) }
}
$sourceJson = ConvertTo-Json -InputObject @($sources.ToArray()) -Depth 4 -Compress
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceJson))).ToLowerInvariant()
$sourceJson | Set-Content (Join-Path $output 'source-files.json') -Encoding utf8
$wix = Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk\7.0.0\tools\net472\x64\wix.exe'
$assets = @(); $internal = @(); $scenarios = @()
foreach ($requestedArchitecture in $Architectures) {
    $architecture = $requestedArchitecture.ToLowerInvariant()
    $exe = Join-Path $PackageDirectory "$architecture\FolderRewind_${Version}_Setup_$architecture.exe"
    $msi = Join-Path $PackageDirectory "$architecture\FolderRewind_${Version}_$architecture.msi"
    $identity = Get-MsiIdentity $msi
    if ($identity.FOLDERREWIND_TEST_ID -or $identity.ProductVersion -ne $Version -or $identity.UpgradeCode -ne '{6BCE6B1A-ADA1-5399-91BD-238EBF552F6E}') { throw 'Candidate contains an unexpected MSI identity.' }
    $extract = Join-Path $output "inspection-$architecture"
    & $wix burn extract -acceptEula wix7 $exe -oba "$extract\ba" -o "$extract\payload" *> (Join-Path $output "extract-$architecture.log")
    if ($LASTEXITCODE) { throw 'Candidate Bundle extraction failed.' }
    [xml]$manifest = Get-Content -LiteralPath "$extract\ba\manifest.xml" -Raw
    $registration = $manifest.SelectSingleNode('//*[local-name()="Registration"]')
    $package = $manifest.SelectSingleNode('//*[local-name()="MsiPackage"]')
    $archVariable = $manifest.SelectSingleNode('//*[local-name()="Variable" and @Id="FolderRewindArchitecture"]')
    if ($manifest.SelectSingleNode('//*[local-name()="Variable" and @Id="FolderRewindTestFail"]')) { throw 'Production Bundle exposes fault injection.' }
    if (-not $registration -or $registration.Code -ne $registration.ProviderKey -or $registration.Version -ne $Version -or
        $package.ProductCode -ne $identity.ProductCode -or $archVariable.Value -ne $architecture) { throw 'Bundle identity/architecture contract differs from its MSI.' }
    $embedded = @(Get-ChildItem "$extract\payload" -Recurse -Filter '*.msi' -File)
    if ($embedded.Count -ne 1 -or (Get-FileHash $embedded[0].FullName).Hash -ne (Get-FileHash $msi).Hash) { throw 'Embedded MSI bytes differ from candidate MSI.' }
    $nativeBytes = [IO.File]::ReadAllBytes("$extract\ba\FolderRewind.BAFunctions.dll")
    if ([Text.Encoding]::ASCII.GetString($nativeBytes).Contains('FailForTesting')) { throw 'Production Bundle contains test-only native exports.' }
    $engine = New-Object -ComObject WindowsInstaller.Installer
    $database = $engine.OpenDatabase([IO.Path]::GetFullPath($msi),0)
    try {
        $summary = $database.SummaryInformation(0)
        $template = [string]$summary.Property(7)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary)
        $expectedPlatform = if ($architecture -eq 'arm64') { 'Arm64' } else { 'x64' }
        if ($template.Split(';')[0] -ine $expectedPlatform) { throw 'MSI platform differs from its candidate architecture.' }
        $query = $database.OpenView('SELECT `Action` FROM `CustomAction` WHERE `Target` = ''FailForTesting''')
        [void]$query.Execute(); if ($query.Fetch()) { throw 'Production MSI contains a fault entry point.' }; [void]$query.Close()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($query)
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine) }
    $file = Get-Item -LiteralPath $exe
    $hash = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
    $assets += @{name=$file.Name;size=$file.Length;architecture=$architecture;sha256=$hash;productCode=$identity.ProductCode;bundleCode=$registration.Code;signature=(Get-AuthenticodeSignature $exe).Status.ToString()}
    $internal += @{name=(Split-Path $msi -Leaf);architecture=$architecture;sha256=(Get-FileHash $msi).Hash.ToLowerInvariant();productCode=$identity.ProductCode}
    if (-not $IdentityOnly) { foreach ($id in (& "$PSScriptRoot\Get-InstallerRequiredScenarios.ps1")) {
        $scenarios += @{id=$id;architecture=$architecture;status='not-run';packageSha256=$hash;evidence='';reason='Evidence must be reviewed and supplied for these exact candidate bytes.'}
    } }
}
if (-not $IdentityOnly) { foreach ($id in @('app-tests','plugin-contract','runtime-tests','msix-regression','release-policy')) {
    $scenarios += @{id=$id;architecture='common';status='not-run';evidence='';reason='Not imported automatically.'}
} }
$report = @{schemaVersion=1;version=$Version;sourceRevision=$revision;sourceDirty=$dirty;sourceTreeSha256=$sourceHash;assets=$assets;internalAssets=$internal;scenarios=$scenarios}
if (-not $IdentityOnly) { $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'acceptance.json') -Encoding utf8 }
$report | Select-Object schemaVersion,version,sourceRevision,sourceDirty,sourceTreeSha256,assets,internalAssets | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'manifest.json') -Encoding utf8
Write-Host "Candidate identity recorded (IdentityOnly=$IdentityOnly): $output"
