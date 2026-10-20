@echo off
rem Double-click to start one server for every brought-up client build (profiles\all).
rem Ports start at 7100 and step by 10 per build (see docs/client-versions.md); kr-1256 relay is 7101.
rem Players live in memory until MySQL is configured in
rem profiles\all\local.patch.yaml. Stop the server with Ctrl+C in this window.
cd /d "%~dp0"
setlocal EnableExtensions
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update-deployment.ps1"
if errorlevel 1 goto :fail
artifacts\deploy\current\BubbleFighter.Fantasy.Host.exe run --profile all %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
:fail
echo Building the server failed; see the messages above.
pause
exit /b 1
