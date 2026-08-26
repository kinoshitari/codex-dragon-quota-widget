[CmdletBinding()]
param(
    [ValidateSet('Codex', 'Agy')]
    [string]$Source = 'Codex'
)

$pluginRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $pluginRoot 'bin\win-x64\CodexDragonQuotaWidget.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Widget executable not found: $executable"
}

$outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-dragon-usage-{0}.json" -f [guid]::NewGuid().ToString('N'))
$diagArg = if ($Source -eq 'Agy') { '--diagnostics-agy' } else { '--diagnostics' }
try {
    Start-Process -FilePath $executable -ArgumentList @($diagArg, $outputPath) -Wait
    Get-Content -LiteralPath $outputPath -Raw
}
finally {
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
}
