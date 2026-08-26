$processes = @(Get-Process -Name 'CodexDragonQuotaWidget' -ErrorAction SilentlyContinue)
if ($processes.Count -eq 0) {
    Write-Output 'Codex dragon quota widget is not running.'
    exit 0
}

$processes | Stop-Process
Write-Output "Stopped Codex dragon quota widget ($($processes.Count) process(es))."
