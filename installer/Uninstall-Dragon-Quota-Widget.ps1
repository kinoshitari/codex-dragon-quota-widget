$ErrorActionPreference = 'Stop'
$pluginName = 'codex-dragon-quota-widget'
$installRoot = Join-Path $env:USERPROFILE "plugins\$pluginName"
$marketplacePath = Join-Path $env:USERPROFILE '.agents\plugins\marketplace.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path $runKey -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) '傻龙插件.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) '傻龙插件') -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $marketplacePath) {
    $marketplace = Get-Content -LiteralPath $marketplacePath -Raw | ConvertFrom-Json
    $marketplace.plugins = @($marketplace.plugins | Where-Object { $_.name -ne $pluginName })
    $marketplace | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $marketplacePath -Encoding UTF8
}

Write-Host '傻龙插件已卸载。' -ForegroundColor Green
$escapedRoot = $installRoot.Replace('"', '""')
Start-Process -FilePath 'cmd.exe' -WindowStyle Hidden -ArgumentList "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q `"$escapedRoot`""
