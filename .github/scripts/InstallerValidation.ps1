# Shared identity and evidence helpers. Dot-source; never installs anything.
function Get-ValidationGuid([string]$Seed) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("FolderRewind.Validation/v2/$Seed"))
    '{' + [guid]::new([byte[]]$bytes[0..15]).ToString().ToUpperInvariant() + '}'
}
function Get-MsiIdentity([string]$Path) {
    $engine = New-Object -ComObject WindowsInstaller.Installer
    $database = $engine.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
    try {
        $identity = @{}
        foreach ($name in @('ProductCode','UpgradeCode','ProductVersion','ProductName','FOLDERREWIND_TEST_ID')) {
            $query = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $name + '''')
            try { [void]$query.Execute(); $row = $query.Fetch(); $identity[$name] = if ($row) { $row.StringData(1) } else { '' } }
            finally { [void]$query.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($query) }
        }
        return $identity
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    }
}
function Assert-ValidationMsi([string]$Path, [string]$TestId) {
    $identity = Get-MsiIdentity $Path
    if ($TestId -notmatch '^[A-Za-z0-9-]{1,32}$' -or $identity.FOLDERREWIND_TEST_ID -cne $TestId -or
        $identity.UpgradeCode -ne (Get-ValidationGuid "$TestId/msi") -or
        $identity.ProductCode -eq '{00000000-0000-0000-0000-000000000000}' -or
        -not $identity.ProductName.StartsWith('FolderRewind MSI Validation ')) {
        throw "Refusing MSI outside this validation run: $Path"
    }
    return $identity
}
function Get-PayloadSnapshot([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $snapshot = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse | Sort-Object FullName) {
        $snapshot[$file.FullName.Substring($base.Length)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $snapshot
}
function Assert-PayloadSnapshot([string]$Directory, $Expected) {
    $actual = Get-PayloadSnapshot $Directory
    if ($actual.Count -ne $Expected.Count) { throw 'Payload file count changed.' }
    foreach ($name in $Expected.Keys) {
        if ($actual[$name] -cne $Expected[$name]) { throw "Payload not restored: $name" }
    }
}
function Export-InstallerResults($Results, [string]$Path, [string]$Package, [string]$Architecture = 'x64') {
    $hash = if ($Package -and (Test-Path -LiteralPath $Package)) { (Get-FileHash -LiteralPath $Package).Hash.ToLowerInvariant() } else { $null }
    foreach ($result in $Results) {
        if (-not $result.ContainsKey('status')) { $result.status = if ($result.passed) { 'passed' } else { 'failed' } }
        $result.architecture = $Architecture
        $result.packageSha256 = $hash
        if (-not $result.ContainsKey('exitCode')) { $result.exitCode = $null }
        if (-not $result.ContainsKey('evidence')) { $result.evidence = if ($result.ContainsKey('log')) { $result.log } else { $Path } }
    }
    ConvertTo-Json -InputObject @($Results.ToArray()) -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}
