@echo off
rem Double-click to stop the ReconAI demo (web app and AI service).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-demo.ps1" -Stop
timeout /t 3 >nul
