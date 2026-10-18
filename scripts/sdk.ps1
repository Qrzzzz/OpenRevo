function Get-OpenRevoSdk {
    param([string]$DotnetPath)
    $candidates = @($DotnetPath, $env:OPENREVO_DOTNET_EXE, (Join-Path $PSScriptRoot '..\.local\dotnet\dotnet.exe'))
    $system = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($system) { $candidates += $system.Source }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) { return (Resolve-Path -LiteralPath $candidate).Path }
        }
    }
    throw '需要 .NET 10 SDK。请通过 -DotnetPath 或 OPENREVO_DOTNET_EXE 指定 SDK 中的 dotnet.exe；仅有运行时不够。'
}

function Invoke-OpenRevoDotnet {
    param([string]$Sdk, [string[]]$ToolArguments, [string]$Log)
    & $Sdk @ToolArguments *> $Log
    $status = $LASTEXITCODE
    $content = Get-Content -LiteralPath $Log -Raw
    if ($content.Length -gt 12000) { Write-Output $content.Substring($content.Length - 12000); Write-Output '[日志已截取末尾 12000 字符，完整记录见日志文件]' }
    else { Write-Output $content }
    if ($status -ne 0) { throw "dotnet 命令失败（退出码 $status），日志：$Log" }
}
