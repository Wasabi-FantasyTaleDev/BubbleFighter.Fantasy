@echo off
rem Refreshes stale binaries, then starts a profile from the current deployment.
rem   start-server.bat                  run kr-1256 from artifacts\deploy\current
rem   start-server.bat cn-717           run another profile
rem   start-server.bat kr-812 rebuild   rebuild from source into artifacts\deploy\current, then run
rem Stop a running server with Ctrl+C in its window; this script never kills other processes.
cd /d "%~dp0"
setlocal EnableExtensions DisableDelayedExpansion
set "PROFILE=%~1"
if "%PROFILE%"=="" set "PROFILE=kr-1256"
set "REFRESH="
if /i "%~2"=="rebuild" set "REFRESH=-ForceRebuild"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update-deployment.ps1" %REFRESH%
if errorlevel 1 goto :fail
if not exist artifacts\deploy\current\BubbleFighter.Fantasy.Host.exe (
  echo No deploy yet. Run: start-server.bat %PROFILE% rebuild
  pause
  exit /b 1
)
artifacts\deploy\current\BubbleFighter.Fantasy.Host.exe run --profile "%PROFILE%"
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
:fail
echo Deployment refresh failed. No existing server was stopped.
pause
exit /b 1
