@echo off
rem HashDoc release: build + package dist\HashDoc_v<version>.zip (program files only)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
if errorlevel 1 (
  echo.
  echo [HashDoc] Release FAILED - see messages above.
  exit /b 1
)
