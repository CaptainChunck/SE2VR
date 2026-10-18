@echo off
rem Installs the SE2VR mod for Space Engineers 2 for the current Windows user.
rem Nothing in the game's folder is changed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\Install-SE2VR.ps1"
echo.
pause
