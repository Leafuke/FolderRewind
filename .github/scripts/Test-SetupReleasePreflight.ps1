[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SourceRevision,
      [Parameter(Mandatory)][string]$DownloadDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$' -or $SourceRevision -notmatch '^[a-f0-9]{40}$') { throw 'Invalid release identity.' }
$number = [version]$Version
$tag = "v$($number.Major).$($number.Minor).$($number.Build)"
$expected = @('x64','arm64' | ForEach-Object { "FolderRewind_${Version}_Setup_$_.exe"; "FolderRewind_${Version}_Setup_$_.exe.sha256" })
$listJson = gh release list --limit 1000 --json tagName
if ($LASTEXITCODE) { throw 'Cannot verify existing releases.' }
$list = @($listJson | ConvertFrom-Json)
if ($list.Count -ge 1000) { throw 'Release listing is truncated.' }
$refsJson = gh api "repos/{owner}/{repo}/git/matching-refs/tags/$tag"
if ($LASTEXITCODE) { throw 'Cannot verify release tag.' }
$refs = @($refsJson | ConvertFrom-Json | Where-Object ref -CEQ "refs/tags/$tag")
if ($refs.Count) {
    $commit = gh api "repos/{owner}/{repo}/commits/$tag" --jq .sha
    if ($LASTEXITCODE -or $commit -cne $SourceRevision) { throw 'Existing tag points to another source revision.' }
}
if (-not @($list | Where-Object tagName -CEQ $tag).Count) { return [pscustomobject]@{alreadyPublished=$false;url=''} }
$json = gh release view $tag --json assets,isDraft,isPrerelease,targetCommitish,url
if ($LASTEXITCODE) { throw 'Cannot inspect existing release.' }
$release = $json | ConvertFrom-Json
if ($release.isPrerelease) { throw 'Existing release is a prerelease.' }
if ($release.isDraft) {
    if ($release.targetCommitish -cne $SourceRevision) { throw 'Existing draft targets another source revision.' }
} elseif (-not $refs.Count) { throw 'Published release has no verifiable tag.' }
$names = @()
foreach ($asset in $release.assets) {
    if ($asset.name -cnotin $expected -or $asset.name -cin $names) { throw 'Unexpected or duplicate existing release asset.' }
    if (-not $asset.PSObject.Properties['digest'] -or $asset.digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Existing release asset has no verifiable digest.' }
    $names += $asset.name
}
if ($release.isDraft) { return [pscustomobject]@{alreadyPublished=$false;url=$release.url} }
if ($names.Count -ne 4) { throw 'Published release attachment set is incomplete.' }
# Only download the tiny checksum attachments; GitHub supplies binary digests.
$directory = Join-Path ([IO.Path]::GetFullPath($DownloadDirectory)) ([guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
gh release download $tag --pattern '*.sha256' --dir $directory
if ($LASTEXITCODE) { throw 'Cannot verify published checksum files.' }
foreach ($architecture in @('x64','arm64')) {
    $name = "FolderRewind_${Version}_Setup_$architecture.exe"
    $binary = @($release.assets | Where-Object name -CEQ $name)[0]
    $checksum = @($release.assets | Where-Object name -CEQ "$name.sha256")[0]
    $path = Join-Path $directory "$name.sha256"
    if ('sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $checksum.digest) { throw 'Published checksum attachment digest differs.' }
    if ((Get-Content -LiteralPath $path -Raw).Trim() -cne "$($binary.digest.Substring(7)) *$name") { throw 'Published checksum does not match binary digest.' }
}
[pscustomobject]@{alreadyPublished=$true;url=$release.url}
