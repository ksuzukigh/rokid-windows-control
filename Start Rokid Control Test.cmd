@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start-sandbox-test.ps1"
if errorlevel 1 (
  echo.
  echo Rokid Control test could not be started.
  pause
)
