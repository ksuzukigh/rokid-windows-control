@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\enable-sandbox.ps1"
if errorlevel 1 (
  echo.
  echo Windows Sandbox could not be enabled.
  pause
)
