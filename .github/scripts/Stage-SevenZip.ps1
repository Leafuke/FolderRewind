param([Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$arch = $Platform.ToLowerInvariant()
$version = 'v26.02-v1.5.7-R2'
$hashes = @{x64='22dc4608d911d7c831437b969db66a42d0477cd3f5d93987cae9f59774857fc1';arm64='9749af751056e203286175527e906acc1ad0ed7b0ccc1a22de05141d77170961'}
if (-not $OutputDirectory) { $OutputDirectory = "$PSScriptRoot\..\..\artifacts\sevenzip\$version\$arch" }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$installer = Join-Path $destination "7z26.02-zstd-$arch.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest "https://github.com/mcmilk/7-Zip-zstd/releases/download/$version/7z26.02-zstd-$arch.exe" -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ine $hashes[$arch]) { throw "7-Zip $arch installer SHA256 mismatch." }
$payload = Join-Path $destination 'payload'
if (-not (Test-Path -LiteralPath (Join-Path $payload '7za.exe'))) {
    $extractor = "$PSScriptRoot\..\..\7za.exe"
    if (-not (Test-Path -LiteralPath $extractor)) { $extractor = (Get-Command 7z -ErrorAction Stop).Source }
    # The pinned installer is a 7z self-extracting archive. Extracting avoids
    # running an ARM64 binary on an x64 CI worker and writes no machine settings.
    & $extractor x $installer "-o$payload" -y 7za.exe License.txt | Out-Host
    if ($LASTEXITCODE) { throw "7-Zip extraction failed: $LASTEXITCODE" }
}
$sevenzip = Join-Path $payload '7za.exe'
if (-not (Test-Path -LiteralPath $sevenzip)) { throw "Architecture-specific standalone 7za.exe not found: $sevenzip" }
$stream = [IO.File]::OpenRead($sevenzip)
try {
    $reader = [IO.BinaryReader]::new($stream)
    $stream.Position = 0x3c; $offset = $reader.ReadInt32()
    $stream.Position = $offset + 4; $machine = $reader.ReadUInt16()
    $expected = if ($arch -eq 'arm64') { 0xaa64 } else { 0x8664 }
    if ($machine -ne $expected) { throw "7za.exe architecture mismatch: $machine" }
} finally { $stream.Dispose() }
@{version=$version;architecture=$arch;installerSha256=$hashes[$arch];payloadSha256=(Get-FileHash $sevenzip -Algorithm SHA256).Hash.ToLowerInvariant()} |
    ConvertTo-Json | Set-Content (Join-Path $destination 'provenance.json') -Encoding utf8
Write-Output $sevenzip
