function Get-FolderRewindRoot {
    [CmdletBinding()]
    param([string]$Start = $PSScriptRoot)
    $directory = [IO.DirectoryInfo][IO.Path]::GetFullPath($Start)
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'FolderRewind.slnx') -PathType Leaf) { return $directory.FullName }
        $directory = $directory.Parent
    }
    throw "FolderRewind.slnx was not found above $Start"
}

function Resolve-FolderRewindPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path, $Root)
}
