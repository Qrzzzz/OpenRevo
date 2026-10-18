param([string]$DotnetPath, [switch]$LiveProbe)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
$sdk = Get-OpenRevoSdk $DotnetPath
$root = Split-Path $PSScriptRoot
$logs = Join-Path $root '.local\validation'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Invoke-OpenRevoDotnet -Sdk $sdk -ToolArguments @('run','--project',(Join-Path $root 'tests\OpenRevo.Tests\OpenRevo.Tests.csproj'),'-c','Release','--no-launch-profile','-p:RestoreLockedMode=true') -Log (Join-Path $logs 'core-tests.log')
if ($LiveProbe) {
    Invoke-OpenRevoDotnet -Sdk $sdk -ToolArguments @('run','--project',(Join-Path $root 'src\OpenRevo.Probe\OpenRevo.Probe.csproj'),'-c','Release','--no-launch-profile','--','20',(Join-Path $root '.local\evidence\csharp-observation.json')) -Log (Join-Path $logs 'live-probe.log')
}
