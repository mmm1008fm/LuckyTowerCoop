@echo off
rem Variant: main Steam. Client: second Steam account in a Sandboxie-Plus sandbox (box Steam2).
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\local-online.ps1" -Steam %*
if errorlevel 1 pause
