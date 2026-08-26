@echo off
setlocal
title 傻龙插件 Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Dragon-Quota-Widget.ps1" -PayloadZip "%~dp0codex-dragon-quota-widget-payload.zip"
if errorlevel 1 (
  echo.
  echo Installation failed. See the error above.
  pause
  exit /b 1
)
echo.
echo Installation completed.
timeout /t 3 >nul
exit /b 0
