param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadZip,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$pluginName = 'codex-dragon-quota-widget'
$installRoot = Join-Path $env:USERPROFILE "plugins\$pluginName"
$installParent = Split-Path -Parent $installRoot
$executable = Join-Path $installRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
$marketplacePath = Join-Path $env:USERPROFILE '.agents\plugins\marketplace.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$operationId = [guid]::NewGuid().ToString('N')
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "CodexDragonWidgetInstall-$operationId"
$stagingRoot = Join-Path $installParent "$pluginName.staging-$operationId"
$backupRoot = Join-Path $installParent "$pluginName.backup-$operationId"
$marketplaceTemp = $null
$marketplaceOriginal = $null
$marketplaceExisted = Test-Path -LiteralPath $marketplacePath
$marketplaceChanged = $false
$hadRunValue = $false
$originalRunValue = $null
$runValueChanged = $false
$oldInstallBackedUp = $false
$newInstallActivated = $false
$wasRunning = $false
$succeeded = $false
$startMenuDirectory = $null
$startMenuExisted = $false
$desktopShortcut = $null
$desktopShortcutExisted = $false

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

function Write-Utf8FileAtomically([string]$path, [string]$content) {
    $directory = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $script:marketplaceTemp = Join-Path $directory (".marketplace-{0}.tmp" -f [guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($script:marketplaceTemp, $content, [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $path) {
        [System.IO.File]::Move($script:marketplaceTemp, $path, $true)
    }
    else {
        Move-Item -LiteralPath $script:marketplaceTemp -Destination $path
    }
    $script:marketplaceTemp = $null
}

function Remove-DirectoryWithRetry([string]$path) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $path)) { return }
            Remove-Item -LiteralPath $path -Recurse -Force
            if (-not (Test-Path -LiteralPath $path)) { return }
        }
        catch {
            if ($attempt -eq 10) { throw }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "无法在重试后移除目录：$path"
}

try {
    $payloadPath = (Get-Item -LiteralPath $PayloadZip).FullName
    New-Item -ItemType Directory -Path $tempRoot, $installParent -Force | Out-Null
    Expand-Archive -LiteralPath $payloadPath -DestinationPath $tempRoot -Force
    $payloadRoot = Join-Path $tempRoot $pluginName
    $payloadManifestPath = Join-Path $payloadRoot '.codex-plugin\plugin.json'
    $payloadExecutable = Join-Path $payloadRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
    if (-not (Test-Path -LiteralPath $payloadManifestPath) -or -not (Test-Path -LiteralPath $payloadExecutable)) {
        throw '安装载荷结构无效：缺少插件清单或主程序。'
    }

    $manifest = Get-Content -LiteralPath $payloadManifestPath -Raw | ConvertFrom-Json
    if ($manifest.name -ne $pluginName -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        throw '安装载荷清单无效：插件名称或版本缺失。'
    }

    Copy-Item -LiteralPath $payloadRoot -Destination $stagingRoot -Recurse -Force
    $stagedExecutable = Join-Path $stagingRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
    if (-not (Test-Path -LiteralPath $stagedExecutable) -or (Get-Item -LiteralPath $stagedExecutable).Length -le 0) {
        throw '安装载荷验证失败：主程序为空或不可读。'
    }

    $marketplaceDirectory = Split-Path -Parent $marketplacePath
    New-Item -ItemType Directory -Path $marketplaceDirectory -Force | Out-Null
    if ($marketplaceExisted) {
        $marketplaceOriginal = Get-Content -LiteralPath $marketplacePath -Raw
        $marketplace = $marketplaceOriginal | ConvertFrom-Json
    }
    else {
        $marketplace = [pscustomobject][ordered]@{
            name = 'personal'
            interface = [pscustomobject][ordered]@{ displayName = 'Personal' }
            plugins = @()
        }
    }
    if ($marketplace.PSObject.Properties.Name -notcontains 'plugins') {
        $marketplace | Add-Member -NotePropertyName plugins -NotePropertyValue @()
    }

    $entry = [pscustomobject][ordered]@{
        name = $pluginName
        source = [pscustomobject][ordered]@{ source = 'local'; path = "./plugins/$pluginName" }
        policy = [pscustomobject][ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
        category = 'Productivity'
    }
    $marketplace.plugins = @($marketplace.plugins | Where-Object { $_.name -ne $pluginName }) + @($entry)
    $marketplaceJson = $marketplace | ConvertTo-Json -Depth 12

    try {
        $originalRunValue = Get-ItemPropertyValue -Path $runKey -Name 'CodexDragonQuotaWidget' -ErrorAction Stop
        $hadRunValue = $true
    }
    catch {
        $hadRunValue = $false
    }

    $programsDirectory = [Environment]::GetFolderPath('Programs')
    if ([string]::IsNullOrWhiteSpace($programsDirectory)) {
        throw '开始菜单目录不可用。'
    }
    $startMenuDirectory = Join-Path $programsDirectory '傻龙插件'
    $startMenuExisted = Test-Path -LiteralPath $startMenuDirectory
    $desktopDirectory = [Environment]::GetFolderPath('Desktop')
    if (-not [string]::IsNullOrWhiteSpace($desktopDirectory)) {
        $desktopShortcut = Join-Path $desktopDirectory '傻龙插件.lnk'
        $desktopShortcutExisted = Test-Path -LiteralPath $desktopShortcut
    }

    $runningProcesses = @(Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue)
    $wasRunning = $runningProcesses.Count -gt 0
    if ($wasRunning) {
        $runningProcesses | Stop-Process -Force
        $runningProcesses | Wait-Process -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $installRoot) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $installRoot -Force | Copy-Item -Destination $backupRoot -Recurse -Force
        $backupManifest = Join-Path $backupRoot '.codex-plugin\plugin.json'
        $backupExecutable = Join-Path $backupRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
        if (-not (Test-Path -LiteralPath $backupManifest) -or -not (Test-Path -LiteralPath $backupExecutable)) {
            throw '旧版备份验证失败，安装已中止。'
        }
        $oldInstallBackedUp = $true
        Remove-DirectoryWithRetry $installRoot
    }
    Move-Item -LiteralPath $stagingRoot -Destination $installRoot
    $newInstallActivated = $true

    Write-Utf8FileAtomically $marketplacePath $marketplaceJson
    $marketplaceChanged = $true
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'CodexDragonQuotaWidget' -Value ('"{0}" --watch-codex' -f $executable)
    $runValueChanged = $true

    New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
    New-WidgetShortcut (Join-Path $startMenuDirectory '启动挂件.lnk') $executable
    New-WidgetShortcut (Join-Path $startMenuDirectory '卸载挂件.lnk') 'powershell.exe' ("-NoProfile -ExecutionPolicy Bypass -File `"{0}`"" -f (Join-Path $installRoot 'installer\Uninstall-Dragon-Quota-Widget.ps1'))

    if ($desktopShortcut -and (Test-Path -LiteralPath $desktopDirectory)) {
        New-WidgetShortcut $desktopShortcut $executable
    }
    else {
        Write-Warning '桌面目录不可用，已跳过桌面快捷方式。'
    }

    if ($oldInstallBackedUp -and (Test-Path -LiteralPath $backupRoot)) {
        try { Remove-DirectoryWithRetry $backupRoot }
        catch { Write-Warning "新版本已安装，但旧版临时备份清理失败：$($_.Exception.Message)" }
        $oldInstallBackedUp = $false
    }
    $succeeded = $true
    $newInstallActivated = $false

    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $codex) {
        try { & $codex.Source plugin add "$pluginName@personal" | Out-Host }
        catch { Write-Warning "Codex 插件注册未完成，但挂件程序已经安装：$($_.Exception.Message)" }
    }
    else {
        Write-Warning '未找到 Codex CLI；挂件程序已经安装，稍后可在 Codex 中重新安装插件。'
    }

    if (-not $NoLaunch) {
        try { Start-Process -FilePath $executable -ArgumentList '--watch-codex' }
        catch { Write-Warning "安装完成，但自动启动失败：$($_.Exception.Message)" }
    }

    Write-Host "安装完成：$installRoot（版本 $($manifest.version)）" -ForegroundColor Green
}
catch {
    $failure = $_
    if ($newInstallActivated -and (Test-Path -LiteralPath $installRoot)) {
        try { Remove-DirectoryWithRetry $installRoot }
        catch { Write-Warning "清理失败的新安装目录时遇到错误：$($_.Exception.Message)" }
    }
    if ($oldInstallBackedUp -and (Test-Path -LiteralPath $backupRoot)) {
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $backupRoot -Force | Copy-Item -Destination $installRoot -Recurse -Force
        if (-not (Test-Path -LiteralPath $executable)) {
            Write-Warning '安装失败且旧版自动恢复不完整，请保留备份目录。'
        }
    }

    try {
        if ($marketplaceChanged -and $marketplaceExisted) {
            [System.IO.File]::WriteAllText($marketplacePath, [string]$marketplaceOriginal, [System.Text.UTF8Encoding]::new($false))
        }
        elseif ($marketplaceChanged -and (Test-Path -LiteralPath $marketplacePath)) {
            Remove-Item -LiteralPath $marketplacePath -Force
        }
    }
    catch { Write-Warning "恢复 marketplace 失败：$($_.Exception.Message)" }

    try {
        if ($runValueChanged -and $hadRunValue) {
            New-Item -Path $runKey -Force | Out-Null
            Set-ItemProperty -Path $runKey -Name 'CodexDragonQuotaWidget' -Value $originalRunValue
        }
        elseif ($runValueChanged) {
            Remove-ItemProperty -Path $runKey -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue
        }
    }
    catch { Write-Warning "恢复启动项失败：$($_.Exception.Message)" }

    if (-not $startMenuExisted -and $startMenuDirectory -and (Test-Path -LiteralPath $startMenuDirectory)) {
        Remove-Item -LiteralPath $startMenuDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $desktopShortcutExisted -and $desktopShortcut -and (Test-Path -LiteralPath $desktopShortcut)) {
        Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
    }

    if ($wasRunning -and (Test-Path -LiteralPath $executable)) {
        try { Start-Process -FilePath $executable -ArgumentList '--watch-codex' }
        catch { Write-Warning "旧版挂件已恢复，但自动重启失败：$($_.Exception.Message)" }
    }
    throw $failure
}
finally {
    if ($marketplaceTemp -and (Test-Path -LiteralPath $marketplaceTemp)) {
        Remove-Item -LiteralPath $marketplaceTemp -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($succeeded -and (Test-Path -LiteralPath $backupRoot)) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
