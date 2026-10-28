@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Build and checks completed. Output: artifacts\bin\ClaudeLauncher.exe
pause
