@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Thin launcher for start-server.ps1. Forwards every argument; does not print .env.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1" %*
exit /b %ERRORLEVEL%
