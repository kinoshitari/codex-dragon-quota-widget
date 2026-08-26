$pluginRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $pluginRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Widget executable not found: $executable"
}

$running = Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $running) {
    Write-Output "Codex dragon quota widget is already running (PID $($running.Id))."
    exit 0
}

$process = Start-Process -FilePath $executable -PassThru
Write-Output "Started Codex dragon quota widget (PID $($process.Id))."
