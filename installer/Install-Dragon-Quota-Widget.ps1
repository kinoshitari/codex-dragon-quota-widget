param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadZip,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$pluginName = 'codex-dragon-quota-widget'
$installRoot = Join-Path $env:USERPROFILE "plugins\$pluginName"
$executable = Join-Path $installRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
$marketplacePath = Join-Path $env:USERPROFILE '.agents\plugins\marketplace.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CodexDragonWidgetInstall-" + [guid]::NewGuid().ToString('N'))

function New-WidgetShortcut([string]$path, [string]$target, [string]$arguments = '') {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $target
    $shortcut.Arguments = $arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $target
    $shortcut.Description = '傻龙插件'
    $shortcut.IconLocation = "$target,0"
    $shortcut.Save()
}

try {
    if (-not (Test-Path -LiteralPath $PayloadZip)) {
        throw "安装载荷不存在：$PayloadZip"
    }

    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Expand-Archive -LiteralPath $PayloadZip -DestinationPath $tempRoot -Force
    $payloadRoot = Join-Path $tempRoot $pluginName
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot '.codex-plugin\plugin.json'))) {
        throw '安装载荷结构无效。'
    }

    Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $payloadRoot -Force | Copy-Item -Destination $installRoot -Recurse -Force

    if (-not (Test-Path -LiteralPath $executable)) {
        throw "挂件程序安装失败：$executable"
    }

    $marketplaceDirectory = Split-Path -Parent $marketplacePath
    New-Item -ItemType Directory -Path $marketplaceDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $marketplacePath) {
        $marketplace = Get-Content -LiteralPath $marketplacePath -Raw | ConvertFrom-Json
    }
    else {
        $marketplace = [pscustomobject][ordered]@{
            name = 'personal'
            interface = [pscustomobject][ordered]@{ displayName = 'Personal' }
            plugins = @()
        }
    }

    $entry = [pscustomobject][ordered]@{
        name = $pluginName
        source = [pscustomobject][ordered]@{ source = 'local'; path = "./plugins/$pluginName" }
        policy = [pscustomobject][ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
        category = 'Productivity'
    }
    $plugins = @($marketplace.plugins | Where-Object { $_.name -ne $pluginName }) + @($entry)
    $marketplace.plugins = $plugins
    $marketplace | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $marketplacePath -Encoding UTF8

    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'CodexDragonQuotaWidget' -Value ('"{0}" --watch-codex' -f $executable)

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '傻龙插件.lnk'
    $startMenuDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) '傻龙插件'
    New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
    New-WidgetShortcut $desktopShortcut $executable
    New-WidgetShortcut (Join-Path $startMenuDirectory '启动挂件.lnk') $executable
    New-WidgetShortcut (Join-Path $startMenuDirectory '卸载挂件.lnk') 'powershell.exe' ("-NoProfile -ExecutionPolicy Bypass -File `"{0}`"" -f (Join-Path $installRoot 'installer\Uninstall-Dragon-Quota-Widget.ps1'))

    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $codex) {
        try { & $codex.Source plugin add "$pluginName@personal" | Out-Host }
        catch { Write-Warning "Codex 插件注册未完成，但挂件程序已经安装：$($_.Exception.Message)" }
    }
    else {
        Write-Warning '未找到 Codex CLI；挂件程序已经安装，稍后可在 Codex 中重新安装插件。'
    }

    if (-not $NoLaunch) {
        Start-Process -FilePath $executable -ArgumentList '--watch-codex'
    }

    Write-Host "安装完成：$installRoot" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
