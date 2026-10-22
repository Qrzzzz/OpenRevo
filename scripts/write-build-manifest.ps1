param([Parameter(Mandatory=$true)][string]$OutputDirectory, [Parameter(Mandatory=$true)][string]$Sdk)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-OpenRevoVersion
$filePaths = @('Directory.Build.props','global.json','protocol/device-profiles.json','protocol/registry.json','protocol/processing-chains.json','protocol/mode-pilot.json','protocol/mode-evidence.json','protocol/stability-evidence.json','protocol/configuration-policy.json')
foreach ($project in @('OpenRevo.App','OpenRevo.Core')) {
    $directory = Join-Path $root ('src\' + $project)
    $filePaths += @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Extension -in @('.cs','.csproj','.xaml','.manifest') -or $_.Name -eq 'packages.lock.json' } |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) })
}
$files = foreach ($relative in ($filePaths | Sort-Object)) {
    [pscustomobject]@{ path=$relative.Replace('\','/'); sha256=(Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$binaries = foreach ($name in @('OpenRevo.exe','OpenRevo.dll','OpenRevo.Core.dll')) {
    [pscustomobject]@{ path=$name; sha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[pscustomobject]@{
    application='OpenRevo'; version=$version; createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    sdk=((& $Sdk --version) | Select-Object -Last 1)
    configuration='Release'; architecture='x64'; selfContained=$true; windowsAppSdkSelfContained=$true
    sources=@($files)
    binaries=@($binaries)
    executableSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'OpenRevo.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-manifest.json') -Encoding utf8
