@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0convert.ps1" %*
set "rc=%errorlevel%"
if not "%rc%"=="0" echo Script exit code: %rc%
echo.
pause
exit /b %rc%
