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
        $relative = $name.Substring($prefix.Length)
        if ($entries.ContainsKey($relative)) { throw "Duplicate ZIP path: $relative" }
        $entries[$relative] = $entry
    }
    foreach ($required in @('OpenRevo.exe','OpenRevo.Background.exe','OpenRevo.dll','OpenRevo.Core.dll','Microsoft.UI.Xaml.dll','coreclr.dll','hostfxr.dll','README.md','docs/mvp-audit.md',"docs/v$version-audit.md",'docs/processing-chains.md','docs/reaudit.md','docs/ui-guidelines.md','docs/v1.0-acceptance.md','protocol/registry.json','protocol/device-profiles.json','protocol/processing-chains.json','protocol/live-evidence.json','protocol/mode-pilot.json','protocol/mode-evidence.json','protocol/stability-evidence.json','protocol/configuration-policy.json','build-manifest.json')) {
        if (!$entries.ContainsKey($required)) { throw "Missing portable dependency: $required" }
    }
    if (!$entries.ContainsKey('protocol/fan-boost.json')) { throw 'Missing fan control policy.' }
    foreach ($required in @('Assets/Lucide.ttf','Assets/OpenRevo.ico','Assets/OpenRevo.png','Assets/branding.json','ThirdPartyNotices/Lucide-LICENSE.txt','ThirdPartyNotices/lucide.json')) {
        if (!$entries.ContainsKey($required)) { throw "Missing icon asset or attribution: $required" }
    }
    $manifestStream = $entries['build-manifest.json'].Open()
    try { $manifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestStream)) }
    finally { $manifestStream.Dispose() }
    if ($manifestHash -ne (Get-FileHash (Join-Path $directory 'build-manifest.json')).Hash) { throw 'Archive manifest differs from verified local manifest.' }
    foreach ($binary in $manifest.binaries) {
        $stream = $entries[$binary.path].Open()
        try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($digest -ne $binary.sha256) { throw "Archived binary mismatch: $($binary.path)" }
    }
    foreach ($asset in @(@('ThirdPartyNotices/Lucide-LICENSE.txt','assets/lucide/LICENSE.txt'),@('ThirdPartyNotices/lucide.json','assets/lucide/manifest.json'),@('Assets/branding.json','assets/branding/manifest.json'))) {
        $stream = $entries[$asset[0]].Open()
        try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($digest -ne (Get-FileHash -LiteralPath (Join-Path $root $asset[1])).Hash) { throw "Lucide attribution mismatch: $($asset[0])" }
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
