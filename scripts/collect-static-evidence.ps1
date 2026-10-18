param(
    [string]$InstallRoot = 'C:\Program Files\OEM\机械革命控制中心',
    [string]$PackageRoot = 'C:\Program Files\WindowsApps\ControlCenter3_5.56.51.49_x64__wrbgcf7aesyd8',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\.local\evidence')
)
$ErrorActionPreference = 'Stop'
# Only copy installed code and driver metadata. Never load the vendor assemblies,
# run their executables, read user profiles/logs, or open a device/IPC endpoint.
$targets = @(
    @{ Root=$InstallRoot; Relative='AiStoneService\GCUBridge.exe' },
    @{ Root=$InstallRoot; Relative='AiStoneService\MyControlCenter\GCUService.exe' },
    @{ Root=$InstallRoot; Relative='AiStoneService\MyControlCenter\GCUServicePlugin.dll' },
    @{ Root=$InstallRoot; Relative='AiStoneService\MyControlCenter\ACPIDriverDll.dll' },
    @{ Root=$InstallRoot; Relative='AiStoneService\MyControlCenter\DisplayInfo.dll' },
    @{ Root=$InstallRoot; Relative='AiStoneService\MyControlCenter\NVControlSetting.dll' },
    @{ Root=$InstallRoot; Relative='UWACPIDriver\UWACPIDriver.sys' },
    @{ Root=$InstallRoot; Relative='UWACPIDriver\UWACPIDriver.inf' },
    @{ Root=$PackageRoot; Relative='GamingCenter3_Cross.dll' },
    @{ Root=$PackageRoot; Relative='Win32\SystrayComponent.exe' },
    @{ Root=$PackageRoot; Relative='AppxManifest.xml' }
)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$manifest = foreach ($target in $targets) {
    $source = Join-Path $target.Root $target.Relative
    $destination = Join-Path $OutputRoot $target.Relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    $before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $source -Destination $destination
    $after = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($before -ne $after) { throw "Snapshot hash mismatch: $source" }
    $file = Get-Item -LiteralPath $destination
    [pscustomobject]@{
        source = $source; relative = $target.Relative; sha256 = $after
        bytes = $file.Length; fileVersion = $file.VersionInfo.FileVersion
    }
}
[pscustomobject]@{
    capturedAt = [DateTimeOffset]::Now.ToString('o')
    method = 'Static file copy only; no vendor code executed'
    files = @($manifest)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'manifest.json') -Encoding utf8
Write-Output "Collected $($targets.Count) files; SHA256 verified."
$settings = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\OEM\GamingCenter2' -ErrorAction SilentlyContinue
$machine = Get-CimInstance Win32_ComputerSystem
[pscustomobject]@{
    capturedAt = [DateTimeOffset]::Now.ToString('o')
    manufacturer = $machine.Manufacturer
    model = $machine.Model
    oemSettings = @{ BridgeType=$settings.BridgeType; CustomizeTarget=$settings.CustomizeTarget; ProjectID=$settings.ProjectID }
    listeners = @(Get-NetTCPConnection -State Listen -LocalPort 13688 -ErrorAction SilentlyContinue |
        Select-Object LocalAddress,LocalPort,OwningProcess)
    bridgeService = @(Get-CimInstance Win32_Service -Filter "Name='GCUBridge'" |
        Select-Object Name,State,PathName,ProcessId)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot 'host-observation.json') -Encoding utf8
