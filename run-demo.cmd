@echo off
rem Double-click to start the ReconAI demo and open it in your browser.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-demo.ps1" %*
if errorlevel 1 (
  echo.
  echo Something went wrong - the message above says what. Press any key to close.
  pause >nul
)
