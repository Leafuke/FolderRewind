param([Parameter(Mandatory)][string]$OutputRoot, [Parameter(Mandatory)][string]$LevelDat)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Corpus output must be new.' }
New-Item -ItemType Directory -Path (Join-Path $root 'region') | Out-Null
Copy-Item -LiteralPath $LevelDat -Destination (Join-Path $root 'level.dat')
# Independent literal NBT, no MineRewind reader/writer generates these region bytes.
[byte[]]$nbt = 10,0,0,3,0,1,118,0,0,0,42,0
$rx = 0
foreach ($compression in 1,2,3,99) {
    foreach ($external in @($false,$true)) {
        $buffer = [IO.MemoryStream]::new()
        if ($compression -eq 1) { $encoder = [IO.Compression.GZipStream]::new($buffer, [IO.Compression.CompressionLevel]::Fastest, $true) }
        elseif ($compression -eq 2) { $encoder = [IO.Compression.ZLibStream]::new($buffer, [IO.Compression.CompressionLevel]::Fastest, $true) }
        else { $encoder = $null }
        if ($null -ne $encoder) { $encoder.Write($nbt); $encoder.Dispose(); $payload = $buffer.ToArray() }
        else { $payload = $nbt }
        $buffer.Dispose()
        $region = [byte[]]::new(16397)
        for ($i=8192; $i -lt $region.Length; $i++) { $region[$i] = ($i * 31 + 7) % 256 }
        $region[2] = 3; $region[3] = 1 # slot 0 -> sector 3, one allocated sector
        $region[4099] = 42 # timestamp is intentionally independent of payload
        $length = if ($external) { 1 } else { $payload.Length + 1 }
        $region[12288] = ($length -shr 24) -band 255; $region[12289] = ($length -shr 16) -band 255
        $region[12290] = ($length -shr 8) -band 255; $region[12291] = $length -band 255
        $region[12292] = if ($external) { $compression -bor 128 } else { $compression }
        if ($external) { [IO.File]::WriteAllBytes((Join-Path $root "region/c.$($rx * 32).0.mcc"), $payload) }
        else { [Array]::Copy($payload, 0, $region, 12293, $payload.Length) }
        [IO.File]::WriteAllBytes((Join-Path $root "region/r.$rx.0.mca"), $region)
        $rx++
    }
}
[IO.File]::WriteAllBytes((Join-Path $root 'region/r.50.0.mca'), [byte[]]::new(0))
[IO.File]::WriteAllBytes((Join-Path $root 'region/c.999.999.mcc'), $nbt)
[IO.File]::WriteAllBytes((Join-Path $root 'passthrough.bin'), [byte[]](0,255,42,17))
Write-Output $root
