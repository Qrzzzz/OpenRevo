$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-OpenRevoVersion
$directory = Join-Path $root "artifacts\OpenRevo-$version-win-x64"
$archivePath = Join-Path $root "artifacts\OpenRevo-$version-win-x64.zip"
$expected = ((Get-Content -LiteralPath (Join-Path $root 'artifacts\SHA256SUMS.txt') -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) { throw 'ZIP SHA256 does not match SHA256SUMS.' }
$manifest = Get-Content -LiteralPath (Join-Path $directory 'build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.application -cne 'OpenRevo' -or $manifest.version -cne $version) { throw 'Portable manifest version mismatch.' }
foreach ($source in $manifest.sources) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $source.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.sha256) {
        throw "Source changed after build manifest: $($source.path)"
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $prefix = "OpenRevo-$version-win-x64/"
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if (!$name.StartsWith($prefix) -or ($name.Split('/') -contains '..')) { throw "Unexpected ZIP path: $name" }
        if ($name -match '(^|/)(\.local|decompiled|GCUBridge\.exe|GCUService\.exe|UWACPIDriver\.sys)(/|$)') { throw "Vendor/local evidence bundled: $name" }
        $entries[$name.Substring($prefix.Length)] = $entry
    }
    foreach ($required in @('OpenRevo.exe','OpenRevo.dll','OpenRevo.Core.dll','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll','README.md','docs/mvp-audit.md','protocol/live-evidence.json','build-manifest.json')) {
        if (!$entries.ContainsKey($required)) { throw "Missing portable dependency: $required" }
    }
    foreach ($binary in $manifest.binaries) {
        $stream = $entries[$binary.path].Open()
        try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($digest -ne $binary.sha256) { throw "Archived binary mismatch: $($binary.path)" }
    }
    $report = [pscustomobject]@{
        checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); archiveSha256=$actual
        sourceFiles=$manifest.sources.Count; applicationBinaries=$manifest.binaries.Count; entries=$zip.Entries.Count
        nativeWinUiAndDotnetDependenciesPresent=$true; vendorBinariesAbsent=$true
        scope='Archive and source identity only; not runtime or UI acceptance'
    }
    $output = Join-Path $root '.local\validation\package-verification.json'
    $report | ConvertTo-Json | Set-Content -LiteralPath $output -Encoding utf8
    Write-Output "Portable archive verified: $($zip.Entries.Count) entries, $($manifest.sources.Count) source identities, $($manifest.binaries.Count) application binary hashes."
    Write-Output "SHA256 $actual"
}
finally { $zip.Dispose() }
