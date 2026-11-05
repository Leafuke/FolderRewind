[CmdletBinding()]
param(
    [ValidateRange(16, 100000)][int]$Rows = 20000,
    [ValidateRange(3, 100)][int]$Operations = 10,
    [ValidateRange(1, 10)][int]$Rounds = 3,
    [string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $sourceRoot 'artifacts\merge-decision-incremental-20261105'
}
$ArtifactRoot = [System.IO.Path]::GetFullPath($ArtifactRoot)
$buildRoot = Join-Path $ArtifactRoot 'build-probe'
$logRoot = Join-Path $ArtifactRoot 'logs'
$null = New-Item -ItemType Directory -Path $logRoot -Force
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)

# Run in an exclusive CPU window. The probe deliberately does not flush OS caches.
# Build the formal Core and the linked production Host code into isolated outputs.
& dotnet build (Join-Path $PSScriptRoot 'MergeDecisionProbe.csproj') -c Release --artifacts-path $buildRoot 2>&1 |
    Tee-Object -FilePath (Join-Path $logRoot "$runId-build.log")
if ($LASTEXITCODE -ne 0) { throw "Probe build failed with exit code $LASTEXITCODE." }

$assembly = Join-Path $buildRoot 'bin\MergeDecisionProbe\release\FolderRewind.Tests.dll'
& dotnet $assembly --rows $Rows --operations $Operations --rounds $Rounds `
    --output (Join-Path $ArtifactRoot 'results') --source-root $sourceRoot 2>&1 |
    Tee-Object -FilePath (Join-Path $logRoot "$runId-matrix.log")
if ($LASTEXITCODE -ne 0) { throw "Probe failed with exit code $LASTEXITCODE." }
