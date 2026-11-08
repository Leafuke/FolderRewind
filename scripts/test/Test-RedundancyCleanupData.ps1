[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot, [string]$ResumeSession, [string]$WorldSourceManifest)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunRoot)
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$data = Join-Path $run 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$session = if ($ResumeSession) { [IO.Path]::GetFullPath($ResumeSession) } else { Join-Path $data ('d-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
if (!$session.StartsWith($data.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Data session must be within this run.' }
New-Item -ItemType Directory -Path $session -Force | Out-Null
$identity = Get-Content (Join-Path $run 'candidate/identity.json') -Raw | ConvertFrom-Json
$exe = Join-Path $identity.faultDirectory 'FolderRewind.exe'
$package = Join-Path $run 'candidate/MineRewind.frplugin'
$rclone = $env:FOLDERREWIND_TEST_RCLONE
if (!$rclone) { $rclone = 'D:\Programs\Software-Test\rclone\rclone.exe' }
function Manifest([string]$Root) {
    $items = @(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'World input contains a link.' }
    @($items | Where-Object { !$_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
}
function Digest($Items) {
    $text = ($Items | ForEach-Object { "$($_.path)|$($_.length)|$($_.sha256)" }) -join "`n"
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}
function Write-Json($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
if (!$WorldSourceManifest) { $WorldSourceManifest = Join-Path $run 'data-inputs.json' }
# Machine-specific originals belong in the ignored run manifest: {worlds:[{name,path}]}.
$worlds = @((Get-Content -LiteralPath $WorldSourceManifest -Raw | ConvertFrom-Json).worlds)
if ($worlds.Count -ne 3 -or @($worlds.name | Select-Object -Unique).Count -ne 3 -or
    @($worlds | Where-Object { $_.name -notmatch '^[a-zA-Z0-9_-]+$' -or ![IO.Path]::IsPathFullyQualified($_.path) }).Count) { throw 'Expected three distinct, named absolute real-world inputs.' }
$results = [Collections.Generic.List[object]]::new()
function Fixture([hashtable]$Request, [string]$Name, [string]$ResultName) {
    $requestPath = Join-Path $session "$Name.json"
    $result = Join-Path $Request.Workspace $ResultName
    if ($ResumeSession -and (Test-Path -LiteralPath $result)) {
        $previous = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
        $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        if (!$report.Success -or $previous.PackageSha256 -ne $Request.PackageSha256 -or $previous.Phase -ne $Request.Phase -or
            !(Test-Path (Join-Path $Request.Workspace 'acceptance-owner'))) { throw "Unverifiable resumed evidence: $Name" }
        $results.Add(@{name=$Name;result=$result;sha256=(Get-FileHash -LiteralPath $result).Hash;resumed=$true})
        Write-Host "Verified existing final-candidate evidence: $Name"
        return
    }
    Write-Json $Request $requestPath
    $process = Start-Process -FilePath $exe -ArgumentList '--native-artifact-acceptance', ('"' + $requestPath + '"') -WindowStyle Hidden -PassThru
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalMinutes -gt 8) { $process.Kill(); throw "Owned fixture timed out: $Name" }
    }
    $errorPath = Join-Path $Request.Workspace ($Request.Phase + '-error.txt')
    if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result)) { throw "Fixture failed: $Name ($($process.ExitCode))" }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if (!$report.Success) { throw "Fixture did not pass: $Name" }
    $results.Add(@{name=$Name;result=$result;sha256=(Get-FileHash -LiteralPath $result).Hash;seconds=$timer.Elapsed.TotalSeconds})
    Write-Host "Data case passed: $Name"
}
$packageHash = (Get-FileHash -LiteralPath $package).Hash
$ownerPath = Join-Path $session 'candidate-owner.json'
$ownerHash = (Get-FileHash "$run/candidate/identity.json").Hash
if ($ResumeSession) {
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    if ($owner.candidateIdentitySha256 -ne $ownerHash) { throw 'Data session belongs to a different candidate.' }
} else { Write-Json @{candidateIdentitySha256=$ownerHash} $ownerPath }
$matrixRootsPath = Join-Path $session 'matrix-roots.json'
$matrixRoots = @{}
if (Test-Path -LiteralPath $matrixRootsPath) {
    $savedRoots = Get-Content -LiteralPath $matrixRootsPath -Raw | ConvertFrom-Json -AsHashtable
    if ($savedRoots.candidateIdentitySha256 -ne $ownerHash) { throw 'Crash matrices belong to a different candidate.' }
    $matrixRoots = $savedRoots.paths
}
function Crash-Matrix([string]$Name, [string]$TemplatePath, [int]$ExpectedCases) {
    $output = $matrixRoots[$Name]
    $complete = $false
    if ($output) {
        if (!$output.StartsWith($data.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Crash output escaped this run.' }
        $resultPath = Join-Path $output 'results.json'
        if ($ResumeSession -and (Test-Path -LiteralPath $resultPath)) {
            $items = @(Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json)
            $complete = $items.Count -eq $ExpectedCases -and @($items | Where-Object { !$_.Success }).Count -eq 0
        }
    }
    if (!$complete) {
        # Keep fixture paths short enough for SQLite and its journal suffixes. Retain failed
        # workspaces for diagnosis instead of moving their persisted absolute references.
        $output = Join-Path $data ('m-' + $Name.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
        $matrixRoots[$Name] = $output
        Write-Json @{candidateIdentitySha256=$ownerHash;paths=$matrixRoots} $matrixRootsPath
        & "$PSScriptRoot/Invoke-NativeArtifactIntegrationCrashMatrix.ps1" -Executable $exe -TemplateRequest $TemplatePath -OutputDirectory $output -Matrix $Name
    }
    $resultPath = Join-Path $output 'results.json'
    $items = @(Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json)
    if ($items.Count -ne $ExpectedCases -or @($items | Where-Object { !$_.Success }).Count) { throw "Incomplete $Name crash matrix." }
    $results.Add(@{name="crash-$Name";result=$resultPath;sha256=(Get-FileHash -LiteralPath $resultPath).Hash;cases=$items.Count;resumed=$complete})
}
$sourceEvidence = @()
try {
    foreach ($world in $worlds) {
        $original = Manifest $world.path
        $copy = Join-Path $session ('inputs/' + $world.name)
        New-Item -ItemType Directory -Path $copy -Force | Out-Null
        # Enumerate explicitly, including hidden entries.
        if (!$ResumeSession) { Get-ChildItem -LiteralPath $world.path -Force | Copy-Item -Destination $copy -Recurse -Force }
        $copied = Manifest $copy
        if ((Digest $original) -ne (Digest $copied)) { throw "World copy differs: $($world.name)" }
        $evidence = @{name=$world.name;original=$world.path;copy=$copy;digest=(Digest $original);files=$original}
        $sourceEvidence += $evidence
        Write-Json $evidence (Join-Path $session ($world.name + '-source.json'))
        $sourceId = if ($ResumeSession) { (Get-Content (Join-Path $session ($world.name + '-capture.json')) -Raw | ConvertFrom-Json).SourceId } else { [guid]::NewGuid().ToString('D') }
        $request = @{Workspace=(Join-Path $session $world.name);Phase='capture';Source=$copy;SourceId=$sourceId;
            Package=$package;PackageSha256=$packageHash;SevenZip="$repo/7za.exe";Incremental=$true;Iterations=1;Encrypted=$false}
        Fixture $request ($world.name + '-capture') 'p3-capture-result.json'
        $request.Phase = 'redundancy-data'
        Fixture $request ($world.name + '-deletions-checkpoint') 'redundancy-data-result.json'
        $request.Phase = 'restart'; $request.Iterations = 3
        Fixture $request ($world.name + '-restart') 'p3-restart-result.json'
    }
    $template = @{Workspace='';Phase='capture';Source=$sourceEvidence[-1].copy;SourceId=[guid]::NewGuid().ToString('D');
        Package=$package;PackageSha256=$packageHash;SevenZip="$repo/7za.exe";Incremental=$true;Iterations=1;Encrypted=$false;Rclone=$rclone}
    $templatePath = Join-Path $session 'crash-template.json'
    Write-Json $template $templatePath
    $cloudAuthority = $template.Clone(); $cloudAuthority.Workspace = Join-Path $session 'cloud-authority'
    Fixture $cloudAuthority 'cloud-authority-capture' 'p3-capture-result.json'
    $cloudAuthority.Phase = 'redundancy-cloud'
    Fixture $cloudAuthority 'cloud-authority' 'redundancy-cloud-result.json'
    foreach ($matrix in @('Capture','Delta','Cloud')) {
        $expected = @{Capture=12;Delta=4;Cloud=8}[$matrix]
        Crash-Matrix $matrix $templatePath $expected
    }
    $migrationExport = $request.Clone(); $migrationExport.Phase = 'migration-export'
    Fixture $migrationExport 'migration-export' 'migration-export-result.json'
    $migrationTemplate = $template.Clone()
    $migrationTemplate.Source = Join-Path $migrationExport.Workspace 'source'
    $migrationTemplate.MigrationBundle = Join-Path $migrationExport.Workspace 'integration.frhistory'
    $migrationTemplatePath = Join-Path $session 'migration-template.json'
    Write-Json $migrationTemplate $migrationTemplatePath
    Crash-Matrix 'Migration' $migrationTemplatePath 4
    $lifecycle = $template.Clone(); $lifecycle.Workspace = Join-Path $session 'branch-merge'; $lifecycle.Lifecycle=$true; $lifecycle.Remove('Rclone')
    Fixture $lifecycle 'branch-merge' 'p3-capture-result.json'
    # The coordination fixture deliberately renames level.dat to level.dat_old.
    # Real worlds already contain that file, so prepare a dedicated input copy
    # without the old metadata before exercising this controlled failure.
    $coordinationInput = Join-Path $session 'coordination-input'
    if (!(Test-Path -LiteralPath $coordinationInput)) {
        New-Item -ItemType Directory -Path $coordinationInput | Out-Null
        Get-ChildItem -LiteralPath $sourceEvidence[-1].copy -Force | Copy-Item -Destination $coordinationInput -Recurse
    }
    $oldMetadata = Join-Path $coordinationInput 'level.dat_old'
    if (Test-Path -LiteralPath $oldMetadata -PathType Leaf) { Remove-Item -LiteralPath $oldMetadata }
    Write-Json @{input=$coordinationInput;originalCopy=$sourceEvidence[-1].copy;removedForControlledRename='level.dat_old';files=(Manifest $coordinationInput)} (Join-Path $session 'coordination-input.json')
    foreach ($kind in @('Lifecycle','Prefer','Require','Review')) {
        $request = $template.Clone(); $request.Source=$coordinationInput; $request.Remove('Rclone'); $request.Workspace = Join-Path $session "coordination-$kind"
        if ($kind -eq 'Lifecycle') { $request.CoordinationLifecycle = $true; $reportName='coordination-lifecycle-result.json' }
        elseif ($kind -eq 'Review') { $request.CoordinationReview = $true; $reportName='coordination-review-result.json' }
        else { $request.CoordinationDeadline=$kind; $reportName='coordination-deadline-result.json' }
        if ($ResumeSession -and (Test-Path -LiteralPath $request.Workspace) -and !(Test-Path (Join-Path $request.Workspace $reportName))) {
            $request.Workspace = Join-Path $session ("coordination-$kind-" + [guid]::NewGuid().ToString('N').Substring(0,4))
        }
        Fixture $request "coordination-$kind" $reportName
    }
    Write-Json @{success=$true;candidateIdentitySha256=$ownerHash;sourceManifestSha256=(Get-FileHash -LiteralPath $WorldSourceManifest).Hash;
        session=$session;worlds=$sourceEvidence;cases=$results.ToArray();onlineGame='Not requested as mandatory; no active companion connection'} "$data/summary.json"
} finally {
    foreach ($world in $sourceEvidence) {
        $after = Manifest $world.original
        Write-Json @{name=$world.name;digest=(Digest $after);files=$after} (Join-Path $session ($world.name + '-original-after.json'))
        if ((Digest $after) -ne $world.digest) { throw "Original world changed during acceptance: $($world.name)" }
    }
}
