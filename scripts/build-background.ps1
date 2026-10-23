param([string]$OutputDirectory, [switch]$MenuPreview)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root '.local\background' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw '轻量后台构建需要 Visual Studio C++ x64 工具。' }
$compilerRoot = (Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$kits = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdkVersion = (Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h') } | Sort-Object Name -Descending | Select-Object -First 1).Name
$compiler = Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe'
$savedInclude = $env:INCLUDE; $savedLib = $env:LIB; $savedPath = $env:PATH
try {
    $env:PATH = (Join-Path $kits "bin\$sdkVersion\x64") + ';' + (Split-Path $compiler) + ';' + $savedPath
    $env:INCLUDE = @((Join-Path $compilerRoot 'include'),(Join-Path $kits "Include\$sdkVersion\ucrt"),(Join-Path $kits "Include\$sdkVersion\shared"),(Join-Path $kits "Include\$sdkVersion\um")) -join ';'
    $env:LIB = @((Join-Path $compilerRoot 'lib\x64'),(Join-Path $kits "Lib\$sdkVersion\ucrt\x64"),(Join-Path $kits "Lib\$sdkVersion\um\x64")) -join ';'
    $target = Join-Path $OutputDirectory 'OpenRevo.Background.exe'
    $objectDirectory = Join-Path $root '.local\background-obj'
    New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null
    $log = Join-Path $objectDirectory 'build.log'
    $manifest = Join-Path $root 'src\OpenRevo.Background\background.manifest'
    $iconResource = Join-Path $objectDirectory 'icons.res'
    & rc.exe /nologo "/I$(Join-Path $root 'src\OpenRevo.App\Assets')" "/fo$iconResource" (Join-Path $root 'src\OpenRevo.Background\background.rc') *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -TotalCount 30; throw 'Lucide icon resource compilation failed.' }
    & $compiler /nologo /O1 /W4 /WX /MT /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE "/Fe:$target" "/Fo:$(Join-Path $objectDirectory 'background.obj')" (Join-Path $root 'src\OpenRevo.Background\main.cpp') /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /CETCOMPAT /MANIFEST:EMBED "/MANIFESTINPUT:$manifest" $iconResource user32.lib shell32.lib gdi32.lib *> $log
    $status = $LASTEXITCODE
    $content = Get-Content -LiteralPath $log -Raw
    Write-Output $content.Substring(0, [Math]::Min(10000, $content.Length))
    if ($status -ne 0) { throw "轻量后台构建失败（$status）：$log" }
    $testExecutable = Join-Path $objectDirectory 'native-tests.exe'
    & $compiler /nologo /O1 /W4 /WX /MT /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE "/Fe:$testExecutable" "/Fo:$(Join-Path $objectDirectory 'native-tests.obj')" (Join-Path $root 'src\OpenRevo.Background\native-tests.cpp') /link /SUBSYSTEM:CONSOLE /MANIFEST:EMBED "/MANIFESTINPUT:$manifest" $iconResource user32.lib gdi32.lib *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -TotalCount 40; throw 'Native tray test build failed.' }
    & $testExecutable *> $log
    $status = $LASTEXITCODE
    Get-Content -LiteralPath $log -TotalCount 30
    if ($status -ne 0) { throw 'Native tray checks failed.' }
    if ($MenuPreview) {
        $previewExecutable = Join-Path $OutputDirectory 'OpenRevo.MenuPreview.exe'
        & $compiler /nologo /O1 /W4 /WX /MT /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE "/Fe:$previewExecutable" "/Fo:$(Join-Path $objectDirectory 'menu-preview.obj')" (Join-Path $root 'src\OpenRevo.Background\menu-preview.cpp') /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED "/MANIFESTINPUT:$manifest" user32.lib *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -TotalCount 40; throw 'Tray menu preview build failed.' }
    }
    Write-Output "已构建轻量后台：$target"
}
finally { $env:INCLUDE = $savedInclude; $env:LIB = $savedLib; $env:PATH = $savedPath }
