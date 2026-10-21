param([string]$DotnetPath, [switch]$Publish)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
. (Join-Path $PSScriptRoot 'version.ps1')
$version = Get-OpenRevoVersion
$sdk = Get-OpenRevoSdk $DotnetPath
$root = Split-Path $PSScriptRoot
$logs = Join-Path $root '.local\validation'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$project = Join-Path $root 'src\OpenRevo.App\OpenRevo.App.csproj'
$baseOutput = '-p:BaseOutputPath=' + (Join-Path $root '.local\build\')
Invoke-OpenRevoDotnet -Sdk $sdk -ToolArguments @('build',$project,'-c','Release','-p:Platform=x64','--nologo','-p:RestoreLockedMode=true',$baseOutput) -Log (Join-Path $logs 'build.log')
if ($Publish) {
    $output = Join-Path $root "artifacts\OpenRevo-$version-win-x64"
    if (Test-Path -LiteralPath $output) { throw "发布目录已存在，为避免混合旧文件，请先另存：$output" }
    Invoke-OpenRevoDotnet -Sdk $sdk -ToolArguments @('publish',$project,'-c','Release','-p:Platform=x64','--nologo','-p:RestoreLockedMode=true',$baseOutput,'-o',$output) -Log (Join-Path $logs 'publish.log')
    Copy-Item -LiteralPath (Join-Path $root 'docs\portable-readme.md') -Destination (Join-Path $output 'README.md')
    $docs = Join-Path $output 'docs'
    $protocol = Join-Path $output 'protocol'
    New-Item -ItemType Directory -Path $docs,$protocol -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\mvp-audit.md') -Destination $docs
    foreach ($name in @("v$version-audit.md",'processing-chains.md')) {
        Copy-Item -LiteralPath (Join-Path $root "docs\$name") -Destination $docs
    }
    foreach ($name in @('live-evidence.json','registry.json','device-profiles.json','processing-chains.json','mode-pilot.json','mode-evidence.json','stability-evidence.json')) {
        Copy-Item -LiteralPath (Join-Path $root "protocol\$name") -Destination $protocol
    }
    & (Join-Path $PSScriptRoot 'write-build-manifest.ps1') -OutputDirectory $output -Sdk $sdk
    $zip = Join-Path $root "artifacts\OpenRevo-$version-win-x64.zip"
    Compress-Archive -LiteralPath $output -DestinationPath $zip
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $root 'artifacts\SHA256SUMS.txt') -Encoding ascii
    Write-Output "已生成本地便携包：$zip"
}
