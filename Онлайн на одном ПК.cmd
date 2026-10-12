@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\local-online.ps1" %*
if errorlevel 1 pause
