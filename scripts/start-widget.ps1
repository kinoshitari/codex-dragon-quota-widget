$pluginRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $pluginRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Widget executable not found: $executable"
}

$running = Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue | Select-Object -First 1
$recoveredHiddenInstance = $false
if ($null -ne $running -and $running.MainWindowHandle -eq 0) {
    # A watcher or tray instance can own the single-instance mutex without
    # exposing any window. Restarting only this widget process is the most
    # reliable recovery path for the user's explicit "open" action.
    Stop-Process -Id $running.Id -Force
    Wait-Process -Id $running.Id -ErrorAction SilentlyContinue
    $running = $null
    $recoveredHiddenInstance = $true
}

$process = Start-Process -FilePath $executable -PassThru
if ($recoveredHiddenInstance) {
    Write-Output "Recovered a hidden widget instance and started a visible one (PID $($process.Id))."
} elseif ($null -ne $running) {
    Write-Output "Requested the running widget to show itself (PID $($running.Id))."
} else {
    Write-Output "Started Codex dragon quota widget (PID $($process.Id))."
}
