@echo off
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0SetupEnvironment.ps1"
if errorlevel 1 echo 环境配置失败，请查看上面的错误。
pause
