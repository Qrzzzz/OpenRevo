param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory=$true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-OpenRevoVersion
if ($Tag -cne "v$version") { throw 'Tag does not match Directory.Build.props.' }
$archiveName = "OpenRevo-$version-win-x64.zip"
$archive = Join-Path $root "artifacts/$archiveName"
$checksums = Join-Path $root 'artifacts/SHA256SUMS.txt'
$notes = Join-Path $root "docs/releases/$Tag.md"
foreach ($file in @($archive,$checksums,$notes)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release input: $file" }
}
$checksumText = (Get-Content -LiteralPath $checksums -Raw).Trim()
if ($checksumText -cnotmatch "^([a-f0-9]{64})  $([regex]::Escape($archiveName))$") { throw 'Unexpected checksum file format.' }
$expectedHash = $Matches[1]
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'Release archive checksum mismatch.'
}
if ($CheckOnly) { Write-Output "Release inputs verified for $Repository $Tag ($expectedHash)."; return }

function Invoke-GitHub {
    param([string[]]$Arguments)
    $output = & gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh failed: $($output -join [Environment]::NewLine)" }
    return ($output -join [Environment]::NewLine)
}
$head = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve build commit.' }
$tagCommit = & git -C $root rev-parse "$Tag^{commit}"
if ($LASTEXITCODE -ne 0 -or $head -ne $tagCommit) { throw 'Release checkout must be the tagged commit.' }

# Create a draft first; publish only after downloading and hashing both uploaded assets.
# Existing releases are never overwritten. A failed run leaves its draft for inspection.
Invoke-GitHub -Arguments @('release','create',$Tag,'--repo',$Repository,'--verify-tag','--draft','--title',"OpenRevo $version",'--notes-file',$notes,$archive,$checksums) | Write-Output
$draft = (Invoke-GitHub -Arguments @('release','view',$Tag,'--repo',$Repository,'--json','id,isDraft,isPrerelease,tagName,assets,url')) | ConvertFrom-Json
if (!$draft.isDraft -or $draft.isPrerelease -or $draft.tagName -cne $Tag -or $draft.assets.Count -ne 2) {
    throw 'Unexpected draft release identity or asset count.'
}
foreach ($file in @($archive,$checksums)) {
    $item = Get-Item -LiteralPath $file
    $asset = @($draft.assets | Where-Object name -CEQ $item.Name)
    if ($asset.Count -ne 1 -or $asset[0].size -ne $item.Length) { throw "Release asset missing or wrong size: $($item.Name)" }
}
$download = Join-Path $root ('.local/release-verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download -Force | Out-Null
Invoke-GitHub -Arguments @('release','download',$Tag,'--repo',$Repository,'--dir',$download,'--pattern',$archiveName,'--pattern','SHA256SUMS.txt') | Write-Output
foreach ($file in @($archive,$checksums)) {
    $remote = Join-Path $download ([IO.Path]::GetFileName($file))
    if ((Get-FileHash -LiteralPath $file).Hash -ne (Get-FileHash -LiteralPath $remote).Hash) {
        throw "Uploaded asset hash mismatch: $remote"
    }
}
Invoke-GitHub -Arguments @('release','edit',$Tag,'--repo',$Repository,'--draft=false','--latest') | Write-Output
$published = (Invoke-GitHub -Arguments @('release','view',$Tag,'--repo',$Repository,'--json','id,isDraft,isPrerelease,tagName,assets,url')) | ConvertFrom-Json
if ($published.id -ne $draft.id -or $published.isDraft -or $published.isPrerelease -or $published.tagName -cne $Tag -or $published.assets.Count -ne 2) {
    throw 'Published release verification failed.'
}
$published | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $download 'published-release.json') -Encoding utf8
Write-Output "Published and verified $($published.url); archive SHA256 $expectedHash"
