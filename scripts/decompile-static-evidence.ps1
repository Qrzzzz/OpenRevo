param([string]$IlspyDll = (Join-Path $PSScriptRoot '..\.local\tools\ilspycmd\tools\net8.0\any\ilspycmd.dll'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (!(Test-Path -LiteralPath $IlspyDll)) { throw 'Install ILSpyCmd 9.1.0.7988 locally first; see docs/static-analysis.md.' }
$targets = @{
    GCUBridge = 'AiStoneService\GCUBridge.exe'
    GCUService = 'AiStoneService\MyControlCenter\GCUService.exe'
    GCUServicePlugin = 'AiStoneService\MyControlCenter\GCUServicePlugin.dll'
    Tray = 'Win32\SystrayComponent.exe'
}
foreach ($name in ($targets.Keys | Sort-Object)) {
    $inputFile = Join-Path $root ('.local\evidence\' + $targets[$name])
    $outputDir = Join-Path $root ('.local\decompiled\' + $name)
    $log = Join-Path $root ('.local\' + $name + '-decompile.log')
    & dotnet $IlspyDll --disable-updatecheck -p -o $outputDir $inputFile *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -TotalCount 25
        throw "ILSpy failed for $name with exit code $code"
    }
    Write-Output "$name : static decompilation completed (does not imply recovered method bodies)."
}
