[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigDirectory = 'C:\Users\admin\AppData\Local\Packages\Leafuke.FolderRewind_xh6qzj9v1wvnm\LocalState\FolderRewind'
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($ConfigDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$taskConfigPath = Join-Path $taskRoot 'config.json'
$taskHistoryPath = Join-Path $taskRoot 'history'

function Assert-TaskChildPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target is outside the selected configuration directory: $resolved"
    }
}

function Assert-AppStopped {
    if (Get-Process -Name FolderRewind -ErrorAction SilentlyContinue) {
        throw 'Please close FolderRewind before archiving its development history.'
    }
}

function Get-ConfigPathSegment([string]$Id) {
    $identity = [Guid]::Empty
    if ([Guid]::TryParse($Id, [ref]$identity) -and $identity -ne [Guid]::Empty) { return $identity.ToString('N') }
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return 'c-' + ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Id.Trim().ToUpperInvariant())))).Replace('-', '').ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

if (-not (Test-Path -LiteralPath $taskConfigPath -PathType Leaf)) { throw 'config.json was not found.' }
if (-not (Test-Path -LiteralPath $taskHistoryPath -PathType Container)) { throw 'No development history directory was found.' }
Assert-TaskChildPath $taskHistoryPath
if ((Get-Item -LiteralPath $taskHistoryPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The history root must not be a link.' }
if (-not $WhatIfPreference) { Assert-AppStopped }

foreach ($taskDescriptorFile in Get-ChildItem -LiteralPath $taskHistoryPath -Recurse -File -Filter 'repository.json') {
    $taskOldDescriptor = Get-Content -Raw -LiteralPath $taskDescriptorFile.FullName | ConvertFrom-Json
    if ($taskOldDescriptor.magic -ne 'FolderRewindHistoryRepository' -or $taskOldDescriptor.formatVersion -ne 1) {
        throw "This tool only resets old development format 1 repositories: $($taskDescriptorFile.FullName)"
    }
}

foreach ($taskJournal in Get-ChildItem -LiteralPath $taskHistoryPath -Recurse -File -Filter '*journal.json') {
    $taskState = Get-Content -Raw -LiteralPath $taskJournal.FullName | ConvertFrom-Json
    $taskExpectedPhase = if ($taskJournal.Name -eq 'restore-journal.json') { 4 } elseif ($taskJournal.Name -eq 'journal.json') { 3 } else { throw "Unknown transaction journal: $($taskJournal.FullName)" }
    if ($null -eq $taskState.phase -or ($taskState.phase -ne $taskExpectedPhase -and $taskState.phase -ne 'Complete')) {
        throw "An unfinished transaction must be recovered by the old app first: $($taskJournal.FullName)"
    }
}

$taskConfig = Get-Content -Raw -LiteralPath $taskConfigPath | ConvertFrom-Json
if ($null -eq $taskConfig.BackupConfigs) { throw 'BackupConfigs is missing from config.json.' }
$taskArchive = Join-Path $taskRoot ('development-history-archives\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$taskPreparedHistory = Join-Path $taskRoot ('.history-reset-' + [Guid]::NewGuid().ToString('N'))
$taskPreparedConfig = Join-Path $taskRoot ('.config-history-reset-' + [Guid]::NewGuid().ToString('N') + '.json')
foreach ($taskPath in @($taskArchive, $taskPreparedHistory, $taskPreparedConfig)) { Assert-TaskChildPath $taskPath }

if (-not $PSCmdlet.ShouldProcess($taskRoot, 'Archive development History, preserve all archives and sources, and initialize format 2 repositories')) { return }

$taskMovedOld = $false
$taskMovedNew = $false
$taskCommitted = $false
$taskEncoding = [Text.UTF8Encoding]::new($false)
try {
    New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
    Copy-Item -LiteralPath $taskConfigPath -Destination (Join-Path $taskArchive 'config.before.json')
    New-Item -ItemType Directory -Path $taskPreparedHistory | Out-Null
    foreach ($taskBackupConfig in $taskConfig.BackupConfigs) {
        if ([string]::IsNullOrWhiteSpace($taskBackupConfig.Id)) { throw 'A backup project has no stable identity.' }
        $taskSegment = Get-ConfigPathSegment $taskBackupConfig.Id
        $taskRepository = Join-Path $taskPreparedHistory $taskSegment
        Assert-TaskChildPath $taskRepository
        New-Item -ItemType Directory -Path $taskRepository | Out-Null
        foreach ($taskSubdirectory in @('packs', 'index', 'local-state', 'transactions', 'quarantine')) {
            New-Item -ItemType Directory -Path (Join-Path $taskRepository $taskSubdirectory) | Out-Null
        }
        $taskNormalizedId = if ($taskSegment.StartsWith('c-')) { $taskBackupConfig.Id.Trim().ToUpperInvariant() } else { $taskSegment }
        $taskDescriptor = [ordered]@{ magic = 'FolderRewindHistoryRepository'; formatVersion = 2; configId = $taskNormalizedId }
        [IO.File]::WriteAllText((Join-Path $taskRepository 'repository.json'), ($taskDescriptor | ConvertTo-Json -Compress), $taskEncoding)
        $taskBackupConfig | Add-Member -MemberType NoteProperty -Name HistoryRepositoryBinding -Value ([pscustomobject]@{ FormatVersion = 2 }) -Force
    }
    # Bind the prepared empty repositories explicitly so released-version import cannot rediscover old records.
    [IO.File]::WriteAllText($taskPreparedConfig, ($taskConfig | ConvertTo-Json -Depth 100), $taskEncoding)
    Assert-AppStopped
    $taskArchivedHistory = Join-Path $taskArchive 'history'
    Assert-TaskChildPath $taskArchivedHistory
    Move-Item -LiteralPath $taskHistoryPath -Destination $taskArchivedHistory
    $taskMovedOld = $true
    Move-Item -LiteralPath $taskPreparedHistory -Destination $taskHistoryPath
    $taskMovedNew = $true
    [IO.File]::Replace($taskPreparedConfig, $taskConfigPath, (Join-Path $taskArchive 'config.replace-backup.json'))
    $taskCommitted = $true
    Write-Output "Development metadata archived to: $taskArchive"
    Write-Output 'Configuration, backup archives and source files were preserved. New backups will populate the new history.'
}
catch {
    if (-not $taskCommitted -and $taskMovedOld) {
        if ($taskMovedNew) {
            $taskIncomplete = Join-Path $taskArchive 'new-history-incomplete'
            Assert-TaskChildPath $taskIncomplete
            Move-Item -LiteralPath $taskHistoryPath -Destination $taskIncomplete
        }
        Assert-TaskChildPath $taskHistoryPath
        Move-Item -LiteralPath (Join-Path $taskArchive 'history') -Destination $taskHistoryPath
    }
    throw
}
