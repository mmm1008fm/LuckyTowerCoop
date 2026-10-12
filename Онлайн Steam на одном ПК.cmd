@echo off
rem Хост: основной Steam. Клиент: второй аккаунт Steam в песочнице Sandboxie-Plus (ящик Steam2).
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\local-online.ps1" -Steam %*
if errorlevel 1 pause
