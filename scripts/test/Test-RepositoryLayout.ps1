[CmdletBinding()]
param([string]$ResultPath)
. (Join-Path $PSScriptRoot '../common/Repository.ps1')
$repository = Get-FolderRewindRoot -Start $PSScriptRoot
if ($ResultPath) { $ResultPath = Resolve-FolderRewindPath -Path $ResultPath -Root $repository }
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $repository
try {
    $projects = @(Get-ChildItem src,tests,tools,benchmarks -Recurse -Filter '*.csproj' -File |
        Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]')
    $graph = @{}
    $core = [IO.Path]::GetFullPath((Join-Path $repository 'src/FolderRewind.Core/FolderRewind.Core.csproj'))
    $app = [IO.Path]::GetFullPath((Join-Path $repository 'src/FolderRewind/FolderRewind.csproj'))
    foreach ($project in $projects) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
        $references = @()
        foreach ($item in $xml.SelectNodes('//ProjectReference')) {
            $include = [string]$item.Include
            if ($include -eq '$(FolderRewindHostProject)') {
                $include = $xml.SelectSingleNode('//FolderRewindHostProject').InnerText
            }
            if ($include.Contains('$(')) { throw "Unresolved project dependency: $($project.Name): $include" }
            $reference = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $include))
            if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing project dependency: $reference" }
            if (-not $reference.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Host project depends on an external source checkout: $reference"
            }
            $references += $reference
        }
        $graph[$project.FullName] = $references
        $manifest = $xml.SelectSingleNode('//ApplicationManifest')
        if ($manifest -and -not $manifest.InnerText.Contains('$(') -and
            -not (Test-Path -LiteralPath (Join-Path $project.DirectoryName $manifest.InnerText) -PathType Leaf)) {
            throw "Missing application manifest: $($project.Name): $($manifest.InnerText)"
        }
        foreach ($item in $xml.SelectNodes('//Compile[@Include]')) {
            $include = [string]$item.Include
            if ($include -match 'FolderRewind\.Core[\\/]') { throw "Core source was linked into $($project.Name)" }
            if (-not $include.Contains('$(') -and -not $include.Contains('*') -and
                -not (Test-Path -LiteralPath (Join-Path $project.DirectoryName $include) -PathType Leaf)) {
                throw "Missing linked source: $($project.Name): $include"
            }
        }
    }
    $visited = @{}; $active = @{}
    function Visit-Project([string]$Project) {
        if ($active.ContainsKey($Project)) { throw "Project dependency cycle at $Project" }
        if ($visited.ContainsKey($Project)) { return }
        if (-not $graph.ContainsKey($Project)) { throw "Dependency is not a Host-owned project: $Project" }
        $active[$Project] = $true
        foreach ($reference in $graph[$Project]) { Visit-Project $reference }
        [void]$active.Remove($Project); $visited[$Project] = $true
    }
    foreach ($project in $projects) { Visit-Project $project.FullName }
    if ($graph[$core] -contains $app) { throw 'Core must not reference the application.' }
    $runtime = [IO.Path]::GetFullPath((Join-Path $repository 'src/FolderRewind.Plugin.Runtime/FolderRewind.Plugin.Runtime.csproj'))
    if ($graph[$runtime] -contains $core -or $graph[$runtime] -contains $app) { throw 'Runtime must not reference Core or the application.' }
    [xml]$coreProject = Get-Content -LiteralPath $core -Raw
    if ($coreProject.SelectNodes('//PackageReference[contains(@Include,"WindowsAppSDK") or contains(@Include,"WinUI")]').Count -gt 0 -or
        $coreProject.SelectSingleNode('//UseWinUI[text()="true"]')) { throw 'Core acquired a WinUI dependency.' }
    [xml]$hostLinks = Get-Content tests/FolderRewind.Tests/HostSourceLinks.props -Raw
    foreach ($item in $hostLinks.SelectNodes('//Compile')) {
        $include = [string]$item.Include
        if (-not $include.StartsWith('../../src/FolderRewind/',[StringComparison]::Ordinal) -or $include.Contains('*')) {
            throw "Host source link is not explicit application-owned source: $include"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $repository "tests/FolderRewind.Tests/$include") -PathType Leaf)) {
            throw "Missing remaining Host source: $include"
        }
    }
    [xml]$solution = Get-Content FolderRewind.slnx -Raw
    foreach ($item in $solution.SelectNodes('//Project')) {
        if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf)) { throw "Missing solution project: $($item.Path)" }
        if ($item.Path -like '*Core.Recovery.Tests*') { throw 'Fault-injection acceptance must use its separate entry point.' }
    }
    $sources = @(Get-ChildItem src,tests,tools,benchmarks,scripts,.github -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.xaml','.resw','.props','.ps1','.py','.yml') } |
        ForEach-Object { [IO.Path]::GetRelativePath($repository,$_.FullName) })
    $ignored = @($sources | git -c core.quotepath=false check-ignore --stdin)
    if ($ignored.Count) { throw "Source files are hidden by ignore rules: $($ignored -join ', ')" }
    $global:LASTEXITCODE = 0 # check-ignore returns 1 when no paths are ignored.
    $syntaxErrors = @()
    Get-ChildItem scripts,benchmarks,.github/scripts,tools -Recurse -Filter '*.ps1' -File |
        Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]' | ForEach-Object {
            $tokens = $null; $errors = $null
            [void][Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$errors)
            foreach ($errorItem in $errors) { $syntaxErrors += "$($_.FullName): $($errorItem.Message)" }
        }
    if ($syntaxErrors.Count) { throw ($syntaxErrors -join "`n") }
    $result = [ordered]@{passed=$true;projects=$projects.Count;remainingHostLinks=$hostLinks.SelectNodes('//Compile').Count;visibleSources=$sources.Count}
    if ($ResultPath) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ResultPath))
        $result | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8
    }
    $result | ConvertTo-Json
} finally { Pop-Location }
