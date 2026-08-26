@echo off
setlocal
set "APP=%~dp0bin\win-x64\CodexDragonQuotaWidget.exe"
if not exist "%APP%" (
  echo Widget executable not found.
  pause
  exit /b 1
)
start "" "%APP%"
exit /b 0
